using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.Adapter.NativeLedger;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization;

public sealed class HostManagerAdapterPrivateResourceRuntime(
    IRuntimePlanProvider runtimePlanProvider,
    HostManagerDeploymentState deploymentState)
{
    private readonly object deploymentGate = new();
    private AdapterPrivateResourceRuntimePlan? lastAppliedPlan;

    public AdapterPrivateResourceRuntimePlan CaptureDesired()
    {
        var plan = runtimePlanProvider.Current.HostManager.RequirePublished();
        return new AdapterPrivateResourceRuntimePlan(
            plan,
            plan.BuildSpecialize.AdapterPrivateResourceLedgerAbiVersion,
            plan.HostRecreate.AdapterPrivateResourceLedger,
            plan.HotPublish.AdapterPrivateResourceLedger);
    }

    public bool CanApplyHot(CompiledHostManagerPlan plan)
        => deploymentState.CanApplyHot(HostManagerModuleKind.AdapterPrivateResourceLedger, plan);

    internal bool ShouldRecreateSession(
        AdapterPrivateResourceRuntimePlan current,
        AdapterPrivateResourceRuntimePlan desired)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(desired);
        if (string.Equals(
            current.HostPlan.DeploymentDigests.AdapterPrivateResourceLedger.RecreateSha256,
            desired.HostPlan.DeploymentDigests.AdapterPrivateResourceLedger.RecreateSha256,
            StringComparison.Ordinal))
        {
            return false;
        }

        lock (deploymentGate)
        {
            return IsExactApplied(
                deploymentState.Snapshot.AdapterPrivateResourceLedger,
                desired.HostPlan);
        }
    }

    internal NativeAdapterResourceLedgerSession CreateSession(
        AdapterPrivateResourceRuntimePlan desired,
        out AdapterPrivateResourceRuntimePlan applied)
    {
        ArgumentNullException.ThrowIfNull(desired);
        lock (deploymentGate)
        {
            var module = deploymentState.Snapshot.AdapterPrivateResourceLedger;
            if (IsExactApplied(module, desired.HostPlan))
            {
                lastAppliedPlan = desired;
                applied = desired;
                return new NativeAdapterResourceLedgerSession(desired.CreateNativeConfiguration());
            }

            if (module.AppliedPlanEpoch == 0)
            {
                return CreateAndSettle(
                    desired,
                    HostManagerDeploymentOperation.InitialCreate,
                    "adapter-private-resource-initial-create-failed",
                    out applied);
            }

            if (CanApplyHot(desired.HostPlan))
            {
                return CreateAndSettle(
                    desired,
                    HostManagerDeploymentOperation.HotPublish,
                    "adapter-private-resource-hot-publish-failed",
                    out applied);
            }

            var retained = lastAppliedPlan
                ?? throw new InvalidOperationException(
                    "The private-resource ledger requires Host recreation and has no retained applied plan.");
            applied = retained;
            return new NativeAdapterResourceLedgerSession(retained.CreateNativeConfiguration());
        }
    }

    internal AdapterPrivateResourceRuntimePlan ApplyConfiguration(
        NativeAdapterResourceLedgerSession session,
        AdapterPrivateResourceRuntimePlan current,
        AdapterPrivateResourceRuntimePlan desired)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(desired);
        if (string.Equals(
            current.HostPlan.DeploymentDigests.AdapterPrivateResourceLedger.HotPublishSha256,
            desired.HostPlan.DeploymentDigests.AdapterPrivateResourceLedger.HotPublishSha256,
            StringComparison.Ordinal))
        {
            return current;
        }

        lock (deploymentGate)
        {
            if (!string.Equals(
                    current.HostPlan.DeploymentDigests.AdapterPrivateResourceLedger.RecreateSha256,
                    desired.HostPlan.DeploymentDigests.AdapterPrivateResourceLedger.RecreateSha256,
                    StringComparison.Ordinal))
            {
                return current;
            }

            var module = deploymentState.Snapshot.AdapterPrivateResourceLedger;
            if (IsExactApplied(module, desired.HostPlan))
            {
                session.ApplyConfiguration(desired.CreateNativeConfiguration());
                lastAppliedPlan = desired;
                return desired;
            }

            var attempt = deploymentState.BeginAttempt(
                HostManagerModuleKind.AdapterPrivateResourceLedger,
                desired.HostPlan,
                HostManagerDeploymentOperation.HotPublish);
            try
            {
                session.ApplyConfiguration(desired.CreateNativeConfiguration());
                HostManagerDeploymentAttemptSettlementGuard.RequireApplied(
                    deploymentState.CompleteAttemptSucceeded(attempt),
                    HostManagerModuleKind.AdapterPrivateResourceLedger);
                lastAppliedPlan = desired;
                return desired;
            }
            catch (Exception exception)
            {
                Exception? settlementException = null;
                try
                {
                    HostManagerDeploymentAttemptSettlementGuard.RequireFailed(
                        deploymentState.CompleteAttemptFailed(
                            attempt,
                            "adapter-private-resource-hot-publish-failed",
                            ToNativeResult(exception)),
                        HostManagerModuleKind.AdapterPrivateResourceLedger);
                }
                catch (Exception settlement)
                {
                    settlementException = settlement;
                }
                if (settlementException is not null)
                {
                    throw new AggregateException(
                        "The adapter private-resource hot publish and deployment settlement both failed.",
                        exception,
                        settlementException);
                }
                throw;
            }
        }
    }

    private NativeAdapterResourceLedgerSession CreateAndSettle(
        AdapterPrivateResourceRuntimePlan desired,
        HostManagerDeploymentOperation operation,
        string failureCode,
        out AdapterPrivateResourceRuntimePlan applied)
    {
        var attempt = deploymentState.BeginAttempt(
            HostManagerModuleKind.AdapterPrivateResourceLedger,
            desired.HostPlan,
            operation);
        NativeAdapterResourceLedgerSession? created = null;
        try
        {
            created = new NativeAdapterResourceLedgerSession(desired.CreateNativeConfiguration());
            HostManagerDeploymentAttemptSettlementGuard.RequireApplied(
                deploymentState.CompleteAttemptSucceeded(attempt),
                HostManagerModuleKind.AdapterPrivateResourceLedger);
            lastAppliedPlan = desired;
            applied = desired;
            var result = created;
            created = null;
            return result;
        }
        catch (Exception exception)
        {
            Exception? cleanupException = null;
            Exception? settlementException = null;
            try
            {
                created?.Dispose();
            }
            catch (Exception cleanup)
            {
                cleanupException = cleanup;
            }
            try
            {
                HostManagerDeploymentAttemptSettlementGuard.RequireFailed(
                    deploymentState.CompleteAttemptFailed(
                        attempt,
                        failureCode,
                        ToNativeResult(exception)),
                    HostManagerModuleKind.AdapterPrivateResourceLedger);
            }
            catch (Exception settlement)
            {
                settlementException = settlement;
            }
            if (cleanupException is not null || settlementException is not null)
            {
                var failures = new List<Exception> { exception };
                if (cleanupException is not null) failures.Add(cleanupException);
                if (settlementException is not null) failures.Add(settlementException);
                throw new AggregateException(
                    "The adapter private-resource session failed to initialize and cleanup or deployment settlement also failed.",
                    failures);
            }
            throw;
        }
    }

    private static bool IsExactApplied(
        HostManagerModuleDeploymentSnapshot module,
        CompiledHostManagerPlan plan)
        => module.AppliedPlanEpoch == plan.PlanEpoch
            && module.AppliedBuildSha256 == plan.BuildSha256
            && module.AppliedRecreateSha256
                == plan.DeploymentDigests.AdapterPrivateResourceLedger.RecreateSha256
            && module.AppliedHotPublishSha256
                == plan.DeploymentDigests.AdapterPrivateResourceLedger.HotPublishSha256;

    private static HostManagerNativeResultSnapshot? ToNativeResult(Exception exception)
        => exception is NativeAdapterResourceLedgerException native
            ? new HostManagerNativeResultSnapshot("adapter-private-resource", native.ResultCode)
            : null;
}

