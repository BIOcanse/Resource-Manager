using ResourceManager.App.Infrastructure.Adaptation;
using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.ReadWrite.Native;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization;

public sealed class HostManagerFileQueryRuntime(
    IRuntimePlanProvider runtimePlanProvider,
    HostManagerDeploymentState deploymentState)
{
    internal CompiledHostManagerFileQueryPlan CaptureDesired()
    {
        var plan = runtimePlanProvider.Current.HostManager.RequirePublished().FileQuery;
        return plan.IsPublished
            ? plan
            : throw new InvalidOperationException(
                "The Host Manager file-query configuration is not published.");
    }

    internal HostManagerDeploymentAttemptToken BeginInitialCreate(CompiledHostManagerPlan plan)
        => deploymentState.BeginAttempt(
            HostManagerModuleKind.FileQuery,
            plan,
            HostManagerDeploymentOperation.InitialCreate);

    internal HostManagerDeploymentAttemptToken BeginHotPublish(CompiledHostManagerPlan plan)
        => deploymentState.BeginAttempt(
            HostManagerModuleKind.FileQuery,
            plan,
            HostManagerDeploymentOperation.HotPublish);

    internal HostManagerDeploymentAttemptToken BeginHostRecreateAndHotPublish(
        CompiledHostManagerPlan plan)
        => deploymentState.BeginAttempt(
            HostManagerModuleKind.FileQuery,
            plan,
            HostManagerDeploymentOperation.HostRecreateAndHotPublish);

    internal HostManagerDeploymentAttemptSettlement CompleteSucceeded(
        HostManagerDeploymentAttemptToken token)
        => HostManagerDeploymentAttemptSettlementGuard.RequireApplied(
            deploymentState.CompleteAttemptSucceeded(token),
            HostManagerModuleKind.FileQuery);

    internal HostManagerDeploymentAttemptSettlement CompleteFailed(
        HostManagerDeploymentAttemptToken token,
        string failureCode)
        => HostManagerDeploymentAttemptSettlementGuard.RequireFailed(
            deploymentState.CompleteAttemptFailed(token, failureCode),
            HostManagerModuleKind.FileQuery);
}

