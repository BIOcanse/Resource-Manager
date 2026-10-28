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

    internal HostManagerDeploymentAttemptToken BeginCreate(CompiledHostManagerPlan plan)
        => deploymentState.BeginResourceCreate(
            HostManagerModuleKind.FileQuery, plan,
            HostManagerDeploymentOperation.HostRecreateAndHotPublish);

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
    INativeFileQueryLeaseSource,
    IDisposable
{
    private readonly ReaderWriterLockSlim gate = new(LockRecursionPolicy.NoRecursion);
    private readonly RuntimePlanProvider planProvider;
    private readonly Func<ulong, uint, INativeFileQueryScratch> createScratch;
    private readonly HostManagerFileQueryRuntime deployment;
    private readonly ILogger<HostManagerFileQueryOwner> logger;
    private WorkspaceHolder? current;
    private readonly List<WorkspaceHolder> pendingRetirements = [];
    private CompiledHostManagerFileQueryPlan? appliedPlan;
    private long nextEpoch;
    private readonly object lifetimeGate = new();
    private int holders;
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

    public async ValueTask<NativeFileQueryLease> AcquireAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WorkspaceHolder holder;
        lock (lifetimeGate)
        {
            ThrowIfDisposed();
            if (holders == 0)
            {
                Action<CompiledRuntimePlan> handler = null!;
                handler = plan =>
                {
                    lock (lifetimeGate)
                        Apply(plan.HostManager.RequirePublished(), throwOnFailure: false, handler);
                };
                publishedHandler = handler;
                planProvider.Published += handler;
                try { Apply(planProvider.Current.HostManager.RequirePublished(), throwOnFailure: true, handler); }
                catch
                {
                    planProvider.Published -= handler;
                    publishedHandler = null;
                    throw;
                }
            }
            holder = current!;
            holder.AddReference();
            holders++;
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
        lock (lifetimeGate)
        {
            if (publishedHandler is { } handler) planProvider.Published -= handler;
            publishedHandler = null;
            disposed = true;
            RetireCurrent();
            DrainPendingRetirement();
        }
        // Late lease callbacks retain close obligations for explicit Dispose retry.
    }

    private void RetireCurrent()
    {
        if (current is { } previous)
        {
            current = null;
            appliedPlan = null;
            pendingRetirements.Add(previous);
        }
    }

    private void DrainPendingRetirement()
    {
        for (var index = pendingRetirements.Count - 1; index >= 0; index--)
            if (pendingRetirements[index].Retire()) pendingRetirements.RemoveAt(index);
    }

    private void Release(WorkspaceHolder holder)
    {
        lock (lifetimeGate)
        {
            var released = false;
            try { holder.Release(); released = true; }
            finally
            {
                if (--holders == 0)
                {
                    if (publishedHandler is { } handler) planProvider.Published -= handler;
                    publishedHandler = null;
                    RetireCurrent();
                }
                if (released) DrainPendingRetirement();
            }
        }
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
                ? deployment.BeginCreate(hostPlan)
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
                if (current is { } previous) pendingRetirements.Add(previous);
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