public sealed record AdapterPrivateResourceRuntimePlan(
    CompiledHostManagerPlan HostPlan,
    uint AbiVersion,
    CompiledHostManagerAdapterPrivateResourceRecreatePlan Recreate,
    CompiledHostManagerAdapterPrivateResourceHotPublishPlan HotPublish)
{
    public NativeAdapterResourceLedgerConfiguration CreateNativeConfiguration()
        => new(
            HostPlan.PlanEpoch,
            Recreate.StateCapacity,
            TimeSpan.FromMilliseconds(HotPublish.MaximumSnapshotAgeMilliseconds),
            TimeSpan.FromMilliseconds(HotPublish.MaximumFutureClockSkewMilliseconds),
            TimeSpan.FromMilliseconds(HotPublish.SettlementIntervalMilliseconds),
            HotPublish.ActiveIncrement,
            HotPublish.DecayNumerator,
            HotPublish.DecayDenominator);
}

public sealed class HostManagerProcessPolicyExecutorRuntime(
    IRuntimePlanProvider runtimePlanProvider,
    HostManagerDeploymentState deploymentState)
{
    public ProcessPolicyExecutorRuntimePlan CaptureDesired()
    {
        var plan = runtimePlanProvider.Current.HostManager.RequirePublished();
        return new ProcessPolicyExecutorRuntimePlan(
            plan,
            plan.BuildSpecialize.ProcessPolicyExecutorAbiVersion,
            plan.HotPublish.ProcessPolicyExecutor);
    }

    internal HostManagerDeploymentAttemptToken BeginInitialCreate(CompiledHostManagerPlan plan)
        => deploymentState.BeginAttempt(
            HostManagerModuleKind.ProcessPolicyExecutor,
            plan,
            HostManagerDeploymentOperation.InitialCreate);

    internal HostManagerDeploymentAttemptToken BeginHotPublish(CompiledHostManagerPlan plan)
        => deploymentState.BeginAttempt(
            HostManagerModuleKind.ProcessPolicyExecutor,
            plan,
            HostManagerDeploymentOperation.HotPublish);

    public bool CanApplyHot(CompiledHostManagerPlan plan)
        => deploymentState.CanApplyHot(HostManagerModuleKind.ProcessPolicyExecutor, plan);

    internal HostManagerDeploymentAttemptSettlement CompleteSucceeded(
        HostManagerDeploymentAttemptToken token)
        => HostManagerDeploymentAttemptSettlementGuard.RequireApplied(
            deploymentState.CompleteAttemptSucceeded(token),
            HostManagerModuleKind.ProcessPolicyExecutor);

    internal HostManagerDeploymentAttemptSettlement CompleteFailed(
        HostManagerDeploymentAttemptToken token,
        string failureCode)
        => HostManagerDeploymentAttemptSettlementGuard.RequireFailed(
            deploymentState.CompleteAttemptFailed(token, failureCode),
            HostManagerModuleKind.ProcessPolicyExecutor);
}

