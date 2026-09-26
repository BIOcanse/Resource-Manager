using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.Paths;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace ResourceManager.App.Infrastructure.Monitoring;

public sealed class HostManagerMetricSnapshotOwner :
    IHostedService,
    IDisposable
{
    private readonly ReaderWriterLockSlim gate =
        new(LockRecursionPolicy.NoRecursion);
    private readonly object reconfigurationGate = new();
    private readonly MonotonicRefreshTicket topologyRefresh = new();
    private readonly RuntimePlanProvider planProvider;
    private readonly HostManagerMetricSnapshotRuntime deployment;
    private readonly HostManagerMetricSnapshotCatalogTopologySource
        topologySource;
    private readonly string hostManagerDataRoot;
    private readonly ILogger<HostManagerMetricSnapshotOwner> logger;
    private readonly TaskCompletionSource ready =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private WorkspaceHolder? current;
    private CompiledHostManagerMetricSnapshotPlan? appliedPlan;
    private ulong workspaceIdentitySequence;
    private bool started;
    private bool disposed;

    public HostManagerMetricSnapshotOwner(
        RuntimePlanProvider planProvider,
        HostManagerMetricSnapshotRuntime deployment,
        HostManagerMetricSnapshotCatalogTopologySource topologySource,
        IHostEnvironment environment,
        ILogger<HostManagerMetricSnapshotOwner> logger)
    {
        this.planProvider = planProvider;
        this.deployment = deployment;
        this.topologySource = topologySource;
        this.logger = logger;
        hostManagerDataRoot = Path.GetFullPath(Path.Combine(
            PackagePathResolver.ResolvePackageRoot(environment.ContentRootPath),
            "UserData",
            "HostManager"));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        if (started)
        {
            return Task.CompletedTask;
        }

        planProvider.Published += OnPublished;
        try
        {
            Apply(
                planProvider.Current.HostManager.RequirePublished(),
                throwOnFailure: true);
            started = true;
            ready.TrySetResult();
            return Task.CompletedTask;
        }
        catch
        {
            planProvider.Published -= OnPublished;
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (started)
        {
            planProvider.Published -= OnPublished;
            started = false;
        }
        return Task.CompletedTask;
    }

    internal WorkspaceLease Acquire()
    {
        ThrowIfDisposed();
        gate.EnterReadLock();
        try
        {
            var holder = current
                ?? throw new InvalidOperationException(
                    "The Host Manager metric-snapshot workspace is not ready.");
            holder.AddReference();
            return new WorkspaceLease(holder);
        }
        finally
        {
            gate.ExitReadLock();
        }
    }

    internal async ValueTask<WorkspaceLease> AcquireAsync(
        CancellationToken cancellationToken)
    {
        await ready.Task.WaitAsync(cancellationToken);
        return Acquire();
    }

    internal void RequestTopologyRefresh()
        => topologyRefresh.Request();

    internal ulong CurrentWorkspaceIdentity
    {
        get
        {
            ThrowIfDisposed();
            gate.EnterReadLock();
            try
            {
                return current?.Identity ?? 0;
            }
            finally
            {
                gate.ExitReadLock();
            }
        }
    }

    internal void RefreshTopologyIfRequested()
    {
        if (!topologyRefresh.HasPending)
        {
            return;
        }

        lock (reconfigurationGate)
        {
            var requestedTicket = topologyRefresh.CaptureRequested();
            if (!topologyRefresh.IsPending(requestedTicket))
            {
                return;
            }

            WorkspaceHolder? observed;
            CompiledHostManagerMetricSnapshotPlan? plan;
            gate.EnterReadLock();
            try
            {
                observed = current;
                plan = appliedPlan;
            }
            finally
            {
                gate.ExitReadLock();
            }
            if (observed is null || plan is null)
            {
                return;
            }

            WorkspaceHolder? replacement = null;
            try
            {
                replacement = CreateWorkspace(plan);
                gate.EnterWriteLock();
                try
                {
                    if (!ReferenceEquals(current, observed)
                        || appliedPlan != plan)
                    {
                        throw new InvalidOperationException(
                            "The metric-snapshot owner changed while a topology replacement was staged.");
                    }
                    observed.StopPublication();
                    current = replacement;
                    topologyRefresh.Confirm(requestedTicket);
                }
                finally
                {
                    gate.ExitWriteLock();
                }
                replacement = null;
                try
                {
                    observed.ReleaseOwnerReference();
                }
                catch (Exception ex)
                {
                    logger.LogError(
                        ex,
                        "The retired metric-snapshot workspace could not be disposed after a topology refresh.");
                }
            }
            catch (Exception ex)
            {
                replacement?.Retire();
                logger.LogWarning(
                    ex,
                    "The exact GPU topology changed, but the metric-snapshot catalog could not be rebuilt.");
            }
        }
    }

    internal T Execute<T>(Func<NativeMetricSnapshotWorkspace, T> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        using var lease = Acquire();
        return operation(lease.Workspace);
    }

    public void Dispose()
    {
        lock (reconfigurationGate)
        {
            if (disposed)
            {
                return;
            }
            if (started)
            {
                planProvider.Published -= OnPublished;
                started = false;
            }

            WorkspaceHolder? previous;
            gate.EnterWriteLock();
            try
            {
                previous = current;
                previous?.StopPublication();
                current = null;
                appliedPlan = null;
                disposed = true;
                ready.TrySetException(new ObjectDisposedException(
                    nameof(HostManagerMetricSnapshotOwner)));
            }
            finally
            {
                gate.ExitWriteLock();
            }
            previous?.ReleaseOwnerReference();
            gate.Dispose();
        }
    }

    private void OnPublished(CompiledRuntimePlan plan)
        => Apply(plan.HostManager.RequirePublished(), throwOnFailure: false);

    private void Apply(CompiledHostManagerPlan hostPlan, bool throwOnFailure)
    {
        var desired = hostPlan.MetricSnapshot;
        lock (reconfigurationGate)
        {
            var initial = current is null;
            var canApplyHot = !initial && deployment.CanApplyHot(hostPlan);
            gate.EnterReadLock();
            try
            {
                if (!initial
                    && CanRetainWorkspace(
                        appliedPlan!,
                        desired,
                        canApplyHot))
                {
                    return;
                }
            }
            finally
            {
                gate.ExitReadLock();
            }

            var requestedTicket = topologyRefresh.CaptureRequested();
            var token = initial
                ? deployment.BeginInitialCreate(hostPlan)
                : canApplyHot
                    ? deployment.BeginHotPublish(hostPlan)
                    : deployment.BeginHostRecreateAndHotPublish(hostPlan);
            WorkspaceHolder? next = null;
            try
            {
                next = CreateWorkspace(desired);
            }
            catch (Exception ex)
            {
                var failure = SettleFailedAndDispose(
                    token,
                    next,
                    null,
                    ex);
                if (throwOnFailure)
                {
                    throw failure;
                }
                logger.LogError(
                    failure,
                    "Host Manager metric-snapshot apply failed; the prior workspace remains active.");
                return;
            }

            WorkspaceHolder? previous;
            gate.EnterWriteLock();
            try
            {
                deployment.CompleteSucceeded(token);
                previous = current;
                previous?.StopPublication();
                current = next;
                appliedPlan = desired;
                if (requestedTicket != 0)
                {
                    topologyRefresh.Confirm(requestedTicket);
                }
            }
            catch (Exception ex)
            {
                next.Retire();
                if (throwOnFailure)
                {
                    throw;
                }
                logger.LogError(
                    ex,
                    "Host Manager metric-snapshot settlement failed; the prior workspace remains active.");
                return;
            }
            finally
            {
                gate.ExitWriteLock();
            }
            next = null;
            try
            {
                previous?.ReleaseOwnerReference();
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "The retired metric-snapshot workspace could not be disposed.");
            }
        }
    }

    internal static bool CanRetainWorkspace(
        CompiledHostManagerMetricSnapshotPlan applied,
        CompiledHostManagerMetricSnapshotPlan desired,
        bool canApplyHot)
    {
        ArgumentNullException.ThrowIfNull(applied);
        ArgumentNullException.ThrowIfNull(desired);
        return canApplyHot
            && string.Equals(
                applied.RecreateSha256,
                desired.RecreateSha256,
                StringComparison.Ordinal)
            && string.Equals(
                applied.HotPublishSha256,
                desired.HotPublishSha256,
                StringComparison.Ordinal);
    }

    private Exception SettleFailedAndDispose(
        HostManagerDeploymentAttemptToken token,
        WorkspaceHolder? holder,
        NativeMetricSnapshotWorkspace? workspace,
        Exception failure)
    {
        var failures = new List<Exception> { failure };
        try
        {
            holder?.Retire();
        }
        catch (Exception ex)
        {
            failures.Add(ex);
        }
        try
        {
            workspace?.Dispose();
        }
        catch (Exception ex)
        {
            failures.Add(ex);
        }
        try
        {
            deployment.CompleteFailed(
                token,
                "metric-snapshot-apply-failed");
        }
        catch (Exception ex)
        {
            failures.Add(ex);
        }

        return failures.Count == 1
            ? failures[0]
            : new AggregateException(failures);
    }

    private WorkspaceHolder CreateWorkspace(
        CompiledHostManagerMetricSnapshotPlan plan)
    {
        NativeMetricSnapshotWorkspace? workspace = null;
        try
        {
            workspace = new NativeMetricSnapshotWorkspace(
                plan,
                ResolvePersistencePath(
                    plan.Recreate.PersistenceRelativePath));
            var topology = topologySource.Capture();
            if (topology.GpuStatus
                    != NativeMetricSnapshotCatalogTopologyStatus.Complete
                || topology.Generation == 0)
            {
                throw new InvalidOperationException(
                    "The required exact GPU topology is unavailable.");
            }
            var catalog = NativeMetricSnapshotCatalogProjection.Create(
                plan,
                topology.GpuAdapters,
                topology.StorageSensors,
                topology.SystemFans,
                workspace.CreateCatalogHandleMap());
            var now = DateTimeOffset.UtcNow;
            var catalogHeader = workspace.ReplaceCatalog(
                catalog,
                now);
            if (catalogHeader.CatalogFingerprint
                    != catalog.NativeRowFingerprint
                || catalogHeader.MetricCount != catalog.MetricIds.Count)
            {
                throw new InvalidOperationException(
                    "The native metric-snapshot catalog does not match the compiled manifest projection.");
            }
            var holder = new WorkspaceHolder(
                NextWorkspaceIdentity(),
                workspace);
            workspace = null;
            return holder;
        }
        finally
        {
            workspace?.Dispose();
        }
    }

    private string ResolvePersistencePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)
            || Path.IsPathRooted(relativePath)
            || relativePath.Contains('\\'))
        {
            throw new InvalidOperationException(
                "The compiled metric-snapshot persistence path is not canonical.");
        }
        var segments = relativePath.Split('/', StringSplitOptions.None);
        if (segments.Any(static segment =>
                string.IsNullOrEmpty(segment) || segment is "." or ".."))
        {
            throw new InvalidOperationException(
                "The compiled metric-snapshot persistence path contains an invalid segment.");
        }
        var result = Path.GetFullPath(
            Path.Combine(hostManagerDataRoot, Path.Combine(segments)));
        var prefix = Path.EndsInDirectorySeparator(hostManagerDataRoot)
            ? hostManagerDataRoot
            : hostManagerDataRoot + Path.DirectorySeparatorChar;
        if (!result.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The compiled metric-snapshot persistence path escapes the Host Manager data root.");
        }
        return result;
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(disposed, this);

    private ulong NextWorkspaceIdentity()
    {
        if (workspaceIdentitySequence == ulong.MaxValue)
        {
            throw new OverflowException(
                "The metric-snapshot workspace identity space is exhausted.");
        }
        workspaceIdentitySequence++;
        return workspaceIdentitySequence;
    }

    internal sealed class WorkspaceLease : IDisposable
    {
        private WorkspaceHolder? holder;

        internal WorkspaceLease(WorkspaceHolder holder)
        {
            this.holder = holder;
            Workspace = holder.Workspace;
            WorkspaceIdentity = holder.Identity;
        }

        internal NativeMetricSnapshotWorkspace Workspace { get; }

        internal ulong WorkspaceIdentity { get; }

        internal bool TryPublish<T>(
            Func<NativeMetricSnapshotWorkspace, T> publication,
            out T result)
        {
            ArgumentNullException.ThrowIfNull(publication);
            var currentHolder = holder;
            if (currentHolder is null)
            {
                throw new ObjectDisposedException(nameof(WorkspaceLease));
            }
            return currentHolder.TryPublish(publication, out result);
        }

        public void Dispose()
            => Interlocked.Exchange(ref holder, null)?.Release();
    }

    internal sealed class WorkspaceHolder(
        ulong identity,
        NativeMetricSnapshotWorkspace workspace)
    {
        private readonly object publicationGate = new();
        private int referenceCount = 1;
        private int retired;

        internal NativeMetricSnapshotWorkspace Workspace { get; } = workspace;

        internal ulong Identity { get; } = identity != 0
            ? identity
            : throw new ArgumentOutOfRangeException(nameof(identity));

        internal void AddReference()
        {
            if (Volatile.Read(ref retired) != 0)
            {
                throw new ObjectDisposedException(
                    nameof(NativeMetricSnapshotWorkspace));
            }
            Interlocked.Increment(ref referenceCount);
        }

        internal bool TryPublish<T>(
            Func<NativeMetricSnapshotWorkspace, T> publication,
            out T result)
        {
            lock (publicationGate)
            {
                if (Volatile.Read(ref retired) != 0)
                {
                    result = default!;
                    return false;
                }
                result = publication(Workspace);
                return true;
            }
        }

        internal void StopPublication()
        {
            lock (publicationGate)
            {
                Volatile.Write(ref retired, 1);
            }
        }

        internal void ReleaseOwnerReference()
            => Release();

        internal void Retire()
        {
            StopPublication();
            ReleaseOwnerReference();
        }

        internal void Release()
        {
            if (Interlocked.Decrement(ref referenceCount) == 0)
            {
                Workspace.Dispose();
            }
        }
    }

    internal sealed class MonotonicRefreshTicket
    {
        private long requested;
        private long applied;

        internal bool HasPending
            => Volatile.Read(ref requested) > Volatile.Read(ref applied);

        internal ulong Request()
        {
            while (true)
            {
                var observed = Volatile.Read(ref requested);
                if (observed == long.MaxValue)
                {
                    throw new OverflowException(
                        "The metric-snapshot topology refresh sequence is exhausted.");
                }
                var next = observed + 1;
                if (Interlocked.CompareExchange(
                        ref requested,
                        next,
                        observed) == observed)
                {
                    return checked((ulong)next);
                }
            }
        }

        internal ulong CaptureRequested()
            => checked((ulong)Volatile.Read(ref requested));

        internal bool IsPending(ulong ticket)
            => ticket != 0
                && ticket > checked((ulong)Volatile.Read(ref applied));

        internal void Confirm(ulong ticket)
        {
            if (ticket == 0 || ticket > CaptureRequested())
            {
                throw new ArgumentOutOfRangeException(nameof(ticket));
            }

            var value = checked((long)ticket);
            while (true)
            {
                var observed = Volatile.Read(ref applied);
                if (observed >= value)
                {
                    return;
                }
                if (Interlocked.CompareExchange(
                        ref applied,
                        value,
                        observed) == observed)
                {
                    return;
                }
            }
        }
    }
}
