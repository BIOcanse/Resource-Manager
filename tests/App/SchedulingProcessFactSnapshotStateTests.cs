using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.ResourceBreakdown;

namespace Resource_Manager_APP.Tests;

public sealed class SchedulingProcessFactSnapshotStateTests
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FreshnessGrace = TimeSpan.FromSeconds(2);

    [Fact]
    public void SoftwareBasesFollowOnlyTheAttributionPublicationIncludingEmptyReplacement()
    {
        var now = DateTimeOffset.UtcNow;
        var state = new SchedulingProcessFactSnapshotState();
        var initial = Sample(now, 1, SchedulingProcessMetricMask.CpuUsage,
            Fact(42, 100, SchedulingProcessMetricMask.CpuUsage, 17, 0)) with
        {
            SoftwareBaseScores = [new("software", 80), new("software:not-running", 40)]
        };
        state.ApplyScheduled(Request(SchedulingProcessMetricMask.CpuUsage), initial, now, Schedule(now));
        var read = FactRequest(SchedulingProcessMetricMask.CpuUsage);
        Assert.Equal(initial.SoftwareBaseScores, state.ReadLatest(read, now)!.SoftwareBaseScores);

        var next = Sample(now.AddSeconds(1), 2, SchedulingProcessMetricMask.CpuUsage,
            Fact(42, 100, SchedulingProcessMetricMask.CpuUsage, 99, 0)) with
        {
            SoftwareBaseScores = [new("unpublished", 1)]
        };
        state.ApplyScheduled(Request(SchedulingProcessMetricMask.CpuUsage, SamplingDatasetIds.ProcessCpuUsage),
            next, now.AddSeconds(1), Schedule(now.AddSeconds(1), 2));
        Assert.Equal(initial.SoftwareBaseScores, state.ReadLatest(read, now)!.SoftwareBaseScores);

        var attributionRequest = Request(SchedulingProcessMetricMask.None, SamplingDatasetIds.ProcessAttribution);
        var prepared = state.PrepareScheduledCopy(attributionRequest,
            next with { SoftwareBaseScores = [new("software", 20)] }, now.AddSeconds(2), Schedule(now.AddSeconds(2), 3));
        Assert.Equal(initial.SoftwareBaseScores, state.ReadLatest(read, now)!.SoftwareBaseScores);
        state.CommitPrepared(prepared);
        Assert.Equal(20, Assert.Single(state.ReadLatest(read, now)!.SoftwareBaseScores).BaseScore);
        Assert.Equal(2, initial.SoftwareBaseScores.Length);

        state.ApplyScheduled(attributionRequest, next with { SoftwareBaseScores = [] },
            now.AddSeconds(3), Schedule(now.AddSeconds(3), 4));
        Assert.Empty(state.ReadLatest(read, now)!.SoftwareBaseScores);
        var failed = state.PrepareFailureCopy(attributionRequest, now.AddSeconds(4), new IOException("fixture"));
        state.CommitPrepared(failed);
        Assert.Null(state.ReadLatest(read, now));
    }

    [Fact]
    public void FailedAttemptRetainsLineageButRevokesCurrentAuthority()
    {
        var now = DateTimeOffset.UtcNow;
        var state = new SchedulingProcessFactSnapshotState();
        var request = Request(SchedulingProcessMetricMask.CpuUsage);
        state.ApplyScheduled(
            request,
            Sample(
                now,
                generation: 1,
                SchedulingProcessMetricMask.CpuUsage,
                Fact(42, 100, SchedulingProcessMetricMask.CpuUsage, 17, 0)),
            now,
            Schedule(now));
        state.ApplyScheduled(
            request,
            new SchedulingProcessFactSnapshot(
                SamplingObservationStatus.Unavailable,
                2,
                now.AddSeconds(1).UtcTicks,
                0,
                0,
                0,
                0,
                SchedulingProcessMetricMask.CpuUsage,
                SchedulingProcessMetricMask.None,
                []),
            now.AddSeconds(1),
            Schedule(now.AddSeconds(1), stateRevision: 2));

        var retained = state.ReadLatest(
            FactRequest(SchedulingProcessMetricMask.CpuUsage),
            now.AddSeconds(2));

        Assert.Null(retained);
    }

    [Fact]
    public void NewInventoryEvictsExitedOrPidReusedInstancesFromEveryDataset()
    {
        var now = DateTimeOffset.UtcNow;
        var state = new SchedulingProcessFactSnapshotState();
        state.ApplyScheduled(
            Request(SchedulingProcessMetricMask.CpuUsage),
            Sample(
                now,
                generation: 1,
                SchedulingProcessMetricMask.CpuUsage,
                Fact(42, 100, SchedulingProcessMetricMask.CpuUsage, 23, 0)),
            now,
            Schedule(now));
        state.ApplyScheduled(
            Request(SchedulingProcessMetricMask.MemoryUsage),
            Sample(
                now.AddSeconds(1),
                generation: 2,
                SchedulingProcessMetricMask.MemoryUsage,
                Fact(42, 200, SchedulingProcessMetricMask.MemoryUsage, 0, 31)),
            now.AddSeconds(1),
            Schedule(now.AddSeconds(1), stateRevision: 2));

        var composed = state.ReadLatest(
            FactRequest(
                SchedulingProcessMetricMask.CpuUsage
                | SchedulingProcessMetricMask.MemoryUsage,
                MemoryDependency(now.AddSeconds(1), 2)),
            now.AddSeconds(2));

        var snapshot = Assert.IsType<SchedulingProcessFactSnapshot>(composed);
        var process = Assert.Single(snapshot.Processes);
        Assert.Equal(200UL, process.ProcessStartKey);
        Assert.Equal(SchedulingProcessMetricMask.MemoryUsage, process.ValidMetricMask);
        Assert.DoesNotContain(
            snapshot.Processes,
            candidate => candidate.ProcessStartKey == 100);
    }

    [Fact]
    public void InventoryRefreshDoesNotRebadgeAnOlderCurrentCpuDataset()
    {
        var now = DateTimeOffset.UtcNow;
        var state = new SchedulingProcessFactSnapshotState();
        state.ApplyScheduled(
            Request(SchedulingProcessMetricMask.CpuUsage),
            Sample(
                now,
                generation: 11,
                SchedulingProcessMetricMask.CpuUsage,
                Fact(42, 100, SchedulingProcessMetricMask.CpuUsage, 23, 0)),
            now,
            Schedule(now));
        state.ApplyScheduled(
            Request(SchedulingProcessMetricMask.MemoryUsage),
            Sample(
                now.AddSeconds(1),
                generation: 12,
                SchedulingProcessMetricMask.MemoryUsage,
                Fact(42, 100, SchedulingProcessMetricMask.MemoryUsage, 0, 31)),
            now.AddSeconds(1),
            Schedule(now.AddSeconds(1), stateRevision: 2));

        var composed = Assert.IsType<SchedulingProcessFactSnapshot>(
            state.ReadLatest(
                FactRequest(
                    SchedulingProcessMetricMask.CpuUsage
                    | SchedulingProcessMetricMask.MemoryUsage,
                    MemoryDependency(now.AddSeconds(1), 12)),
                now.AddSeconds(2)));

        Assert.Equal(12UL, composed.Generation);
        Assert.Equal(12UL, Assert.Single(composed.Processes).SourceGeneration);
        var cpu = composed.DatasetObservations[
            SchedulingProcessMetricMask.CpuUsage];
        var memory = composed.DatasetObservations[
            SchedulingProcessMetricMask.MemoryUsage];
        Assert.Equal(11UL, cpu.SourceGeneration);
        Assert.Equal(11UL, cpu.InventoryGeneration);
        Assert.Equal(12UL, memory.SourceGeneration);
        Assert.Equal(12UL, memory.InventoryGeneration);
        Assert.True(composed.IsCpuCurrentComplete());
        Assert.True(composed.IsMemoryCurrentComplete(
            MemoryDependency(now.AddSeconds(1), 12)));
    }

    [Fact]
    public void CpuPublicationCannotRefreshInventoryOrAttributionFoundation()
    {
        var now = DateTimeOffset.UtcNow;
        var state = new SchedulingProcessFactSnapshotState();
        state.ApplyScheduled(
            Request(
                SchedulingProcessMetricMask.None,
                SamplingDatasetIds.ProcessInventory),
            Sample(
                now,
                generation: 10,
                SchedulingProcessMetricMask.None,
                Fact(42, 100, SchedulingProcessMetricMask.None, 0, 0)),
            now,
            Schedule(now));
        state.ApplyScheduled(
            Request(
                SchedulingProcessMetricMask.None,
                SamplingDatasetIds.ProcessAttribution),
            Sample(
                now.AddMilliseconds(1),
                generation: 20,
                SchedulingProcessMetricMask.None,
                Fact(42, 100, SchedulingProcessMetricMask.None, 0, 0)),
            now.AddMilliseconds(1),
            Schedule(now.AddMilliseconds(1), stateRevision: 2));

        var changedAttribution = Fact(
            42,
            100,
            SchedulingProcessMetricMask.CpuUsage,
            37,
            0) with
        {
            SoftwareId = "must-not-publish",
            SoftwareName = "Must Not Publish",
            BaseScore = 99
        };
        state.ApplyScheduled(
            Request(
                SchedulingProcessMetricMask.CpuUsage,
                SamplingDatasetIds.ProcessCpuUsage),
            Sample(
                now.AddMilliseconds(2),
                generation: 30,
                SchedulingProcessMetricMask.CpuUsage,
                changedAttribution),
            now.AddMilliseconds(2),
            Schedule(now.AddMilliseconds(2), stateRevision: 3));

        var composed = Assert.IsType<SchedulingProcessFactSnapshot>(
            state.ReadLatest(
                FactRequest(SchedulingProcessMetricMask.CpuUsage),
                now.AddSeconds(1)));
        var process = Assert.Single(composed.Processes);
        Assert.Equal("software", process.SoftwareId);
        Assert.Equal(1, process.BaseScore);
        Assert.Equal(37, process.CpuUsagePercent);
        Assert.Equal(
            10UL,
            composed.FoundationDatasetObservations[
                SamplingDatasetIds.ProcessInventory].SourceGeneration);
        Assert.Equal(
            20UL,
            composed.FoundationDatasetObservations[
                SamplingDatasetIds.ProcessAttribution].SourceGeneration);
        Assert.Equal(
            30UL,
            composed.DatasetObservations[
                SchedulingProcessMetricMask.CpuUsage].SourceGeneration);
    }

    [Fact]
    public void MissingAttributionOmitsOnlyThatProcessFromCpuCandidates()
    {
        var now = DateTimeOffset.UtcNow;
        var state = new SchedulingProcessFactSnapshotState();
        state.ApplyScheduled(
            Request(
                SchedulingProcessMetricMask.None,
                SamplingDatasetIds.ProcessInventory),
            Sample(
                now,
                generation: 10,
                SchedulingProcessMetricMask.None,
                Fact(41, 100, SchedulingProcessMetricMask.None, 0, 0),
                Fact(42, 200, SchedulingProcessMetricMask.None, 0, 0)),
            now,
            Schedule(now));
        state.ApplyScheduled(
            Request(
                SchedulingProcessMetricMask.None,
                SamplingDatasetIds.ProcessAttribution),
            Sample(
                now.AddMilliseconds(1),
                generation: 20,
                SchedulingProcessMetricMask.None,
                Fact(42, 200, SchedulingProcessMetricMask.None, 0, 0)),
            now.AddMilliseconds(1),
            Schedule(now.AddMilliseconds(1), stateRevision: 2));
        state.ApplyScheduled(
            Request(
                SchedulingProcessMetricMask.CpuUsage,
                SamplingDatasetIds.ProcessCpuUsage),
            Sample(
                now.AddMilliseconds(2),
                generation: 30,
                SchedulingProcessMetricMask.CpuUsage,
                Fact(41, 100, SchedulingProcessMetricMask.CpuUsage, 80, 0),
                Fact(42, 200, SchedulingProcessMetricMask.CpuUsage, 25, 0)),
            now.AddMilliseconds(2),
            Schedule(now.AddMilliseconds(2), stateRevision: 3));

        var composed = Assert.IsType<SchedulingProcessFactSnapshot>(
            state.ReadLatest(
                FactRequest(SchedulingProcessMetricMask.CpuUsage),
                now.AddSeconds(1)));

        var process = Assert.Single(composed.Processes);
        Assert.Equal(42, process.ProcessId);
        Assert.Equal(25, process.CpuUsagePercent);
        Assert.True(composed.IsCpuCurrentComplete());
    }

    [Fact]
    public void MemoryDependencyMismatchRevokesOnlyTheMemoryDomain()
    {
        var now = DateTimeOffset.UtcNow;
        var state = new SchedulingProcessFactSnapshotState();
        state.ApplyScheduled(
            Request(SchedulingProcessMetricMask.CpuUsage),
            Sample(
                now,
                generation: 1,
                SchedulingProcessMetricMask.CpuUsage,
                Fact(42, 100, SchedulingProcessMetricMask.CpuUsage, 23, 0)),
            now,
            Schedule(now));
        state.ApplyScheduled(
            Request(SchedulingProcessMetricMask.MemoryUsage),
            Sample(
                now.AddSeconds(1),
                generation: 2,
                SchedulingProcessMetricMask.MemoryUsage,
                Fact(42, 100, SchedulingProcessMetricMask.MemoryUsage, 0, 31)),
            now.AddSeconds(1),
            Schedule(now.AddSeconds(1), stateRevision: 2));

        var mismatched = Assert.IsType<SchedulingProcessFactSnapshot>(
            state.ReadLatest(
                FactRequest(
                    SchedulingProcessMetricMask.CpuUsage
                    | SchedulingProcessMetricMask.MemoryUsage,
                    MemoryDependency(now.AddSeconds(1), 3)),
                now.AddSeconds(2)));

        Assert.Equal(
            SchedulingProcessMetricMask.CpuUsage,
            mismatched.CurrentMetricMask);
        Assert.True(mismatched.IsCpuCurrentComplete());
        Assert.False(mismatched.IsMemoryCurrentComplete());
        var memory = mismatched.DatasetObservations[
            SchedulingProcessMetricMask.MemoryUsage];
        Assert.Equal(SamplingObservationStatus.Unavailable, memory.Status);
        Assert.Equal(
            "scheduling-process-memory-dependency-mismatch",
            memory.FailureCode);
        Assert.Equal(
            MemoryDependency(now.AddSeconds(1), 2),
            memory.MemoryUsageDependency);
    }

    [Fact]
    public void CurrentDatasetDoesNotExpireBeforeItsNextPublishedValue()
    {
        var now = DateTimeOffset.UtcNow;
        var state = new SchedulingProcessFactSnapshotState();
        state.ApplyScheduled(
            Request(SchedulingProcessMetricMask.CpuUsage),
            Sample(
                now,
                generation: 1,
                SchedulingProcessMetricMask.CpuUsage,
                Fact(42, 100, SchedulingProcessMetricMask.CpuUsage, 23, 0)),
            now,
            Schedule(now));

        var expired = state.ReadLatest(
            FactRequest(SchedulingProcessMetricMask.CpuUsage),
            now + RefreshInterval + FreshnessGrace + TimeSpan.FromTicks(1));

        var current = Assert.IsType<SchedulingProcessFactSnapshot>(expired);
        Assert.True(current.IsCpuCurrentComplete());
        Assert.Equal(23, Assert.Single(current.Processes).CpuUsagePercent);
    }

    [Fact]
    public void OwnerReplacementDoesNotClearTheLastPublishedCurrentValue()
    {
        var now = DateTimeOffset.UtcNow;
        var state = new SchedulingProcessFactSnapshotState();
        state.ApplyScheduled(
            Request(SchedulingProcessMetricMask.CpuUsage),
            Sample(
                now,
                generation: 1,
                SchedulingProcessMetricMask.CpuUsage,
                Fact(42, 100, SchedulingProcessMetricMask.CpuUsage, 23, 0)),
            now,
            Schedule(now));

        state.AcceptSchedule(Schedule(
            now.AddSeconds(1),
            stateRevision: 1,
            workspaceIncarnation: 2));

        var current = Assert.IsType<SchedulingProcessFactSnapshot>(
            state.ReadLatest(
                FactRequest(SchedulingProcessMetricMask.CpuUsage),
                now.AddSeconds(1)));
        Assert.True(current.IsCpuCurrentComplete());
        Assert.Equal(23, Assert.Single(current.Processes).CpuUsagePercent);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResidencyBelongsOnlyToMemoryPublication(bool hasDedicatedCapacity)
    {
        var now = DateTimeOffset.UtcNow;
        var state = new SchedulingProcessFactSnapshotState();
        var usage = SchedulingProcessMetricMask.GpuUsage;
        var memory = SchedulingProcessMetricMask.GpuDedicatedMemory;
        var gpu = new SchedulingProcessGpuFact(1, 23, usage, 0, 0, 1, 0, 9, 0)
        {
            PrivateMemoryBytes = 999,
            SharedMemoryBytes = 999
        };
        void Publish(SchedulingProcessMetricMask metric, ulong generation, params SchedulingProcessGpuFact[] values)
        {
            var sample = Sample(now, generation, metric, Fact(42, 100, metric, 0, 0) with { Gpus = values });
            sample = sample with
            {
                DatasetObservations = new Dictionary<SchedulingProcessMetricMask, SchedulingProcessDatasetObservation>
                {
                    [metric] = SchedulingProcessDatasetObservation.CreateCurrent(metric, generation,
                        now.UtcTicks, generation, now.UtcTicks, topologyGeneration: 9, topologyFingerprint: 17)
                }
            };
            state.ApplyScheduled(Request(metric), sample, now, Schedule(now));
        }
        SchedulingProcessFactSnapshot Read() => Assert.IsType<SchedulingProcessFactSnapshot>(
            state.ReadLatest(FactRequest(usage | memory), now));
        Publish(usage, 1, gpu);
        Assert.Null(Assert.Single(Assert.Single(Read().Processes).Gpus).ResidentMemoryBytes);

        var memoryGpu = gpu with
        {
            ValidMetricMask = hasDedicatedCapacity ? memory : SchedulingProcessMetricMask.None,
            UsageSourceGeneration = 0,
            UsageTopologyGeneration = 0,
            DedicatedMemorySourceGeneration = 2,
            DedicatedMemoryTopologyGeneration = 9,
            PrivateMemoryBytes = 10,
            SharedMemoryBytes = 20
        };
        Publish(memory, 2, memoryGpu);
        Assert.Equal(30, Assert.Single(Assert.Single(Read().Processes).Gpus).ResidentMemoryBytes);
        Publish(memory, 3, memoryGpu with { DedicatedMemorySourceGeneration = 3, PrivateMemoryBytes = 40 });
        var updated = Read();
        Assert.Equal(60, Assert.Single(Assert.Single(updated.Processes).Gpus).ResidentMemoryBytes);
        var inventory = new SchedulingGpuInventorySnapshot(SamplingObservationStatus.Current, 9, now.UtcTicks,
            1, 0, 0, 17,
            [new SchedulingGpuAdapterObservation(1, 23,
                hasDedicatedCapacity ? SchedulingGpuCapabilityMask.DedicatedMemory : SchedulingGpuCapabilityMask.None,
                SchedulingGpuMetricMask.Usage | (hasDedicatedCapacity
                    ? SchedulingGpuMetricMask.TotalDedicatedMemory | SchedulingGpuMetricMask.UsedDedicatedMemory
                    : SchedulingGpuMetricMask.None), SamplingObservationStatus.Current,
                hasDedicatedCapacity ? SamplingObservationStatus.Current : SamplingObservationStatus.Unavailable,
                0, 0, hasDedicatedCapacity ? 8192UL : 0UL, 9, now.UtcTicks)]);
        Assert.True(updated.IsGpuDedicatedMemoryCurrentComplete(inventory));

        Publish(memory, 4);
        var cleared = Assert.Single(Assert.Single(Read().Processes).Gpus);
        Assert.Null(cleared.ResidentMemoryBytes);
        Assert.Equal(0UL, cleared.DedicatedMemorySourceGeneration);
        Publish(usage, 5, gpu with { UsageSourceGeneration = 5 });
        Assert.Null(Assert.Single(Assert.Single(Read().Processes).Gpus).ResidentMemoryBytes);
    }

    private static ResourceBreakdownSampleRequest Request(
        SchedulingProcessMetricMask metric,
        params string[] publicationDatasetIds)
        => new(
            [],
            new Dictionary<string, string>(),
            ProcessSampleDetailLevel.SmartSchedulingLite,
            metric)
        {
            PublicationDatasetIds = publicationDatasetIds,
            DatasetRefreshIntervals = new Dictionary<string, TimeSpan>(
                StringComparer.OrdinalIgnoreCase)
            {
                [metric == SchedulingProcessMetricMask.None
                    ? publicationDatasetIds.Single()
                    : DatasetId(metric)] = RefreshInterval
            }
        };

    private static SchedulingProcessFactRequest FactRequest(
        SchedulingProcessMetricMask metrics,
        SystemMemoryUsageDependency? memoryUsageDependency = null)
        => new(
            metrics,
            metrics.HasFlag(SchedulingProcessMetricMask.MemoryUsage)
                ? memoryUsageDependency
                : null,
            new SchedulingGpuInventorySnapshot(
                SamplingObservationStatus.NotRequested,
                0,
                0,
                0,
                0,
                0,
                0,
                []));

    private static SchedulingProcessFactSnapshot Sample(
        DateTimeOffset observedAt,
        ulong generation,
        SchedulingProcessMetricMask current,
        params SchedulingProcessFact[] facts)
    {
        var currentFacts = facts
            .Select(fact => fact with { SourceGeneration = generation })
            .ToArray();
        return new SchedulingProcessFactSnapshot(
            SamplingObservationStatus.Current,
            generation,
            observedAt.UtcTicks,
            checked((uint)currentFacts.Length),
            checked((uint)currentFacts.Length),
            0,
            0,
            current,
            current,
            currentFacts)
        {
            DatasetObservations = current == SchedulingProcessMetricMask.None
                ? new Dictionary<
                    SchedulingProcessMetricMask,
                    SchedulingProcessDatasetObservation>()
                : new Dictionary<
                    SchedulingProcessMetricMask,
                    SchedulingProcessDatasetObservation>
                {
                    [current] = SchedulingProcessDatasetObservation.CreateCurrent(
                        current,
                        generation,
                        observedAt.UtcTicks,
                        generation,
                        observedAt.UtcTicks,
                        memoryUsageDependency:
                            current == SchedulingProcessMetricMask.MemoryUsage
                                ? MemoryDependency(observedAt, generation)
                                : null)
                }
        };
    }

    private static SystemMemoryUsageDependency MemoryDependency(
        DateTimeOffset observedAt,
        ulong generation)
        => TestSystemMemoryUsageDependency.Create(
            sourceGeneration: generation,
            observedAtUtcTicks: observedAt.UtcTicks,
            committedGeneration: generation);

    private static SchedulingProcessFact Fact(
        int processId,
        ulong startKey,
        SchedulingProcessMetricMask metric,
        double cpu,
        double memory)
        => new(
            processId,
            startKey,
            "process",
            null,
            "software",
            "Software",
            "Other",
            "Other",
            1,
            metric,
            cpu,
            memory,
            1,
            []);

    private static NativeItemSamplingSubscriptionScheduleReceipt Schedule(
        DateTimeOffset commandAt,
        ulong stateRevision = 1,
        ulong workspaceIncarnation = 1)
        => new(
            NativeItemSamplingSubscriptionScheduleOrigin.Completion,
            workspaceIncarnation,
            1,
            stateRevision,
            stateRevision,
            commandAt,
            RefreshInterval,
            FreshnessGrace,
            commandAt + RefreshInterval,
            null,
            1,
            1,
            0,
            0);

    private static string DatasetId(SchedulingProcessMetricMask metric)
        => metric switch
        {
            SchedulingProcessMetricMask.CpuUsage =>
                SamplingDatasetIds.ProcessCpuUsage,
            SchedulingProcessMetricMask.MemoryUsage =>
                SamplingDatasetIds.ProcessMemoryUsage,
            SchedulingProcessMetricMask.GpuUsage => SamplingDatasetIds.ProcessGpuUsage,
            SchedulingProcessMetricMask.GpuDedicatedMemory => SamplingDatasetIds.ProcessGpuVram,
            _ => throw new ArgumentOutOfRangeException(nameof(metric))
        };
}
