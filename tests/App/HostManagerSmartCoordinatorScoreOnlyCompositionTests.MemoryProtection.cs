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
        fixture.ProtectionService.Targets = [processProtection
            ? CreateProcessProtection(pid, state, level)
            : CreateSoftwareProtection(software, state, level)];

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
        fixture.ProtectionService.Targets = [CreateProcessProtection(
            pid, OptimizationProtectionStates.Active, OptimizationProtectionLevels.NoFreeze)];

        _ = await fixture.RunRealtimeCycleAsync();

        Assert.Equal([3U], writes);
        Assert.Single(fixture.Coordinator.NonAdaptedMemoryModeProjection!.Processes);
        Assert.Single((await fixture.NativeTransactions.AppliedOwnership.ReadSnapshotAsync(
            CancellationToken.None)).Records);
    }

    [Theory]
    [InlineData(80, false)]
    [InlineData(81, true)]
    [InlineData(90, true)]
    public async Task EffectiveHighTierOmitsMemoryPriorityDirectiveWithoutProtection(double score, bool blocked)
    {
        const int pid = 24059;
        const ulong startKey = 132_537_599_910_000_000;
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true,
            processFactsSnapshot: CreateCompleteProcessFacts(pid, startKey, "software:high-score-memory", score, 1, 25),
            memoryModePolicyEnabled: true,
            automaticMemoryCleanupEnabled: false,
            failFastEffects: true);
        _ = await fixture.RunRealtimeCycleAsync();
        Assert.Equal(HostManagerSchedulingAuthorityAvailability.Ready, fixture.Coordinator.SchedulingAuthority.Availability);
        var projection = fixture.Coordinator.NonAdaptedMemoryModeProjection!;
        if (blocked) Assert.Empty(projection.Processes);
        else Assert.Single(projection.Processes);
        Assert.Equal(0, fixture.ProcessPolicyWriter.BatchCalls);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(-1)]
    [InlineData(255)]
    public async Task StoredLegacyOrUnknownProtectionIsNormalizedBeforeNative(int storedLevel)
    {
        const int pid = 24060;
        const ulong startKey = 132_537_599_910_000_000;
        const string software = "software:legacy-protection";
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true,
            processFactsSnapshot: CreateCompleteProcessFacts(pid, startKey, software, 20, 1, 25));
        fixture.ProtectionService.Targets = [CreateSoftwareProtection(software, OptimizationProtectionStates.Active, storedLevel)];
        var rows = await CaptureIdentityRowsAsync(fixture);
        Assert.All(rows.Where(row => row.ProcessId == pid), row => Assert.Equal(1, row.ProtectionLevel));
        Assert.Contains(rows, row => row.ProcessId == pid && row.ProtectionLevel == 1);
    }

    // Entries as the protection list holds them: a software identity, or every process with an image name
    // (CreateCompleteProcessFacts names process <pid> "cycle-budget-<pid>").
    private static ProtectedOptimizationTarget CreateSoftwareProtection(
        string software, string state, int level)
        => new("protection:memory", OptimizationProtectionTargetTypes.Software, "software:" + software, software,
            null, null, DateTimeOffset.UtcNow, "test", "report:memory", DateTimeOffset.UtcNow,
            state, AllowsPlacementAvoidance: true, ProtectionLevel: level);

    private static ProtectedOptimizationTarget CreateProcessProtection(
        int pid, string state, int level)
        => new("protection:memory", OptimizationProtectionTargetTypes.Process, $"process-name:cycle-budget-{pid}", null,
            null, null, DateTimeOffset.UtcNow, "test", "report:memory", DateTimeOffset.UtcNow,
            state, AllowsPlacementAvoidance: true, ProtectionLevel: level)
        {
            ProcessName = $"cycle-budget-{pid}"
        };
}
