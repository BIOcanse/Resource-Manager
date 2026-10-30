using System.Collections.Immutable;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerSmartCoordinatorScoreOnlyCompositionTests
{
    [Fact]
    public async Task CurrentCapacityAddsWelfareBeforeSmartConsumesTheCanonicalScore()
    {
        const int processId = 4_240;
        const ulong startKey = 132_537_599_890_000_000;
        const string softwareId = "software:welfare-chain";
        var facts = CreateCompleteProcessFacts(processId, startKey, softwareId, 80, 50, 0);
        var observations = facts.DatasetObservations.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value);
        observations[SchedulingProcessMetricMask.GpuUsage] =
            SchedulingProcessDatasetObservation.CreateCurrent(
                SchedulingProcessMetricMask.GpuUsage,
                1,
                facts.ObservedAtUtcTicks,
                1,
                facts.ObservedAtUtcTicks,
                topologyGeneration: 1,
                topologyFingerprint: 1);
        facts = facts with
        {
            HasSeparateFoundationPayloads = true,
            FoundationDatasetObservations = new Dictionary<string, SchedulingProcessFoundationDatasetObservation>
            {
                [SamplingDatasetIds.ProcessInventory] = SchedulingProcessFoundationDatasetObservation.CreateCurrent(
                    SamplingDatasetIds.ProcessInventory, facts.Generation, facts.ObservedAtUtcTicks),
                [SamplingDatasetIds.ProcessAttribution] = SchedulingProcessFoundationDatasetObservation.CreateCurrent(
                    SamplingDatasetIds.ProcessAttribution, 1, facts.ObservedAtUtcTicks)
            },
            SoftwareBaseScores = [new(softwareId, 80), new("software:not-running", 40)],
            CurrentMetricMask = SchedulingProcessMetricMask.CpuUsage |
                SchedulingProcessMetricMask.GpuUsage |
                SchedulingProcessMetricMask.RuntimeState,
            RequestedMetricMask = SchedulingProcessMetricMask.CpuUsage |
                SchedulingProcessMetricMask.GpuUsage |
                SchedulingProcessMetricMask.RuntimeState,
            GpuSourceGeneration = 1,
            GpuObservedAtUtcTicks = facts.ObservedAtUtcTicks,
            GpuTopologyGeneration = 1,
            GpuTopologyFingerprint = 1,
            DatasetObservations = observations,
            Processes = [facts.Processes[0] with
            {
                SoftwareKind = SoftwareKinds.Adapted,
                ValidMetricMask = SchedulingProcessMetricMask.CpuUsage |
                    SchedulingProcessMetricMask.GpuUsage |
                    SchedulingProcessMetricMask.RuntimeState,
                Gpus =
                [
                    new SchedulingProcessGpuFact(
                        0,
                        1,
                        SchedulingProcessMetricMask.GpuUsage,
                        25,
                        0,
                        1,
                        0,
                        UsageTopologyGeneration: 1)
                ]
            }]
        };
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true,
            processFactsSnapshot: facts,
            scoreOnlyEnabled: true,
            automaticMemoryCleanupEnabled: false,
            failFastEffects: true);

        fixture.MetricSampler.SetSnapshot(CreateWelfareHardwareSnapshot(
            cpuUsagePercent: 50,
            memoryUsagePercent: 50,
            gpuUsagePercent: 50,
            usedVramBytes: 50,
            totalVramBytes: 100));
        await fixture.Coordinator.RunOnceAsync(CancellationToken.None);

        var idleAuthority = fixture.Coordinator.SchedulingAuthority;
        Assert.Equal(0.0625, idleAuthority.Compute!.Welfare.Multiplier, 10);
        Assert.Equal(0.9375, idleAuthority.Compute.Welfare.SystemPressure, 10);
        Assert.Equal(100, idleAuthority.Compute.Welfare.WelfareBaseScore, 10);
        Assert.Equal(6.25, idleAuthority.Compute.Welfare.Share, 10);
        Assert.Equal(50D, idleAuthority.Compute.Welfare.CpuBonus, 10);
        Assert.Equal(idleAuthority.Compute.Welfare.CpuBonus, idleAuthority.Compute.Welfare.MemoryBonus);
        var idleCompute = Assert.Single(idleAuthority.Compute.Cpu!.Scores, static score =>
            score.Kind == NativeComputeScoringOutputKind.ProcessCpu);
        var idleGpuCompute = Assert.Single(idleAuthority.Compute.Gpu!.Scores, static score =>
            score.Kind == NativeComputeScoringOutputKind.ProcessGpu);
        Assert.Equal(18 + idleAuthority.Compute.Welfare.CpuBonus, idleCompute.Score, 10);
        Assert.Equal(15.25, idleGpuCompute.Score, 10);
        var idleSmart = Assert.Single(fixture.Workspace.CurrentSnapshotRows.ToArray(), static row =>
            row.RowKind == NativeSmartCoordinatorSnapshotRowKind.Process);
        Assert.Equal(idleCompute.Score, idleSmart.CpuScore, 10);
        AssertSoftwareCpuScore(fixture, idleCompute.Score);

        await PublishCatalog([new(softwareId, 80), new("software:not-running", 0)]);
        await PublishCatalog([new(softwareId, 80), new("software:not-running", 0), new("software:third", 40)]);
        await PublishCatalog([]);
        await PublishCatalog([new(softwareId, 80), new("software:not-running", 40)]);

        fixture.MetricSampler.SetSnapshot(CreateWelfareHardwareSnapshot(
            cpuUsagePercent: 100,
            memoryUsagePercent: 50,
            gpuUsagePercent: 50,
            usedVramBytes: 50,
            totalVramBytes: 100));
        await fixture.Coordinator.RunOnceAsync(CancellationToken.None);

        var saturatedAuthority = fixture.Coordinator.SchedulingAuthority;
        Assert.Equal(0, saturatedAuthority.Compute!.Welfare.Share);
        Assert.Equal(0, saturatedAuthority.Compute.Welfare.CpuBonus);
        Assert.Equal(idleAuthority.Compute.Welfare.MemoryBonus, saturatedAuthority.Compute.Welfare.MemoryBonus);
        Assert.Equal(Assert.Single(idleAuthority.Compute.Memory!.Scores).Score,
            Assert.Single(saturatedAuthority.Compute.Memory!.Scores).Score);
        var saturatedCompute = Assert.Single(saturatedAuthority.Compute.Cpu!.Scores, static score =>
            score.Kind == NativeComputeScoringOutputKind.ProcessCpu);
        var saturatedGpuCompute = Assert.Single(saturatedAuthority.Compute.Gpu!.Scores, static score =>
            score.Kind == NativeComputeScoringOutputKind.ProcessGpu);
        Assert.Equal(18, saturatedCompute.Score, 10);
        Assert.Equal(9, saturatedGpuCompute.Score, 10);
        var saturatedSmart = Assert.Single(fixture.Workspace.CurrentSnapshotRows.ToArray(), static row =>
            row.RowKind == NativeSmartCoordinatorSnapshotRowKind.Process);
        Assert.Equal(saturatedCompute.Score, saturatedSmart.CpuScore, 10);
        AssertSoftwareCpuScore(fixture, saturatedCompute.Score);

        async Task PublishCatalog(ImmutableArray<SoftwareBaseScore> catalog)
        {
            var before = fixture.Coordinator.SchedulingAuthority.Compute!;
            var current = fixture.ProcessFacts.Snapshot;
            var publications = current.FoundationDatasetObservations.ToDictionary(
                static pair => pair.Key, static pair => pair.Value);
            var attribution = publications[SamplingDatasetIds.ProcessAttribution];
            publications[SamplingDatasetIds.ProcessAttribution] =
                SchedulingProcessFoundationDatasetObservation.CreateCurrent(
                    SamplingDatasetIds.ProcessAttribution,
                    attribution.SourceGeneration + 1,
                    attribution.ObservedAtUtcTicks + 1);
            fixture.ProcessFacts.SetSnapshot(current with
            {
                FoundationDatasetObservations = publications,
                SoftwareBaseScores = catalog
            });
            await fixture.Coordinator.RunOnceAsync(CancellationToken.None);

            var updated = fixture.Coordinator.SchedulingAuthority.Compute!;
            Assert.Equal(100, updated.Welfare.WelfareBaseScore);
            Assert.Equal(100 * 0.0625, updated.Welfare.Share);
            Assert.Equal(before.RoundNumber + 1, updated.RoundNumber);
            var cpu = Assert.Single(updated.Cpu!.Scores, static row =>
                row.Kind == NativeComputeScoringOutputKind.ProcessCpu);
            var gpu = Assert.Single(updated.Gpu!.Scores, static row =>
                row.Kind == NativeComputeScoringOutputKind.ProcessGpu);
            Assert.Equal(50D, updated.Welfare.CpuBonus, 10);
            Assert.Equal(18 + updated.Welfare.CpuBonus, cpu.Score, 10);
            Assert.Equal(9 + updated.Welfare.Share, gpu.Score, 10);
            var smart = Assert.Single(fixture.Workspace.CurrentSnapshotRows.ToArray(), static row =>
                row.RowKind == NativeSmartCoordinatorSnapshotRowKind.Process);
            Assert.Equal(cpu.Score, smart.CpuScore, 10);
            AssertSoftwareCpuScore(fixture, cpu.Score);

            await fixture.Coordinator.RunOnceAsync(CancellationToken.None);
            var repeated = fixture.Coordinator.SchedulingAuthority.Compute!;
            Assert.Equal(updated.RoundNumber, repeated.RoundNumber);
            Assert.Equal(cpu.Score, Assert.Single(repeated.Cpu!.Scores, static row =>
                row.Kind == NativeComputeScoringOutputKind.ProcessCpu).Score);
            Assert.Equal(gpu.Score, Assert.Single(repeated.Gpu!.Scores, static row =>
                row.Kind == NativeComputeScoringOutputKind.ProcessGpu).Score);
            Assert.Equal(0, fixture.ProcessFacts.CaptureCalls);
        }
    }

    [Fact]
    public async Task SameValueHostedCapacityPublicationAdvancesHysteresisExactlyOnce()
    {
        const int processId = 4_242;
        const ulong startKey = 132_537_599_910_000_000;
        const string softwareId = "software:welfare-publication";
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true,
            processFactsSnapshot: CreateCompleteProcessFacts(
                processId,
                startKey,
                softwareId,
                baseScore: 80,
                cpuUsagePercent: 50,
                memoryUsagePercent: 0),
            scoreOnlyEnabled: false,
            automaticMemoryCleanupEnabled: false,
            failFastEffects: true);
        fixture.MetricSampler.SetSnapshot(CreateWelfareHardwareSnapshot(
            cpuUsagePercent: 100,
            memoryUsagePercent: 50,
            gpuUsagePercent: 50,
            usedVramBytes: 50,
            totalVramBytes: 100));

        await fixture.Coordinator.RunOnceAsync(CancellationToken.None);
        var first = GetProcessRow();
        var firstRoundNumber = fixture.Coordinator.SchedulingAuthority
            .Compute!.RoundNumber;
        Assert.Equal(1U, first.ProcessPendingCount);
        Assert.NotEqual(
            NativeSmartCoordinatorProcessGrade.Normal,
            first.PendingProcessGrade);

        await fixture.Coordinator.RunOnceAsync(CancellationToken.None);
        var repeated = GetProcessRow();
        var repeatedRoundNumber = fixture.Coordinator.SchedulingAuthority
            .Compute!.RoundNumber;
        Assert.Equal(firstRoundNumber, repeatedRoundNumber);
        Assert.Equal(1U, repeated.ProcessPendingCount);

        fixture.MetricSampler.PublishNextHostedCpuObservation();
        await fixture.Coordinator.RunOnceAsync(CancellationToken.None);
        var republished = GetProcessRow();
        var republishedRoundNumber = fixture.Coordinator.SchedulingAuthority
            .Compute!.RoundNumber;
        Assert.Equal(firstRoundNumber + 1, republishedRoundNumber);
        Assert.Equal(first.CpuScore, republished.CpuScore, 10);
        Assert.Equal(2U, republished.ProcessPendingCount);

        await fixture.Coordinator.RunOnceAsync(CancellationToken.None);
        var reread = GetProcessRow();
        Assert.Equal(
            republishedRoundNumber,
            fixture.Coordinator.SchedulingAuthority.Compute!.RoundNumber);
        Assert.Equal(2U, reread.ProcessPendingCount);
        Assert.Equal(0U, fixture.Workspace.Snapshot.InflightCount);
        Assert.Equal(0, fixture.ProcessPolicyWriter.BatchCalls);
        Assert.Equal(0, fixture.MemoryCleanupPlanner.PlanCalls);

        NativeSmartCoordinatorSnapshotRow GetProcessRow()
            => Assert.Single(
                fixture.Workspace.CurrentSnapshotRows.ToArray(),
                static row => row.RowKind ==
                    NativeSmartCoordinatorSnapshotRowKind.Process);
    }

    [Fact]
    public async Task CpuWelfareUsesCurrentCapacityWithoutGpuCapacityOrPressureHistory()
    {
        const int processId = 4_241;
        const ulong startKey = 132_537_599_900_000_000;
        const string softwareId = "software:history-adapter";
        var now = DateTimeOffset.UtcNow;
        var clock = new ManualTimeProvider(now);
        var facts = CreateCompleteProcessFacts(processId, startKey, softwareId, 80, 50, 0);
        facts = facts with
        {
            CurrentMetricMask = SchedulingProcessMetricMask.CpuUsage | SchedulingProcessMetricMask.RuntimeState,
            Processes = [facts.Processes[0] with
            {
                SoftwareKind = SoftwareKinds.Adapted,
                ValidMetricMask = SchedulingProcessMetricMask.CpuUsage | SchedulingProcessMetricMask.RuntimeState
            }]
        };
        using var source = new CurrentHardwareObservationSource();
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true, processFactsSnapshot: facts, scoreOnlyEnabled: true,
            automaticMemoryCleanupEnabled: false,
            metricSamplerOverride: source, timeProvider: clock, failFastEffects: true);
        Assert.Empty(fixture.RuntimePlan.HostManager.DataHistory.Requirements);
        var nativePlanProbe = new CapturingNativePlanProbe(fixture.Workspace);
        fixture.Coordinator.TransitionProbe = nativePlanProbe;
        source.Publish(CreateHardwareSnapshot(cpuUsagePercent: 50, capturedAt: now.AddSeconds(-5)), "cpu.usage");
        await fixture.Coordinator.RunOnceAsync(CancellationToken.None);
        var baselineProcess = Assert.Single(fixture.Workspace.CurrentSnapshotRows.ToArray(),
            row => row.RowKind == NativeSmartCoordinatorSnapshotRowKind.Process);
        // Missing GPU/VRAM capacity cancels GPU share, while CPU keeps its own direct bonus.
        Assert.Equal(0, fixture.Coordinator.SchedulingAuthority.Compute!.Welfare.Share);
        Assert.Equal(68, baselineProcess.CpuScore, 10);
        AssertSoftwareCpuScore(fixture, 68);
        Assert.Equal(50, fixture.Coordinator.SchedulingAuthority.Compute.Welfare.CpuBonus, 10);

        source.Publish(CreateHardwareSnapshot(cpuUsagePercent: 95, capturedAt: now), "cpu.usage");
        for (var index = 0; index < 10; index++)
        {
            await fixture.Coordinator.RunOnceAsync(CancellationToken.None);
            var process = Assert.Single(fixture.Workspace.CurrentSnapshotRows.ToArray(),
                row => row.RowKind == NativeSmartCoordinatorSnapshotRowKind.Process);
            Assert.Equal(50, process.CpuOccupancyPercent);
            // Raw 80 * physical-core W 0.5 * headless 0.45 / B 1, plus 100 * 0.05 welfare.
            Assert.Equal(23, process.CpuScore, 10);
            AssertSoftwareCpuScore(fixture, 23);
            Assert.Equal(23, CaptureScoreOnlyDecision(nativePlanProbe).CpuScore, 10);
            Assert.Empty(source.Latest.History);
            clock.Advance(TimeSpan.FromSeconds(30));
        }

        source.Publish(CreateHardwareSnapshot(cpuUsagePercent: 95, capturedAt: now.AddSeconds(1)), "cpu.usage");
        await fixture.Coordinator.RunOnceAsync(CancellationToken.None);
        Assert.Empty(source.Latest.History);
        AssertSoftwareCpuScore(fixture, 23);
        Assert.Equal(23, CaptureScoreOnlyDecision(nativePlanProbe).CpuScore, 10);

        source.Publish(CreateHardwareSnapshot(cpuUsagePercent: 10, capturedAt: now.AddSeconds(2)), "memory.usage");
        await fixture.Coordinator.RunOnceAsync(CancellationToken.None);
        Assert.Equal(95, source.Latest.Cpu.UsagePercent);
        Assert.Empty(source.Latest.History);
        AssertSoftwareCpuScore(fixture, 23);
        Assert.Equal(23, CaptureScoreOnlyDecision(nativePlanProbe).CpuScore, 10);

        source.PublishEmptyCpu(now.AddSeconds(3));
        await fixture.Coordinator.RunOnceAsync(CancellationToken.None);
        Assert.False(source.Latest.Cpu.IsUsageAvailable);
        Assert.Empty(source.Latest.History);
        AssertSoftwareCpuScore(fixture, 18);
        Assert.Equal(18, CaptureScoreOnlyDecision(nativePlanProbe).CpuScore, 10);

        source.Publish(CreateHardwareSnapshot(cpuUsagePercent: 95, capturedAt: now.AddSeconds(4)), "cpu.usage");
        await fixture.Coordinator.RunOnceAsync(CancellationToken.None);
        AssertSoftwareCpuScore(fixture, 23);
        Assert.Equal(23, CaptureScoreOnlyDecision(nativePlanProbe).CpuScore, 10);
        Assert.Equal(0, fixture.ProcessFacts.CaptureCalls);
        Assert.Equal(0, fixture.MetricSampler.CaptureCalls);
        Assert.Empty(source.Subscriptions);
    }

    private static void AssertSoftwareCpuScore(ScoreOnlyCoordinatorFixture fixture, double expected)
    {
        var software = Assert.Single(fixture.Workspace.CurrentSnapshotRows.ToArray(),
            row => row.RowKind == NativeSmartCoordinatorSnapshotRowKind.Software);
        Assert.False(software.ReasonMask.HasFlag(NativeSmartCoordinatorReason.Ineligible));
        Assert.Equal(expected, software.CpuScore, 9);
    }

    private static HardwareMetricSnapshot CreateWelfareHardwareSnapshot(
        double cpuUsagePercent,
        double memoryUsagePercent,
        double gpuUsagePercent,
        ulong usedVramBytes,
        ulong totalVramBytes)
    {
        var capturedAt = DateTimeOffset.UtcNow;
        var snapshot = CreateHardwareSnapshot(
            memoryUsagePercent,
            cpuUsagePercent,
            capturedAt: capturedAt,
            gpuInventoryCurrent: true);
        var datasets = snapshot.Datasets.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value,
            StringComparer.OrdinalIgnoreCase);
        datasets[SamplingDatasetIds.SystemGpuInventory] =
            CreateHardwareDatasetObservation(
                SamplingDatasetIds.SystemGpuInventory,
                SamplingObservationStatus.Current,
                capturedAt,
                1);
        datasets["gpu.0.usage"] = CreateHardwareDatasetObservation(
            "gpu.0.usage",
            SamplingObservationStatus.Current,
            capturedAt,
            1);
        datasets["gpu.0.vram"] = CreateHardwareDatasetObservation(
            "gpu.0.vram",
            SamplingObservationStatus.Current,
            capturedAt,
            1);
        return snapshot with
        {
            Datasets = datasets,
            GpuInventory = new SchedulingGpuInventorySnapshot(
                SamplingObservationStatus.Current,
                1,
                capturedAt.UtcTicks,
                1,
                0,
                0,
                1,
                [
                    new SchedulingGpuAdapterObservation(
                        0,
                        1,
                        SchedulingGpuCapabilityMask.Usage |
                            SchedulingGpuCapabilityMask.DedicatedMemory,
                        SchedulingGpuMetricMask.Usage |
                            SchedulingGpuMetricMask.UsedDedicatedMemory |
                            SchedulingGpuMetricMask.TotalDedicatedMemory,
                        SamplingObservationStatus.Current,
                        SamplingObservationStatus.Current,
                        gpuUsagePercent,
                        usedVramBytes,
                        totalVramBytes,
                        1,
                        capturedAt.UtcTicks)
                ])
        };
    }

    private static (
        NativeSmartCoordinatorActionScope Scope,
        NativeSmartCoordinatorActionDisposition Disposition,
        NativeSmartCoordinatorGradeDomains DomainMask,
        NativeSmartCoordinatorProcessGrade ProcessGrade,
        uint ProcessId,
        ulong SoftwareKey,
        double CpuScore) CaptureScoreOnlyDecision(
            CapturingNativePlanProbe probe)
    {
        var action = Assert.Single(probe.Actions);
        return (
            action.Scope,
            action.Disposition,
            action.DomainMask,
            action.ToProcessGrade,
            action.ProcessId,
            action.SoftwareKey,
            action.CpuScore);
    }

    private sealed class CurrentHardwareObservationSource : IMetricSnapshotObservationSource, IDisposable
    {
        private readonly LastSuccessfulHardwareMetricSnapshot owner = new();
        private readonly SamplingOwnerToken workspace = new(1, 1);
        private readonly ulong run;
        private readonly HardwareMetricHostedPublicationTicket ticket;

        internal CurrentHardwareObservationSource()
        {
            run = owner.OpenHostedRun();
            var now = DateTimeOffset.UtcNow;
            var schedule = new NativeItemSamplingSubscriptionScheduleReceipt(
                NativeItemSamplingSubscriptionScheduleOrigin.Plan, 1, 1, 1, 1,
                now, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1), now.AddSeconds(5), now, 1, 1, 1, 0);
            Assert.True(owner.TryCaptureHostedTicket(run, workspace, schedule, out ticket));
        }

        internal HardwareMetricSnapshot Latest => owner.Read()!;
        internal List<MetricObservationSubscription> Subscriptions { get; } = [];
        internal void Publish(HardwareMetricSnapshot raw, string item)
        {
            Assert.True(owner.TryPublishHosted(ticket, raw, MetricSampleRequest.ForIds([item]),
                [item], null, out var publication));
            Assert.True(publication.IsSuccessful);
        }

        internal void PublishEmptyCpu(DateTimeOffset at)
            => Assert.True(owner.TryRecordHostedFailure(ticket, MetricSampleRequest.ForIds(["cpu.usage"]),
                ["cpu.usage"], at, "fixture-empty"));

        public HardwareMetricSnapshot? ReadLatest(MetricSampleRequest request) => owner.Read();

        public IDisposable AcquireSubscription(string subscriptionId, MetricSampleRequest request, TimeSpan refreshInterval)
        {
            Subscriptions.Add(new(subscriptionId, request, refreshInterval));
            return new RecordingSubscription(static () => { });
        }

        public void Dispose()
        {
            Assert.True(owner.CloseHostedRun(run, DateTimeOffset.UtcNow, "fixture-stop"));
            owner.Dispose();
        }
    }
}
