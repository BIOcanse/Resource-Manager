using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization;

public sealed class HostManagerSamplingSubscriptionRuntime(
    IRuntimePlanProvider runtimePlanProvider,
    HostManagerDeploymentState deploymentState)
{
    internal CompiledHostManagerSamplingSubscriptionPlan CaptureDesired()
    {
        var plan = runtimePlanProvider.Current.HostManager.RequirePublished().SamplingSubscription;
        return plan.IsPublished
            ? plan
            : throw new InvalidOperationException(
                "The Host Manager sampling-subscription configuration is not published.");
    }

    internal HostManagerDeploymentAttemptToken BeginInitialCreate(CompiledHostManagerPlan plan)
        => deploymentState.BeginAttempt(
            HostManagerModuleKind.SamplingSubscription,
            plan,
            HostManagerDeploymentOperation.InitialCreate);

    internal HostManagerDeploymentAttemptToken BeginHotPublish(CompiledHostManagerPlan plan)
        => deploymentState.BeginAttempt(
            HostManagerModuleKind.SamplingSubscription,
            plan,
            HostManagerDeploymentOperation.HotPublish);

    internal HostManagerDeploymentAttemptToken BeginHostRecreateAndHotPublish(CompiledHostManagerPlan plan)
        => deploymentState.BeginAttempt(
            HostManagerModuleKind.SamplingSubscription,
            plan,
            HostManagerDeploymentOperation.HostRecreateAndHotPublish);

    internal bool CanApplyHot(CompiledHostManagerPlan plan)
        => deploymentState.CanApplyHot(HostManagerModuleKind.SamplingSubscription, plan);

    internal HostManagerDeploymentAttemptSettlement CompleteSucceeded(
        HostManagerDeploymentAttemptToken token)
        => HostManagerDeploymentAttemptSettlementGuard.RequireApplied(
            deploymentState.CompleteAttemptSucceeded(token),
            HostManagerModuleKind.SamplingSubscription);

    internal HostManagerDeploymentAttemptSettlement CompleteFailed(
        HostManagerDeploymentAttemptToken token,
        string failureCode)
        => HostManagerDeploymentAttemptSettlementGuard.RequireFailed(
            deploymentState.CompleteAttemptFailed(token, failureCode),
            HostManagerModuleKind.SamplingSubscription);
}

public sealed class HostManagerSamplingSubscriptionOwner : IHostedService, IDisposable
{
    private readonly object lifecycleGate = new();
    private readonly ReaderWriterLockSlim gate = new(LockRecursionPolicy.NoRecursion);
    private readonly RuntimePlanProvider planProvider;
    private readonly HostManagerSamplingSubscriptionRuntime deployment;
    private readonly ILogger<HostManagerSamplingSubscriptionOwner> logger;
    private WorkspaceHolder? current;
    private CompiledHostManagerSamplingSubscriptionPlan? appliedPlan;
    private ulong nextWorkspaceIncarnation;
    private bool started;
    private bool disposed;

    public HostManagerSamplingSubscriptionOwner(
        RuntimePlanProvider planProvider,
        HostManagerSamplingSubscriptionRuntime deployment,
        ILogger<HostManagerSamplingSubscriptionOwner> logger)
    {
        this.planProvider = planProvider;
        this.deployment = deployment;
        this.logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (lifecycleGate)
        {
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
            }
            catch
            {
                planProvider.Published -= OnPublished;
                throw;
            }
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        lock (lifecycleGate)
        {
            if (started)
            {
                planProvider.Published -= OnPublished;
                started = false;
                RetireCurrentWorkspace();
            }
        }
        return Task.CompletedTask;
    }

    internal T Execute<T>(int roleId, Func<NativeSamplingSubscriptionSession, T> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        using var lease = Acquire(roleId);
        return operation(lease.Session);
    }

    internal SessionLease Acquire(int roleId)
    {
        lock (lifecycleGate)
        {
            ThrowIfDisposed();
            if (!started)
            {
                throw new InvalidOperationException(
                    "The Host Manager sampling-subscription owner is not running.");
            }
            return AcquireCurrent(roleId);
        }
    }

    internal SessionLease? TryAcquireForCleanup(int roleId)
    {
        lock (lifecycleGate)
        {
            return !started || disposed ? null : AcquireCurrent(roleId);
        }
    }

    private SessionLease AcquireCurrent(int roleId)
    {
        gate.EnterReadLock();
        try
        {
            var holder = current
                ?? throw new InvalidOperationException(
                    "The Host Manager sampling-subscription workspace is not ready.");
            return holder.Acquire(roleId);
        }
        finally
        {
            gate.ExitReadLock();
        }
    }

