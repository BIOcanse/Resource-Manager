using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Domain.Settings;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerSmartCoordinatorScoreOnlyCompositionTests
{
    [Fact]
    public async Task RealtimeIdealPlacementProgressesBeforeProcessPolicyBacklogIsDrained()
    {
        const int firstPid = 42_600;
        const ulong start = 133_900_000_000_000_040;
        var startedAt = DateTimeOffset.FromFileTime((long)start);
        var topology = CreateAutomaticPlacementTopology();
        var inputs = Enumerable.Range(firstPid, 20)
            .Select(pid => (pid, start, $"software:placement-fairness:{pid}", 40d, 1d, 1d)).ToArray();
        var policies = inputs.ToDictionary(x => x.pid, x => new ProcessResourcePolicySnapshot(
            x.pid, "fixture", @"C:\tests\fixture.exe", startedAt, "Normal", 1, 1,
            MemoryPriority: 5, PowerThrottlingControlMask: 0, PowerThrottlingStateMask: 0));
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            // Saturated CPU capacity makes the policy backlog independent of idle welfare.
            warm: true, cpuUsagePercent: 100, scoreOnlyEnabled: false, processFactsSnapshot: CreateCompleteProcessFacts(inputs),
            processRecoveryRead: pid => RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(new(pid, startedAt)),
            automaticMemoryCleanupEnabled: false, optimizationMode: AppOptimizationModes.CpuOnly,
            optimizationCapabilities: OptimizationModeCapabilities.HardwarePlacement | OptimizationModeCapabilities.AutomaticProcessPolicies,
            cpuCoreReader: new RecordingCpuCoreResidencyReader(() => CpuCoreResidencyTestValues.Create(
                95, DateTimeOffset.UtcNow, inputs.Select(x => CpuCoreResidencyTestValues.Process(x.pid, start, ("core:0", 1))).ToArray())),
            cpuTopology: topology, cpuScoring: HostManagerTestPlanFactory.CreateCpuScoring(topology),
            automaticPlacementTopology: topology,
            realtimeActionBudget: 1);
        var policyWrites = 0;
        fixture.ProcessPolicyWriter.ProcessReadHandler = pid => policies[pid];
        fixture.ProcessPolicyWriter.BatchHandler = requests => requests.Select(request =>
        {
            var current = policies[request.ProcessId];
            var fields = new List<ProcessResourcePolicyBatchFieldResult>();
            if (request.PriorityClass is { } priority)
            {
                policyWrites++;
                current = current with { PriorityClass = priority };
                fields.Add(new(ProcessResourcePolicyBatchFields.PriorityClass, true, "recorded"));
            }
            if (request.PowerControlMask is { } mask)
            {
                current = current with { PowerThrottlingControlMask = mask, PowerThrottlingStateMask = request.PowerStateMask };
                fields.Add(new(ProcessResourcePolicyBatchFields.PowerThrottling, true, "recorded"));
            }
            policies[request.ProcessId] = current;
            return new ProcessResourcePolicyBatchWriteResult(request.ProcessId, fields);
        }).ToArray();
        var threads = new Dictionary<int, ThreadIdealProcessorPolicySnapshot>
        {
            [10001] = new(firstPid, start, 10001, start + 1, new(0, 0), 0, 15, []),
            [10002] = new(firstPid, start, 10002, start + 2, new(0, 0), 0, 15, [])
        };
        var threadsAvailable = false;
        fixture.ProcessPolicyWriter.IdealThreadsReadHandler = (pid, _, _) =>
            RecoveryReadResult<IReadOnlyList<ThreadIdealProcessorPolicySnapshot>>.Found(
                threadsAvailable && pid == firstPid ? threads.Values.ToArray() : []);
        fixture.ProcessPolicyWriter.PlacementReadHandler = (pid, _) =>
            RecoveryReadResult<ProcessPlacementRecoverySnapshot>.Found(
                new(pid, startedAt, "fixture", @"C:\tests\fixture.exe", null, [1, 2, 3, 4], null, null));
        fixture.ProcessPolicyWriter.IdealReadHandler = (_, _, tid) =>
            RecoveryReadResult<ThreadIdealProcessorPolicySnapshot>.Found(threads[tid]);
        fixture.ProcessPolicyWriter.IdealWriteHandler = (expected, desired) =>
        {
            Assert.Equal(expected, threads[expected.ThreadId]);
            threads[expected.ThreadId] = expected with { IdealProcessor = desired };
            return new(true, "recorded hint");
        };
        // A first usable thread observation can arrive while policy work is
        // already pending. It must not have to wait for that backlog to drain.
        for (var cycle = 0; cycle < 3; cycle++)
        {
            fixture.PublishNextHostedCpuObservation();
            _ = await fixture.RunRealtimeCycleAsync();
        }
        var policyWritesBeforeThreads = policyWrites;
        threadsAvailable = true;
        for (var cycle = 0; cycle < 10; cycle++)
        {
            fixture.PublishNextHostedCpuObservation();
            var before = policyWrites + fixture.ProcessPolicyWriter.IdealProcessorWriteCalls;
            _ = await fixture.RunRealtimeCycleAsync();
            Assert.InRange(policyWrites + fixture.ProcessPolicyWriter.IdealProcessorWriteCalls - before, 0, 1);
        }
        Assert.InRange(policyWrites, policyWritesBeforeThreads + 1, inputs.Length - 1);
        Assert.True(fixture.ProcessPolicyWriter.IdealProcessorWriteCalls > 0,
            $"No thread preference progressed while {inputs.Length - policyWrites} process policies remained.");
        Assert.Equal(0, fixture.ProcessPolicyWriter.DefaultCpuSetWriteCalls);
    }
}
