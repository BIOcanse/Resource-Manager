using System.Text;
using System.Text.Json.Nodes;
using ResourceManager.App.Application.Settings;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace Resource_Manager_APP.Tests;

public sealed class HostManagerDeploymentStateTests
{
    [Fact]
    public void DeploymentState_TracksExactInitialAndHotAttempts()
    {
        var state = new HostManagerDeploymentState();
        var first = Compile(ReadDefaultProfile(), 1, AppSettingsDefaults.Create().Performance);
        Publish(state, first, 1);

        Assert.Equal(1UL, state.Snapshot.PublicationSequence);
        Assert.Equal(1L, state.Snapshot.RuntimePlanVersion);
        Assert.All(Modules(state.Snapshot), static module =>
        {
            Assert.Equal(HostManagerDeploymentStatus.Initializing, module.Status);
            Assert.Equal(HostManagerPendingLifecycle.InitialCreate, module.PendingLifecycle);
            Assert.Equal(HostManagerDeploymentHealth.Unknown, module.Health);
            Assert.Null(module.AppliedAtUtc);
        });

        foreach (var module in Enum.GetValues<HostManagerModuleKind>())
        {
            var attempt = state.BeginAttempt(
                module,
                first,
                HostManagerDeploymentOperation.InitialCreate);
            Assert.Equal(
                HostManagerDeploymentAttemptSettlement.Applied,
                state.CompleteAttemptSucceeded(attempt));
        }

        Assert.All(Modules(state.Snapshot), static module =>
        {
            Assert.Equal(HostManagerDeploymentStatus.InSync, module.Status);
            Assert.Equal(HostManagerPendingLifecycle.None, module.PendingLifecycle);
            Assert.Equal(HostManagerDeploymentHealth.Healthy, module.Health);
            Assert.Equal(1UL, module.AppliedPublicationSequence);
            Assert.NotNull(module.AppliedAtUtc);
            Assert.Equal(0UL, module.PendingSincePlanEpoch);
        });

        var second = Compile(
            ReadDefaultProfile(),
            2,
            AppSettingsDefaults.Create().Performance with
            {
                PhysicalMemoryOptimizationTargetUsagePercent = 75
            });
        Publish(state, second, 2);

        Assert.Equal(HostManagerDeploymentStatus.Pending, state.Snapshot.ResourceScheduler.Status);
        Assert.Equal(HostManagerPendingLifecycle.HotPublish, state.Snapshot.ResourceScheduler.PendingLifecycle);
        Assert.True(state.CanApplyHot(HostManagerModuleKind.ResourceScheduler, second));

        var hotAttempt = state.BeginAttempt(
            HostManagerModuleKind.ResourceScheduler,
            second,
            HostManagerDeploymentOperation.HotPublish);
        Assert.Equal(
            HostManagerDeploymentAttemptSettlement.Applied,
            state.CompleteAttemptSucceeded(hotAttempt));
        Assert.Equal(HostManagerDeploymentStatus.InSync, state.Snapshot.ResourceScheduler.Status);
    }

    [Fact]
    public void DeploymentState_RequiresExplicitRecreateThenHotPublish()
    {
        var state = new HostManagerDeploymentState();
        var first = Compile(ReadDefaultProfile(), 1, AppSettingsDefaults.Create().Performance);
        Publish(state, first, 1);
        ApplyInitial(state, HostManagerModuleKind.ResourceScheduler, first);

        var changedJson = JsonNode.Parse(ReadDefaultProfile())!.AsObject();
        changedJson["host_recreate"]!["resource_scheduler"]!["pending_capacity"] = 4096;
        var changed = Compile(
            Encoding.UTF8.GetBytes(changedJson.ToJsonString()),
            2,
            AppSettingsDefaults.Create().Performance);
        Publish(state, changed, 2);

        Assert.False(state.CanApplyHot(HostManagerModuleKind.ResourceScheduler, changed));
        Assert.Throws<InvalidOperationException>(() => state.BeginAttempt(
            HostManagerModuleKind.ResourceScheduler,
            changed,
            HostManagerDeploymentOperation.HotPublish));

        var recreate = state.BeginAttempt(
            HostManagerModuleKind.ResourceScheduler,
            changed,
            HostManagerDeploymentOperation.HostRecreate);
        state.CompleteAttemptSucceeded(recreate);
        Assert.Equal(HostManagerPendingLifecycle.HotPublish, state.Snapshot.ResourceScheduler.PendingLifecycle);
        Assert.Null(state.Snapshot.ResourceScheduler.AppliedAtUtc);

        var hot = state.BeginAttempt(
            HostManagerModuleKind.ResourceScheduler,
            changed,
            HostManagerDeploymentOperation.HotPublish);
        state.CompleteAttemptSucceeded(hot);
        Assert.Equal(HostManagerDeploymentStatus.InSync, state.Snapshot.ResourceScheduler.Status);
    }

