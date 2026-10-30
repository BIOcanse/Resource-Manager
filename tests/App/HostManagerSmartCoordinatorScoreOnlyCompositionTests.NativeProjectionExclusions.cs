using System.Collections.Immutable;
using System.Reflection;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Endpoints;
using ResourceManager.App.Infrastructure.Security;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerSmartCoordinatorScoreOnlyCompositionTests
{
    [Fact]
    public async Task SamplingExitAndUnattributedLiveProcessDoNotVoidPeerExitEvidence()
    {
        var facts = CreateCompleteProcessFacts((42831, 101, "software:healthy", 20, 25, 10),
            (42832, 102, "software:unattributed", 20, 25, 10));
        var initial = CreateCompleteProcessFacts((42830, 100, "software:exited", 20, 25, 10),
            (42831, 101, "software:healthy", 20, 25, 10),
            (42832, 102, "software:unattributed", 20, 25, 10));
        facts = facts with
        {
            // One enumerated process exited during capture. Another live identity
            // has no attribution/decision target, but remains in the inventory.
            EnumeratedCount = 3, ExcludedCount = 1, EmittedCount = 1, SkippedCount = 1,
            HasSeparateFoundationPayloads = true, InventoryProcesses = facts.Processes,
            AttributionProcesses = [facts.Processes[0]], Processes = [facts.Processes[0]]
        };
        Assert.True(facts.IsInventoryCurrentComplete());
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true,
            processFactsSnapshot: initial, automaticMemoryCleanupEnabled: false,
            cpuCoreReader: new RecordingCpuCoreResidencyReader(() => null));
        await fixture.Coordinator.RunOnceAsync(CancellationToken.None);
        Assert.Equal(3U, fixture.Workspace.Snapshot.ProcessCount);
        fixture.ProcessFacts.SetSnapshot(facts);
        var probe = new NativeSmartInputCaptureProbe();
        fixture.Coordinator.TransitionProbe = probe;
        await fixture.Coordinator.RunOnceAsync(CancellationToken.None);
        var diagnostics = await fixture.Coordinator.GetDecisionDiagnosticsAsync(CancellationToken.None);
        Assert.DoesNotContain(diagnostics.FactExclusions, e => e.Fact == SchedulingFactCode.Inventory);
        Assert.Contains(probe.Rows, row => row.ProcessId == 42832);
        Assert.Equal(2U, fixture.Workspace.Snapshot.ProcessCount);
        Assert.DoesNotContain(fixture.Workspace.CurrentSnapshotRows.ToArray(), row => row.ProcessId == 42830);
        Assert.DoesNotContain(diagnostics.NativeCoordinator!.ReasonMask, reason => reason == "no_complete_snapshot");
    }

    [Fact]
    public async Task IncompleteInventoryChurnReportsCapacityFailureAndNextFullRoundRecovers()
    {
        var facts = CreateCompleteProcessFacts(43001, 100, "software:churn", 20, 25, 10) with { OverflowCount = 1 };
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true,
            processFactsSnapshot: facts, smartCoordinatorMaximumProcesses: 2, automaticMemoryCleanupEnabled: false,
            cpuCoreReader: new RecordingCpuCoreResidencyReader(() => null));
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.ConfigureHttpJsonOptions(options =>
            ResourceManagerEndpointRouteBuilderExtensions.ConfigureJsonSerializerOptions(options.SerializerOptions));
        builder.Services.AddSingleton<IHostManagerSmartCoordinator>(fixture.Coordinator);
        await using var app = builder.Build();
        app.MapHostManagerSmartCoordinatorEndpoints(LoopbackApiPipelineMode.StandardUser);
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var initialStatus = await client.GetFromJsonAsync<JsonElement>("/api/optimization/smart/status", timeout.Token);
        AssertClearedStatusFailure(initialStatus);
        await using var stream = fixture.Coordinator.SubscribeDecisionDiagnosticsAsync(timeout.Token).GetAsyncEnumerator();
        Assert.True(await stream.MoveNextAsync());
        for (var i = 0; i < 3; i++)
        {
            var publication = stream.MoveNextAsync().AsTask();
            fixture.ProcessFacts.SetSnapshot(CreateCompleteProcessFacts(43001 + i, (ulong)(100 + i), "software:churn", 20, 25, 10)
                with { OverflowCount = 1 });
            if (i == 2) await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Coordinator.RunOnceAsync(CancellationToken.None));
            else await fixture.Coordinator.RunOnceAsync(CancellationToken.None);
            Assert.True(await publication.WaitAsync(timeout.Token));
        }
        var diagnostics = await fixture.Coordinator.GetDecisionDiagnosticsAsync(CancellationToken.None);
        Assert.Same(diagnostics, stream.Current);
        var failure = Assert.IsType<HostManagerCycleFailureDiagnostics>(diagnostics.Failure);
        Assert.Equal(HostManagerCycleFailureKind.NativePlanFailed, failure.Kind);
        Assert.Equal(nameof(InvalidDataException), failure.ExceptionType);
        Assert.Equal(SchedulingFactExclusionReason.CapacityExceeded, failure.NativePlanStatus!.Reason);
        Assert.Null(diagnostics.NativeCoordinator);
        Assert.Empty(diagnostics.NonAdaptedMemoryDecisions);
        Assert.Equal("unavailable", diagnostics.SchedulingAuthority.Availability);
        Assert.False(diagnostics.SchedulerRunning);
        Assert.Null((await fixture.Coordinator.GetStateAsync(CancellationToken.None)).Coordinator);
        var failedStatus = await fixture.Coordinator.GetStatusAsync(CancellationToken.None);
        Assert.False(failedStatus.SchedulerRunning);
        Assert.Null(failedStatus.FaultClosedReason);
        Assert.Same(diagnostics.Failure, failedStatus.Failure);
        Assert.Same(diagnostics.FactExclusionCounts, failedStatus.FactExclusionCounts);
        Assert.Same(diagnostics.FactExclusions, failedStatus.FactExclusions);
        var failedWire = await client.GetFromJsonAsync<JsonElement>("/api/optimization/smart/status", timeout.Token);
        Assert.False(failedWire.GetProperty("schedulerRunning").GetBoolean());
        Assert.Equal("native-plan-failed", failedWire.GetProperty("failure").GetProperty("kind").GetString());
        Assert.Equal("capacity-exceeded", failedWire.GetProperty("failure").GetProperty("nativePlanStatus").GetString());
        Assert.Equal(nameof(InvalidDataException), failedWire.GetProperty("failure").GetProperty("exceptionType").GetString());
        Assert.Equal(diagnostics.FactExclusionCount, failedWire.GetProperty("factExclusionCount").GetInt32());
        var jsonOptions = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>().Value.SerializerOptions;
        var diagnosticsWire = JsonSerializer.SerializeToElement(diagnostics, jsonOptions);
        foreach (var field in new[] { "failure", "factExclusionCounts", "factExclusions" })
            Assert.Equal(diagnosticsWire.GetProperty(field).GetRawText(), failedWire.GetProperty(field).GetRawText());
        Assert.Contains(diagnostics.FactExclusionCounts, e => e.Fact == SchedulingFactCode.NativePlan && e.Reason == SchedulingFactExclusionReason.CapacityExceeded);
        Assert.Contains(diagnostics.FactExclusionCounts, e => e.Fact == SchedulingFactCode.Inventory && e.Reason == SchedulingFactExclusionReason.InventoryOverflow);
        Assert.Equal(NativeSmartCoordinatorStatus.Ok, fixture.Workspace.RefreshSnapshot());
        Assert.Equal(2U, fixture.Workspace.Snapshot.ProcessCount);
        fixture.ProcessFacts.SetSnapshot(CreateCompleteProcessFacts(43004, 104, "software:churn", 20, 25, 10));
        await fixture.Coordinator.RunOnceAsync(CancellationToken.None);
        Assert.Equal(1U, fixture.Workspace.Snapshot.ProcessCount);
        diagnostics = await fixture.Coordinator.GetDecisionDiagnosticsAsync(CancellationToken.None);
        Assert.Null(diagnostics.Failure);
        Assert.NotNull(diagnostics.NativeCoordinator);
        Assert.DoesNotContain(diagnostics.FactExclusions, e => e.Fact == SchedulingFactCode.NativePlan);
        var recoveredStatus = await fixture.Coordinator.GetStatusAsync(CancellationToken.None);
        Assert.Null(recoveredStatus.Failure);
        Assert.Equal(0, recoveredStatus.FactExclusionCount);
        Assert.Empty(recoveredStatus.FactExclusionCounts);
        Assert.Empty(recoveredStatus.FactExclusions);
        var recoveredWire = await client.GetFromJsonAsync<JsonElement>("/api/optimization/smart/status", timeout.Token);
        AssertClearedStatusFailure(recoveredWire);
    }

    private static void AssertClearedStatusFailure(JsonElement status)
    {
        Assert.Equal(JsonValueKind.Null, status.GetProperty("failure").ValueKind);
        Assert.Equal(0, status.GetProperty("factExclusionCount").GetInt32());
        Assert.Empty(status.GetProperty("factExclusionCounts").EnumerateArray());
        Assert.Empty(status.GetProperty("factExclusions").EnumerateArray());
    }

    [Fact]
    public async Task BusyInventoryWithSamplingExitsAndUnattributedPeersRemovesOnlyExitedRows()
    {
        var initial = CreateScaleProcessFacts(256);
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true,
            processFactsSnapshot: initial, automaticMemoryCleanupEnabled: false,
            cpuCoreReader: new RecordingCpuCoreResidencyReader(() => null));
        await fixture.Coordinator.RunOnceAsync(CancellationToken.None);
        Assert.Equal(256U, fixture.Workspace.Snapshot.ProcessCount);
        var current = initial with
        {
            HasSeparateFoundationPayloads = true,
            InventoryProcesses = initial.Processes.Take(240).ToArray(),
            AttributionProcesses = initial.Processes.Take(160).ToArray(),
            Processes = initial.Processes.Take(160).ToArray(),
            EmittedCount = 160, SkippedCount = 80, ExcludedCount = 16
        };
        Assert.True(current.IsInventoryCurrentComplete());
        fixture.ProcessFacts.SetSnapshot(current);
        await fixture.Coordinator.RunOnceAsync(CancellationToken.None);
        Assert.Equal(240U, fixture.Workspace.Snapshot.ProcessCount);
        var rows = fixture.Workspace.CurrentSnapshotRows.ToArray();
        Assert.All(initial.Processes.Skip(240), p => Assert.DoesNotContain(rows, row => row.ProcessId == p.ProcessId));
        Assert.All(initial.Processes.Skip(160).Take(80), p => Assert.Contains(rows, row => row.ProcessId == p.ProcessId));
    }

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
            new Dictionary<NativeAppliedOwnershipProcessIncarnation, NativeAppliedOwnershipProcessAnchor>());
        object projection;
        using (BeginNativeFactProjection(fixture, sample, ownership))
        {
            projection = typeof(HostManagerSmartCoordinator).GetMethod("ProjectNativeFacts", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(fixture.Coordinator, [fixture.Workspace, sample, ownership, OptimizationProtectionTable.Create([]),
                    fixture.RuntimePlan.OptimizationMode, false, fixture.Coordinator.SchedulingAuthority.Compute])!;
        }
        Assert.Equal(NativeSmartCoordinatorInputValidity.ProcessIdentity,
            Assert.Single(probe.Rows, row => row.ProcessId == malformedPid).ValidMask);
        Assert.True(Assert.Single(probe.Rows, row => row.ProcessId == healthyPid
            && row.MetricKind == NativeSmartCoordinatorMetricKind.CpuUsagePercent)
            .ValidMask.HasFlag(NativeSmartCoordinatorInputValidity.ProcessScore));
        Assert.True((bool)projection.GetType().GetProperty("FullProcessSnapshot")!.GetValue(projection)!);
        var exclusions = Assert.IsType<List<SchedulingFactExclusion>>(ReadPrivateReference(fixture.Coordinator, "cycleFactExclusions"));
        var expectedReason = mismatch switch
        {
            "process-count" => SchedulingFactExclusionReason.TargetProcessCountNotOne,
            "start-key" => SchedulingFactExclusionReason.TargetProcessIdentityMissing,
            _ => SchedulingFactExclusionReason.TargetProcessIdentityMismatch
        };
        var identityExclusions = exclusions.Where(exclusion => exclusion.Reason == expectedReason).ToArray();
        Assert.NotEmpty(identityExclusions);
        Assert.All(identityExclusions, exclusion => Assert.Equal(SchedulingFactCode.Identity, exclusion.Fact));
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
                .Invoke(fixture.Coordinator, [fixture.Workspace, sample, ownership, OptimizationProtectionTable.Create([]),
                    fixture.RuntimePlan.OptimizationMode, false, compute])!;
        }
        Assert.True((bool)projection.GetType().GetProperty("FullProcessSnapshot")!.GetValue(projection)!);
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
            new Dictionary<NativeAppliedOwnershipProcessIncarnation, NativeAppliedOwnershipProcessAnchor>());
        using (BeginNativeFactProjection(fixture, sample, ownership))
        {
            typeof(HostManagerSmartCoordinator).GetMethod("ProjectNativeFacts", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(fixture.Coordinator, [fixture.Workspace, sample, ownership, OptimizationProtectionTable.Create([]),
                    fixture.RuntimePlan.OptimizationMode, false, compute]);
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
        Assert.Contains(exclusions, exclusion => exclusion.Fact == (gpuDomain ? SchedulingFactCode.GpuScore : SchedulingFactCode.CpuScore)
            && exclusion.Reason == SchedulingFactExclusionReason.CanonicalSoftwareMembershipIncomplete);
        var scoreExclusions = exclusions.Where(exclusion => exclusion.Fact ==
            (gpuDomain ? SchedulingFactCode.GpuScore : SchedulingFactCode.CpuScore)).ToArray();
        Assert.All(scoreExclusions, exclusion => Assert.Equal(
            gpuDomain ? facts.Processes[0].Gpus[0].AdapterKey : (ulong?)null, exclusion.AdapterKey));
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
