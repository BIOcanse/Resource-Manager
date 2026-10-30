using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ResourceManager.App.Application.ProcessAttribution;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.ProcessAttribution;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization;
using ResourceManager.App.Infrastructure.ResourceBreakdown;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using Microsoft.Extensions.Logging.Abstractions;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerSmartCoordinatorScoreOnlyCompositionTests
{
    private const string RealProviderScoreOnlyEvidenceRootEnvironmentVariable =
        "RESOURCE_MANAGER_REAL_PROVIDER_SCORE_ONLY_EVIDENCE_ROOT";
    private const string RealProviderScoreOnlyRunPlanEnvironmentVariable =
        "RESOURCE_MANAGER_REAL_PROVIDER_SCORE_ONLY_RUN_PLAN";
    private const string RealProviderScoreOnlyRunPlanContract =
        "non-adapted-real-provider-score-only-run-plan-v4";
    private const string RealProviderScoreOnlyReceiptContract =
        "non-adapted-real-provider-score-only-receipt-v4";
    private const string RealProviderScoreOnlyMeasurementContract =
        "non-adapted-real-provider-observation-without-physical-cpu-v4";

    [Fact]
    public async Task RealWindowsCpuMemoryProviderFeedsOneWarmScoreOnlyCycleWithoutEffects()
    {
        var evidence = LoadRealProviderScoreOnlyEvidenceContext();
        using var catalog = RuntimeProcessAttributionCatalog.CreateOrReuse(
            current: null,
            softwareRecords: [],
            adapterRegistrations: [],
            controlledRegistrations: [],
            packageIdentityResolver: new NoMatchPackageIdentityResolver(),
            serviceIdentityResolver: new NoMatchServiceIdentityResolver(),
            rootIdentityResolver: new NoMatchRootIdentityResolver(),
            systemClassifier: new NoMatchSystemProcessClassifier(),
            softwareIdentityCatalog: SoftwareIdentityCatalogTestData.Empty,
            softwareIdentityOwner: SoftwareIdentityCatalogTestData.Owner);
        var catalogProvider = new StaticProcessAttributionCatalogProvider(catalog);
        var inventoryReader = new ExactTwoCaptureWindowsProcessInventoryReader(
            WindowsProcessInventoryReader.Instance);
        using var providerResources = new FiniteRealProviderResources();
        WindowsResourceBreakdownSampler? sampler = null;
        CpuMemoryCapturingSchedulingProcessFactSource? realSource = null;
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true,
            performanceLogEnabled: true,
            failFastEffects: true,
            cpuCoreReader: new RecordingCpuCoreResidencyReader(() => null),
            processFactsFactory: runtimePlanProvider =>
            {
                var plans = Assert.IsType<RuntimePlanProvider>(runtimePlanProvider);
                providerResources.SubscriptionOwner = new HostManagerSamplingSubscriptionOwner(plans,
                    new HostManagerSamplingSubscriptionRuntime(plans, new HostManagerDeploymentState()),
                    NullLogger<HostManagerSamplingSubscriptionOwner>.Instance);
                // This fixture performs exactly two finite captures. It supplies the
                // real subscription owner without entering a background sampling block.
                providerResources.Sampler = sampler = new WindowsResourceBreakdownSampler(
                    metricSampler: null!,
                    processAttributionCatalogProvider: catalogProvider,
                    residualBreakdownProvider: null!,
                    physicalDiskIoAttributionReader: null!,
                    networkAttributionReader: null!,
                    runtimePlanProvider: runtimePlanProvider,
                    samplingSubscriptionOwner: providerResources.SubscriptionOwner,
                    processGpuReader: new PdhProcessGpuReader(),
                    processInventoryReader: inventoryReader);
                realSource = new CpuMemoryCapturingSchedulingProcessFactSource(sampler);
                return realSource;
            });
        Assert.NotNull(realSource);
        Assert.NotNull(sampler);
        var durableBefore = CaptureDurableFiles(fixture.Root);
        var rollbackIncarnationBefore =
            fixture.StateStore.Current.NativeHostSessionIncarnation;
        var workspaceBefore = fixture.Workspace;
        var capacityBefore = fixture.Workspace.Capacity;
        var runSalt = evidence?.Plan.RunNonce ?? Guid.NewGuid().ToString("N");
        await using var mutationWatcher = new HostedEffectMutationWatcher(
            fixture.CaptureHostedScoreOnlyEffectCounters);
        await mutationWatcher.Started.WaitAsync(TimeSpan.FromSeconds(5));

        var startedAtUtc = DateTimeOffset.UtcNow;
        var startedAtQpcTicks = Stopwatch.GetTimestamp();
        var providerRequest = new SchedulingProcessFactRequest(
            SchedulingProcessMetricMask.CpuUsage
                | SchedulingProcessMetricMask.MemoryUsage
                | SchedulingProcessMetricMask.RuntimeState,
            fixture.MetricSampler.Snapshot.GpuInventory);
        _ = await realSource!.CaptureAsync(
            providerRequest,
            CancellationToken.None);
        var captureAttemptsBeforeScheduling = realSource.AttemptCount;
        var providerCapturesBeforeScheduling = realSource.ProviderCaptureCount;
        var nativeInputs = new NativeSmartInputCaptureProbe();
        fixture.Coordinator.TransitionProbe = nativeInputs;
        var status = await fixture.Coordinator.RunOnceAsync(CancellationToken.None);
        var completedAtQpcTicks = Stopwatch.GetTimestamp();
        var completedAtUtc = DateTimeOffset.UtcNow;
        await mutationWatcher.StopAsync();

        Assert.InRange(
            Stopwatch.GetElapsedTime(startedAtQpcTicks, completedAtQpcTicks),
            TimeSpan.Zero,
            TimeSpan.FromSeconds(30));
        Assert.True(completedAtQpcTicks >= startedAtQpcTicks);
        Assert.Equal(captureAttemptsBeforeScheduling, realSource.AttemptCount);
        Assert.Equal(providerCapturesBeforeScheduling, realSource.ProviderCaptureCount);
        Assert.Equal(1, realSource.AttemptCount);
        Assert.Equal(2, realSource.ProviderCaptureCount);
        Assert.Equal(1, realSource.ReadLatestCount);
        Assert.Equal(2, inventoryReader.AttemptCount);
        Assert.Equal(2, inventoryReader.InnerReadCount);
        Assert.Same(inventoryReader.Inner, WindowsProcessInventoryReader.Instance);
        var baselineFacts = Assert.IsType<SchedulingProcessFactSnapshot>(
            realSource.BaselineSnapshot);
        var processFacts = Assert.IsType<SchedulingProcessFactSnapshot>(
            realSource.LastSnapshot);
        var readFacts = Assert.IsType<SchedulingProcessFactSnapshot>(realSource.ReadLatestSnapshot);
        var readMask = processFacts.CurrentMetricMask & realSource.CoordinatorRequest!.RequestedMetricMask;
        Assert.Equal(readMask, readFacts.CurrentMetricMask);
        Assert.Equal(
            OrderSchedulingIdentities(CreateSchedulingIdentitySet(processFacts.Processes
                .Where(fact => (fact.ValidMetricMask & readMask) != SchedulingProcessMetricMask.None))),
            OrderSchedulingIdentities(CreateSchedulingIdentitySet(readFacts.Processes)));
        Assert.Equal(
            OrderSchedulingIdentities(CreateSchedulingIdentitySet(processFacts.CurrentInventoryProcesses)),
            OrderSchedulingIdentities(CreateSchedulingIdentitySet(readFacts.CurrentInventoryProcesses)));
        output.WriteLine($"Real-provider raw decision rows={processFacts.Processes.Count}, read rows={readFacts.Processes.Count}, "
            + $"inventory rows={readFacts.CurrentInventoryProcesses.Count}; empty requested facts: "
            + string.Join(", ", processFacts.Processes.Where(fact => (fact.ValidMetricMask & readMask) == SchedulingProcessMetricMask.None)
                .Select(fact => $"{fact.ProcessId}/{fact.ProcessStartKey} {fact.ProcessName} mask={fact.ValidMetricMask}")));
        Assert.Equal(
            SchedulingProcessMetricMask.CpuUsage |
            SchedulingProcessMetricMask.MemoryUsage |
            SchedulingProcessMetricMask.RuntimeState,
            realSource.CoordinatorRequest!.RequestedMetricMask);
        Assert.Equal(
            SchedulingProcessMetricMask.CpuUsage |
            SchedulingProcessMetricMask.MemoryUsage |
            SchedulingProcessMetricMask.RuntimeState,
            realSource.ProviderRequest!.RequestedMetricMask);
        Assert.Equal(
            SchedulingProcessMetricMask.CpuUsage |
            SchedulingProcessMetricMask.MemoryUsage |
            SchedulingProcessMetricMask.RuntimeState,
            baselineFacts.RequestedMetricMask);
        const SchedulingProcessMetricMask cpuMemory =
            SchedulingProcessMetricMask.CpuUsage | SchedulingProcessMetricMask.MemoryUsage;
        Assert.Equal(cpuMemory, baselineFacts.CurrentMetricMask & cpuMemory);
        Assert.Equal(SchedulingProcessMetricMask.None,
            baselineFacts.CurrentMetricMask & ~baselineFacts.RequestedMetricMask);
        Assert.Equal(
            baselineFacts.TryGetCurrentDataset(SchedulingProcessMetricMask.RuntimeState, out _),
            baselineFacts.CurrentMetricMask.HasFlag(SchedulingProcessMetricMask.RuntimeState));
        Assert.Equal(SamplingObservationStatus.Current, baselineFacts.InventoryStatus);
        Assert.True(baselineFacts.IsInventoryCurrentComplete(),
            $"{baselineFacts.InventoryIncompleteReason()}; enumerated={baselineFacts.EnumeratedCount}, emitted={baselineFacts.EmittedCount}, inventory={baselineFacts.CurrentInventoryProcesses.Count}, excluded={baselineFacts.ExcludedCount}, skipped={baselineFacts.SkippedCount}");
        Assert.True(baselineFacts.IsMemoryCurrentComplete());
        Assert.True(baselineFacts.IsCpuCurrentComplete());
        Assert.All(baselineFacts.Processes, fact =>
        {
            Assert.Equal(baselineFacts.Generation, fact.SourceGeneration);
            if (!fact.ValidMetricMask.HasFlag(SchedulingProcessMetricMask.MemoryUsage))
                Assert.Equal(0, fact.MemoryUsedBytes);
        });
        Assert.Contains(baselineFacts.Processes, fact => fact.ProcessId == Environment.ProcessId
            && fact.ValidMetricMask.HasFlag(SchedulingProcessMetricMask.MemoryUsage));
        Assert.Equal(SamplingObservationStatus.Current, processFacts.InventoryStatus);
        Assert.True(processFacts.IsInventoryCurrentComplete());
        Assert.True(processFacts.IsMemoryCurrentComplete());
        Assert.Equal(
            SchedulingProcessMetricMask.CpuUsage |
            SchedulingProcessMetricMask.MemoryUsage |
            SchedulingProcessMetricMask.RuntimeState,
            processFacts.RequestedMetricMask);
        Assert.Equal(
            checked(baselineFacts.Generation + 1),
            processFacts.Generation);
        Assert.True(realSource.BaselineCaptureStartedAtQpcTicks >= startedAtQpcTicks);
        Assert.True(realSource.BaselineCaptureCompletedAtQpcTicks >=
            realSource.BaselineCaptureStartedAtQpcTicks);
        Assert.True(realSource.MeasuredCaptureStartedAtQpcTicks >=
            realSource.BaselineCaptureCompletedAtQpcTicks);
        Assert.True(realSource.MeasuredCaptureCompletedAtQpcTicks >=
            realSource.MeasuredCaptureStartedAtQpcTicks);
        Assert.True(completedAtQpcTicks >= realSource.MeasuredCaptureCompletedAtQpcTicks);
        Assert.InRange(processFacts.EnumeratedCount, 1U, uint.MaxValue);
        Assert.InRange(processFacts.EmittedCount, 1U, uint.MaxValue);
        Assert.Equal(processFacts.EmittedCount, checked((uint)processFacts.Processes.Count));
        Assert.Equal(0U, processFacts.OverflowCount);
        Assert.Equal(
            (ulong)processFacts.EnumeratedCount,
            (ulong)processFacts.EmittedCount
            + processFacts.ExcludedCount
            + processFacts.SkippedCount);
        var baselineIdentities = CreateSchedulingIdentitySet(baselineFacts.Processes);
        var measuredIdentities = CreateSchedulingIdentitySet(processFacts.Processes);
        var stableIdentities = measuredIdentities
            .Where(baselineIdentities.Contains)
            .ToHashSet();
        var enteredIdentities = measuredIdentities
            .Where(identity => !baselineIdentities.Contains(identity))
            .ToHashSet();
        var exitedIdentities = baselineIdentities
            .Where(identity => !measuredIdentities.Contains(identity))
            .ToHashSet();
        var cpuValidIdentities = CreateSchedulingIdentitySet(
            processFacts.Processes.Where(static fact =>
                fact.ValidMetricMask.HasFlag(SchedulingProcessMetricMask.CpuUsage)));
        var cpuMissingIdentities = CreateSchedulingIdentitySet(
            processFacts.Processes.Where(static fact =>
                !fact.ValidMetricMask.HasFlag(SchedulingProcessMetricMask.CpuUsage)));
        Assert.NotEmpty(stableIdentities);
        Assert.Empty(cpuValidIdentities.Intersect(cpuMissingIdentities));
        Assert.Equal(
            OrderSchedulingIdentities(measuredIdentities),
            OrderSchedulingIdentities(cpuValidIdentities.Concat(
                cpuMissingIdentities)));
        Assert.True(processFacts.IsCpuCurrentComplete());
        Assert.All(processFacts.Processes, fact =>
        {
            Assert.Equal(processFacts.Generation, fact.SourceGeneration);
            if (!fact.ValidMetricMask.HasFlag(SchedulingProcessMetricMask.MemoryUsage))
                Assert.Equal(0, fact.MemoryUsedBytes);
        });
        Assert.Contains(processFacts.Processes, fact => fact.ProcessId == Environment.ProcessId
            && fact.ValidMetricMask.HasFlag(SchedulingProcessMetricMask.MemoryUsage));

        // Compare the consumed request projection even when runtime facts are
        // unavailable, so that branch also checks the sample accounting.
        var projectedProcesses = readFacts.ProjectValidFacts(fixture.MetricSampler.Snapshot.GpuInventory).Processes;
        var record = Assert.Single(fixture.DebugLogWriter.Records);
        var counts = Assert.IsType<Dictionary<string, object?>>(record.Properties["counts"]);
        Assert.Equal(projectedProcesses.Count, Assert.IsType<int>(counts["sampleProcesses"]));

        if (!processFacts.IsRuntimeStateCurrentComplete())
        {
            var unavailable = fixture.Coordinator.SchedulingAuthority;
            Assert.Equal(HostManagerSchedulingAuthorityAvailability.Unavailable,
                unavailable.Availability);
            Assert.Equal("compute-sample-incomplete", unavailable.UnavailableReason);
            Assert.Equal(1UL, unavailable.RoundNumber);
            Assert.Null(unavailable.Compute);
            Assert.Null(unavailable.MemoryModes);
            Assert.Same(workspaceBefore, fixture.Workspace);
            Assert.Equal(capacityBefore, fixture.Workspace.Capacity);
            Assert.True(fixture.Workspace.Snapshot.CycleSequence > 0);
            Assert.Equal(processFacts.CurrentInventoryProcesses.Count, nativeInputs.Rows.Length);
            Assert.All(nativeInputs.Rows, row => Assert.Equal(
                NativeSmartCoordinatorInputValidity.ProcessIdentity, row.ValidMask));
            Assert.Equal(rollbackIncarnationBefore,
                fixture.StateStore.Current.NativeHostSessionIncarnation);
            Assert.Equal(durableBefore, CaptureDurableFiles(fixture.Root));
            fixture.CaptureHostedScoreOnlyEffectCounters().AssertAllZero();
            mutationWatcher.AssertNoMutation();
            Assert.NotNull(status);
            Assert.Throws<InvalidOperationException>(() => inventoryReader.GetProcesses());
            Assert.Equal(3, inventoryReader.AttemptCount);
            Assert.Equal(2, inventoryReader.InnerReadCount);
            Assert.True(evidence is null,
                "The requested real-provider scoring measurement requires current RuntimeState; "
                + "the environment did not provide it, scheduling did not run, and no receipt was written.");
            return;
        }

        // Match the value returned to the coordinator: request projection removes
        // empty decision rows while its separate inventory still proves presence.
        var schedulableProcesses = projectedProcesses
            .Where(static fact => fact.ValidMetricMask.HasFlag(
                SchedulingProcessMetricMask.RuntimeState))
            .ToArray();
        var schedulableFacts = processFacts with
        {
            Processes = schedulableProcesses,
            EmittedCount = checked((uint)schedulableProcesses.Length),
            SkippedCount = checked(processFacts.SkippedCount
                + (uint)(processFacts.Processes.Count - schedulableProcesses.Length))
        };
        Assert.Equal((ulong)processFacts.EnumeratedCount,
            (ulong)schedulableFacts.ExcludedCount + schedulableFacts.SkippedCount
                + schedulableFacts.EmittedCount);
        var schedulableCpuValidCount = schedulableProcesses.Count(static fact =>
            fact.ValidMetricMask.HasFlag(SchedulingProcessMetricMask.CpuUsage));
        var targetIdentities = CreateSchedulingIdentitySet(schedulableProcesses);
        var nativeFacts = schedulableFacts with
        {
            Processes = schedulableProcesses.Concat(processFacts.CurrentInventoryProcesses
                .Where(fact => !targetIdentities.Contains(new(fact.ProcessId, fact.ProcessStartKey)))
                .Select(fact => fact with { SoftwareId = string.Empty, BaseScore = 0,
                    CpuUsagePercent = 0, ValidMetricMask = SchedulingProcessMetricMask.None })).ToArray()
        };

        Assert.Same(workspaceBefore, fixture.Workspace);
        Assert.Equal(capacityBefore, fixture.Workspace.Capacity);
        var native = fixture.Workspace.Snapshot;
        Assert.Equal(1UL, native.CycleSequence);
        Assert.Equal((uint)processFacts.CurrentInventoryProcesses.Count, native.ProcessCount);
        Assert.InRange(native.SoftwareCount, 1U, native.ProcessCount);
        Assert.Equal(
            checked(native.ProcessCount + native.SoftwareCount),
            native.SnapshotRowCount);
        Assert.Equal(0U, native.ActionCount);
        Assert.Equal(0U, native.PendingCount);
        Assert.Equal(
            native.InvalidFactCount != 0,
            native.Flags.HasFlag(
                NativeSmartCoordinatorSnapshotFlags.HasInvalidFacts));
        Assert.True(native.Flags.HasFlag(
            NativeSmartCoordinatorSnapshotFlags.ScoreOnly));
        var schedulingAuthority = fixture.Coordinator.SchedulingAuthority;
        // No kernel core residency in this fixture: CPU occupancy comes from process CPU usage.
        Assert.Equal(
            HostManagerCpuOccupancySource.ProcessCpuUsage,
            schedulingAuthority.Compute?.CpuOccupancySource);
        var sourceDigest = ComputeRunSaltedProcessIdentityDigest(
            runSalt,
            nativeFacts.Processes);
        var nativeDigest = AssertAndComputeRunSaltedNativeIdentityDigest(
            runSalt,
            nativeFacts.Processes,
            fixture.Workspace);
        Assert.Equal(sourceDigest.Leaves, nativeDigest.Leaves);
        Assert.Equal(sourceDigest.AggregateSha256, nativeDigest.AggregateSha256);
        var nativeCpuScores = AssertAndCaptureNativeCpuScores(
            runSalt,
            nativeFacts,
            fixture.Workspace,
            fixture.RuntimePlan.HostManager.RequirePublished()
                .HotPublish.SmartCoordinator.ProcessStateMultipliers,
            schedulingAuthority.Compute!.Welfare.CpuBonus);
        Assert.All(nativeCpuScores, score => Assert.False(score.CpuScoreValid));
        var nativeInputProjection = AssertAndCaptureNativeInputProjection(
            runSalt,
            nativeFacts,
            nativeInputs.Rows);

        Assert.Equal("completed", Assert.IsType<string>(record.Properties["outcome"]));
        Assert.Equal("manual", Assert.IsType<string>(record.Properties["trigger"]));
        Assert.True(Assert.IsType<bool>(record.Properties["scoreOnly"]));
        Assert.InRange(
            Assert.IsType<int>(counts["scoredProcesses"]),
            1,
            schedulableFacts.Processes.Count);
        var details = Assert.IsType<Dictionary<string, object?>>(record.Properties["details"]);
        Assert.Equal(
            checked(native.ProcessCount + (uint)schedulableCpuValidCount),
            Assert.IsType<uint>(details["nativeInputCount"]));
        Assert.Equal(native.SoftwareCount, Assert.IsType<uint>(details["nativeSoftwareCount"]));
        Assert.Equal(
            native.InvalidFactCount,
            Assert.IsType<uint>(details["nativeInvalidFactCount"]));

        Assert.Equal(rollbackIncarnationBefore,
            fixture.StateStore.Current.NativeHostSessionIncarnation);
        var durableAfter = CaptureDurableFiles(fixture.Root);
        Assert.Equal(durableBefore, durableAfter);
        var effectCounters = fixture.CaptureHostedScoreOnlyEffectCounters();
        effectCounters.AssertAllZero();
        mutationWatcher.AssertNoMutation();
        Assert.NotNull(status);

        Assert.Throws<InvalidOperationException>(() => inventoryReader.GetProcesses());
        Assert.Equal(3, inventoryReader.AttemptCount);
        Assert.Equal(2, inventoryReader.InnerReadCount);

        if (evidence is not null)
        {
            // The externally verified v4 receipt requires raw/native one-to-one
            // identity. Mixed runtime validity is outside that measurement contract.
            Assert.True(measuredIdentities.SetEquals(
                CreateSchedulingIdentitySet(schedulableProcesses)),
                "The legacy v4 real-provider receipt requires every raw process to have current RuntimeState; no receipt was written.");
            WriteRealProviderScoreOnlyReceipt(
                evidence,
                sampler!,
                inventoryReader,
                realSource,
                fixture,
                workspaceBefore,
                capacityBefore,
                baselineFacts,
                processFacts,
                native,
                record,
                sourceDigest,
                nativeDigest,
                baselineIdentities,
                measuredIdentities,
                stableIdentities,
                enteredIdentities,
                exitedIdentities,
                cpuValidIdentities,
                cpuMissingIdentities,
                nativeCpuScores,
                nativeInputProjection,
                effectCounters,
                mutationWatcher,
                durableBefore,
                durableAfter,
                rollbackIncarnationBefore,
                startedAtUtc,
                completedAtUtc,
                startedAtQpcTicks,
                completedAtQpcTicks);
        }
    }

    [Theory]
    [InlineData(SchedulingProcessMetricMask.None)]
    [InlineData(SchedulingProcessMetricMask.MemoryUsage)]
    public async Task RequestWithoutDecisionFactsPreservesLiveInventoryButDoesNotCountAnEmptySampleRow(
        SchedulingProcessMetricMask unreadableMask)
    {
        const int unreadablePid = 42_901;
        const int healthyPid = 42_902;
        var raw = CreateCompleteProcessFacts(
            (unreadablePid, 101, "software:unreadable", 20, 25, 10),
            (healthyPid, 102, "software:healthy", 20, 25, 10));
        raw = raw with
        {
            HasSeparateFoundationPayloads = true,
            InventoryProcesses = raw.Processes,
            AttributionProcesses = raw.Processes,
            Processes = [raw.Processes[0] with { ValidMetricMask = unreadableMask }, raw.Processes[1]]
        };
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true,
            processFactsSnapshot: raw, automaticMemoryCleanupEnabled: false, performanceLogEnabled: true,
            cpuCoreReader: new RecordingCpuCoreResidencyReader(() => null));
        var request = new SchedulingProcessFactRequest(
            SchedulingProcessMetricMask.CpuUsage | SchedulingProcessMetricMask.RuntimeState,
            fixture.MetricSampler.Snapshot.GpuInventory);
        var read = Assert.IsType<SchedulingProcessFactSnapshot>(SchedulingProcessFactTestProjection.Project(raw, request));
        Assert.Equal(2, raw.ProjectValidFacts(request.GpuInventory).Processes.Count);
        Assert.Equal(healthyPid, Assert.Single(read.ProjectValidFacts(request.GpuInventory).Processes).ProcessId);
        Assert.Equal(2, read.CurrentInventoryProcesses.Count);
        Assert.True(read.IsInventoryCurrentComplete());

        var inputs = new NativeSmartInputCaptureProbe();
        fixture.Coordinator.TransitionProbe = inputs;
        await fixture.Coordinator.RunOnceAsync(CancellationToken.None);
        var record = Assert.Single(fixture.DebugLogWriter.Records);
        var counts = Assert.IsType<Dictionary<string, object?>>(record.Properties["counts"]);
        Assert.Equal(1, Assert.IsType<int>(counts["sampleProcesses"]));
        Assert.Equal(1, Assert.IsType<int>(counts["sampleTargets"]));
        Assert.Equal(2U, fixture.Workspace.Snapshot.ProcessCount);
        Assert.Equal(NativeSmartCoordinatorInputValidity.ProcessIdentity,
            Assert.Single(inputs.Rows, row => row.ProcessId == unreadablePid).ValidMask);
        Assert.Contains(inputs.Rows, row => row.ProcessId == healthyPid
            && row.MetricKind == NativeSmartCoordinatorMetricKind.CpuUsagePercent);
        fixture.AssertNoEffectCollaboratorCalls();
    }

    [Fact]
    public async Task ScoreOnlyNativeProjectionExcludesOnlyFactsWithoutCurrentRuntimeState()
    {
        var facts = CreateCompleteProcessFacts(
            (4_242, 132_537_599_910_000_000, "software:runtime-current", 100D, 35D, 25D),
            (4_243, 132_537_599_920_000_000, "software:runtime-missing", 100D, 20D, 30D));
        facts = facts with
        {
            Processes =
            [
                facts.Processes[0],
                facts.Processes[1] with
                {
                    ValidMetricMask = facts.Processes[1].ValidMetricMask
                        & ~SchedulingProcessMetricMask.RuntimeState
                }
            ]
        };
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true, processFactsSnapshot: facts, performanceLogEnabled: true);

        await fixture.Coordinator.RunOnceAsync(CancellationToken.None);

        Assert.Equal(2U, facts.EmittedCount);
        Assert.Equal(2, facts.Processes.Count);
        var native = fixture.Workspace.Snapshot;
        Assert.Equal(2U, native.ProcessCount);
        var row = Assert.Single(fixture.Workspace.CurrentSnapshotRows.ToArray(),
            static row => row.RowKind == NativeSmartCoordinatorSnapshotRowKind.Process && row.ProcessId == 4_242);
        Assert.Equal(4_242U, row.ProcessId);
        Assert.Equal(132_537_599_910_000_000UL, row.ProcessStartKey);
        Assert.Contains(fixture.Workspace.CurrentSnapshotRows.ToArray(),
            static row => row.ProcessId == 4_243);
        var record = Assert.Single(fixture.DebugLogWriter.Records);
        var counts = Assert.IsType<Dictionary<string, object?>>(record.Properties["counts"]);
        Assert.Equal(2, Assert.IsType<int>(counts["sampleProcesses"])); // Identity remains observable even without runtime state.
        var diagnostics = await fixture.Coordinator.GetDecisionDiagnosticsAsync(CancellationToken.None);
        Assert.Contains(diagnostics.FactExclusions, exclusion => exclusion.ProcessId == 4_243
            && exclusion.Fact == SchedulingFactCode.RuntimeState);
        var details = Assert.IsType<Dictionary<string, object?>>(record.Properties["details"]);
        Assert.Equal(3U, Assert.IsType<uint>(details["nativeInputCount"]));
        Assert.Equal(0U, native.ActionCount);
        Assert.Equal(0U, native.PendingCount);
        fixture.AssertNoEffectCollaboratorCalls();
    }

    private static HashSet<RealProviderProcessIdentity> CreateSchedulingIdentitySet(
        IEnumerable<SchedulingProcessFact> processes)
        => processes
            .Select(static process => new RealProviderProcessIdentity(
                process.ProcessId,
                process.ProcessStartKey))
            .ToHashSet();

    private static RealProviderProcessIdentity[] OrderSchedulingIdentities(
        IEnumerable<RealProviderProcessIdentity> identities)
        => identities
            .OrderBy(static identity => identity.ProcessId)
            .ThenBy(static identity => identity.ProcessStartKey)
            .ToArray();

    private static RealProviderIdentityDigest ComputeRunSaltedLineageIdentityDigest(
        string runSalt,
        IEnumerable<RealProviderProcessIdentity> identities)
        => CreateRealProviderIdentityDigest(
            identities.Select(identity => FormattableString.Invariant(
                $"{runSalt}|{identity.ProcessId}|{identity.ProcessStartKey}")),
            requireNonEmpty: false);

    private static RealProviderIdentityDigest ComputeRunSaltedProcessIdentityDigest(
        string runSalt,
        IReadOnlyList<SchedulingProcessFact> processes)
    {
        return CreateRealProviderIdentityDigest(processes.Select(process =>
            FormattableString.Invariant(
                $"{runSalt}|{process.ProcessId}|{process.ProcessStartKey}|{process.SoftwareId}")));
    }

    private static string ComputeRunSaltedProcessInstanceIdentityLeaf(
        string runSalt,
        int processId,
        ulong processStartKey)
    {
        var canonicalIdentity = FormattableString.Invariant(
            $"{runSalt}|{processId}|{processStartKey}");
        return Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(canonicalIdentity)));
    }

    private static RealProviderIdentityDigest AssertAndComputeRunSaltedNativeIdentityDigest(
        string runSalt,
        IReadOnlyList<SchedulingProcessFact> processes,
        NativeSmartCoordinatorWorkspace workspace)
    {
        var snapshot = workspace.Snapshot;
        var rows = workspace.CurrentSnapshotRows;
        Assert.Equal(snapshot.SnapshotRowCount, checked((uint)rows.Length));
        var expected = processes
            .Select(static process => new HostedNativeProcessIdentity(
                NativeStableIdentity.CreateCaseInsensitiveKey(
                    HostManagerTargetIdentity.CreateProcessTargetId(
                        process.ProcessId,
                        process.ProcessStartKey)),
                string.IsNullOrEmpty(process.SoftwareId) ? 0 : NativeStableIdentity.CreateCaseInsensitiveKey(process.SoftwareId),
                process.ProcessStartKey,
                checked((uint)process.ProcessId)))
            .ToArray();
        var actual = new HostedNativeProcessIdentity[
            checked((int)snapshot.ProcessCount)];
        for (var index = 0; index < actual.Length; index++)
        {
            var row = rows[index];
            Assert.Equal(NativeSmartCoordinatorSnapshotRowKind.Process, row.RowKind);
            actual[index] = new HostedNativeProcessIdentity(
                row.TargetKey,
                row.SoftwareKey,
                row.ProcessStartKey,
                row.ProcessId);
        }
        Array.Sort(expected, HostedNativeProcessIdentityComparer.Instance);
        Array.Sort(actual, HostedNativeProcessIdentityComparer.Instance);
        Assert.Equal(expected, actual);
        var softwareIds = processes
            .GroupBy(static process =>
                string.IsNullOrEmpty(process.SoftwareId) ? 0 : NativeStableIdentity.CreateCaseInsensitiveKey(process.SoftwareId))
            .ToDictionary(
                static group => group.Key,
                static group => Assert.Single(
                    group.Select(static process => process.SoftwareId)
                        .Distinct(StringComparer.Ordinal)));
        return CreateRealProviderIdentityDigest(actual.Select(identity =>
            FormattableString.Invariant(
                $"{runSalt}|{identity.ProcessId}|{identity.ProcessStartKey}|{softwareIds[identity.SoftwareKey]}")));
    }

    private static RealProviderNativeCpuScoreEvidence[] AssertAndCaptureNativeCpuScores(
        string runSalt,
        SchedulingProcessFactSnapshot processFacts,
        NativeSmartCoordinatorWorkspace workspace,
        IReadOnlyList<double> processStateMultipliers,
        double cpuWelfareBonus)
    {
        var rows = workspace.CurrentSnapshotRows
            .ToArray()
            .Where(static row => row.RowKind ==
                NativeSmartCoordinatorSnapshotRowKind.Process)
            .ToArray();
        Assert.Equal(processFacts.Processes.Count, rows.Length);
        var factsByIdentity = processFacts.Processes.ToDictionary(
            static fact => new RealProviderProcessIdentity(
                fact.ProcessId,
                fact.ProcessStartKey));
        var expectedSourceIndexes = new Dictionary<RealProviderProcessIdentity, uint>();
        var expectedInputRowCount = 0U;
        foreach (var fact in processFacts.Processes)
        {
            var identity = new RealProviderProcessIdentity(
                fact.ProcessId,
                fact.ProcessStartKey);
            expectedSourceIndexes.Add(identity, expectedInputRowCount++);
            if (fact.ValidMetricMask.HasFlag(SchedulingProcessMetricMask.CpuUsage))
            {
                expectedInputRowCount++;
            }
        }
        var evidence = new RealProviderNativeCpuScoreEvidence[rows.Length];
        var observedSourceIndexes = new HashSet<uint>();
        for (var index = 0; index < rows.Length; index++)
        {
            var row = rows[index];
            var identity = new RealProviderProcessIdentity(
                checked((int)row.ProcessId),
                row.ProcessStartKey);
            Assert.True(factsByIdentity.TryGetValue(identity, out var fact));
            Assert.Equal(expectedSourceIndexes[identity], row.SourceIndex);
            Assert.True(observedSourceIndexes.Add(row.SourceIndex));
            Assert.Equal(fact.BaseScore, row.BaseScore, precision: 12);
            var cpuCurrent = fact.ValidMetricMask.HasFlag(
                SchedulingProcessMetricMask.CpuUsage);
            var snapshotMetricBitClear = !row.ValidMask.HasFlag(
                NativeSmartCoordinatorInputValidity.Metric);
            var missingCpuMetric = row.ReasonMask.HasFlag(
                NativeSmartCoordinatorReason.MissingCpuMetric);
            const bool cpuScoreValid = false;
            Assert.True(snapshotMetricBitClear);
            Assert.Equal(fact.CpuUsagePercent, row.CpuOccupancyPercent, precision: 12);
            var runtimeStateValue = checked((int)row.RuntimeState);
            Assert.InRange(runtimeStateValue, 0, processStateMultipliers.Count - 1);
            var multiplier = processStateMultipliers[runtimeStateValue];
            // Without core residency the CPU domain scores each process from its CPU usage. A process
            // without a current CPU usage reading is left out of CPU scoring and scores zero. A running
            // process with no usage keeps exactly the CPU welfare bonus; a process that is not running
            // gets no welfare. Any usage of a weighted process scores above zero.
            Assert.True(double.IsFinite(row.CpuScore));
            Assert.True(row.CpuScore >= 0);
            if (!cpuCurrent)
            {
                Assert.Equal(0d, row.CpuScore);
            }
            else if (fact.CpuUsagePercent == 0)
            {
                Assert.Equal(
                    row.RuntimeState == NativeSmartCoordinatorRuntimeState.NotRunning ? 0d : cpuWelfareBonus,
                    row.CpuScore,
                    precision: 12);
            }
            else if (fact.BaseScore > 0 && multiplier > 0)
            {
                Assert.True(row.CpuScore > 0);
            }
            var expectedCpuScore = row.CpuScore;
            evidence[index] = new(
                ComputeRunSaltedProcessInstanceIdentityLeaf(
                    runSalt,
                    fact.ProcessId,
                    fact.ProcessStartKey),
                fact.ProcessId,
                fact.ProcessStartKey.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                row.SourceIndex,
                fact.BaseScore,
                row.BaseScore,
                fact.CpuUsagePercent,
                row.CpuOccupancyPercent,
                runtimeStateValue,
                multiplier,
                expectedCpuScore,
                row.CpuScore,
                (ulong)row.ValidMask,
                ((ulong)row.ReasonMask).ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                cpuCurrent,
                snapshotMetricBitClear,
                cpuScoreValid,
                missingCpuMetric);
        }
        Array.Sort(
            evidence,
            static (left, right) => StringComparer.Ordinal.Compare(
                left.IdentityLeaf,
                right.IdentityLeaf));
        Assert.Equal(
            evidence.Length,
            evidence.Select(static score => score.IdentityLeaf)
                .Distinct(StringComparer.Ordinal)
                .Count());
        Assert.Equal(expectedInputRowCount, checked((uint)(
            processFacts.Processes.Count +
            evidence.Count(static score => score.SourceCpuMetricCurrent))));
        return evidence;
    }

    private static RealProviderNativeInputProjectionEvidence[]
        AssertAndCaptureNativeInputProjection(
            string runSalt,
            SchedulingProcessFactSnapshot processFacts,
            ReadOnlySpan<NativeSmartCoordinatorInputRow> inputs)
    {
        var expectedInputRowCount = checked(
            processFacts.Processes.Count +
            processFacts.Processes.Count(static fact =>
                fact.ValidMetricMask.HasFlag(
                    SchedulingProcessMetricMask.CpuUsage)));
        var evidence = new RealProviderNativeInputProjectionEvidence[
            expectedInputRowCount];
        var cursor = 0;
        foreach (var fact in processFacts.Processes)
        {
            var identityRow = inputs[cursor];
            var identityLeaf = ComputeRunSaltedProcessInstanceIdentityLeaf(
                runSalt,
                checked((int)identityRow.ProcessId),
                identityRow.ProcessStartKey);
            Assert.Equal(fact.ProcessId, checked((int)identityRow.ProcessId));
            Assert.Equal(fact.ProcessStartKey, identityRow.ProcessStartKey);
            Assert.Equal(
                ComputeRunSaltedProcessInstanceIdentityLeaf(
                    runSalt,
                    fact.ProcessId,
                    fact.ProcessStartKey),
                identityLeaf);
            Assert.Equal(checked((uint)cursor), identityRow.SourceIndex);
            Assert.Equal(
                NativeSmartCoordinatorMetricKind.None,
                identityRow.MetricKind);
            Assert.False(identityRow.ValidMask.HasFlag(
                NativeSmartCoordinatorInputValidity.Metric));
            // A process scored from its CPU usage carries complete CPU metrics.
            Assert.True(!identityRow.Flags.HasFlag(
                    NativeSmartCoordinatorInputFlags.ProcessCpuMetricsComplete)
                || fact.ValidMetricMask.HasFlag(SchedulingProcessMetricMask.CpuUsage));
            Assert.Equal(0d, identityRow.MetricValue);
            evidence[cursor] = new(
                cursor,
                "processIdentity",
                identityLeaf,
                identityRow.SourceIndex,
                (ulong)identityRow.ValidMask,
                checked((int)identityRow.MetricKind),
                identityRow.MetricValue);
            cursor++;

            if (!fact.ValidMetricMask.HasFlag(
                    SchedulingProcessMetricMask.CpuUsage))
            {
                continue;
            }

            var metricRow = inputs[cursor];
            var metricIdentityLeaf = ComputeRunSaltedProcessInstanceIdentityLeaf(
                runSalt,
                checked((int)metricRow.ProcessId),
                metricRow.ProcessStartKey);
            Assert.Equal(identityLeaf, metricIdentityLeaf);
            Assert.Equal(checked((uint)cursor), metricRow.SourceIndex);
            Assert.Equal(
                NativeSmartCoordinatorMetricKind.CpuUsagePercent,
                metricRow.MetricKind);
            Assert.True(metricRow.ValidMask.HasFlag(
                NativeSmartCoordinatorInputValidity.Metric));
            Assert.Equal(fact.CpuUsagePercent, metricRow.MetricValue, precision: 12);
            evidence[cursor] = new(
                cursor,
                "cpuMetric",
                metricIdentityLeaf,
                metricRow.SourceIndex,
                (ulong)metricRow.ValidMask,
                checked((int)metricRow.MetricKind),
                metricRow.MetricValue);
            cursor++;
        }
        Assert.Equal(expectedInputRowCount, cursor);
        return evidence;
    }

    private static RealProviderIdentityDigest CreateRealProviderIdentityDigest(
        IEnumerable<string> canonicalIdentities,
        bool requireNonEmpty = true)
    {
        var leaves = canonicalIdentities
            .Select(static value => Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(value))))
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (requireNonEmpty)
        {
            Assert.NotEmpty(leaves);
        }
        Assert.Equal(leaves.Length, leaves.Distinct(StringComparer.Ordinal).Count());
        var aggregate = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(string.Join("\n", leaves))));
        return new RealProviderIdentityDigest(leaves, aggregate);
    }

    private static RealProviderScoreOnlyEvidenceContext?
        LoadRealProviderScoreOnlyEvidenceContext()
    {
        var requestedRoot = Environment.GetEnvironmentVariable(
            RealProviderScoreOnlyEvidenceRootEnvironmentVariable);
        var requestedPlan = Environment.GetEnvironmentVariable(
            RealProviderScoreOnlyRunPlanEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(requestedRoot)
            && string.IsNullOrWhiteSpace(requestedPlan))
        {
            return null;
        }
        if (string.IsNullOrWhiteSpace(requestedRoot)
            || string.IsNullOrWhiteSpace(requestedPlan))
        {
            throw new InvalidDataException(
                "The real-provider evidence root and run plan must be supplied together.");
        }

        var root = ValidateScoreOnlyPerformanceEvidenceRoot(requestedRoot);
        var expectedPlanPath = Path.Combine(root, "run-plan.json");
        var planPath = Path.GetFullPath(requestedPlan);
        if (!string.Equals(planPath, expectedPlanPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The real-provider run plan must be the run-root run-plan.json file.");
        }
        var planFile = new FileInfo(planPath);
        if (!planFile.Exists
            || planFile.Attributes.HasFlag(FileAttributes.ReparsePoint)
            || planFile.Length is <= 0 or > 65_536)
        {
            throw new InvalidDataException(
                "The real-provider run plan is missing, redirected, empty, or oversized.");
        }
        var image = File.ReadAllBytes(planPath);
        using var document = JsonDocument.Parse(image, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 8
        });
        var expectedProperties = new[]
        {
            "schemaVersion",
            "contract",
            "evidenceRunId",
            "runNonce",
            "measurementContract",
            "timingScope",
            "inputScope",
            "providerScope",
            "hostScope",
            "durabilityScope",
            "coordinatorRequestedMetricMask",
            "providerRequestedMetricMask",
            "expectedSourceCaptureAttempts",
            "expectedProviderCaptureAttempts",
            "expectedInventoryAttempts",
            "expectedInventoryInnerReads",
            "expectedRejectedExtraReads",
            "receiptFile"
        };
        Assert.Equal(
            expectedProperties,
            document.RootElement.EnumerateObject().Select(static property => property.Name));
        var plan = JsonSerializer.Deserialize<RealProviderScoreOnlyRunPlan>(
            image,
            ScoreOnlyPerformanceJsonOptions)
            ?? throw new InvalidDataException("The real-provider run plan is invalid.");
        var runRootName = new DirectoryInfo(root).Name;
        if (plan.SchemaVersion != 4
            || !string.Equals(
                plan.Contract,
                RealProviderScoreOnlyRunPlanContract,
                StringComparison.Ordinal)
            || !string.Equals(plan.EvidenceRunId, runRootName, StringComparison.Ordinal)
            || plan.RunNonce.Length != 32
            || plan.RunNonce.Any(static value =>
                !char.IsAsciiHexDigit(value) || char.IsUpper(value))
            || !plan.EvidenceRunId.EndsWith(
                $"-{plan.RunNonce}",
                StringComparison.Ordinal)
            || !string.Equals(
                plan.MeasurementContract,
                RealProviderScoreOnlyMeasurementContract,
                StringComparison.Ordinal)
            || !string.Equals(
                plan.TimingScope,
                "warm-workspace-live-cpu-baseline-and-measured-captures-through-score-only-coordinator",
                StringComparison.Ordinal)
            || !string.Equals(
                plan.InputScope,
                "live-uncontrolled-windows-process-inventory",
                StringComparison.Ordinal)
            || !string.Equals(
                plan.ProviderScope,
                "exact-windows-sampler-scheduling-path-cpu-memory-two-capture",
                StringComparison.Ordinal)
            || !string.Equals(
                plan.HostScope,
                "dedicated-non-hosted-score-only-test-composition",
                StringComparison.Ordinal)
            || !string.Equals(
                plan.DurabilityScope,
                "single-cycle-logical-observation-without-durable-writer-qualification",
                StringComparison.Ordinal)
            || plan.CoordinatorRequestedMetricMask != (ulong)(
                SchedulingProcessMetricMask.CpuUsage |
                SchedulingProcessMetricMask.RuntimeState)
            || plan.ProviderRequestedMetricMask !=
                (ulong)(SchedulingProcessMetricMask.CpuUsage |
                    SchedulingProcessMetricMask.MemoryUsage |
                    SchedulingProcessMetricMask.RuntimeState)
            || plan.ExpectedSourceCaptureAttempts != 1
            || plan.ExpectedProviderCaptureAttempts != 2
            || plan.ExpectedInventoryAttempts != 3
            || plan.ExpectedInventoryInnerReads != 2
            || plan.ExpectedRejectedExtraReads != 1
            || !string.Equals(
                plan.ReceiptFile,
                "real-provider-receipt.json",
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The real-provider run plan does not match the exact S0 contract.");
        }
        var receiptPath = Path.Combine(root, plan.ReceiptFile);
        if (File.Exists(receiptPath))
        {
            throw new InvalidDataException(
                "The real-provider receipt path must not exist before the cycle.");
        }
        return new RealProviderScoreOnlyEvidenceContext(
            root,
            plan,
            new RealProviderFileIdentity(
                "run-plan.json",
                image.LongLength,
                Convert.ToHexString(SHA256.HashData(image))));
    }

    private static void WriteRealProviderScoreOnlyReceipt(
        RealProviderScoreOnlyEvidenceContext evidence,
        WindowsResourceBreakdownSampler sampler,
        ExactTwoCaptureWindowsProcessInventoryReader inventoryReader,
        CpuMemoryCapturingSchedulingProcessFactSource realSource,
        ScoreOnlyCoordinatorFixture fixture,
        NativeSmartCoordinatorWorkspace workspaceBefore,
        NativeSmartCoordinatorCapacity capacityBefore,
        SchedulingProcessFactSnapshot baselineFacts,
        SchedulingProcessFactSnapshot processFacts,
        NativeSmartCoordinatorSnapshot native,
        ResourceManager.App.Domain.Diagnostics.DebugDiagnosticLogRecord record,
        RealProviderIdentityDigest sourceDigest,
        RealProviderIdentityDigest nativeDigest,
        IReadOnlySet<RealProviderProcessIdentity> baselineIdentities,
        IReadOnlySet<RealProviderProcessIdentity> measuredIdentities,
        IReadOnlySet<RealProviderProcessIdentity> stableIdentities,
        IReadOnlySet<RealProviderProcessIdentity> enteredIdentities,
        IReadOnlySet<RealProviderProcessIdentity> exitedIdentities,
        IReadOnlySet<RealProviderProcessIdentity> cpuValidIdentities,
        IReadOnlySet<RealProviderProcessIdentity> cpuMissingIdentities,
        IReadOnlyList<RealProviderNativeCpuScoreEvidence> nativeCpuScores,
        IReadOnlyList<RealProviderNativeInputProjectionEvidence> nativeInputProjection,
        HostedScoreOnlyEffectCounterSnapshot effectCounters,
        HostedEffectMutationWatcher mutationWatcher,
        IReadOnlyList<DurableFileIdentity> durableBefore,
        IReadOnlyList<DurableFileIdentity> durableAfter,
        ulong rollbackIncarnationBefore,
        DateTimeOffset startedAtUtc,
        DateTimeOffset completedAtUtc,
        long startedAtQpcTicks,
        long completedAtQpcTicks)
    {
        var coordinatorRequest = Assert.IsType<SchedulingProcessFactRequest>(
            realSource.CoordinatorRequest);
        var providerRequest = Assert.IsType<SchedulingProcessFactRequest>(
            realSource.ProviderRequest);
        var counts = Assert.IsType<Dictionary<string, object?>>(record.Properties["counts"]);
        var guards = Assert.IsType<Dictionary<string, object?>>(record.Properties["guards"]);
        var details = Assert.IsType<Dictionary<string, object?>>(record.Properties["details"]);
        Assert.True(Assert.IsType<bool>(guards["sampling"]));
        Assert.False(Assert.IsType<bool>(guards["scoring"]));
        Assert.Equal(0U, native.InflightCount);
        Assert.Equal(0U, Assert.IsType<uint>(details["nativeFeedbackCount"]));
        Assert.True(processFacts.IsMemoryCurrentComplete());
        var capacity = fixture.Workspace.Capacity;
        Assert.Same(workspaceBefore, fixture.Workspace);
        Assert.Equal(capacityBefore, capacity);
        var durableBeforeSha256 = ComputeHostedScoreOnlyIdentitySha256(durableBefore);
        var durableAfterSha256 = ComputeHostedScoreOnlyIdentitySha256(durableAfter);
        Assert.Equal(durableBeforeSha256, durableAfterSha256);
        var elapsedMilliseconds = Stopwatch.GetElapsedTime(
            startedAtQpcTicks,
            completedAtQpcTicks).TotalMilliseconds;
        var baselineLineageDigest = ComputeRunSaltedLineageIdentityDigest(
            evidence.Plan.RunNonce,
            baselineIdentities);
        var measuredLineageDigest = ComputeRunSaltedLineageIdentityDigest(
            evidence.Plan.RunNonce,
            measuredIdentities);
        var stableLineageDigest = ComputeRunSaltedLineageIdentityDigest(
            evidence.Plan.RunNonce,
            stableIdentities);
        var enteredLineageDigest = ComputeRunSaltedLineageIdentityDigest(
            evidence.Plan.RunNonce,
            enteredIdentities);
        var exitedLineageDigest = ComputeRunSaltedLineageIdentityDigest(
            evidence.Plan.RunNonce,
            exitedIdentities);
        var cpuValidLineageDigest = ComputeRunSaltedLineageIdentityDigest(
            evidence.Plan.RunNonce,
            cpuValidIdentities);
        var cpuMissingLineageDigest = ComputeRunSaltedLineageIdentityDigest(
            evidence.Plan.RunNonce,
            cpuMissingIdentities);
        var receiptOutsideFixtureRoot = !string.Equals(
                evidence.Root,
                fixture.Root,
                StringComparison.OrdinalIgnoreCase)
            && !evidence.Root.StartsWith(
                fixture.Root + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
        Assert.True(receiptOutsideFixtureRoot);
        var sourceInputProjectionRows = processFacts.Processes
            .Select(process => new RealProviderSourceInputProjectionEvidence(
                ComputeRunSaltedProcessInstanceIdentityLeaf(
                    evidence.Plan.RunNonce,
                    process.ProcessId,
                    process.ProcessStartKey),
                process.ValidMetricMask.HasFlag(
                    SchedulingProcessMetricMask.CpuUsage)))
            .ToArray();
        Assert.Equal(
            sourceInputProjectionRows.Length,
            sourceInputProjectionRows
                .Select(static row => row.IdentityLeaf)
                .Distinct(StringComparer.Ordinal)
                .Count());
        Assert.Equal(
            cpuValidIdentities.Count,
            sourceInputProjectionRows.Count(
                static row => row.SourceCpuMetricCurrent));
        var receipt = new
        {
            schemaVersion = 4,
            contract = RealProviderScoreOnlyReceiptContract,
            evidenceRunId = evidence.Plan.EvidenceRunId,
            evidence.Plan.MeasurementContract,
            evidence.Plan.TimingScope,
            evidence.Plan.InputScope,
            evidence.Plan.ProviderScope,
            evidence.Plan.HostScope,
            evidence.Plan.DurabilityScope,
            runPlan = evidence.RunPlanIdentity,
            timing = new
            {
                startedAtUtc,
                completedAtUtc,
                startedAtQpcTicks,
                completedAtQpcTicks,
                qpcFrequency = Stopwatch.Frequency,
                elapsedMilliseconds,
                realSource.BaselineCaptureStartedAtQpcTicks,
                realSource.BaselineCaptureCompletedAtQpcTicks,
                realSource.MeasuredCaptureStartedAtQpcTicks,
                realSource.MeasuredCaptureCompletedAtQpcTicks
            },
            providerIdentity = new
            {
                sampler = GetRealProviderRuntimeTypeIdentity(sampler),
                processInventoryReader =
                    GetRealProviderRuntimeTypeIdentity(inventoryReader.Inner),
                coordinator = GetRealProviderRuntimeTypeIdentity(fixture.Coordinator),
                nativeWorkspace = GetRealProviderRuntimeTypeIdentity(fixture.Workspace),
                samplerStarted = false,
                hostStarted = false,
                warmWorkspace = true
            },
            requests = new
            {
                coordinatorMetricMask = (ulong)coordinatorRequest.RequestedMetricMask,
                providerMetricMask = (ulong)providerRequest.RequestedMetricMask,
                totalMemoryBytes = fixture.MetricSampler.Snapshot.Memory.TotalBytes.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                providerTotalMemoryBytes = fixture.MetricSampler.Snapshot.Memory.TotalBytes.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                sourceCaptureAttempts = realSource.AttemptCount,
                providerCaptureAttempts = realSource.ProviderCaptureCount,
                inventoryAttempts = inventoryReader.AttemptCount,
                inventoryInnerReads = inventoryReader.InnerReadCount,
                rejectedExtraReads = inventoryReader.AttemptCount -
                    inventoryReader.InnerReadCount,
                thirdAttemptRejectedBeforeDelegation = true
            },
            baselineInventory = new
            {
                status = baselineFacts.InventoryStatus.ToString(),
                generation = baselineFacts.Generation.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                baselineFacts.ObservedAtUtcTicks,
                baselineFacts.EnumeratedCount,
                baselineFacts.ExcludedCount,
                baselineFacts.SkippedCount,
                baselineFacts.EmittedCount,
                baselineFacts.OverflowCount,
                processArrayCount = baselineFacts.Processes.Count,
                requestedMetricMask = (ulong)baselineFacts.RequestedMetricMask,
                currentMetricMask = (ulong)baselineFacts.CurrentMetricMask,
                inventoryCurrentComplete = baselineFacts.IsInventoryCurrentComplete(),
                memoryCurrentComplete = baselineFacts.IsMemoryCurrentComplete(),
                cpuCurrentComplete = baselineFacts.IsCpuCurrentComplete(),
                allFactsCurrentGeneration = baselineFacts.Processes.All(
                    fact => fact.SourceGeneration == baselineFacts.Generation),
                allFactsMemoryCurrent = baselineFacts.Processes.All(
                    static fact => fact.ValidMetricMask.HasFlag(
                        SchedulingProcessMetricMask.MemoryUsage)),
                allFactsCpuAbsent = baselineFacts.Processes.All(
                    static fact => !fact.ValidMetricMask.HasFlag(
                        SchedulingProcessMetricMask.CpuUsage)),
                conservationSatisfied = (ulong)baselineFacts.EnumeratedCount ==
                    (ulong)baselineFacts.ExcludedCount +
                    baselineFacts.SkippedCount +
                    baselineFacts.EmittedCount
            },
            measuredInventory = new
            {
                status = processFacts.InventoryStatus.ToString(),
                generation = processFacts.Generation.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                processFacts.ObservedAtUtcTicks,
                processFacts.EnumeratedCount,
                processFacts.ExcludedCount,
                processFacts.SkippedCount,
                processFacts.EmittedCount,
                processFacts.OverflowCount,
                processArrayCount = processFacts.Processes.Count,
                requestedMetricMask = (ulong)processFacts.RequestedMetricMask,
                currentMetricMask = (ulong)processFacts.CurrentMetricMask,
                inventoryCurrentComplete = processFacts.IsInventoryCurrentComplete(),
                memoryCurrentComplete = processFacts.IsMemoryCurrentComplete(),
                cpuCurrentComplete = processFacts.IsCpuCurrentComplete(),
                allFactsCurrentGeneration = processFacts.Processes.All(
                    fact => fact.SourceGeneration == processFacts.Generation),
                allFactsMemoryCurrent = processFacts.Processes.All(
                    static fact => fact.ValidMetricMask.HasFlag(
                        SchedulingProcessMetricMask.MemoryUsage)),
                cpuValidCount = cpuValidIdentities.Count,
                cpuMissingCount = cpuMissingIdentities.Count,
                conservationSatisfied = (ulong)processFacts.EnumeratedCount ==
                    (ulong)processFacts.ExcludedCount +
                    processFacts.SkippedCount +
                    processFacts.EmittedCount
            },
            cpuLineage = new
            {
                algorithm = "SHA-256",
                saltRunNonce = evidence.Plan.RunNonce,
                baseline = baselineLineageDigest,
                measured = measuredLineageDigest,
                stable = stableLineageDigest,
                entered = enteredLineageDigest,
                exited = exitedLineageDigest,
                cpuValid = cpuValidLineageDigest,
                cpuMissing = cpuMissingLineageDigest,
                stableEqualsCpuValid = stableIdentities.SetEquals(cpuValidIdentities),
                enteredEqualsCpuMissing = enteredIdentities.SetEquals(cpuMissingIdentities)
            },
            sourceInputProjection = new
            {
                contract = "source-process-input-order-v1",
                processCount = sourceInputProjectionRows.Length,
                sourceCpuMetricCurrentCount = sourceInputProjectionRows.Count(
                    static row => row.SourceCpuMetricCurrent),
                sourceCpuMetricMissingCount = sourceInputProjectionRows.Count(
                    static row => !row.SourceCpuMetricCurrent),
                rows = sourceInputProjectionRows
            },
            abiInputProjection = new
            {
                contract = "native-process-cpu-input-plane-v1",
                inputRowCount = nativeInputProjection.Count,
                processRowCount = nativeInputProjection.Count(
                    static row => row.Kind == "processIdentity"),
                cpuMetricRowCount = nativeInputProjection.Count(
                    static row => row.Kind == "cpuMetric"),
                rows = nativeInputProjection
            },
            nativeScoring = new
            {
                contract = "native-process-observation-without-physical-cpu-v4",
                physicalCpuObserved = false,
                weightedCpuScoringAdmitted = false,
                inputRowCount = checked(
                    processFacts.Processes.Count + cpuValidIdentities.Count),
                processCount = nativeCpuScores.Count,
                sourceCpuMetricCurrentCount = nativeCpuScores.Count(
                    static score => score.SourceCpuMetricCurrent),
                cpuScoreValidCount = nativeCpuScores.Count(
                    static score => score.CpuScoreValid),
                cpuScoreMissingCount = nativeCpuScores.Count(
                    static score => !score.CpuScoreValid),
                rows = nativeCpuScores
            },
            native = new
            {
                native.AbiVersion,
                cycleSequence = native.CycleSequence.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                native.ProcessCount,
                native.SoftwareCount,
                native.SnapshotRowCount,
                native.ActionCount,
                native.PendingCount,
                native.InflightCount,
                native.InvalidFactCount,
                flagsValue = (uint)native.Flags,
                reasonValue = ((ulong)native.ReasonMask).ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                scoreOnly = native.Flags.HasFlag(
                    NativeSmartCoordinatorSnapshotFlags.ScoreOnly),
                hasInvalidFacts = native.Flags.HasFlag(
                    NativeSmartCoordinatorSnapshotFlags.HasInvalidFacts),
                missingCpuMetric = native.ReasonMask.HasFlag(
                    NativeSmartCoordinatorReason.MissingCpuMetric),
                processMissingCpuReasonCount = nativeCpuScores.Count(
                    static score => score.MissingCpuMetric),
                capacity = new
                {
                    capacity.InputRowCapacity,
                    capacity.ActionCapacity,
                    capacity.FeedbackCapacity,
                    capacity.SnapshotRowCapacity,
                    capacity.ProcessCapacity,
                    capacity.SoftwareCapacity
                }
            },
            diagnostics = new
            {
                recordCount = fixture.DebugLogWriter.Records.Count,
                outcome = Assert.IsType<string>(record.Properties["outcome"]),
                trigger = Assert.IsType<string>(record.Properties["trigger"]),
                scoreOnly = Assert.IsType<bool>(record.Properties["scoreOnly"]),
                sampleProcesses = Assert.IsType<int>(counts["sampleProcesses"]),
                scoredProcesses = Assert.IsType<int>(counts["scoredProcesses"]),
                samplingComplete = Assert.IsType<bool>(guards["sampling"]),
                scoringComplete = Assert.IsType<bool>(guards["scoring"]),
                nativeInputCount = Assert.IsType<uint>(details["nativeInputCount"]),
                nativeSoftwareCount = Assert.IsType<uint>(details["nativeSoftwareCount"]),
                nativeInvalidFactCount = Assert.IsType<uint>(
                    details["nativeInvalidFactCount"]),
                nativeFeedbackCount = Assert.IsType<uint>(details["nativeFeedbackCount"])
            },
            identityProjection = new
            {
                algorithm = "SHA-256",
                saltRunNonce = evidence.Plan.RunNonce,
                sourceLeaves = sourceDigest.Leaves,
                nativeLeaves = nativeDigest.Leaves,
                sourceAggregateSha256 = sourceDigest.AggregateSha256,
                nativeAggregateSha256 = nativeDigest.AggregateSha256,
                identityCount = sourceDigest.Leaves.Length,
                setsEqual = sourceDigest.Leaves.SequenceEqual(
                    nativeDigest.Leaves,
                    StringComparer.Ordinal)
            },
            effects = new
            {
                failFastEnabled = true,
                watcherSampleCount = mutationWatcher.SampleCount,
                mutationObserved = mutationWatcher.MutationObserved,
                canonical = effectCounters.ToCanonicalString(),
                counters = new
                {
                    effectCounters.RollbackReserveAttempts,
                    effectCounters.RollbackReserveCalls,
                    effectCounters.RollbackSaveAttempts,
                    effectCounters.RollbackSaveCalls,
                    effectCounters.ProcessPolicyTotalCalls,
                    effectCounters.ProcessPolicyRecoveryReadCalls,
                    effectCounters.ProcessPolicyBatchCalls,
                    effectCounters.ProcessPolicyBatchRequestCount,
                    effectCounters.MemoryCleanupPlanAttempts,
                    effectCounters.MemoryCleanupPlanCalls,
                    effectCounters.MemoryCleanupCompleteAttempts,
                    effectCounters.MemoryCleanupCompleteCalls,
                    effectCounters.MemoryCleanupCompletionHistoryCount,
                    effectCounters.GraphicsTotalCalls
                },
                allZero = effectCounters.AllZero
            },
            durability = new
            {
                durableFileCountBefore = durableBefore.Count,
                durableFileCountAfter = durableAfter.Count,
                durableStateSha256Before = durableBeforeSha256,
                durableStateSha256After = durableAfterSha256,
                durableStateUnchanged = durableBeforeSha256 == durableAfterSha256,
                rollbackIncarnationBefore = rollbackIncarnationBefore.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                rollbackIncarnationAfter = fixture.StateStore.Current
                    .NativeHostSessionIncarnation.ToString(
                        System.Globalization.CultureInfo.InvariantCulture),
                rollbackIncarnationUnchanged = rollbackIncarnationBefore ==
                    fixture.StateStore.Current.NativeHostSessionIncarnation,
                normalReleaseEvidenceUnchanged = true,
                workspaceSameInstance = ReferenceEquals(
                    fixture.Workspace,
                    workspaceBefore),
                workspaceCapacityUnchanged = capacity.Equals(capacityBefore),
                receiptOutsideFixtureRoot
            },
            satisfied = true
        };
        WriteOuterLoopJsonFile(
            Path.Combine(evidence.Root, evidence.Plan.ReceiptFile),
            receipt);
    }

    private static RealProviderRuntimeTypeIdentity GetRealProviderRuntimeTypeIdentity(
        object value)
    {
        var type = value.GetType();
        var assemblyName = type.Assembly.GetName();
        return new RealProviderRuntimeTypeIdentity(
            type.FullName ?? throw new InvalidDataException(
                "A real-provider runtime type has no full name."),
            assemblyName.Name ?? throw new InvalidDataException(
                "A real-provider runtime type has no assembly name."),
            assemblyName.Version?.ToString() ?? throw new InvalidDataException(
                "A real-provider runtime type has no assembly version."),
            type.Module.ModuleVersionId.ToString("D"));
    }

    private sealed class CpuMemoryCapturingSchedulingProcessFactSource(
        WindowsResourceBreakdownSampler inner)
        : ISchedulingProcessFactSource,
          ISchedulingProcessFactObservationSource,
          IDisposable
    {
        private int attemptCount;
        private int providerCaptureCount;
        private int readLatestCount;

        internal int AttemptCount => Volatile.Read(ref attemptCount);

        internal int ProviderCaptureCount => Volatile.Read(ref providerCaptureCount);

        internal int ReadLatestCount => Volatile.Read(ref readLatestCount);

        internal SchedulingProcessFactRequest? CoordinatorRequest { get; private set; }

        internal SchedulingProcessFactRequest? ProviderRequest { get; private set; }

        internal SchedulingProcessFactSnapshot? BaselineSnapshot { get; private set; }

        internal SchedulingProcessFactSnapshot? LastSnapshot { get; private set; }

        internal SchedulingProcessFactSnapshot? ReadLatestSnapshot { get; private set; }

        internal long BaselineCaptureStartedAtQpcTicks { get; private set; }

        internal long BaselineCaptureCompletedAtQpcTicks { get; private set; }

        internal long MeasuredCaptureStartedAtQpcTicks { get; private set; }

        internal long MeasuredCaptureCompletedAtQpcTicks { get; private set; }

        public async Task<SchedulingProcessFactSnapshot> CaptureAsync(
            SchedulingProcessFactRequest request,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref attemptCount) != 1)
            {
                throw new InvalidOperationException(
                    "The real-provider S0 source may be captured exactly once.");
            }
            ProviderRequest = request;
            BaselineCaptureStartedAtQpcTicks = Stopwatch.GetTimestamp();
            Interlocked.Increment(ref providerCaptureCount);
            BaselineSnapshot = await inner.CaptureAsync(
                request,
                cancellationToken);
            BaselineCaptureCompletedAtQpcTicks = Stopwatch.GetTimestamp();
            MeasuredCaptureStartedAtQpcTicks = Stopwatch.GetTimestamp();
            Interlocked.Increment(ref providerCaptureCount);
            LastSnapshot = await inner.CaptureAsync(request, cancellationToken);
            MeasuredCaptureCompletedAtQpcTicks = Stopwatch.GetTimestamp();
            return LastSnapshot;
        }

        public IDisposable AcquireSubscription(
            string subscriptionId,
            SchedulingProcessMetricMask metricMask,
            TimeSpan refreshInterval)
            => inner.AcquireSubscription(
                subscriptionId,
                metricMask,
                refreshInterval);

        public SchedulingProcessFactSnapshot? ReadLatest(
            SchedulingProcessFactRequest request)
        {
            Interlocked.Increment(ref readLatestCount);
            CoordinatorRequest = request;
            return ReadLatestSnapshot = LastSnapshot is null
                ? null
                : SchedulingProcessFactTestProjection.Project(LastSnapshot, request);
        }

        public void Dispose() => inner.Dispose();
    }

    private sealed class ExactTwoCaptureWindowsProcessInventoryReader
        : IWindowsProcessInventoryReader
    {
        private readonly IWindowsProcessInventoryReader inner;
        private int attemptCount;
        private int innerReadCount;

        internal ExactTwoCaptureWindowsProcessInventoryReader(
            IWindowsProcessInventoryReader inner)
        {
            this.inner = inner;
        }

        internal IWindowsProcessInventoryReader Inner => inner;

        internal int AttemptCount => Volatile.Read(ref attemptCount);

        internal int InnerReadCount => Volatile.Read(ref innerReadCount);

        public Process[] GetProcesses()
        {
            if (Interlocked.Increment(ref attemptCount) > 2)
            {
                throw new InvalidOperationException(
                    "The guarded real Windows inventory may be read exactly twice.");
            }
            Interlocked.Increment(ref innerReadCount);
            return inner.GetProcesses();
        }
    }

    private sealed class StaticProcessAttributionCatalogProvider(
        RuntimeProcessAttributionCatalog catalog)
        : IRuntimeProcessAttributionCatalogProvider
    {
        public long SoftwareSnapshotGeneration => 0;

        public Task<RuntimeProcessAttributionCatalogLease> AcquireCatalogAsync(
            string? windowsSid,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // The enclosing fixture owns this direct-created catalog and outlives all operations.
            return Task.FromResult(new RuntimeProcessAttributionCatalogLease(catalog, static () => { }));
        }

        public Task InvalidateSoftwareSnapshotAsync()
            => Task.CompletedTask;
    }

    private sealed class HostedEffectMutationWatcher : IAsyncDisposable
    {
        private readonly Func<HostedScoreOnlyEffectCounterSnapshot> capture;
        private readonly CancellationTokenSource cancellation = new();
        private readonly TaskCompletionSource started = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task worker;
        private int sampleCount;
        private int mutationObserved;

        internal HostedEffectMutationWatcher(
            Func<HostedScoreOnlyEffectCounterSnapshot> capture)
        {
            this.capture = capture;
            worker = Task.Run(WatchAsync);
        }

        internal Task Started => started.Task;

        internal int SampleCount => Volatile.Read(ref sampleCount);

        internal bool MutationObserved => Volatile.Read(ref mutationObserved) != 0;

        private async Task WatchAsync()
        {
            try
            {
                while (!cancellation.IsCancellationRequested)
                {
                    Interlocked.Increment(ref sampleCount);
                    if (!capture().AllZero)
                    {
                        Interlocked.Exchange(ref mutationObserved, 1);
                    }
                    started.TrySetResult();
                    await Task.Delay(1, cancellation.Token);
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }
        }

        internal async Task StopAsync()
        {
            cancellation.Cancel();
            await worker;
        }

        internal void AssertNoMutation()
        {
            Assert.InRange(Volatile.Read(ref sampleCount), 1, int.MaxValue);
            Assert.Equal(0, Volatile.Read(ref mutationObserved));
        }

        public async ValueTask DisposeAsync()
        {
            if (!cancellation.IsCancellationRequested)
            {
                await StopAsync();
            }
            cancellation.Dispose();
        }
    }

    private sealed record RealProviderScoreOnlyRunPlan(
        int SchemaVersion,
        string Contract,
        string EvidenceRunId,
        string RunNonce,
        string MeasurementContract,
        string TimingScope,
        string InputScope,
        string ProviderScope,
        string HostScope,
        string DurabilityScope,
        ulong CoordinatorRequestedMetricMask,
        ulong ProviderRequestedMetricMask,
        int ExpectedSourceCaptureAttempts,
        int ExpectedProviderCaptureAttempts,
        int ExpectedInventoryAttempts,
        int ExpectedInventoryInnerReads,
        int ExpectedRejectedExtraReads,
        string ReceiptFile);

    private sealed record RealProviderScoreOnlyEvidenceContext(
        string Root,
        RealProviderScoreOnlyRunPlan Plan,
        RealProviderFileIdentity RunPlanIdentity);

    private sealed record RealProviderFileIdentity(
        string Path,
        long Bytes,
        string Sha256);

    private sealed record RealProviderIdentityDigest(
        string[] Leaves,
        string AggregateSha256);

    private readonly record struct RealProviderProcessIdentity(
        int ProcessId,
        ulong ProcessStartKey);

    private sealed record RealProviderSourceInputProjectionEvidence(
        string IdentityLeaf,
        bool SourceCpuMetricCurrent);

    private sealed record RealProviderNativeInputProjectionEvidence(
        int Ordinal,
        string Kind,
        string IdentityLeaf,
        uint SourceIndex,
        ulong ValidMaskValue,
        int MetricKindValue,
        double MetricReadingValue);

    private sealed record RealProviderNativeCpuScoreEvidence(
        string IdentityLeaf,
        int ProcessId,
        string ProcessStartKey,
        uint SourceIndex,
        double SourceBaseScore,
        double NativeBaseScore,
        double SourceCpuUsagePercent,
        double NativeCpuOccupancyPercent,
        int RuntimeStateValue,
        double StateMultiplier,
        double ExpectedCpuScore,
        double NativeCpuScore,
        ulong ValidMaskValue,
        string ReasonValue,
        bool SourceCpuMetricCurrent,
        bool SnapshotMetricBitClear,
        bool CpuScoreValid,
        bool MissingCpuMetric);

    private sealed record RealProviderRuntimeTypeIdentity(
        string TypeFullName,
        string AssemblyName,
        string AssemblyVersion,
        string ModuleVersionId);

    private sealed class FiniteRealProviderResources : IDisposable
    {
        internal HostManagerSamplingSubscriptionOwner? SubscriptionOwner { get; set; }
        internal WindowsResourceBreakdownSampler? Sampler { get; set; }

        public void Dispose()
        {
            try { Sampler?.Dispose(); }
            finally { SubscriptionOwner?.Dispose(); }
        }
    }

    private sealed class NoMatchPackageIdentityResolver : IRuntimePackageIdentityResolver
    {
        public RuntimeAttributionObservation Observe(RuntimeProcessIdentity process)
            => RuntimeAttributionObservation.NoMatch;
    }

    private sealed class NoMatchServiceIdentityResolver : IRuntimeServiceIdentityResolver
    {
        public RuntimeAttributionObservation Observe(RuntimeProcessIdentity process)
            => RuntimeAttributionObservation.NoMatch;
    }

    private sealed class NoMatchRootIdentityResolver : IRuntimeRootIdentityResolver
    {
        public RuntimeAttributionObservation Observe(RuntimeProcessIdentity process)
            => RuntimeAttributionObservation.NoMatch;
    }

    private sealed class NoMatchSystemProcessClassifier : IRuntimeSystemProcessClassifier
    {
        public RuntimeAttributionObservation Observe(RuntimeProcessIdentity process)
            => RuntimeAttributionObservation.NoMatch;
    }
}