    [Fact]
    public void DeploymentState_AllowsOnlyOneActiveAttemptAndRejectsDuplicateSettlement()
    {
        var state = new HostManagerDeploymentState();
        var plan = Compile(ReadDefaultProfile(), 1, AppSettingsDefaults.Create().Performance);
        Publish(state, plan, 1);

        var attempt = state.BeginAttempt(
            HostManagerModuleKind.SmartCoordinator,
            plan,
            HostManagerDeploymentOperation.InitialCreate);
        Assert.Throws<InvalidOperationException>(() => state.BeginAttempt(
            HostManagerModuleKind.SmartCoordinator,
            plan,
            HostManagerDeploymentOperation.InitialCreate));
        Assert.Equal(
            HostManagerDeploymentAttemptSettlement.Stale,
            state.CompleteAttemptSucceeded(attempt with { AttemptId = attempt.AttemptId + 1 }));
        Assert.Equal(attempt.AttemptId, state.Snapshot.SmartCoordinator.ActiveAttempt?.AttemptId);

        Assert.Equal(
            HostManagerDeploymentAttemptSettlement.Applied,
            state.CompleteAttemptSucceeded(attempt));
        var applied = state.Snapshot.SmartCoordinator;
        Assert.Equal(
            HostManagerDeploymentAttemptSettlement.Stale,
            state.CompleteAttemptSucceeded(attempt));

        var afterDuplicate = state.Snapshot.SmartCoordinator;
        Assert.Equal(applied.AppliedPlanEpoch, afterDuplicate.AppliedPlanEpoch);
        Assert.Equal(applied.AppliedHotPublishSha256, afterDuplicate.AppliedHotPublishSha256);
        Assert.Equal(2U, afterDuplicate.StaleCompletionCount);
        Assert.Null(afterDuplicate.ActiveAttempt);
        Assert.Equal(
            HostManagerDeploymentAttemptSettlement.Stale,
            state.CompleteAttemptSucceeded(new HostManagerDeploymentAttemptToken(
                ulong.MaxValue,
                ulong.MaxValue,
                (HostManagerModuleKind)byte.MaxValue)));
    }

    [Fact]
    public void DeploymentState_OldSuccessIsStaleAndLeavesNewDesiredUnapplied()
    {
        var state = new HostManagerDeploymentState();
        var first = Compile(ReadDefaultProfile(), 1, AppSettingsDefaults.Create().Performance);
        Publish(state, first, 1);
        var firstAttempt = state.BeginAttempt(
            HostManagerModuleKind.ResourceScheduler,
            first,
            HostManagerDeploymentOperation.InitialCreate);

        var second = Compile(
            ReadDefaultProfile(),
            2,
            AppSettingsDefaults.Create().Performance with
            {
                PhysicalMemoryOptimizationTargetUsagePercent = 75
            });
        Publish(state, second, 2);

        Assert.Equal(
            HostManagerDeploymentAttemptSettlement.Stale,
            state.CompleteAttemptSucceeded(firstAttempt));
        var snapshot = state.Snapshot.ResourceScheduler;
        Assert.Equal(0UL, snapshot.AppliedPublicationSequence);
        Assert.Equal(2UL, snapshot.DesiredPublicationSequence);
        Assert.Equal(0UL, snapshot.AppliedPlanEpoch);
        Assert.Equal(second.PlanEpoch, snapshot.DesiredPlanEpoch);
        Assert.Equal(HostManagerDeploymentStatus.Initializing, snapshot.Status);
        Assert.Equal(HostManagerPendingLifecycle.InitialCreate, snapshot.PendingLifecycle);
        Assert.Equal(HostManagerDeploymentHealth.Unknown, snapshot.Health);
        Assert.Null(snapshot.ActiveAttempt);
        Assert.Equal(1U, snapshot.StaleCompletionCount);
    }