public sealed record ProcessPolicyExecutorRuntimePlan(
    CompiledHostManagerPlan HostPlan,
    uint AbiVersion,
    CompiledHostManagerProcessPolicyExecutorHotPublishPlan HotPublish);

public sealed class HostManagerPdhCollectorRuntime(
    IRuntimePlanProvider runtimePlanProvider,
    HostManagerDeploymentState deploymentState)
{
    public PdhCollectorRuntimePlan CaptureDesired()
    {
        var runtimePlan = runtimePlanProvider.Current;
        var plan = runtimePlan.HostManager.RequirePublished();
        return new PdhCollectorRuntimePlan(
            runtimePlan.Version,
            plan,
            plan.BuildSpecialize.PdhCollectorAbiVersion,
            plan.HostRecreate.PdhCollector,
            plan.HotPublish.PdhCollector);
    }

    internal HostManagerDeploymentAttemptToken BeginInitialCreate(CompiledHostManagerPlan plan)
        => deploymentState.BeginAttempt(
            HostManagerModuleKind.PdhCollector,
            plan,
            HostManagerDeploymentOperation.InitialCreate);

    internal HostManagerDeploymentAttemptToken BeginHotPublish(CompiledHostManagerPlan plan)
        => deploymentState.BeginAttempt(
            HostManagerModuleKind.PdhCollector,
            plan,
            HostManagerDeploymentOperation.HotPublish);

    internal HostManagerDeploymentAttemptToken BeginHostRecreateAndHotPublish(
        CompiledHostManagerPlan plan)
        => deploymentState.BeginAttempt(
            HostManagerModuleKind.PdhCollector,
            plan,
            HostManagerDeploymentOperation.HostRecreateAndHotPublish);

    internal HostManagerDeploymentAttemptSettlement CompleteSucceeded(
        HostManagerDeploymentAttemptToken token)
        => HostManagerDeploymentAttemptSettlementGuard.RequireApplied(
            deploymentState.CompleteAttemptSucceeded(token),
            HostManagerModuleKind.PdhCollector);

    internal HostManagerDeploymentAttemptSettlement CompleteFailed(
        HostManagerDeploymentAttemptToken token,
        string failureCode)
        => HostManagerDeploymentAttemptSettlementGuard.RequireFailed(
            deploymentState.CompleteAttemptFailed(token, failureCode),
            HostManagerModuleKind.PdhCollector);
}

public sealed record PdhCollectorRuntimePlan(
    long RuntimePlanVersion,
    CompiledHostManagerPlan HostPlan,
    uint AbiVersion,
    CompiledHostManagerPdhCollectorRecreatePlan Recreate,
    CompiledHostManagerPdhCollectorHotPublishPlan HotPublish);
