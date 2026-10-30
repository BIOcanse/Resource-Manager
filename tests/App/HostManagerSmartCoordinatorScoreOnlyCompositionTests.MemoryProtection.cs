using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerSmartCoordinatorScoreOnlyCompositionTests
{
    // Protection only forbids freezing (user 10-29: 「取消2级保护机制」「只需要留一级保护」); "never optimized" is the
    // high base-score tier. A protection, including a level 2 stored before 10-29, keeps the memory directive.
    [Theory]
    [InlineData(true, OptimizationProtectionStates.Active, 2)]
    [InlineData(false, OptimizationProtectionStates.Active, 2)]
    [InlineData(true, OptimizationProtectionStates.Stale, 2)]
    [InlineData(true, OptimizationProtectionStates.Active, 1)]
    public async Task MemoryPriorityProjectionIsNotChangedByNoFreezeProtection(
        bool processProtection, string state, int level)
    {
        const int pid = 24057;
        const ulong startKey = 132_537_599_910_000_000;
        const string software = "software:memory-protection";
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true,
            processFactsSnapshot: CreateCompleteProcessFacts(pid, startKey, software, 20, 50, 25),
            memoryModePolicyEnabled: true,
            automaticMemoryCleanupEnabled: false,
            failFastEffects: true);
        fixture.ProtectionService.Targets = [CreateMemoryProtection(
            processProtection ? HostManagerTargetIdentity.CreateProcessTargetId(pid, startKey) : software,
            state, level)];

        _ = await fixture.RunRealtimeCycleAsync();

        var authority = fixture.Coordinator.SchedulingAuthority;
        Assert.Equal(HostManagerSchedulingAuthorityAvailability.Ready, authority.Availability);
        Assert.Equal(NativeMemoryMode.Optimize, Assert.Single(authority.MemoryModes!.Software).Mode);
        var projection = Assert.IsType<HostManagerNonAdaptedMemoryModeProjectionSnapshot>(
            fixture.Coordinator.NonAdaptedMemoryModeProjection);
        Assert.Single(projection.Processes);
        Assert.Equal(0, fixture.ProcessPolicyWriter.BatchCalls);
    }

    [Fact]
    public async Task NewNoFreezeProtectionKeepsOwnedMemoryPriority()
    {
        const int pid = 24058;
        var startedAt = CreateProcessStartedAtOrderedBeforeSoftwareTarget(pid);
        var startKey = checked((ulong)startedAt.ToFileTime());
        var software = CreateSoftwareIdOrderedAfterProcessTarget(pid, startKey);
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true,
            scoreOnlyEnabled: false,
            processFactsSnapshot: CreateCompleteProcessFacts(pid, startKey, software, 20, 50, 25),
            processRecoveryRead: _ => RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                new ProcessInstanceRecoverySnapshot(pid, startedAt)),
            memoryModePolicyEnabled: true,
            automaticMemoryCleanupEnabled: false,
            optimizationMode: AppOptimizationModes.MemoryOnly);
        var currentPriority = 5U;
        var writes = new List<uint>();
        fixture.ProcessPolicyWriter.ProcessReadHandler = _ => new ProcessResourcePolicySnapshot(
            pid, "protected-memory", @"c:\tests\protected-memory.exe", startedAt,
            "Normal", ProcessorAffinityMask: 1, LogicalProcessorCount: 1,
            MemoryPriority: currentPriority);
        fixture.ProcessPolicyWriter.BatchHandler = requests =>
        {
            var request = Assert.Single(requests);
            Assert.Equal(pid, request.ProcessId);
            Assert.Equal(startedAt, request.ExpectedStartedAt);
            Assert.Null(request.PriorityClass);
            Assert.False(request.TrimWorkingSet);
            currentPriority = Assert.IsType<uint>(request.MemoryPriority);
            writes.Add(currentPriority);
            return [new ProcessResourcePolicyBatchWriteResult(pid,
                [new ProcessResourcePolicyBatchFieldResult(
                    ProcessResourcePolicyBatchFields.MemoryPriority, true, "applied")])];
        };

        _ = await fixture.RunRealtimeCycleAsync();
        Assert.Equal([3U], writes);
        Assert.Single((await fixture.NativeTransactions.AppliedOwnership.ReadSnapshotAsync(
            CancellationToken.None)).Records);
        fixture.ProtectionService.Targets = [CreateMemoryProtection(
            HostManagerTargetIdentity.CreateProcessTargetId(pid, startKey),
            OptimizationProtectionStates.Active, OptimizationProtectionLevels.NoFreeze)];

        _ = await fixture.RunRealtimeCycleAsync();

        Assert.Equal([3U], writes);
        Assert.Single(fixture.Coordinator.NonAdaptedMemoryModeProjection!.Processes);
        Assert.Single((await fixture.NativeTransactions.AppliedOwnership.ReadSnapshotAsync(
            CancellationToken.None)).Records);
    }

    private static ProtectedOptimizationTarget CreateMemoryProtection(
        string key, string state, int level)
        => new("protection:memory", "Process", key, null, null, null,
            DateTimeOffset.UtcNow, "test", "report:memory", DateTimeOffset.UtcNow,
            state, AllowsPlacementAvoidance: true, ProtectionLevel: level);
}