public sealed class HostManagerFileQueryOwner :
    IHostedService,
    INativeFileQueryLeaseSource,
    IDisposable
{
    private readonly ReaderWriterLockSlim gate = new(LockRecursionPolicy.NoRecursion);
    private readonly RuntimePlanProvider planProvider;
    private readonly Func<ulong, uint, INativeFileQueryScratch> createScratch;
    private readonly HostManagerFileQueryRuntime deployment;
    private readonly ILogger<HostManagerFileQueryOwner> logger;
    private WorkspaceHolder? current;
    private WorkspaceHolder? pendingRetirement;
    private CompiledHostManagerFileQueryPlan? appliedPlan;
    private long nextEpoch;
    private bool started;
    private bool disposed;
    private Action<CompiledRuntimePlan>? publishedHandler;

    public HostManagerFileQueryOwner(
        RuntimePlanProvider planProvider,
        HostManagerFileQueryRuntime deployment,
        ILogger<HostManagerFileQueryOwner> logger,
        ResourceManagerLedgerOwner memoryOwner)
        : this(planProvider, deployment, logger,
            (length, alignment) => FileQueryScratch.Create(memoryOwner.CreateScratch, length, alignment)) { }

    internal HostManagerFileQueryOwner(
        RuntimePlanProvider planProvider,
        HostManagerFileQueryRuntime deployment,
        ILogger<HostManagerFileQueryOwner> logger,
        Func<ulong, uint, INativeFileQueryScratch> createScratch)
    {
        this.planProvider = planProvider;
        this.deployment = deployment;
        this.logger = logger;
        this.createScratch = createScratch;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Action<CompiledRuntimePlan> handler = null!;
        handler = plan => Apply(plan.HostManager.RequirePublished(), throwOnFailure: false, handler);
        gate.EnterWriteLock();
        try
        {
            ThrowIfDisposed();
            if (started) return Task.CompletedTask;
            publishedHandler = handler;
            planProvider.Published += handler;
            started = true;
        }
        finally { gate.ExitWriteLock(); }
        try { Apply(planProvider.Current.HostManager.RequirePublished(), throwOnFailure: true, handler); }
        catch { StopAsync(CancellationToken.None).GetAwaiter().GetResult(); throw; }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        gate.EnterWriteLock();
        try
        {
            if (publishedHandler is { } handler) planProvider.Published -= handler;
            publishedHandler = null;
            started = false;
        }
        finally { gate.ExitWriteLock(); }
        return Task.CompletedTask;
    }

    public async ValueTask<NativeFileQueryLease> AcquireAsync(
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        WorkspaceHolder holder;
        gate.EnterReadLock();
        try
        {
            holder = current
                ?? throw new InvalidOperationException(
                    "The Host Manager file-query workspace is not ready.");
            holder.AddReference();
        }
        finally
        {
            gate.ExitReadLock();
        }

        NativeFileQueryLease? inner = null;
        try
        {
            inner = await holder.Workspace.AcquireAsync(cancellationToken).ConfigureAwait(false);
            return new NativeFileQueryLease(
                inner.Session,
                (_, reusable) =>
                {
                    try
                    {
                        if (reusable) inner.MarkReusable();
                        inner.Dispose();
                    }
                    finally { Release(holder); }
                });
        }
        catch
        {
            try { inner?.Dispose(); }
            finally { Release(holder); }
            throw;
        }
    }

    public ulong NextEpoch()
    {
        var value = Interlocked.Increment(ref nextEpoch);
        return value > 0
            ? checked((ulong)value)
            : throw new InvalidOperationException(
                "The Host Manager file-query epoch space is exhausted.");
    }

    public void Dispose()
    {
        gate.EnterWriteLock();
        try
        {
            if (publishedHandler is { } handler) planProvider.Published -= handler;
            publishedHandler = null;
            started = false;
            disposed = true;
            DrainPendingRetirement();
            if (current?.Retire() == true) current = null;
            appliedPlan = null;
        }
        finally
        {
            gate.ExitWriteLock();
        }
        // Late lease callbacks can still retain a failed close after shutdown.
        // Keep the gate usable so another Dispose can retry those obligations.
    }

    private void DrainPendingRetirement()
    {
        if (pendingRetirement?.Retire() == true) pendingRetirement = null;
    }

    private void Release(WorkspaceHolder holder)
    {
        gate.EnterWriteLock();
        try { holder.Release(); }
        finally { gate.ExitWriteLock(); }
    }

    private void Apply(CompiledHostManagerPlan hostPlan, bool throwOnFailure,
        Action<CompiledRuntimePlan>? expectedHandler = null)
    {
        var desired = hostPlan.FileQuery;
        gate.EnterWriteLock();
        try
        {
            if (expectedHandler is not null && !ReferenceEquals(publishedHandler, expectedHandler)) return;
            DrainPendingRetirement();
            if (pendingRetirement is not null)
                throw new InvalidOperationException(
                    "The previous file-query session pool is still in use; replacement must wait for its retirement.");
            if (appliedPlan?.ConfigurationSha256 == desired.ConfigurationSha256
                && appliedPlan.Build == desired.Build
                && appliedPlan.Recreate == desired.Recreate)
            {
                return;
            }

            var initial = current is null;
            var recreate = !initial
                && (appliedPlan!.Build != desired.Build || appliedPlan.Recreate != desired.Recreate);
            var token = initial
                ? deployment.BeginInitialCreate(hostPlan)
                : recreate
                    ? deployment.BeginHostRecreateAndHotPublish(hostPlan)
                    : deployment.BeginHotPublish(hostPlan);
            NativeFileQueryWorkspace? replacement = null;
            WorkspaceHolder? next = null;
            try
            {
                replacement = new NativeFileQueryWorkspace(desired, createScratch);
                next = new WorkspaceHolder(replacement);
                replacement = null;
                deployment.CompleteSucceeded(token);
                pendingRetirement = current;
                current = next;
                appliedPlan = desired;
                next = null;
            }
            catch (Exception ex)
            {
                next?.Retire();
                replacement?.Dispose();
                try
                {
                    deployment.CompleteFailed(token, "file-query-apply-failed");
                }
                catch (Exception settlement)
                {
                    throw new AggregateException(ex, settlement);
                }
                throw;
            }
            // Deployment already succeeded. A failed old-pool close retains
            // that holder and must not settle the successful attempt as failed.
            DrainPendingRetirement();
        }
        catch (Exception ex) when (!throwOnFailure)
        {
            logger.LogError(
                ex,
                "Host Manager file-query plan apply or retirement failed; the current session pool remains active.");
        }
        finally
        {
            gate.ExitWriteLock();
        }
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(disposed, this);

    private sealed class WorkspaceHolder(NativeFileQueryWorkspace workspace)
    {
        private int referenceCount = 1;
        private int retired;

        internal NativeFileQueryWorkspace Workspace { get; } = workspace;

        internal void AddReference()
        {
            if (Volatile.Read(ref retired) != 0)
            {
                throw new ObjectDisposedException(nameof(NativeFileQueryWorkspace));
            }
            Interlocked.Increment(ref referenceCount);
        }

        internal bool Retire()
        {
            if (Interlocked.Exchange(ref retired, 1) == 0)
            {
                try { Release(); }
                catch
                {
                    // A failed final close retains this owner's reference.
                    // Allow an explicit owner Dispose retry to retire it again.
                    Volatile.Write(ref retired, 0);
                    throw;
                }
            }
            return Volatile.Read(ref referenceCount) == 0;
        }

        internal void Release()
        {
            if (Interlocked.Decrement(ref referenceCount) == 0)
            {
                try { Workspace.Dispose(); }
                catch
                {
                    // Transfer the failed close back to the owner for Retire
                    // to retry; the lease reference was already released.
                    Interlocked.Increment(ref referenceCount);
                    Volatile.Write(ref retired, 0);
                    throw;
                }
            }
        }
    }
}
