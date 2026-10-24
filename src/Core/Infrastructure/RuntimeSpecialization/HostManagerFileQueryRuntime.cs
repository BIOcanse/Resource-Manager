using ResourceManager.App.Infrastructure.Adaptation;
using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.NativeCore;

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
    private readonly ResourceManagerLedgerOwner memoryOwner;
    private readonly HostManagerFileQueryRuntime deployment;
    private readonly ILogger<HostManagerFileQueryOwner> logger;
    private WorkspaceHolder? current;
    private WorkspaceHolder? pendingRetirement;
    private CompiledHostManagerFileQueryPlan? appliedPlan;
    private long nextEpoch;
    private bool started;
    private bool disposed;

    public HostManagerFileQueryOwner(
        RuntimePlanProvider planProvider,
        HostManagerFileQueryRuntime deployment,
        ILogger<HostManagerFileQueryOwner> logger,
        ResourceManagerLedgerOwner memoryOwner)
    {
        this.planProvider = planProvider;
        this.deployment = deployment;
        this.logger = logger;
        this.memoryOwner = memoryOwner;
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
        started = true;
        Apply(planProvider.Current.HostManager.RequirePublished(), throwOnFailure: true);
        return Task.CompletedTask;
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
                    if (reusable) inner.MarkReusable();
                    inner.Dispose();
                    holder.Release();
                });
        }
        catch
        {
            inner?.Dispose();
            holder.Release();
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
        if (disposed)
        {
            return;
        }

        if (started)
        {
            planProvider.Published -= OnPublished;
            started = false;
        }
        gate.EnterWriteLock();
        try
        {
            DrainPendingRetirement();
            current?.Retire();
            current = null;
            appliedPlan = null;
            disposed = true;
        }
        finally
        {
            gate.ExitWriteLock();
        }
        gate.Dispose();
    }

    private void DrainPendingRetirement()
    {
        pendingRetirement?.Retire();
        pendingRetirement = null;
    }

    private void OnPublished(CompiledRuntimePlan plan)
        => Apply(plan.HostManager.RequirePublished(), throwOnFailure: false);

    private void Apply(CompiledHostManagerPlan hostPlan, bool throwOnFailure)
    {
        var desired = hostPlan.FileQuery;
        gate.EnterWriteLock();
        try
        {
            DrainPendingRetirement();
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
                replacement = new NativeFileQueryWorkspace(desired,
                    (length, alignment) => FileQueryScratch.Create(memoryOwner.CreateScratch, length, alignment));
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

        internal void Retire()
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
        }

        internal void Release()
        {
            if (Interlocked.Decrement(ref referenceCount) == 0)
            {
                try { Workspace.Dispose(); }
                catch
                {
                    // The lease that attempted the final release still owns
                    // this holder until its explicit retry closes the pool.
                    Interlocked.Increment(ref referenceCount);
                    throw;
                }
            }
        }
    }
}
