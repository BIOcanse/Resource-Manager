using ResourceManager.Subscription;
using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Adaptation;

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
    public ResourceManager.Subscription.SubscriptionActivity Activity { get; } = new();

    internal bool HasPublishedSettings => planProvider.Current.Version > 0;
    internal event Action? SettingsPublished;

    internal ISubscriptionRoleSettings? GetRoleSettings(int roleId)
    {
        var plan = planProvider.Current;
        return plan.Version == 0 ? null : plan.HostManager.SamplingSubscription.HotPublish.Roles[roleId - 1];
    }
    private readonly object lifecycleGate = new();
    private readonly ReaderWriterLockSlim gate = new(LockRecursionPolicy.NoRecursion);
    private readonly RuntimePlanProvider planProvider;
    private readonly HostManagerSamplingSubscriptionRuntime deployment;
    private readonly ILogger<HostManagerSamplingSubscriptionOwner> logger;
    private readonly Func<ulong, IComputeScratch>? createScratch;
    private WorkspaceHolder? current;
    private WorkspaceHolder? pending;
    private CompiledHostManagerSamplingSubscriptionPlan? appliedPlan;
    private ulong nextWorkspaceIncarnation;
    private bool started;
    private bool disposed;

    internal void ReportWorkspaceResize(string message)
        => logger.LogInformation("{NativeWorkspaceResize}", message);

    public HostManagerSamplingSubscriptionOwner(
        RuntimePlanProvider planProvider,
        HostManagerSamplingSubscriptionRuntime deployment,
        ILogger<HostManagerSamplingSubscriptionOwner> logger,
        ResourceManagerLedgerOwner? ledgerOwner = null)
    {
        this.planProvider = planProvider;
        this.deployment = deployment;
        this.logger = logger;
        createScratch = ledgerOwner is null ? null : ledgerOwner.CreateScratch;
        planProvider.Published += OnSettingsPublished;
    }

    private void OnSettingsPublished(CompiledRuntimePlan _) => SettingsPublished?.Invoke();

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

            if (current is not null || pending is not null)
            {
                RetireCurrentWorkspace();
                if (current is not null || pending is not null)
                    throw new InvalidOperationException("Sampling subscription readers must finish before restart.");
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
            if (disposed)
                return Task.CompletedTask;
            if (started)
            {
                planProvider.Published -= OnPublished;
                started = false;
            }
            RetireCurrentWorkspace();
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
            RetireCurrentWorkspace();
            if (current is not null || pending is not null)
                throw new InvalidOperationException("Sampling subscription workspace is still in use; dispose after its readers finish.");
            planProvider.Published -= OnSettingsPublished;
            disposed = true;
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
        var committed = false;
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
                _ = TryClosePending();
                return;
            }

            var recreate = !firstCreation && (!hasCurrent || !canApplyHot);
            var token = firstCreation
                ? deployment.BeginInitialCreate(hostPlan)
                : recreate
                    ? deployment.BeginHostRecreateAndHotPublish(hostPlan)
                    : deployment.BeginHotPublish(hostPlan);
            WorkspaceHolder? candidate = null;
            try
            {
                if (!TryClosePending())
                    throw new InvalidOperationException("The retired sampling subscription workspace still has readers; replacement is not admitted.");
                var workspaceIncarnation = NextWorkspaceIncarnation();
                var replacement = new NativeSamplingSubscriptionWorkspace();
                candidate = new WorkspaceHolder(
                    replacement,
                    desired.HotPublish.Roles,
                    workspaceIncarnation,
                    desired.ConfigurationGeneration);
                pending = candidate;
                replacement.Initialize(desired, createScratch);
                deployment.CompleteSucceeded(token);
                committed = true;
                var previous = current;
                current = candidate;
                appliedPlan = desired;
                pending = previous;
                _ = TryClosePending();
            }
            catch (Exception ex)
            {
                // Successful deployment settlement is never reversed by retirement failure.
                if (committed)
                    throw;
                var failures = new List<Exception> { ex };
                if (candidate is not null)
                {
                    try { _ = TryClosePending(); }
                    catch (Exception cleanup) { failures.Add(cleanup); }
                }
                try
                {
                    deployment.CompleteFailed(token, "sampling-subscription-apply-failed");
                }
                catch (Exception settlement)
                {
                    failures.Add(settlement);
                }
                if (failures.Count > 1)
                    throw new AggregateException(failures);
                throw;
            }
        }
        catch (Exception ex) when (!throwOnFailure)
        {
            logger.LogError(
                ex,
                committed
                    ? "Host Manager sampling-subscription workspace was published; retired workspace close remains pending."
                    : "Host Manager sampling-subscription plan apply failed; the prior workspace remains active.");
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
            Exception? failure = null;
            try { _ = TryClosePending(); }
            catch (Exception ex) { failure = ex; }
            try
            {
                if (current is not null && current.RetireAndTryClose())
                {
                    current = null;
                    appliedPlan = null;
                }
            }
            catch (Exception ex)
            {
                failure = failure is null ? ex : new AggregateException(failure, ex);
            }
            if (failure is not null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
        finally
        {
            gate.ExitWriteLock();
        }
    }

    private bool TryClosePending()
    {
        if (pending is null)
            return true;
        if (!pending.RetireAndTryClose())
            return false;
        pending = null;
        return true;
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

    internal sealed class SessionLease : ISamplingSubscriptionSessionLease<SamplingOwnerToken>
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

        ISubscriptionRoleSettings ISamplingSubscriptionSessionLease<SamplingOwnerToken>.HotPublish => HotPublish;
        SamplingOwnerToken ISamplingSubscriptionSessionLease<SamplingOwnerToken>.OwnerToken => OwnerToken;

        public void Dispose()
            => Interlocked.Exchange(ref holder, null)?.Release();
    }

    internal sealed class WorkspaceHolder
    {
        private readonly object sync = new();
        private readonly NativeSamplingSubscriptionWorkspace workspace;
        private readonly IReadOnlyList<CompiledHostManagerSamplingSubscriptionRoleHotPublishPlan> roles;
        private int referenceCount = 1;
        private bool retired;
        private bool closed;

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
            lock (sync)
            {
                ObjectDisposedException.ThrowIf(retired, this);
                var session = workspace.GetSession(roleId);
                var lease = new SessionLease(this, session, roles[roleId - 1]);
                referenceCount++;
                return lease;
            }
        }

        public bool RetireAndTryClose()
        {
            OwnerToken.Revoke();
            lock (sync)
            {
                if (!retired)
                {
                    retired = true;
                    referenceCount--;
                }
                return TryClose();
            }
        }

        public void Release()
        {
            lock (sync)
            {
                if (referenceCount <= 0)
                    throw new InvalidOperationException("Sampling subscription reference was already returned.");
                referenceCount--;
                _ = TryClose();
            }
        }

        private bool TryClose()
        {
            if (closed)
                return true;
            if (!retired || referenceCount != 0)
                return false;
            workspace.Dispose();
            closed = true;
            return true;
        }
    }
}