    [Fact]
    public void DeploymentState_OldFailureIsImmediatelySupersededByNewDesired()
    {
        var state = new HostManagerDeploymentState();
        var first = Compile(ReadDefaultProfile(), 1, AppSettingsDefaults.Create().Performance);
        Publish(state, first, 1);
        var firstAttempt = state.BeginAttempt(
            HostManagerModuleKind.ResourceScheduler,
            first,
            HostManagerDeploymentOperation.InitialCreate);

        var second = Compile(
            ReadDefaultProfile(),
            2,
            AppSettingsDefaults.Create().Performance with
            {
                PhysicalMemoryOptimizationTargetUsagePercent = 75
            });
        Publish(state, second, 2);
        state.CompleteAttemptFailed(firstAttempt, "resource-scheduler-create", null);

        var snapshot = state.Snapshot.ResourceScheduler;
        var failure = Assert.IsType<HostManagerModuleFailureSnapshot>(snapshot.LastFailure);
        Assert.False(failure.Active);
        Assert.Equal(HostManagerFailureResolution.Superseded, failure.Resolution);
        Assert.NotNull(failure.ResolvedAtUtc);
        Assert.NotEqual(HostManagerDeploymentHealth.Failed, snapshot.Health);
        Assert.Equal(second.PlanEpoch, snapshot.DesiredPlanEpoch);
    }

    [Fact]
    public void DeploymentState_ExactRetryAccumulatesAndRecoversFailure()
    {
        var state = new HostManagerDeploymentState();
        var first = Compile(ReadDefaultProfile(), 1, AppSettingsDefaults.Create().Performance);
        Publish(state, first, 1);
        ApplyInitial(state, HostManagerModuleKind.ResourceScheduler, first);

        var second = Compile(
            ReadDefaultProfile(),
            2,
            AppSettingsDefaults.Create().Performance with
            {
                PhysicalMemoryOptimizationTargetUsagePercent = 75
            });
        Publish(state, second, 2);
        var nativeResult = new HostManagerNativeResultSnapshot("resource-scheduler", 17);

        for (var index = 0; index < 2; index++)
        {
            var failedAttempt = state.BeginAttempt(
                HostManagerModuleKind.ResourceScheduler,
                second,
                HostManagerDeploymentOperation.HotPublish);
            state.CompleteAttemptFailed(
                failedAttempt,
                "resource-scheduler-reconfigure",
                nativeResult);
        }

        var failed = state.Snapshot.ResourceScheduler;
        Assert.Equal(HostManagerDeploymentHealth.Failed, failed.Health);
        Assert.Equal(2U, failed.LastFailure?.OccurrenceCount);
        Assert.True(failed.LastFailure?.Active);

        var retry = state.BeginAttempt(
            HostManagerModuleKind.ResourceScheduler,
            second,
            HostManagerDeploymentOperation.HotPublish);
        state.CompleteAttemptSucceeded(retry);

        var recovered = state.Snapshot.ResourceScheduler;
        Assert.Equal(HostManagerDeploymentStatus.InSync, recovered.Status);
        Assert.Equal(HostManagerDeploymentHealth.Healthy, recovered.Health);
        Assert.False(recovered.LastFailure?.Active);
        Assert.Equal(HostManagerFailureResolution.Recovered, recovered.LastFailure?.Resolution);
        Assert.NotNull(recovered.LastFailure?.ResolvedAtUtc);
    }