    public void Dispose()
    {
        lock (lifecycleGate)
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
            disposed = true;
            RetireCurrentWorkspace();
        }
        gate.Dispose();
    }

    private void OnPublished(CompiledRuntimePlan plan)
    {
        lock (lifecycleGate)
        {
            if (!started || disposed)
            {
                return;
            }
            Apply(plan.HostManager.RequirePublished(), throwOnFailure: false);
        }
    }

    private void Apply(CompiledHostManagerPlan hostPlan, bool throwOnFailure)
    {
        var desired = hostPlan.SamplingSubscription;
        gate.EnterWriteLock();
        try
        {
            var hasCurrent = current is not null;
            var firstCreation = nextWorkspaceIncarnation == 0;
            var canApplyHot = hasCurrent && deployment.CanApplyHot(hostPlan);
            if (hasCurrent
                && canApplyHot
                && appliedPlan!.ConfigurationSha256 == desired.ConfigurationSha256)
            {
                return;
            }

            var recreate = !firstCreation && (!hasCurrent || !canApplyHot);
            var workspaceIncarnation = NextWorkspaceIncarnation();
            var token = firstCreation
                ? deployment.BeginInitialCreate(hostPlan)
                : recreate
                    ? deployment.BeginHostRecreateAndHotPublish(hostPlan)
                    : deployment.BeginHotPublish(hostPlan);
            NativeSamplingSubscriptionWorkspace? replacement = null;
            try
            {
                replacement = new NativeSamplingSubscriptionWorkspace(desired);
                deployment.CompleteSucceeded(token);
                var next = new WorkspaceHolder(
                    replacement,
                    desired.HotPublish.Roles,
                    workspaceIncarnation,
                    desired.ConfigurationGeneration);
                var previous = current;
                previous?.Retire();
                current = next;
                appliedPlan = desired;
                replacement = null;
            }
            catch (Exception ex)
            {
                replacement?.Dispose();
                try
                {
                    deployment.CompleteFailed(token, "sampling-subscription-apply-failed");
                }
                catch (Exception settlement)
                {
                    throw new AggregateException(ex, settlement);
                }
                throw;
            }
        }
        catch (Exception ex) when (!throwOnFailure)
        {
            logger.LogError(
                ex,
                "Host Manager sampling-subscription plan apply failed; the prior workspace remains active.");
        }
        finally
        {
            gate.ExitWriteLock();
        }
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(disposed, this);

    private void RetireCurrentWorkspace()
    {
        gate.EnterWriteLock();
        try
        {
            current?.Retire();
            current = null;
            appliedPlan = null;
        }
        finally
        {
            gate.ExitWriteLock();
        }
    }

    private ulong NextWorkspaceIncarnation()
    {
        if (nextWorkspaceIncarnation == ulong.MaxValue)
        {
            throw new InvalidOperationException(
                "Sampling subscription workspace incarnation identity is exhausted.");
        }
        return ++nextWorkspaceIncarnation;
    }

    internal sealed class SessionLease : IDisposable
    {
        private WorkspaceHolder? holder;

        internal SessionLease(
            WorkspaceHolder holder,
            NativeSamplingSubscriptionSession session,
            CompiledHostManagerSamplingSubscriptionRoleHotPublishPlan hotPublish)
        {
            this.holder = holder;
            Session = session;
            HotPublish = hotPublish;
            WorkspaceIncarnation = holder.WorkspaceIncarnation;
            OwnerToken = holder.OwnerToken;
        }

        public NativeSamplingSubscriptionSession Session { get; }

        public CompiledHostManagerSamplingSubscriptionRoleHotPublishPlan HotPublish { get; }

        public ulong WorkspaceIncarnation { get; }

        internal SamplingOwnerToken OwnerToken { get; }

        public void Dispose()
            => Interlocked.Exchange(ref holder, null)?.Release();
    }

    internal sealed class WorkspaceHolder
    {
        private readonly NativeSamplingSubscriptionWorkspace workspace;
        private readonly IReadOnlyList<CompiledHostManagerSamplingSubscriptionRoleHotPublishPlan> roles;
        private int referenceCount = 1;
        private int retired;

        public WorkspaceHolder(
            NativeSamplingSubscriptionWorkspace workspace,
            IReadOnlyList<CompiledHostManagerSamplingSubscriptionRoleHotPublishPlan> roles,
            ulong workspaceIncarnation,
            ulong configurationGeneration)
        {
            this.workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
            this.roles = roles ?? throw new ArgumentNullException(nameof(roles));
            if (workspaceIncarnation == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(workspaceIncarnation));
            }
            WorkspaceIncarnation = workspaceIncarnation;
            OwnerToken = new SamplingOwnerToken(
                workspaceIncarnation,
                configurationGeneration);
            if (roles.Count != CompiledHostManagerSamplingSubscriptionPlan.RoleCount
                || roles.Where((role, index) => role.RoleId != index + 1).Any())
            {
                throw new InvalidOperationException(
                    "Sampling subscription workspace role policy is invalid.");
            }
        }

        public ulong WorkspaceIncarnation { get; }

        internal SamplingOwnerToken OwnerToken { get; }

        public SessionLease Acquire(int roleId)
        {
            if (Volatile.Read(ref retired) != 0)
            {
                throw new ObjectDisposedException(nameof(NativeSamplingSubscriptionWorkspace));
            }
            Interlocked.Increment(ref referenceCount);
            return new SessionLease(
                this,
                workspace.GetSession(roleId),
                roles[roleId - 1]);
        }

        public void Retire()
        {
            OwnerToken.Revoke();
            if (Interlocked.Exchange(ref retired, 1) == 0)
            {
                Release();
            }
        }

        public void Release()
        {
            if (Interlocked.Decrement(ref referenceCount) == 0)
            {
                workspace.Dispose();
            }
        }
    }
}
