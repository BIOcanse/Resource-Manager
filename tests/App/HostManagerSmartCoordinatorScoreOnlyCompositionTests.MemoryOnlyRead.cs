using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Infrastructure.NativeCore;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerSmartCoordinatorScoreOnlyCompositionTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MemoryCaptureApplyAndRestoreDoNotNeedUnreadableCpuPolicy(bool unreadablePriority)
    {
        const int pid = 42_930;
        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var start = checked((ulong)startedAt.ToFileTime());
        var facts = CreateCompleteProcessFacts(pid, start, "software:memory-narrow-read", 35, 0, 25);
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true, scoreOnlyEnabled: false,
            processFactsSnapshot: facts, memoryModePolicyEnabled: true, automaticMemoryCleanupEnabled: false,
            optimizationMode: AppOptimizationModes.MemoryOnly,
            processRecoveryRead: _ => RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(new(pid, startedAt)));
        fixture.MetricSampler.SetSnapshot(CreateHardwareSnapshot(memoryUsagePercent: 100, cpuUsagePercent: 0));
        var priority = 5U;
        var fullReads = 0;
        var unreadable = new ProcessResourcePolicySnapshot(pid, "fixture", null, startedAt,
            unreadablePriority ? string.Empty : "Normal", unreadablePriority ? 1 : 0, 1, MemoryPriority: priority);
        fixture.ProcessPolicyWriter.ProcessReadHandler = _ =>
        {
            fullReads++;
            return string.IsNullOrEmpty(unreadable.PriorityClass) || unreadable.ProcessorAffinityMask == 0 ? null : unreadable;
        };
        fixture.ProcessPolicyWriter.MemoryReadHandler = _ =>
            RecoveryReadResult<ProcessMemoryPriorityRecoverySnapshot>.Found(new(pid, startedAt, priority));
        var writes = new List<uint>();
        fixture.ProcessPolicyWriter.BatchHandler = requests => requests.Select(request =>
        {
            Assert.Equal(pid, request.ProcessId);
            Assert.Null(request.PriorityClass);
            Assert.Null(request.PowerControlMask);
            Assert.False(request.TrimWorkingSet);
            priority = Assert.IsType<uint>(request.MemoryPriority);
            writes.Add(priority);
            return new ProcessResourcePolicyBatchWriteResult(pid,
                [new(ProcessResourcePolicyBatchFields.MemoryPriority, true, "memory only")]);
        }).ToArray();

        await fixture.RunRealtimeCycleAsync();
        Assert.Equal(3U, priority);
        Assert.Single((await fixture.NativeTransactions.AppliedOwnership.ReadSnapshotAsync(CancellationToken.None)).Records);
        var applied = await fixture.Coordinator.GetDecisionDiagnosticsAsync(CancellationToken.None);
        Assert.True(Assert.Single(applied.NonAdaptedMemoryDecisions).TransactionPrepared);
        Assert.Empty(applied.NonAdaptedMemorySkipCounts);

        fixture.ProcessFacts.SetSnapshot(facts with
        {
            Processes = facts.Processes.Select(process => process with { BaseScore = 100 }).ToArray()
        });
        fixture.PublishNextHostedCpuObservation();
        await fixture.RunRealtimeCycleAsync();
        Assert.Equal(new uint[] { 3, 5 }, writes);
        Assert.Empty((await fixture.NativeTransactions.AppliedOwnership.ReadSnapshotAsync(CancellationToken.None)).Records);
        Assert.Equal(0, fullReads);
        var restored = await fixture.Coordinator.GetDecisionDiagnosticsAsync(CancellationToken.None);
        Assert.True(Assert.Single(restored.NonAdaptedMemoryDecisions).TransactionPrepared);
        Assert.Empty(restored.NonAdaptedMemorySkipCounts);
    }
}