    [Fact]
    public void DeploymentState_StaleOldSuccessCannotRecoverNewFailure()
    {
        var state = new HostManagerDeploymentState();
        var first = Compile(ReadDefaultProfile(), 1, AppSettingsDefaults.Create().Performance);
        Publish(state, first, 1);
        var oldAttempt = state.BeginAttempt(
            HostManagerModuleKind.ResourceScheduler,
            first,
            HostManagerDeploymentOperation.InitialCreate);

        var second = Compile(
            ReadDefaultProfile(),
            2,
            AppSettingsDefaults.Create().Performance with
            {
                PhysicalMemoryOptimizationTargetUsagePercent = 75
            });
        Publish(state, second, 2);
        state.CompleteAttemptSucceeded(oldAttempt);

        var currentAttempt = state.BeginAttempt(
            HostManagerModuleKind.ResourceScheduler,
            second,
            HostManagerDeploymentOperation.InitialCreate);
        state.CompleteAttemptFailed(currentAttempt, "current-hot-failure", null);
        Assert.Equal(
            HostManagerDeploymentAttemptSettlement.Stale,
            state.CompleteAttemptSucceeded(oldAttempt));

        var snapshot = state.Snapshot.ResourceScheduler;
        Assert.Equal(HostManagerDeploymentHealth.Failed, snapshot.Health);
        Assert.True(snapshot.LastFailure?.Active);
        Assert.Equal(currentAttempt.AttemptId, snapshot.LastFailure?.AttemptId);
    }

    private static void ApplyInitial(
        HostManagerDeploymentState state,
        HostManagerModuleKind module,
        CompiledHostManagerPlan plan)
    {
        var attempt = state.BeginAttempt(module, plan, HostManagerDeploymentOperation.InitialCreate);
        Assert.Equal(
            HostManagerDeploymentAttemptSettlement.Applied,
            state.CompleteAttemptSucceeded(attempt));
    }

    private static void Publish(
        HostManagerDeploymentState state,
        CompiledHostManagerPlan hostPlan,
        long version)
        => state.PublishRuntimePlan(CompiledRuntimePlan.Default with
        {
            Version = version,
            CompiledAt = DateTimeOffset.UtcNow,
            Reason = $"test-{version}",
            HostManager = hostPlan
        });

    private static CompiledHostManagerPlan Compile(
        byte[] profile,
        ulong epoch,
        ResourceManager.App.Domain.Settings.AppPerformanceSettings performance)
    {
        var loaded = StrictHostManagerProfileLoader.LoadBytes(profile, "test/profile.json");
        return new HostManagerPlanCompiler().Compile(
            loaded,
            HostManagerTestPlanFactory.CreateSettingsInput(performance),
            epoch,
            HostManagerTestPlanFactory.CreateCpuTopology(1),
            HostManagerTestPlanFactory.CreateCpuScoring(HostManagerTestPlanFactory.CreateCpuTopology(1)));
    }

    private static byte[] ReadDefaultProfile()
    {
        using var stream = typeof(StrictHostManagerProfileLoader).Assembly
            .GetManifestResourceStream("ResourceManager.Configuration.HostManager.default.json")
            ?? throw new InvalidOperationException("Embedded Host Manager profile was not found.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static IReadOnlyList<HostManagerModuleDeploymentSnapshot> Modules(
        HostManagerDeploymentSnapshot snapshot)
        =>
        [
            snapshot.SharedResources,
            snapshot.ResourceScheduler,
            snapshot.AdapterPrivateResourceLedger,
            snapshot.SmartCoordinator,
            snapshot.MemoryCleanup,
            snapshot.PlacementCoordinator,
            snapshot.TransactionJournal,
            snapshot.ProcessPolicyExecutor,
            snapshot.PdhCollector
        ];
}
