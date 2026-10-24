using System.Collections.Immutable;
using System.Reflection;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerSmartCoordinatorScoreOnlyCompositionTests
{
    [Theory]
    [InlineData("process-count")]
    [InlineData("start-key")]
    [InlineData("target-id")]
    public async Task NativeProjectionSkipsMalformedTargetWithoutDroppingHealthyTarget(string mismatch)
    {
        const int malformedPid = 42_821;
        const int healthyPid = 42_822;
        const ulong startKey = 133_900_000_000_000_001;
        var facts = CreateCompleteProcessFacts((malformedPid, startKey, "software:malformed-target", 20, 25, 10),
            (healthyPid, startKey + 1, "software:healthy-target", 20, 25, 10));
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true,
            processFactsSnapshot: facts, automaticMemoryCleanupEnabled: false,
            cpuCoreReader: new RecordingCpuCoreResidencyReader(() => null));
        await fixture.Coordinator.RunOnceAsync(CancellationToken.None);
        var capture = (Task)typeof(HostManagerSmartCoordinator).GetMethod("CaptureHostManagerSampleAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(fixture.Coordinator,
            [fixture.RuntimePlan, CancellationToken.None])!;
        await capture;
        var captured = capture.GetType().GetProperty("Result")!.GetValue(capture)!;
        var sample = captured.GetType().GetProperty("Sample")!.GetValue(captured)!;
        var targets = (System.Collections.IEnumerable)sample.GetType().GetProperty("Targets")!.GetValue(sample)!;
        var target = targets.Cast<object>().Single(value =>
            ((IReadOnlyList<int>)value.GetType().GetProperty("ProcessIds")!.GetValue(value)!).Contains(malformedPid));
        if (mismatch == "process-count")
            target.GetType().GetProperty("ProcessIds")!.SetValue(target, new[] { malformedPid, malformedPid + 10 });
        else if (mismatch == "start-key")
            target.GetType().GetProperty("ProcessStartKeys")!.SetValue(target, new Dictionary<int, ulong>());
        else
            target.GetType().GetProperty("TargetId")!.SetValue(target, "mismatched-target-id");
        var probe = new NativeSmartInputCaptureProbe();
        fixture.Coordinator.TransitionProbe = probe;
        var ownership = new NativeAppliedOwnershipFactIndex(0, new Dictionary<NativeAppliedOwnershipProcessIdentity, NativeAppliedOwnershipRecord>(),
            new Dictionary<NativeAppliedOwnershipProcessIdentity, NativeAppliedOwnershipRecord>(),
            new Dictionary<ulong, NativeAppliedOwnershipRecord>(),
            new Dictionary<NativeAppliedOwnershipProcessIncarnation, NativeAppliedOwnershipProcessAnchor>());
        object projection;
        using (BeginNativeFactProjection(fixture, sample, ownership))
        {
            projection = typeof(HostManagerSmartCoordinator).GetMethod("ProjectNativeFacts", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(fixture.Coordinator, [fixture.Workspace, sample, ownership, new Dictionary<string, int>(),
                    fixture.RuntimePlan.OptimizationMode, false, false, fixture.Coordinator.SchedulingAuthority.Compute])!;
        }
        Assert.DoesNotContain(probe.Rows, row => row.ProcessId == malformedPid);
        Assert.True(Assert.Single(probe.Rows, row => row.ProcessId == healthyPid
            && row.MetricKind == NativeSmartCoordinatorMetricKind.CpuUsagePercent)
            .ValidMask.HasFlag(NativeSmartCoordinatorInputValidity.ProcessScore));
        Assert.False((bool)projection.GetType().GetProperty("FullProcessSnapshot")!.GetValue(projection)!);
        var exclusions = Assert.IsType<List<SchedulingFactExclusion>>(ReadPrivateReference(fixture.Coordinator, "cycleFactExclusions"));
        Assert.Contains(exclusions, exclusion => exclusion.Fact == "identity" && exclusion.Reason.StartsWith("target-process-", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("base-score")]
    [InlineData("process-name")]
    [InlineData("software-id")]
    [InlineData("joined-facts")]
    public async Task NativeProjectionMissingMetadataPreservesLiveIdentityAndHealthyScores(string missingFact)
    {
        const int missingPid = 42_811;
        const int healthyPid = 42_812;
        const ulong startKey = 133_900_000_000_000_001;
        const string software = "software:metadata-healthy";
        var facts = CreateCompleteProcessFacts((missingPid, startKey, software, 20, 25, 10),
            (healthyPid, startKey + 1, software, 20, 25, 10));
        var inventoryProcesses = facts.Processes;
        facts = facts with
        {
            Processes = [missingFact switch
            {
                "base-score" => facts.Processes[0] with { BaseScore = double.NaN },
                "process-name" => facts.Processes[0] with { ProcessName = "" },
                "software-id" => facts.Processes[0] with { SoftwareId = null! },
                _ => facts.Processes[0]
            }, facts.Processes[1]]
        };
        if (missingFact == "joined-facts")
        {
            facts = facts with
            {
                HasSeparateFoundationPayloads = true,
                InventoryProcesses = inventoryProcesses,
                Processes = [facts.Processes[1]],
                EmittedCount = 1,
                SkippedCount = 1
            };
        }
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true,
            processFactsSnapshot: facts, automaticMemoryCleanupEnabled: false,
            cpuCoreReader: new RecordingCpuCoreResidencyReader(() => null));
        await fixture.Coordinator.RunOnceAsync(CancellationToken.None);
        var compute = Assert.IsType<HostManagerComputeScoringCycleResult>(fixture.Coordinator.SchedulingAuthority.Compute);
        await fixture.SeedProcessOwnershipAsync(missingPid, startKey, software, memoryPolicy: false, actionId: 28_001);
        var ownershipSnapshot = await fixture.NativeTransactions.AppliedOwnership.ReadSnapshotAsync(CancellationToken.None);
        var ownership = typeof(HostManagerSmartCoordinator).GetMethod("CreateAppliedOwnershipFactIndex",
            BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [ownershipSnapshot])!;
        var capture = (Task)typeof(HostManagerSmartCoordinator).GetMethod("CaptureHostManagerSampleAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(fixture.Coordinator,
            [fixture.RuntimePlan, CancellationToken.None])!;
        await capture;
        var captured = capture.GetType().GetProperty("Result")!.GetValue(capture)!;
        var sample = captured.GetType().GetProperty("Sample")!.GetValue(captured)!;
        var observedFacts = Assert.IsType<SchedulingProcessFactSnapshot>(sample.GetType().GetProperty("ProcessFacts")!.GetValue(sample));
        Assert.Equal(missingFact == "joined-facts" ? 1 : 2, observedFacts.Processes.Count);
        Assert.Equal(2, observedFacts.CurrentInventoryProcesses.Count);
        Assert.True(observedFacts.IsInventoryCurrentComplete());
        Assert.Empty(HostManagerSmartCoordinator.SelectMissingProcessOwnership(observedFacts, ownershipSnapshot));
        var probe = new NativeSmartInputCaptureProbe();
        fixture.Coordinator.TransitionProbe = probe;
        object projection;
        using (BeginNativeFactProjection(fixture, sample, ownership))
        {
            projection = typeof(HostManagerSmartCoordinator).GetMethod("ProjectNativeFacts", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(fixture.Coordinator, [fixture.Workspace, sample, ownership, new Dictionary<string, int>(),
                    fixture.RuntimePlan.OptimizationMode, false, false, compute])!;
        }
        Assert.False((bool)projection.GetType().GetProperty("FullProcessSnapshot")!.GetValue(projection)!);
        Assert.True(Assert.Single(probe.Rows, row => row.ProcessId == healthyPid
            && row.MetricKind == NativeSmartCoordinatorMetricKind.CpuUsagePercent)
            .ValidMask.HasFlag(NativeSmartCoordinatorInputValidity.ProcessScore));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task NativeProjectionExcludesOnlyIncompleteSoftwareAndDomain(bool gpuDomain, bool removeProcessScore)
    {
        const int affectedPid = 42_801;
        const int healthyPid = 42_802;
        const ulong startKey = 133_900_000_000_000_001;
        var inventory = new WindowsGpuAdapterInventoryRead(SamplingObservationStatus.Current, 1,
            DateTimeOffset.UtcNow.UtcTicks, 1, 0, 0, 789,
            [new(0, "Projection GPU", 0x10de, 1, 0, new AdapterLuid { LowPart = 123 }, false,
                8UL << 30, WindowsGpuAdapterKind.Dedicated)]);
        var observations = new[] { affectedPid, healthyPid }.Select(pid =>
            CreateNativeGpuCycleFacts(new(pid, startKey, $"projection-{pid}", $@"C:\fixture\projection-{pid}.exe"),
                $"software:projection-{pid}", inventory, 0, includeDedicatedMemory: true)).ToArray();
        var cpuMask = SchedulingProcessMetricMask.CpuUsage;
        var datasets = observations[0].DatasetObservations.ToDictionary();
        datasets[cpuMask] = CreateCurrentDatasetObservations(cpuMask, 1, inventory.ObservedAtUtcTicks)[cpuMask];
        var facts = observations[0] with
        {
            EnumeratedCount = 2,
            EmittedCount = 2,
            RequestedMetricMask = observations[0].RequestedMetricMask | cpuMask,
            CurrentMetricMask = observations[0].CurrentMetricMask | cpuMask,
            DatasetObservations = datasets,
            Processes = observations.SelectMany(snapshot => snapshot.Processes).Select(process => process with
            {
                ValidMetricMask = process.ValidMetricMask | cpuMask,
                CpuUsagePercent = 25,
                RuntimeState = HostManagerRuntimeStates.BackgroundProcess
            }).ToArray(),
            SoftwareBaseScores = [.. observations.SelectMany(snapshot => snapshot.SoftwareBaseScores)]
        };
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true,
            processFactsSnapshot: facts, automaticMemoryCleanupEnabled: false,
            cpuCoreReader: new RecordingCpuCoreResidencyReader(() => null));
        fixture.MetricSampler.SetSnapshot(CreateNativeGpuCycleHardware(inventory, 123, includeDedicatedMemory: true));
        await fixture.Coordinator.RunOnceAsync(CancellationToken.None);
        var compute = Assert.IsType<HostManagerComputeScoringCycleResult>(fixture.Coordinator.SchedulingAuthority.Compute);
        var affectedSoftware = NativeStableIdentity.CreateCaseInsensitiveKey($"software:projection-{affectedPid}");
        var domain = Assert.IsType<HostManagerComputeScoreDomainSnapshot>(gpuDomain ? compute.Gpu : compute.Cpu);
        var changed = domain with
        {
            Scores = domain.Scores
                .Where(score => !removeProcessScore || score.ProcessId != affectedPid)
                .Select(score => !removeProcessScore && score.SoftwareKey == affectedSoftware && score.ProcessId == 0
                    ? score with { MemberCount = score.MemberCount + 1 }
                    : score).ToImmutableArray()
        };
        compute = gpuDomain ? compute with { Gpu = changed } : compute with { Cpu = changed };

        var capture = (Task)typeof(HostManagerSmartCoordinator).GetMethod("CaptureHostManagerSampleAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(fixture.Coordinator,
            [fixture.RuntimePlan, CancellationToken.None])!;
        await capture;
        var captured = capture.GetType().GetProperty("Result")!.GetValue(capture)!;
        var sample = captured.GetType().GetProperty("Sample")!.GetValue(captured)!;
        var probe = new NativeSmartInputCaptureProbe();
        fixture.Coordinator.TransitionProbe = probe;
        var ownership = new NativeAppliedOwnershipFactIndex(0, new Dictionary<NativeAppliedOwnershipProcessIdentity, NativeAppliedOwnershipRecord>(),
            new Dictionary<NativeAppliedOwnershipProcessIdentity, NativeAppliedOwnershipRecord>(),
            new Dictionary<ulong, NativeAppliedOwnershipRecord>(),
            new Dictionary<NativeAppliedOwnershipProcessIncarnation, NativeAppliedOwnershipProcessAnchor>());
        using (BeginNativeFactProjection(fixture, sample, ownership))
        {
            typeof(HostManagerSmartCoordinator).GetMethod("ProjectNativeFacts", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(fixture.Coordinator, [fixture.Workspace, sample, ownership, new Dictionary<string, int>(),
                    fixture.RuntimePlan.OptimizationMode, false, false, compute]);
        }

        var excludedKind = gpuDomain ? NativeSmartCoordinatorMetricKind.GpuUsagePercent : NativeSmartCoordinatorMetricKind.CpuUsagePercent;
        var independentKind = gpuDomain ? NativeSmartCoordinatorMetricKind.CpuUsagePercent : NativeSmartCoordinatorMetricKind.GpuUsagePercent;
        var affected = Assert.Single(probe.Rows, row => row.ProcessId == affectedPid && row.MetricKind == excludedKind);
        Assert.True(affected.ValidMask.HasFlag(NativeSmartCoordinatorInputValidity.Metric));
        Assert.Equal(!removeProcessScore, affected.ValidMask.HasFlag(NativeSmartCoordinatorInputValidity.ProcessScore));
        Assert.False(affected.ValidMask.HasFlag(NativeSmartCoordinatorInputValidity.SoftwareScore));
        Assert.Equal(0U, affected.ScoreMemberCount);
        Assert.True(Assert.Single(probe.Rows, row => row.ProcessId == affectedPid && row.MetricKind == independentKind)
            .ValidMask.HasFlag(NativeSmartCoordinatorInputValidity.ProcessScore));
        Assert.True(Assert.Single(probe.Rows, row => row.ProcessId == healthyPid && row.MetricKind == excludedKind)
            .ValidMask.HasFlag(NativeSmartCoordinatorInputValidity.ProcessScore));
        Assert.True(Assert.Single(probe.Rows, row => row.ProcessId == affectedPid && row.MetricKind == NativeSmartCoordinatorMetricKind.VramUsagePercent)
            .ValidMask.HasFlag(NativeSmartCoordinatorInputValidity.Metric));
        var exclusions = Assert.IsType<List<SchedulingFactExclusion>>(ReadPrivateReference(fixture.Coordinator, "cycleFactExclusions"));
        Assert.Contains(exclusions, exclusion => exclusion.Fact == (gpuDomain ? "gpu-score" : "cpu-score")
            && exclusion.Reason.StartsWith("canonical-software-membership-incomplete", StringComparison.Ordinal));
    }

    private static NativeSmartCoordinatorWorkspace.Operation BeginNativeFactProjection(
        ScoreOnlyCoordinatorFixture fixture, object sample, object ownership)
    {
        var rows = (uint)typeof(HostManagerSmartCoordinator)
            .GetMethod("CountNativeFactRows", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [sample, ownership])!;
        fixture.Workspace.EnsureRows(Math.Min(rows, fixture.Workspace.Capacity.InputRowCapacity));
        return fixture.Workspace.BeginOperation();
    }
}
