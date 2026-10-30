using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerSmartCoordinatorScoreOnlyCompositionTests
{
    [Fact]
    public void StatusCountsMemoryPriorityOwnershipOnItsProcessObject()
    {
        const int processId = 404;
        const ulong startKey = 303;
        var processRow = new NativeSmartCoordinatorSnapshotRow
        {
            RowKind = NativeSmartCoordinatorSnapshotRowKind.Process,
            TargetKey = NativeStableIdentity.CreateCaseInsensitiveKey(
                HostManagerTargetIdentity.CreateProcessTargetId(processId, startKey)),
            Flags = NativeSmartCoordinatorSnapshotRowFlags.ProcessOwned
        };
        var sameProcess = CreateMemoryPriorityOwner(processId, startKey);
        var otherProcess = CreateMemoryPriorityOwner(processId + 1, startKey + 1);

        Assert.Equal(1, HostManagerSmartCoordinator.CountAppliedTargets(
            [], [], [sameProcess]));
        Assert.Equal(1, HostManagerSmartCoordinator.CountAppliedTargets(
            [], [processRow], [sameProcess]));
        Assert.Equal(2, HostManagerSmartCoordinator.CountAppliedTargets(
            [], [processRow], [sameProcess, otherProcess]));
    }

    [Fact]
    public async Task StatusCountsLedgerMemoryPriorityOwnershipWithoutNewActions()
    {
        const int processId = 24_061;
        var startedAt = CreateProcessStartedAtOrderedBeforeSoftwareTarget(processId);
        var startKey = checked((ulong)startedAt.ToFileTime());
        var softwareId = CreateSoftwareIdOrderedAfterProcessTarget(processId, startKey);
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true,
            scoreOnlyEnabled: false,
            automaticMemoryCleanupEnabled: false,
            optimizationMode: AppOptimizationModes.MemoryOnly);
        Assert.Equal(0, (await fixture.Coordinator.GetStatusAsync(CancellationToken.None))
            .AppliedTargetCount);

        _ = await fixture.SeedProcessOwnershipAsync(
            processId, startKey, softwareId, memoryPolicy: true, actionId: 911);
        var status = await fixture.Coordinator.GetStatusAsync(CancellationToken.None);

        Assert.Equal(1, status.AppliedTargetCount);
        Assert.Null(status.FaultClosedReason);
        Assert.Equal(0, fixture.ProcessPolicyWriter.TotalCalls);
    }

    [Fact]
    public async Task StatusComposesLoopLivenessWithCurrentModeAndFaultClosedScope()
    {
        var identity = new HostManagerComputeProcessIdentity(24_071, 132_537_600_000_000_071);
        using var validation = CoordinatorValidationScopeFixture.Create(identity);
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true,
            scoreOnlyEnabled: false,
            automaticMemoryCleanupEnabled: false,
            optimizationMode: AppOptimizationModes.Normal,
            processEffectValidationScopeAuthority: validation.Authority);
        // 后台循环存活，且上一周期是在普通模式下跑完的。
        WritePrivateField(fixture.Coordinator, "schedulerRunning", true);

        var normal = await fixture.Coordinator.GetStatusAsync(CancellationToken.None);
        Assert.False(normal.SchedulerRunning);
        Assert.Null(normal.FaultClosedReason);

        // 模式刚切到 CPU 调度、循环还没跑下一周期：状态按当前模式给出，不沿用上一周期的结论。
        fixture.RuntimePlanProvider.Publish(fixture.RuntimePlan with
        {
            Version = 2,
            OptimizationMode = CompiledOptimizationModePlan.Compile(AppOptimizationModes.CpuOnly)
        });
        var cpu = await fixture.Coordinator.GetStatusAsync(CancellationToken.None);
        Assert.Equal(AppOptimizationModes.CpuOnly, cpu.Mode);
        Assert.True(cpu.SchedulerRunning);
        Assert.Null(cpu.FaultClosedReason);

        // 验证作用域留下未结清的准入，运行时故障关闭：不再报告调度运行中，并给出原因码。
        Assert.True(validation.Authority.TryBeginAdmission(
            validation.Authority.Capture(),
            HostManagerProcessEffectValidationFamily.NonAdaptedMemoryTransaction,
            "status-fault-closed",
            identity,
            out var blocker));
        Assert.NotNull(blocker);
        var faulted = await fixture.Coordinator.GetStatusAsync(CancellationToken.None);
        Assert.Equal("faultedBlocked", validation.Authority.GetStatus().State);
        Assert.False(faulted.SchedulerRunning);
        Assert.Equal("effect-scope-admission-pending", faulted.FaultClosedReason);
    }

    private static NativeAppliedOwnershipProcessIdentity CreateMemoryPriorityOwner(
        int processId,
        ulong startKey)
        => new(
            NativeStableIdentity.CreateCaseInsensitiveKey(
                HostManagerTargetIdentity.CreateProcessMemoryPolicyTargetId(
                    processId,
                    startKey)),
            SoftwareKey: 77,
            checked((uint)processId),
            startKey);

    [Fact]
    public void StatusMergesLegacyAndNativeTargetsWithoutMergingDifferentProcesses()
    {
        var first = CreateLegacyPlacementReceipt();
        var duplicate = first with { ResourceKind = "Gpu" };
        var separate = first with { TargetId = "process:legacy:2" };
        var empty = first with { TargetId = "process:empty", Records = [] };
        var rows = new[]
        {
            new NativeSmartCoordinatorSnapshotRow
            {
                RowKind = NativeSmartCoordinatorSnapshotRowKind.Process,
                TargetKey = NativeStableIdentity.CreateCaseInsensitiveKey(first.TargetId),
                Flags = NativeSmartCoordinatorSnapshotRowFlags.ProcessOwned
            },
            new NativeSmartCoordinatorSnapshotRow
            {
                RowKind = NativeSmartCoordinatorSnapshotRowKind.Process,
                TargetKey = NativeStableIdentity.CreateCaseInsensitiveKey("process:native:3"),
                Flags = NativeSmartCoordinatorSnapshotRowFlags.ProcessOwned
            },
            new NativeSmartCoordinatorSnapshotRow
            {
                RowKind = NativeSmartCoordinatorSnapshotRowKind.Software,
                TargetKey = NativeStableIdentity.CreateCaseInsensitiveKey(first.SoftwareId!),
                Flags = NativeSmartCoordinatorSnapshotRowFlags.CriticalFact
            }
        };

        Assert.Equal(3, HostManagerSmartCoordinator.CountAppliedTargets(
            [first, duplicate, separate, empty], rows, []));
        Assert.Equal(2, HostManagerSmartCoordinator.CountAppliedTargets(
            [first, duplicate, separate, empty], [], []));
        Assert.Equal(0, HostManagerSmartCoordinator.CountAppliedTargets([], [], []));
    }

    [Theory]
    [InlineData((uint)NativeSmartCoordinatorSnapshotRowFlags.ProcessOwned, 1)]
    [InlineData((uint)NativeSmartCoordinatorSnapshotRowFlags.ProcessInflight, 0)]
    [InlineData((uint)NativeSmartCoordinatorSnapshotRowFlags.None, 0)]
    public void StatusRestoredProcessIsNotCounted(
        uint flags,
        int expected)
    {
        var row = new NativeSmartCoordinatorSnapshotRow
        {
            RowKind = NativeSmartCoordinatorSnapshotRowKind.Process,
            TargetKey = 91,
            Flags = (NativeSmartCoordinatorSnapshotRowFlags)flags,
            AppliedProcessGrade = NativeSmartCoordinatorProcessGrade.Normal
        };
        Assert.Equal(expected, HostManagerSmartCoordinator.CountAppliedTargets([], [row], []));
    }
}
