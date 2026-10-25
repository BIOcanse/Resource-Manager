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
    public void BadDecisionIdentityDoesNotInvalidateTheIndependentInventory()
    {
        var sample = Sample(DateTimeOffset.UtcNow, 1, SchedulingProcessMetricMask.CpuUsage,
            Fact(42, 100, SchedulingProcessMetricMask.CpuUsage, 17, 0),
            Fact(43, 101, SchedulingProcessMetricMask.CpuUsage, 18, 0));
        sample = sample with { HasSeparateFoundationPayloads = true,
            InventoryProcesses = sample.Processes,
            Processes = [sample.Processes[0] with { ProcessStartKey = 0 }, sample.Processes[1]] };
        var projected = sample.ProjectValidFacts(FactRequest(SchedulingProcessMetricMask.CpuUsage).GpuInventory);
        Assert.True(projected.IsInventoryCurrentComplete());
        Assert.Equal(2, projected.CurrentInventoryProcesses.Count);
        Assert.Single(projected.Processes);
        Assert.Equal(1U, projected.EmittedCount);
        Assert.Equal(1U, projected.SkippedCount);
        Assert.Contains(projected.FactExclusions, e => e.ProcessId == 42 && e.Fact == "identity");
    }

    [Fact]
    public void InventoryPublicationPreservesUnidentifiedLiveAbsenceAndReplacesItOnRecovery()
    {
        var now = DateTimeOffset.UtcNow;
        var state = new SchedulingProcessFactSnapshotState();
        var sample = Sample(now, 1, SchedulingProcessMetricMask.CpuUsage,
            Fact(42, 100, SchedulingProcessMetricMask.CpuUsage, 17, 0)) with
        { HasUnidentifiedProcesses = true, EnumeratedCount = 2, ExcludedCount = 1 };
        state.ApplyScheduled(Request(SchedulingProcessMetricMask.CpuUsage), sample, now, Schedule(now));
        var current = state.ReadLatest(FactRequest(SchedulingProcessMetricMask.CpuUsage), now)!;
        Assert.True(current.HasUnidentifiedProcesses);
        Assert.False(current.IsInventoryCurrentComplete());
        Assert.Contains(current.ProjectValidFacts(FactRequest(SchedulingProcessMetricMask.CpuUsage).GpuInventory).FactExclusions,
            e => e.Fact == "inventory" && e.Reason == "unidentified-live-process");
        state.ApplyScheduled(Request(SchedulingProcessMetricMask.CpuUsage),
            sample with { HasUnidentifiedProcesses = false }, now, Schedule(now));
        Assert.True(state.ReadLatest(FactRequest(SchedulingProcessMetricMask.CpuUsage), now)!.IsInventoryCurrentComplete());
    }

    [Theory]
    [InlineData(false, "game")]
    [InlineData(true, "game")]
    [InlineData(false, "runtime_package")]
    [InlineData(true, "runtime_package")]
    public void SoftwareCategoryAndSourceDoNotDependOnProcessEnumerationOrder(bool reverse, string kind)
    {
        var now = DateTimeOffset.UtcNow;
        var state = new SchedulingProcessFactSnapshotState();
        var game = Fact(42, 100, SchedulingProcessMetricMask.None, 0, 0) with
        {
            SoftwareKind = kind, SoftwareDisplayKind = kind == "game" ? "game" : "other",
            SoftwareCategorySource = kind == "game" ? @"Windows GameConfigStore\Children\MatchedExeFullPath" : null,
            SoftwareRootPaths = [@"C:\Apps\Game"]
        };
        var sibling = game with { ProcessId = 43, ProcessStartKey = 101,
            SoftwareKind = "runtime_product", SoftwareDisplayKind = "other", SoftwareCategorySource = null };
        var sample = Sample(now, 1, SchedulingProcessMetricMask.None,
            reverse ? sibling : game, reverse ? game : sibling);
        state.ApplyScheduled(Request(SchedulingProcessMetricMask.None, SamplingDatasetIds.ProcessInventory), sample, now, Schedule(now));
        state.ApplyScheduled(Request(SchedulingProcessMetricMask.None, SamplingDatasetIds.ProcessAttribution), sample, now, Schedule(now));
        var software = Assert.Single(state.ReadLatestSoftware());
        Assert.Equal(game.SoftwareId, software.Id);
        Assert.Equal(game.SoftwareKind, software.Kind);
        Assert.Equal(game.SoftwareCategorySource, software.CategorySource);
        Assert.Equal(game.SoftwareRootPaths, software.RootPaths);
    }

    [Fact]
    public void RuntimeSoftwareReadsAttributionWithoutRequiringPerformanceMetrics()
    {
        var now = DateTimeOffset.UtcNow;
        var state = new SchedulingProcessFactSnapshotState();
        var fact = Fact(42, 100, SchedulingProcessMetricMask.None, 0, 0) with
        {
            SoftwareKind = "runtime_product", SoftwareRootPaths = [@"C:\Apps\Runtime"]
        };
        var sample = Sample(now, 1, SchedulingProcessMetricMask.None, fact);
        Assert.Empty(state.ReadLatestSoftware());
        state.ApplyScheduled(Request(SchedulingProcessMetricMask.None, SamplingDatasetIds.ProcessInventory), sample, now, Schedule(now));
        state.ApplyScheduled(Request(SchedulingProcessMetricMask.None, SamplingDatasetIds.ProcessAttribution), sample, now, Schedule(now));
        var software = Assert.Single(state.ReadLatestSoftware());
        Assert.Equal(fact.SoftwareName, software.Name);
        Assert.Equal(fact.SoftwareRootPaths, software.RootPaths);
        state.ApplyScheduled(Request(SchedulingProcessMetricMask.None, SamplingDatasetIds.ProcessInventory),
            Sample(now.AddSeconds(1), 2, SchedulingProcessMetricMask.None), now.AddSeconds(1), Schedule(now.AddSeconds(1), 2));
        Assert.Empty(state.ReadLatestSoftware());
    }

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
                | SchedulingProcessMetricMask.MemoryUsage),
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
                    | SchedulingProcessMetricMask.MemoryUsage),
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
        Assert.True(composed.IsMemoryCurrentComplete());
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
    public void MemoryPublicationComposesWithoutHardwareLockstep()
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
                    | SchedulingProcessMetricMask.MemoryUsage),
                now.AddSeconds(2)));

        Assert.Equal(SchedulingProcessMetricMask.CpuUsage | SchedulingProcessMetricMask.MemoryUsage,
            mismatched.CurrentMetricMask);
        Assert.True(mismatched.IsCpuCurrentComplete());
        Assert.True(mismatched.IsMemoryCurrentComplete());
        Assert.Equal(31, Assert.Single(mismatched.Processes).MemoryUsedBytes);

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
        SchedulingProcessMetricMask metrics)
        => new(
            metrics,
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
                        observedAt.UtcTicks)
                }
        };
    }

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
            "other",
            "other",
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
