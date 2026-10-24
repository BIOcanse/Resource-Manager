using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization.Transactions;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerSmartCoordinatorScoreOnlyCompositionTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(32)]
    public async Task SteadyMemoryOwnershipCycleDoesNotReadDurablePayloadOrSequence(int count)
    {
        const int firstPid = 45_000;
        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var start = checked((ulong)startedAt.ToFileTime());
        var inputs = Enumerable.Range(0, count).Select(index => (
            ProcessId: firstPid + index, ProcessStartKey: start,
            SoftwareId: $"software:journal-cost:{index}", BaseScore: 35D,
            CpuUsagePercent: 1D, MemoryUsagePercent: 25D)).ToArray();
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true, scoreOnlyEnabled: false,
            processFactsSnapshot: CreateCompleteProcessFacts(inputs),
            processRecoveryRead: pid => RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(new(pid, startedAt)),
            memoryModePolicyEnabled: true, automaticMemoryCleanupEnabled: false,
            optimizationMode: AppOptimizationModes.MemoryOnly, cpuUsagePercent: 1,
            realtimeActionBudget: 32);
        fixture.MetricSampler.SetSnapshot(CreateHardwareSnapshot(memoryUsagePercent: 100, cpuUsagePercent: 1));
        fixture.ProcessFacts.SetSnapshot(CreateHostedFinalAdmissionProcessFacts(
            fixture.ProcessFacts.Snapshot, fixture.MetricSampler.Snapshot));
        foreach (var input in inputs)
            await fixture.SeedProcessOwnershipAsync(input.ProcessId, start, input.SoftwareId,
                memoryPolicy: true, actionId: checked((ulong)input.ProcessId));
        var liveInspections = 0;
        fixture.ProcessPolicyWriter.ProcessReadHandler = pid =>
        {
            liveInspections++;
            return new ProcessResourcePolicySnapshot(pid, $"process-{pid}", $@"c:\tests\{pid}.exe",
                startedAt, "Normal", ProcessorAffinityMask: 1, LogicalProcessorCount: 1, MemoryPriority: 3);
        };
        fixture.ProcessPolicyWriter.BatchHandler = _ => throw new InvalidOperationException("No new effects expected.");
        var journal = (HostManagerTransactionJournalRuntime)fixture.NativeTransactions.GetType()
            .GetField("journal", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(fixture.NativeTransactions)!;
        var payloads = (NativeTransactionJournalPayloadStore)journal.GetType()
            .GetField("payloadStore", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(journal)!;
        // Initial reconciliation/recovery is allowed to read disk. Warm the actual cycle once.
        await fixture.RunRealtimeCycleAsync();
        var payloadReads = payloads.PayloadFileReadCount;
        var sequenceReads = payloads.SequenceFileReadCount;
        liveInspections = 0;

        await fixture.RunRealtimeCycleAsync();

        Assert.True(liveInspections >= count, $"Live OS inspection count {liveInspections} < {count}.");
        var payloadDelta = payloads.PayloadFileReadCount - payloadReads;
        var sequenceDelta = payloads.SequenceFileReadCount - sequenceReads;
        Assert.True(payloadDelta == 0 && sequenceDelta == 0,
            $"Payload file reads: {payloadDelta}; sequence file reads: {sequenceDelta}; owners: {count}.");
        Assert.Equal(0, fixture.ProcessPolicyWriter.BatchCalls);
        Assert.Empty((await fixture.ReadJournalSnapshotAsync()).Records);
        Assert.Equal(count, (await fixture.NativeTransactions.AppliedOwnership.ReadSnapshotAsync(CancellationToken.None)).Records.Count);
    }
}
