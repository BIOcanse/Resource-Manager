using System.Collections.Concurrent;
using ResourceManager.App.Infrastructure.Adaptation;
using ResourceManager.ReadWrite.Native;
using ResourceManager.ReadWrite.Windows.Metrics.Topology;
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
    private readonly Func<ulong, uint, INativeMetricSnapshotScratch> createScratch;
    // Actual holders remain owned until their final close succeeds, including
    // failures arriving later from already-retired external leases.
    private readonly ConcurrentDictionary<WorkspaceHolder, byte> workspaces = new();
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
        ILogger<HostManagerMetricSnapshotOwner> logger,
        ResourceManagerLedgerOwner ledgerOwner)
        : this(planProvider, deployment, topologySource, environment, logger,
            (bytes, alignment) => MetricSnapshotScratch.Create(
                (ledgerOwner ?? throw new ArgumentNullException(nameof(ledgerOwner))).CreateScratch,
                bytes, alignment))
    {
        ArgumentNullException.ThrowIfNull(ledgerOwner);
    }

    internal HostManagerMetricSnapshotOwner(
        RuntimePlanProvider planProvider,
        HostManagerMetricSnapshotRuntime deployment,
        HostManagerMetricSnapshotCatalogTopologySource topologySource,
        IHostEnvironment environment,
        ILogger<HostManagerMetricSnapshotOwner> logger,
        Func<ulong, uint, INativeMetricSnapshotScratch> createScratch)
    {
        this.createScratch = createScratch ?? throw new ArgumentNullException(nameof(createScratch));
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

    internal bool TryAcquire(out WorkspaceLease? lease)
    {
        ThrowIfDisposed();
        gate.EnterReadLock();
        try
        {
            if (current is null) { lease = null; return false; }
            current.AddReference();
            lease = new WorkspaceLease(current);
            return true;
        }
        finally { gate.ExitReadLock(); }
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

    internal NativeMetricSnapshotCatalogTopologySnapshot? RefreshTopologyIfRequested()
    {
        if (!topologyRefresh.HasPending)
        {
            return null;
        }

        lock (reconfigurationGate)
        {
            RetryWorkspaceCloses();
            var requestedTicket = topologyRefresh.CaptureRequested();
            if (!topologyRefresh.IsPending(requestedTicket))
            {
                return null;
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
                return null;
            }

            WorkspaceHolder? replacement = null;
            NativeMetricSnapshotCatalogTopologySnapshot? topology = null;
            try
            {
                topology = topologySource.Capture(WindowsNpuMemoryTopology.EffectiveMaximumInstanceCount(
                    NativeMetricSnapshotHostConfiguration.CreateCatalogConfiguration(plan).RuleTemplates), WindowsIntelFanTopology.EffectiveMaximumInstanceCount(
                    NativeMetricSnapshotHostConfiguration.CreateCatalogConfiguration(plan).RuleTemplates), current?.Workspace.ReadCatalog(), WindowsNpuEngineTopology.EffectiveMaximumInstanceCount(
                    NativeMetricSnapshotHostConfiguration.CreateCatalogConfiguration(plan).RuleTemplates), WindowsAmdCcdTopology.EffectiveMaximumInstanceCount(
                    NativeMetricSnapshotHostConfiguration.CreateCatalogConfiguration(plan).RuleTemplates));
                RequireGpuTopology(topology);
                var priorCatalog = observed.Workspace.ReadCatalog();
                var availableTopology = RetainUnavailableOptionalIdentities(topology, priorCatalog);
                var desiredCatalog = ProjectCatalog(plan, availableTopology, priorCatalog.HandleMap);
                if (CanRetainCatalog(priorCatalog, desiredCatalog))
                {
                    observed.UpdateTopology(topology);
                    topologyRefresh.Confirm(requestedTicket);
                    return topology;
                }
                // CreateWorkspace consumes the original capture references even if creation fails.
                topology = topology with { IgclFanDirectoryReference = null, NpuEngineDirectoryReference = null };
                replacement = CreateWorkspace(plan, availableTopology);
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
                topology = (topology ?? observed.ReadTopology()) with
                {
                    GpuStatus = NativeMetricSnapshotCatalogTopologyStatus.Unavailable,
                    StorageStatus = NativeMetricSnapshotCatalogTopologyStatus.Unavailable,
                    FanStatus = NativeMetricSnapshotCatalogTopologyStatus.Unavailable,
                    NvmlFanStatus = NativeMetricSnapshotCatalogTopologyStatus.Unavailable,
                    IgclFanStatus = NativeMetricSnapshotCatalogTopologyStatus.Unavailable,
                    NotebookOemFanStatus = NativeMetricSnapshotCatalogTopologyStatus.Unavailable,
                    BatteryStatus = NativeMetricSnapshotCatalogTopologyStatus.Unavailable,
                    NpuEngineStatus = NativeMetricSnapshotCatalogTopologyStatus.Unavailable,
                    NpuMemoryStatus = NativeMetricSnapshotCatalogTopologyStatus.Unavailable,
                    AmdCcdStatus = NativeMetricSnapshotCatalogTopologyStatus.Unavailable,
                    NetworkInterfaceStatus = NativeMetricSnapshotCatalogTopologyStatus.Unavailable,
                    NumaStatus = NativeMetricSnapshotCatalogTopologyStatus.Unavailable,
                    IntelCpuCoreStatus = NativeMetricSnapshotCatalogTopologyStatus.Unavailable,
                    IntelCpuPackageStatus = NativeMetricSnapshotCatalogTopologyStatus.Unavailable,
                    IntelCpuLogicalProcessorStatus = NativeMetricSnapshotCatalogTopologyStatus.Unavailable,
                    IntelCpuDieStatus = NativeMetricSnapshotCatalogTopologyStatus.Unavailable,
                    IntelPlatformStatus = NativeMetricSnapshotCatalogTopologyStatus.Unavailable
                };
                if (ReferenceEquals(current, observed)) observed.UpdateTopology(topology);
                logger.LogWarning(
                    ex,
                    "The device topology could not be refreshed; the prior metric-snapshot catalog remains declared.");
            }
            finally { try { topology?.IgclFanDirectoryReference?.Dispose(); } finally { topology?.NpuEngineDirectoryReference?.Dispose(); } }
            return topology;
        }
    }

    internal static NativeMetricSnapshotCatalogTopologySnapshot RetainUnavailableOptionalIdentities(
        NativeMetricSnapshotCatalogTopologySnapshot topology, NativeMetricSnapshotCatalogProjection prior)
        => ResourceManager.ReadWrite.Windows.Metrics.Topology.WindowsMetricCatalogTopologyPublication.RetainUnavailableOptionalIdentities(topology, prior);

    internal static bool CanRetainCatalog(NativeMetricSnapshotCatalogProjection prior, NativeMetricSnapshotCatalogProjection desired)
        => ResourceManager.ReadWrite.Windows.Metrics.Topology.WindowsMetricCatalogTopologyPublication.CanRetainCatalog(prior, desired);
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
                RetryWorkspaceCloses();
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
            var failures = new List<Exception>();
            try { previous?.ReleaseOwnerReference(); }
            catch (Exception exception) { failures.Add(exception); }
            try { RetryWorkspaceCloses(previous); }
            catch (Exception exception) { failures.Add(exception); }
            finally { gate.Dispose(); }
            if (failures.Count == 1) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
            if (failures.Count > 1) throw new AggregateException(failures);
        }
    }

    private void RetryWorkspaceCloses(WorkspaceHolder? skip = null)
    {
        var failures = new List<Exception>();
        foreach (var holder in workspaces.Keys)
        {
            if (ReferenceEquals(holder, skip)) continue;
            try { holder.CloseIfUnused(); }
            catch (Exception exception) { failures.Add(exception); }
        }
        if (failures.Count == 1) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count > 1) throw new AggregateException(failures);
    }

    private void OnPublished(CompiledRuntimePlan plan)
        => Apply(plan.HostManager.RequirePublished(), throwOnFailure: false);

    private void Apply(CompiledHostManagerPlan hostPlan, bool throwOnFailure)
    {
        var desired = hostPlan.MetricSnapshot;
        lock (reconfigurationGate)
        {
            try { RetryWorkspaceCloses(); }
            catch (Exception exception)
            {
                if (throwOnFailure) throw;
                logger.LogError(exception, "A retired metric-snapshot workspace is still closing; replacement is blocked.");
                return;
            }
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
        CompiledHostManagerMetricSnapshotPlan plan,
        NativeMetricSnapshotCatalogTopologySnapshot? topology = null)
    {
        NativeMetricSnapshotWorkspace? workspace = null;
        WorkspaceHolder? holder = null;
        try
        {
            RetryWorkspaceCloses();
            workspace = NativeMetricSnapshotHostConfiguration.CreateWorkspace(
                plan,
                ResolvePersistencePath(
                    plan.Recreate.PersistenceRelativePath), createScratch);
            topology ??= topologySource.Capture(WindowsNpuMemoryTopology.EffectiveMaximumInstanceCount(
                    NativeMetricSnapshotHostConfiguration.CreateCatalogConfiguration(plan).RuleTemplates), WindowsIntelFanTopology.EffectiveMaximumInstanceCount(
                    NativeMetricSnapshotHostConfiguration.CreateCatalogConfiguration(plan).RuleTemplates), current?.Workspace.ReadCatalog(), WindowsNpuEngineTopology.EffectiveMaximumInstanceCount(
                    NativeMetricSnapshotHostConfiguration.CreateCatalogConfiguration(plan).RuleTemplates), WindowsAmdCcdTopology.EffectiveMaximumInstanceCount(
                    NativeMetricSnapshotHostConfiguration.CreateCatalogConfiguration(plan).RuleTemplates));
            RequireGpuTopology(topology);
            if (current is { } active)
                topology = RetainUnavailableOptionalIdentities(topology, active.Workspace.ReadCatalog());
            var catalog = ProjectCatalog(plan, topology, workspace.CreateCatalogHandleMap());
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
            holder = new WorkspaceHolder(
                NextWorkspaceIdentity(),
                workspace,
                topology,
                closedHolder => workspaces.TryRemove(closedHolder, out _));
            workspaces.TryAdd(holder, 0);
            workspace = null;
            topology = null; // The registered holder now owns every Native and directory close obligation.
            holder.RetainDirectories();
            holder.ReleaseInitialReferences();
            return holder;
        }
        catch (Exception failure)
        {
            var failures = new List<Exception> { failure };
            try { holder?.Retire(); }
            catch (Exception exception) { failures.Add(exception); }
            try { workspace?.Dispose(); }
            catch (Exception exception) { failures.Add(exception); }
            try { topology?.IgclFanDirectoryReference?.Dispose(); }
            catch (Exception exception) { failures.Add(exception); }
            try { topology?.NpuEngineDirectoryReference?.Dispose(); }
            catch (Exception exception) { failures.Add(exception); }
            if (failures.Count == 1) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            throw new AggregateException(failures);
        }
    }

    private static void RequireGpuTopology(NativeMetricSnapshotCatalogTopologySnapshot topology)
        => ResourceManager.ReadWrite.Windows.Metrics.Topology.WindowsMetricCatalogTopologyPublication.RequireGpuTopology(topology);

    private static NativeMetricSnapshotCatalogProjection ProjectCatalog(
        CompiledHostManagerMetricSnapshotPlan plan,
        NativeMetricSnapshotCatalogTopologySnapshot topology,
        NativeMetricSnapshotCatalogHandleMap handleMap)
        => NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(plan, topology.GpuAdapters,
            topology.StorageSensors, topology.SystemFans, handleMap, topology.CpuVendor, topology.Batteries, topology.NumaNodes, topology.IntelCpuIdentities, topology.NetworkInterfaces, topology.NpuMemoryIdentities, topology.NpuEngineIdentities, topology.NpuEngineDirectory, topology.AmdCcdIdentities);

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

        internal NativeMetricSnapshotCatalogTopologySnapshot Topology
            => (Volatile.Read(ref holder) ?? throw new ObjectDisposedException(nameof(WorkspaceLease))).ReadTopology();

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

    internal sealed class WorkspaceHolder
    {
        private readonly object publicationGate = new();
        private int referenceCount = 1;
        private int retired;
        private NativeMetricSnapshotCatalogTopologySnapshot topology;
        private IDisposable? intelFanDirectoryLease;
        private IDisposable? npuEngineDirectoryLease;
        private IDisposable? initialIntelDirectoryReference;
        private IDisposable? initialNpuDirectoryReference;
        private readonly Action<WorkspaceHolder>? onClosed;
        private bool closed;
        private Exception? closeFailure;
        internal Exception? CloseFailure => Volatile.Read(ref closeFailure);

        internal WorkspaceHolder(
            ulong identity,
            NativeMetricSnapshotWorkspace workspace,
            NativeMetricSnapshotCatalogTopologySnapshot initialTopology,
            Action<WorkspaceHolder>? onClosed = null)
        {
            if (identity == 0) throw new ArgumentOutOfRangeException(nameof(identity));
            ArgumentNullException.ThrowIfNull(workspace);
            ArgumentNullException.ThrowIfNull(initialTopology);
            Identity = identity;
            Workspace = workspace;
            topology = initialTopology;
            this.onClosed = onClosed;
            initialIntelDirectoryReference = initialTopology.IgclFanDirectoryReference;
            initialNpuDirectoryReference = initialTopology.NpuEngineDirectoryReference;
        }

        // Called once, after registry ownership is established. Partial acquisition
        // failure remains represented by this same holder's non-null fields.
        internal void RetainDirectories()
        {
            intelFanDirectoryLease = WindowsIntelFanTopology.RetainDirectory(topology.SystemFans);
            npuEngineDirectoryLease = topology.NpuEngineDirectory?.AcquireLease();
        }

        internal void ReleaseInitialReferences()
        {
            lock (publicationGate)
            {
                var failures = new List<Exception>();
                try { initialIntelDirectoryReference?.Dispose(); initialIntelDirectoryReference = null; }
                catch (Exception exception) { failures.Add(exception); }
                try { initialNpuDirectoryReference?.Dispose(); initialNpuDirectoryReference = null; }
                catch (Exception exception) { failures.Add(exception); }
                if (failures.Count == 1) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
                if (failures.Count > 1) throw new AggregateException(failures);
            }
        }

        internal NativeMetricSnapshotCatalogTopologySnapshot ReadTopology() => Volatile.Read(ref topology);
        internal void UpdateTopology(NativeMetricSnapshotCatalogTopologySnapshot observed) => Volatile.Write(ref topology, observed);

        internal NativeMetricSnapshotWorkspace Workspace { get; }

        internal ulong Identity { get; }

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
            // A lease releases its reference exactly once. The original owner
            // already retains this holder, so a close failure transfers no count.
            if (Interlocked.Decrement(ref referenceCount) == 0) CloseIfUnused();
        }

        internal void CloseIfUnused()
        {
            lock (publicationGate)
            {
                if (closed || Volatile.Read(ref referenceCount) != 0) return;
                try
                {
                    // Keep directory loans too if Native/endUse is still closing.
                    Workspace.Dispose();
                    var failures = new List<Exception>();
                    try { ReleaseInitialReferences(); }
                    catch (Exception exception) { failures.Add(exception); }
                    try { intelFanDirectoryLease?.Dispose(); intelFanDirectoryLease = null; }
                    catch (Exception exception) { failures.Add(exception); }
                    try { npuEngineDirectoryLease?.Dispose(); npuEngineDirectoryLease = null; }
                    catch (Exception exception) { failures.Add(exception); }
                    if (failures.Count == 1) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
                    if (failures.Count > 1) throw new AggregateException(failures);
                    closed = true;
                    Volatile.Write(ref closeFailure, null);
                    onClosed?.Invoke(this);
                }
                catch (Exception exception)
                {
                    // Keep the first real close failure until this holder closes.
                    Interlocked.CompareExchange(ref closeFailure, exception, null);
                    throw;
                }
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

