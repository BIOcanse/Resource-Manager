using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Domain.CpuTopology;
using ResourceManager.App.Domain.Diagnostics;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Hosting;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization;

namespace Resource_Manager_APP.Tests;

[Collection(HostManagerSmartCoordinatorEvidenceCollection.Name)]
public sealed partial class HostManagerSmartCoordinatorScoreOnlyCompositionTests
{
    [Fact]
    [Trait("Category", "LongSoak")]
    public async Task ProductionScheduledLoopProducesBoundedLongSoakEvidence()
    {
        var requestedRoot = Environment.GetEnvironmentVariable(
            LongSoakEvidenceRootEnvironmentVariable);
        var requestedPlanPath = Environment.GetEnvironmentVariable(
            LongSoakPlanEnvironmentVariable);
        var ownsEvidenceRoot = false;
        if (string.IsNullOrWhiteSpace(requestedRoot)
            && string.IsNullOrWhiteSpace(requestedPlanPath))
        {
            requestedRoot = Path.Combine(
                Path.GetTempPath(),
                "resource-manager-long-soak-smoke",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(requestedRoot);
            requestedPlanPath = Path.Combine(requestedRoot, "long-soak-plan.json");
            WriteLongSoakJsonFile(
                requestedRoot,
                "long-soak-plan.json",
                CreateDefaultLongSoakEvidencePlan(new DirectoryInfo(requestedRoot).Name));
            ownsEvidenceRoot = true;
        }
        else if (string.IsNullOrWhiteSpace(requestedRoot)
                 || string.IsNullOrWhiteSpace(requestedPlanPath))
        {
            throw new InvalidDataException(
                "The long-soak evidence root and plan must be supplied together.");
        }

        var evidenceRoot = ValidateScoreOnlyPerformanceEvidenceRoot(requestedRoot!);
        var plan = LoadLongSoakEvidencePlan(evidenceRoot, requestedPlanPath!);
        var profile = ResolveLongSoakProfile(plan.Profile);
        var permits = CreateLongSoakCyclePermits(plan);
        var source = new LongSoakBoundedSnapshotSource(profile, permits);
        try
        {
            await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
                warm: true,
                performanceLogEnabled: true,
                smartCoordinatorMaximumProcesses: LongSoakMaximumProcesses,
                metricSamplerOverride: source,
                processFactsOverride: source,
                cpuCoreReader: new RecordingCpuCoreResidencyReader(source.ReadCpuCoreResidency),
                normalIntervalMilliseconds: profile.NormalIntervalMilliseconds,
                eventIntervalMilliseconds: profile.NormalIntervalMilliseconds,
                failFastEffects: true);
            source.ConfigureHistory(fixture.RuntimePlan.HostManager.DataHistory);
            fixture.DebugLogWriter.DisableRecordRetention();
            var shutdownProbe = new HostedScoreOnlyShutdownProbe();
            fixture.Coordinator.TransitionProbe = shutdownProbe;
            var deadline = ReadPrivateField<HostManagerVersionedWakeDeadline>(
                fixture.Coordinator,
                "schedulingWakeDeadline")
                ?? throw new InvalidDataException(
                    "The production long-soak wake deadline is unavailable.");
            var runtimeIdentity = CreateLongSoakRuntimeIdentity();
            using var shardWriter = new LongSoakSampleShardWriter(
                evidenceRoot,
                profile.MaximumShardCount);
            using var durableMutationProbe = new LongSoakDurableMutationProbe(fixture.Root);
            var durableBefore = CaptureDurableFiles(fixture.Root);
            var durableBeforeSha256 = ComputeHostedScoreOnlyIdentitySha256(durableBefore);
            var rollbackIncarnationBefore =
                fixture.StateStore.Current.NativeHostSessionIncarnation;
            var normalReleaseEvidenceBefore = CapturePrivateState(
                fixture.Coordinator,
                "hostPublicResourceNormalReleaseEvidence");
            var workspaceBefore = fixture.Workspace;
            var capacityBefore = fixture.Workspace.Capacity;
            var lifecycleBefore = fixture.Coordinator.LifecycleState;
            if (lifecycleBefore != HostManagerSmartCoordinatorLifecycleState.Created)
            {
                throw new InvalidDataException(
                    "The long-soak coordinator did not begin in Created state.");
            }

            var startedClock = CaptureLongSoakClock();
            source.Arm(startedClock, runtimeIdentity.OpportunityLedgerId);
            using var observer = new LongSoakCompactSampleObserver(
                plan,
                profile,
                source,
                fixture,
                shardWriter,
                runtimeIdentity);
            fixture.DebugLogWriter.AttachRecordObserver(observer);
            var observerAttached = true;
            IHost? host = null;
            var hostStopped = false;
            LongSoakClockReading completedClock = default;
            TimeSpan stopElapsed = TimeSpan.Zero;
            ulong deadlineVersionBeforeStop = 0;
            try
            {
                host = BuildHostedScoreOnlyTestHost(fixture.Coordinator);
                var concrete = host.Services.GetRequiredService<HostManagerSmartCoordinator>();
                var contract = host.Services.GetRequiredService<IHostManagerSmartCoordinator>();
                var hostedOwner = Assert.Single(host.Services.GetServices<IHostedService>());
                var alias = Assert.IsType<NonOwningHostedService<HostManagerSmartCoordinator>>(
                    hostedOwner);
                Assert.Same(fixture.Coordinator, concrete);
                Assert.Same(concrete, contract);
                Assert.Same(concrete, alias.Service);

                using (var startCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                {
                    await host.StartAsync(startCancellation.Token);
                }
                if (fixture.Coordinator.LifecycleState
                    != HostManagerSmartCoordinatorLifecycleState.Running)
                {
                    throw new InvalidDataException(
                        "The production long-soak coordinator did not enter Running state.");
                }

                var pollMilliseconds = Math.Clamp(
                    profile.NormalIntervalMilliseconds / 4,
                    25,
                    1_000);
                while (true)
                {
                    source.ThrowIfFaulted();
                    observer.ThrowIfFaulted();
                    var current = CaptureLongSoakClock();
                    var activeMilliseconds =
                        (current.ActiveTime100Nanoseconds
                         - startedClock.ActiveTime100Nanoseconds) / 10_000D;
                    if (activeMilliseconds >= profile.ActiveDurationMilliseconds
                        && source.CompletedCount >= profile.MinimumCycleCount
                        && HasLongSoakQuiescentStopWindow(
                            profile,
                            source,
                            deadline))
                    {
                        break;
                    }
                    var qpcMilliseconds =
                        (current.QpcTicks - startedClock.QpcTicks)
                        * 1_000D / Stopwatch.Frequency;
                    var wallFuseMilliseconds = checked(
                        profile.ActiveDurationMilliseconds
                        + profile.MaximumSuspendedDurationMilliseconds
                        + 30_000);
                    if (qpcMilliseconds > wallFuseMilliseconds)
                    {
                        throw new TimeoutException(
                            "The long-soak wall fuse expired before its active duration completed.");
                    }
                    await Task.Delay(pollMilliseconds);
                }

                source.ThrowIfFaulted();
                observer.ThrowIfFaulted();
                if (!ReferenceEquals(workspaceBefore, fixture.Workspace)
                    || !capacityBefore.Equals(fixture.Workspace.Capacity))
                {
                    throw new InvalidDataException(
                        "The long-soak native workspace identity or capacity changed.");
                }
                deadlineVersionBeforeStop = deadline.Snapshot.Version;
                var stopStarted = Stopwatch.GetTimestamp();
                using (var stopCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                {
                    await host.StopAsync(stopCancellation.Token);
                }
                stopElapsed = Stopwatch.GetElapsedTime(stopStarted);
                hostStopped = true;
                completedClock = CaptureLongSoakClock();
                if (stopElapsed > TimeSpan.FromSeconds(10)
                    || fixture.Coordinator.LifecycleState
                        != HostManagerSmartCoordinatorLifecycleState.Closed)
                {
                    throw new InvalidDataException(
                        "The production long-soak coordinator did not stop inside its bound.");
                }
                shutdownProbe.AssertExactOrder();
                AssertOwnedResourcesCleared(fixture.Coordinator);

                fixture.DebugLogWriter.DetachRecordObserver(observer);
                observerAttached = false;
                source.ThrowIfFaulted();
                observer.ThrowIfFaulted();
                var opportunities = source.AssertCompleteAndCapture();
                var samples = observer.AssertCompleteAndCapture(opportunities.Length);
                if (samples.Length != shardWriter.RecordCount
                    || samples.Length != opportunities.Length
                    || deadlineVersionBeforeStop != checked((ulong)samples.Length))
                {
                    throw new InvalidDataException(
                        "The long-soak opportunity, profiler, shard, or deadline ledger is incomplete.");
                }

                var clockAssessment = AssessLongSoakClock(
                    profile,
                    startedClock,
                    completedClock);
                var assessment = AssessLongSoakSamples(
                    plan,
                    profile,
                    samples,
                    opportunities,
                    startedClock,
                    completedClock,
                    source.HardwareCaptureCount,
                    source.ProcessCaptureCount,
                    observer.MaterializedRecordCount,
                    observer.WriterAdmissionCount,
                    observer.CommittedSampleCount);
                if (!assessment.Passed)
                {
                    throw new InvalidDataException(
                        "The long-soak cadence, profiler, or resource assessment failed: " +
                        JsonSerializer.Serialize(assessment, LongSoakCompactJsonOptions));
                }

                fixture.AssertNoMemoryCleanupAttemptEntries();
                var effectCounters = fixture.CaptureHostedScoreOnlyEffectCounters();
                effectCounters.AssertAllZero();
                var rollbackIncarnationAfter =
                    fixture.StateStore.Current.NativeHostSessionIncarnation;
                var durableAfter = CaptureDurableFiles(fixture.Root);
                var durableAfterSha256 = ComputeHostedScoreOnlyIdentitySha256(durableAfter);
                if (rollbackIncarnationBefore != rollbackIncarnationAfter
                    || durableBeforeSha256 != durableAfterSha256
                    || !normalReleaseEvidenceBefore.SequenceEqual(
                        CapturePrivateState(
                            fixture.Coordinator,
                            "hostPublicResourceNormalReleaseEvidence")))
                {
                    throw new InvalidDataException(
                        "The long-soak durable or rollback identity changed.");
                }
                await Task.Delay(100);
                durableMutationProbe.StopAndAssertNoMutations();

                var shardIndex = shardWriter.Seal(plan.EvidenceRunId, plan.Profile);
                if (shardIndex.RecordCount != samples.Length)
                {
                    throw new InvalidDataException(
                        "The sealed long-soak shard index has an unexpected record count.");
                }
                var indexIdentity = WriteLongSoakJsonFile(
                    evidenceRoot,
                    "shard-index.json",
                    shardIndex);
                var assessmentIdentity = WriteLongSoakJsonFile(
                    evidenceRoot,
                    "assessment.json",
                    assessment);
                var planIdentity = CaptureLongSoakFileIdentity(
                    evidenceRoot,
                    "long-soak-plan.json");
                var uptimeAtEnd = Environment.TickCount64;
                var bootEstimateAtEnd = checked(
                    DateTimeOffset.UtcNow.UtcDateTime.Ticks
                    - uptimeAtEnd * TimeSpan.TicksPerMillisecond);
                if (uptimeAtEnd < runtimeIdentity.SystemUptimeAtStartMilliseconds
                    || Math.Abs(bootEstimateAtEnd - runtimeIdentity.BootEstimateUtcTicks)
                        > 5 * TimeSpan.TicksPerSecond)
                {
                    throw new InvalidDataException(
                        "The long-soak boot/process continuity identity changed.");
                }
                WriteLongSoakLoopReceipt(
                    evidenceRoot,
                    plan,
                    profile,
                    runtimeIdentity,
                    clockAssessment,
                    assessment,
                    shardIndex,
                    planIdentity,
                    indexIdentity,
                    assessmentIdentity,
                    lifecycleBefore,
                    fixture.Coordinator.LifecycleState,
                    shutdownProbe.Points,
                    stopElapsed,
                    deadlineVersionBeforeStop,
                    source,
                    observer,
                    capacityBefore,
                    effectCounters,
                    rollbackIncarnationBefore,
                    rollbackIncarnationAfter,
                    durableBeforeSha256,
                    durableAfterSha256,
                    durableMutationProbe.MutationCount,
                    uptimeAtEnd,
                    bootEstimateAtEnd);
            }
            finally
            {
                if (host is not null && !hostStopped)
                {
                    try
                    {
                        using var stopCancellation =
                            new CancellationTokenSource(TimeSpan.FromSeconds(10));
                        await host.StopAsync(stopCancellation.Token);
                    }
                    catch
                    {
                    }
                }
                if (observerAttached)
                {
                    try
                    {
                        fixture.DebugLogWriter.DetachRecordObserver(observer);
                    }
                    catch
                    {
                    }
                }
                host?.Dispose();
            }
        }
        finally
        {
            if (ownsEvidenceRoot && Directory.Exists(evidenceRoot))
            {
                var fullRoot = Path.GetFullPath(evidenceRoot);
                var tempRoot = Path.GetFullPath(Path.GetTempPath());
                if (!fullRoot.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "The owned long-soak evidence root escaped the temporary directory.");
                }
                Directory.Delete(fullRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void LongSoakProfilesCannotUpgradeContractSmokeOrChangeFixedBounds()
    {
        var smoke = CreateDefaultLongSoakEvidencePlan();
        ValidateLongSoakEvidencePlan(smoke, expectedRunId: null);
        Assert.False(smoke.AllowLongRunning);
        Assert.False(smoke.RequestedDurationQualification);
        Assert.Throws<InvalidDataException>(() => ValidateLongSoakEvidencePlan(
            smoke with
            {
                Profile = "idle-8h",
                AllowLongRunning = true,
                RequestedDurationQualification = true
            },
            expectedRunId: null));
        Assert.Throws<InvalidDataException>(() => ValidateLongSoakEvidencePlan(
            smoke with { ActiveDurationMilliseconds = 8 * 60 * 60 * 1_000 },
            expectedRunId: null));
    }

    [Fact]
    public void LongSoakClockContractRejectsInsufficientActiveTimeSuspendAndClockJumps()
    {
        var profile = ResolveLongSoakProfile("contract-smoke");
        var started = new LongSoakClockReading(
            10_000_000UL,
            1_000_000L,
            638901408000000000L);
        var valid = new LongSoakClockReading(
            started.ActiveTime100Nanoseconds + 40_000_000UL,
            started.QpcTicks + 4 * Stopwatch.Frequency,
            started.UtcTicks + 4 * TimeSpan.TicksPerSecond);
        var assessment = AssessLongSoakClock(profile, started, valid);
        Assert.InRange(assessment.ActiveElapsedMilliseconds, 4_000D, 4_001D);

        Assert.Throws<InvalidDataException>(() => AssessLongSoakClock(
            profile,
            started,
            valid with
            {
                ActiveTime100Nanoseconds =
                    started.ActiveTime100Nanoseconds + 30_000_000UL
            }));
        Assert.Throws<InvalidDataException>(() => AssessLongSoakClock(
            profile,
            started,
            valid with
            {
                QpcTicks = started.QpcTicks + checked(7 * Stopwatch.Frequency)
            }));
        Assert.Throws<InvalidDataException>(() => AssessLongSoakClock(
            profile,
            started,
            valid with
            {
                UtcTicks = started.UtcTicks + 8 * TimeSpan.TicksPerHour
            }));
        Assert.Throws<InvalidDataException>(() => AssessLongSoakClock(
            profile,
            started,
            valid with { QpcTicks = started.QpcTicks - 1 }));
        Assert.Throws<InvalidDataException>(() => AssessLongSoakClock(
            profile,
            started,
            valid with { UtcTicks = started.UtcTicks - 1 }));
    }

    [Fact]
    public void LongSoakStopAdmissionRequiresVersionBoundedQuiescentLead()
    {
        const int completedCount = 15;
        const long now = 1_000;
        const long frequency = 1_000;
        Assert.False(IsLongSoakQuiescentStopWindow(
            250,
            completedCount,
            new HostManagerWakeDeadlineSnapshot(15, HasDeadline: false, 2_000),
            now,
            frequency));
        Assert.False(IsLongSoakQuiescentStopWindow(
            250,
            completedCount,
            new HostManagerWakeDeadlineSnapshot(14, HasDeadline: true, 2_000),
            now,
            frequency));
        Assert.False(IsLongSoakQuiescentStopWindow(
            250,
            completedCount,
            new HostManagerWakeDeadlineSnapshot(15, HasDeadline: true, 1_061),
            now,
            frequency));
        Assert.True(IsLongSoakQuiescentStopWindow(
            250,
            completedCount,
            new HostManagerWakeDeadlineSnapshot(15, HasDeadline: true, 1_062),
            now,
            frequency));
        Assert.False(IsLongSoakQuiescentStopWindow(
            60_000,
            completedCount,
            new HostManagerWakeDeadlineSnapshot(15, HasDeadline: true, 1_999),
            now,
            frequency));
        Assert.True(IsLongSoakQuiescentStopWindow(
            60_000,
            completedCount,
            new HostManagerWakeDeadlineSnapshot(15, HasDeadline: true, 2_000),
            now,
            frequency));
    }

    [Fact]
    public void LongSoakShardWriterRotatesWithHashChainAndFailsClosedAfterSequenceFault()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "resource-manager-long-soak-writer",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using (var writer = new LongSoakSampleShardWriter(root, maximumShardCount: 2))
            {
                for (var sequence = 1; sequence <= 129; sequence++)
                {
                    writer.Append(CreateSyntheticLongSoakSample(sequence));
                }
                var index = writer.Seal("writer-contract-test", "contract-smoke");
                Assert.Equal(129, index.RecordCount);
                Assert.Equal(2, index.Shards.Length);
                Assert.Equal(128, index.Shards[0].RecordCount);
                Assert.Equal(1, index.Shards[1].RecordCount);
                Assert.Equal(index.Shards[0].Sha256, index.Shards[1].PreviousShardSha256);
                Assert.Throws<InvalidOperationException>(() =>
                    writer.Append(CreateSyntheticLongSoakSample(130)));
            }

            var faultRoot = Path.Combine(root, "fault");
            Directory.CreateDirectory(faultRoot);
            using var faulted = new LongSoakSampleShardWriter(
                faultRoot,
                maximumShardCount: 1);
            faulted.Append(CreateSyntheticLongSoakSample(1));
            Assert.Throws<InvalidDataException>(() =>
                faulted.Append(CreateSyntheticLongSoakSample(3)));
            Assert.Throws<InvalidDataException>(() =>
                faulted.Seal("writer-fault-test", "contract-smoke"));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static LongSoakCompactSample CreateSyntheticLongSoakSample(int sequence)
    {
        var active = checked(10_000_000UL + (ulong)sequence * 2_500_000UL);
        var qpc = checked(1_000_000L + sequence * 10_000L);
        var utc = checked(638901408000000000L + sequence * 2_500_000L);
        return new LongSoakCompactSample(
            1,
            LongSoakSampleContract,
            "writer-contract-test",
            "contract-smoke",
            sequence,
            new string('a', 32),
            new string('b', 32),
            new string('c', 32),
            new string('d', 32),
            1234,
            638901408000000000L,
            new string('E', 64),
            "scheduled",
            "smoke-drain",
            0,
            active,
            active,
            qpc,
            utc,
            0,
            qpc,
            qpc + 1,
            Stopwatch.Frequency,
            active,
            qpc + 2,
            utc + 1,
            1,
            0,
            1_024,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            EffectsAllZero: true,
            new string('F', 64),
            0,
            0,
            0,
            ResourceStabilization: "none",
            1_024,
            1_024,
            0,
            4_096,
            4_096,
            1,
            1);
    }

    private static LongSoakCyclePermit[] CreateLongSoakCyclePermits(
        LongSoakEvidencePlan plan)
    {
        var permits = new LongSoakCyclePermit[plan.MaximumCycleCount];
        for (var index = 0; index < permits.Length; index++)
        {
            var sequence = index + 1;
            var shape = ResolveLongSoakShape(plan.Profile, sequence);
            var generation = checked((ulong)sequence);
            var observedAtUtcTicks = checked(638901408000000000L + sequence);
            var observedAt = new DateTimeOffset(observedAtUtcTicks, TimeSpan.Zero);
            var hardware = CreateHardwareSnapshot(
                memoryUsagePercent: 50,
                cpuUsagePercent: 50,
                memoryUsageAvailable: true,
                committedGeneration: generation,
                capturedAt: observedAt);
            const SchedulingProcessMetricMask metrics =
                SchedulingProcessMetricMask.CpuUsage |
                SchedulingProcessMetricMask.RuntimeState;
            var processes = new SchedulingProcessFact[shape.ProcessCount];
            for (var processIndex = 0; processIndex < processes.Length; processIndex++)
            {
                var replacement = shape.IdentitySet is "replacement" or "pid-reuse";
                var processId = (replacement ? 40_000 : 30_000) + processIndex;
                var processStartKey = checked(
                    (replacement
                        ? 132_537_700_000_000_000UL
                        : 132_537_600_000_000_000UL)
                    + checked((ulong)processIndex * 10_000_000UL)
                    + (shape.IdentitySet == "pid-reuse" ? 50_000_000UL : 0UL));
                var softwareId = FormattableString.Invariant(
                    $"software:long-soak:{shape.IdentitySet}:{processIndex:D3}");
                processes[processIndex] = new SchedulingProcessFact(
                    processId,
                    processStartKey,
                    $"long-soak-{processId}",
                    $@"c:\tests\long-soak-{processId}.exe",
                    softwareId,
                    $"Long Soak {processId}",
                    "general",
                    "General",
                    35,
                    metrics,
                    50,
                    50,
                    generation,
                    []);
            }
            var processSnapshot = new SchedulingProcessFactSnapshot(
                SamplingObservationStatus.Current,
                generation,
                observedAtUtcTicks,
                checked((uint)processes.Length),
                checked((uint)processes.Length),
                0,
                0,
                metrics,
                metrics,
                processes)
            {
                DatasetObservations = CreateCurrentDatasetObservations(
                    metrics,
                    generation,
                    observedAtUtcTicks)
            };
            permits[index] = new LongSoakCyclePermit(
                sequence,
                shape,
                hardware,
                processSnapshot);
        }
        return permits;
    }

    private static LongSoakRuntimeIdentity CreateLongSoakRuntimeIdentity()
    {
        using var process = Process.GetCurrentProcess();
        var uptime = Environment.TickCount64;
        var bootEstimate = checked(
            DateTimeOffset.UtcNow.UtcDateTime.Ticks
            - uptime * TimeSpan.TicksPerMillisecond);
        var bootBucket = bootEstimate / (10 * TimeSpan.TicksPerSecond);
        var bootCanonical = FormattableString.Invariant(
            $"{Environment.MachineName}|{bootBucket}");
        return new LongSoakRuntimeIdentity(
            Environment.MachineName,
            process.Id,
            process.StartTime.ToUniversalTime().Ticks,
            uptime,
            bootEstimate,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(bootCanonical))),
            Guid.NewGuid().ToString("N"),
            Guid.NewGuid().ToString("N"),
            Guid.NewGuid().ToString("N"));
    }

    private static LongSoakAssessment AssessLongSoakSamples(
        LongSoakEvidencePlan plan,
        LongSoakProfileDefinition profile,
        IReadOnlyList<LongSoakCompactSample> samples,
        IReadOnlyList<LongSoakOpportunity> opportunities,
        LongSoakClockReading started,
        LongSoakClockReading completed,
        int scheduledOpportunities,
        int providerPairs,
        int materializedRecords,
        int writerAdmissions,
        int committedSamples)
    {
        if (samples.Count == 0 || samples.Count != opportunities.Count)
        {
            throw new InvalidDataException("The long-soak sample/opportunity corpus is empty or split.");
        }
        var gapValues = new List<double>(Math.Max(0, opportunities.Count - 1));
        for (var index = 0; index < opportunities.Count; index++)
        {
            var opportunity = opportunities[index];
            var sample = samples[index];
            if (opportunity.Sequence != index + 1
                || sample.Sequence != index + 1
                || opportunity.Clock.ActiveTime100Nanoseconds
                    != sample.OpportunityActiveTime100Nanoseconds
                || opportunity.Clock.QpcTicks != sample.OpportunityQpcTicks
                || opportunity.Clock.UtcTicks != sample.OpportunityUtcTicks)
            {
                throw new InvalidDataException(
                    "The long-soak sample does not bind its exact schedule opportunity.");
            }
            if (index > 0)
            {
                var previous = opportunities[index - 1];
                if (opportunity.Clock.ActiveTime100Nanoseconds
                        <= previous.Clock.ActiveTime100Nanoseconds
                    || opportunity.Clock.QpcTicks <= previous.Clock.QpcTicks
                    || opportunity.Clock.UtcTicks < previous.Clock.UtcTicks)
                {
                    throw new InvalidDataException(
                        "A long-soak opportunity clock is duplicate or non-monotonic.");
                }
                gapValues.Add(
                    (opportunity.Clock.ActiveTime100Nanoseconds
                     - previous.Clock.ActiveTime100Nanoseconds) / 10_000D);
            }
        }

        var minimumGap = gapValues.Count == 0 ? 0D : gapValues.Min();
        var maximumGap = gapValues.Count == 0 ? 0D : gapValues.Max();
        var maximumLateness = opportunities.Max(static value => value.LatenessMilliseconds);
        var finalTail =
            (completed.ActiveTime100Nanoseconds
             - opportunities[^1].Clock.ActiveTime100Nanoseconds) / 10_000D;
        var trendWindow = profile.TrendWindowSize;
        if (samples.Count < checked(2 * trendWindow))
        {
            throw new InvalidDataException(
                "The long-soak sample corpus is too small for its fixed trend windows.");
        }
        var measured = samples.Skip(profile.WarmupCycleCount).ToArray();
        if (measured.Length < checked(2 * trendWindow))
        {
            throw new InvalidDataException(
                "The post-warmup long-soak corpus is too small for its trend windows.");
        }
        var early = measured.Take(trendWindow).ToArray();
        var late = samples.TakeLast(trendWindow).ToArray();
        var managedEarly = PercentileLong(
            early.Select(static sample => sample.ManagedHeapBytes),
            0.50D);
        var managedLate = PercentileLong(
            late.Select(static sample => sample.ManagedHeapBytes),
            0.50D);
        var privateEarly = PercentileLong(
            early.Select(static sample => sample.PrivateBytes),
            0.50D);
        var privateLate = PercentileLong(
            late.Select(static sample => sample.PrivateBytes),
            0.50D);
        var workingEarly = PercentileLong(
            early.Select(static sample => sample.WorkingSetBytes),
            0.50D);
        var workingLate = PercentileLong(
            late.Select(static sample => sample.WorkingSetBytes),
            0.50D);
        var handleEarly = PercentileDouble(
            early.Select(static sample => (double)sample.HandleCount),
            0.50D);
        var handleLate = PercentileDouble(
            late.Select(static sample => (double)sample.HandleCount),
            0.50D);
        var threadEarly = PercentileDouble(
            early.Select(static sample => (double)sample.ThreadCount),
            0.50D);
        var threadLate = PercentileDouble(
            late.Select(static sample => (double)sample.ThreadCount),
            0.50D);
        var trend = new LongSoakResourceTrend(
            trendWindow,
            managedEarly,
            managedLate,
            managedLate - managedEarly,
            privateEarly,
            privateLate,
            privateLate - privateEarly,
            workingEarly,
            workingLate,
            workingLate - workingEarly,
            handleEarly,
            handleLate,
            handleLate - handleEarly,
            threadEarly,
            threadLate,
            threadLate - threadEarly);
        var elapsedP99 = PercentileDouble(
            measured.Select(static sample => sample.ElapsedMilliseconds),
            0.99D);
        var cpuP99 = PercentileDouble(
            measured.Select(static sample => sample.ProcessCpuMilliseconds),
            0.99D);
        var allocationP99 = PercentileLong(
            measured.Select(static sample => sample.AllocatedBytes),
            0.99D);
        var maximumGc = measured.Max(static sample => checked(
            sample.Gen0Collections + sample.Gen1Collections + sample.Gen2Collections));
        var exactStages = scheduledOpportunities == samples.Count
            && providerPairs == samples.Count
            && materializedRecords == samples.Count
            && writerAdmissions == samples.Count
            && committedSamples == samples.Count;
        var passed = samples.Count >= profile.MinimumCycleCount
            && samples.Count <= profile.MaximumCycleCount
            && exactStages
            && (gapValues.Count == 0
                || minimumGap >= profile.MinimumOpportunityGapMilliseconds)
            && maximumGap <= profile.MaximumOpportunityGapMilliseconds
            && maximumLateness <= profile.MaximumOpportunityLatenessMilliseconds
            && finalTail <= profile.MaximumOpportunityGapMilliseconds
            && elapsedP99 <= profile.Budgets.ElapsedP99Milliseconds
            && cpuP99 <= profile.Budgets.ProcessCpuP99Milliseconds
            && allocationP99 <= profile.Budgets.AllocationP99Bytes
            && maximumGc <= profile.Budgets.MaximumGcCollectionsPerCycle
            && trend.ManagedHeapGrowth <= profile.Budgets.ManagedHeapGrowthBytes
            && trend.PrivateBytesGrowth <= profile.Budgets.PrivateBytesGrowthBytes
            && trend.WorkingSetGrowth <= profile.Budgets.WorkingSetGrowthBytes
            && trend.HandleGrowth <= profile.Budgets.HandleGrowth
            && trend.ThreadGrowth <= profile.Budgets.ThreadGrowth
            && samples.All(static sample => sample.EffectsAllZero)
            && completed.ActiveTime100Nanoseconds >= started.ActiveTime100Nanoseconds;
        return new LongSoakAssessment(
            1,
            LongSoakAssessmentContract,
            plan.EvidenceRunId,
            plan.Profile,
            samples.Count,
            profile.WarmupCycleCount,
            measured.Length,
            1,
            samples.Count,
            scheduledOpportunities,
            scheduledOpportunities,
            providerPairs,
            materializedRecords,
            writerAdmissions,
            committedSamples,
            minimumGap,
            maximumGap,
            maximumLateness,
            finalTail,
            elapsedP99,
            cpuP99,
            allocationP99,
            maximumGc,
            trend,
            passed);
    }

    private static double PercentileDouble(IEnumerable<double> source, double percentile)
    {
        var values = source.Order().ToArray();
        if (values.Length == 0 || percentile is <= 0 or > 1)
        {
            throw new InvalidDataException("A long-soak percentile input is invalid.");
        }
        var index = Math.Clamp(
            checked((int)Math.Ceiling(values.Length * percentile) - 1),
            0,
            values.Length - 1);
        return values[index];
    }

    private static long PercentileLong(IEnumerable<long> source, double percentile)
    {
        var values = source.Order().ToArray();
        if (values.Length == 0 || percentile is <= 0 or > 1)
        {
            throw new InvalidDataException("A long-soak percentile input is invalid.");
        }
        var index = Math.Clamp(
            checked((int)Math.Ceiling(values.Length * percentile) - 1),
            0,
            values.Length - 1);
        return values[index];
    }

    private static void StabilizeLongSoakResourceSnapshot()
    {
        GC.Collect(
            GC.MaxGeneration,
            GCCollectionMode.Forced,
            blocking: true,
            compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(
            GC.MaxGeneration,
            GCCollectionMode.Forced,
            blocking: true,
            compacting: true);
    }

    private static string ResolveLongSoakResourceStabilization(
        LongSoakProfileDefinition profile,
        int sequence)
    {
        var earlySequence = checked(profile.WarmupCycleCount + 1);
        var lateSequence = checked(
            profile.MinimumCycleCount - profile.TrendWindowSize + 1);
        return sequence == earlySequence || sequence == lateSequence
            ? "trend-window-start"
            : "none";
    }

    private static bool HasLongSoakQuiescentStopWindow(
        LongSoakProfileDefinition profile,
        LongSoakBoundedSnapshotSource source,
        HostManagerVersionedWakeDeadline deadline)
    {
        var completedCount = source.CompletedCount;
        var snapshot = deadline.Snapshot;
        var now = deadline.GetTimestamp();
        return IsLongSoakQuiescentStopWindow(
            profile.NormalIntervalMilliseconds,
            completedCount,
            snapshot,
            now,
            deadline.TimestampFrequency);
    }

    private static bool IsLongSoakQuiescentStopWindow(
        int normalIntervalMilliseconds,
        int completedCount,
        HostManagerWakeDeadlineSnapshot snapshot,
        long now,
        long timestampFrequency)
    {
        if (!snapshot.HasDeadline
            || snapshot.Version != checked((ulong)completedCount)
            || snapshot.DeadlineTimestamp <= now)
        {
            return false;
        }

        var minimumLeadMilliseconds = Math.Clamp(
            normalIntervalMilliseconds / 4,
            25,
            1_000);
        var minimumLeadTicks = checked((long)(
            ((Int128)minimumLeadMilliseconds * timestampFrequency + 999)
            / 1_000));
        return snapshot.DeadlineTimestamp - now >= minimumLeadTicks;
    }

    private static IReadOnlyDictionary<string, object?> ReadDictionary(
        IReadOnlyDictionary<string, object?> owner,
        string name)
    {
        if (!owner.TryGetValue(name, out var value)
            || value is not IReadOnlyDictionary<string, object?> dictionary)
        {
            throw new InvalidDataException(
                $"The long-soak record property '{name}' is not a dictionary.");
        }
        return dictionary;
    }

    private static string ReadString(
        IReadOnlyDictionary<string, object?> owner,
        string name)
        => ReadRequiredValue(owner, name) as string
            ?? throw new InvalidDataException(
                $"The long-soak record property '{name}' is not a string.");

    private static bool ReadBool(
        IReadOnlyDictionary<string, object?> owner,
        string name)
        => ReadRequiredValue(owner, name) is bool value
            ? value
            : throw new InvalidDataException(
                $"The long-soak record property '{name}' is not a Boolean.");

    private static int ReadInt(
        IReadOnlyDictionary<string, object?> owner,
        string name)
        => ReadRequiredValue(owner, name) is int value
            ? value
            : throw new InvalidDataException(
                $"The long-soak record property '{name}' is not an Int32.");

    private static uint ReadUInt(
        IReadOnlyDictionary<string, object?> owner,
        string name)
        => ReadRequiredValue(owner, name) is uint value
            ? value
            : throw new InvalidDataException(
                $"The long-soak record property '{name}' is not a UInt32.");

    private static long ReadLong(
        IReadOnlyDictionary<string, object?> owner,
        string name)
        => ReadRequiredValue(owner, name) is long value
            ? value
            : throw new InvalidDataException(
                $"The long-soak record property '{name}' is not an Int64.");

    private static double ReadDouble(
        IReadOnlyDictionary<string, object?> owner,
        string name)
        => ReadRequiredValue(owner, name) is double value && double.IsFinite(value)
            ? value
            : throw new InvalidDataException(
                $"The long-soak record property '{name}' is not a finite Double.");

    private static object ReadRequiredValue(
        IReadOnlyDictionary<string, object?> owner,
        string name)
    {
        if (!owner.TryGetValue(name, out var value) || value is null)
        {
            throw new InvalidDataException(
                $"The long-soak record property '{name}' is missing or null.");
        }
        return value;
    }

    private static LongSoakFileIdentity WriteLongSoakJsonFile(
        string evidenceRoot,
        string relativePath,
        object value)
    {
        var root = ValidateScoreOnlyPerformanceEvidenceRoot(evidenceRoot);
        var path = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!string.Equals(Path.GetDirectoryName(path), root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("A long-soak terminal file escaped its run root.");
        }
        var image = JsonSerializer.SerializeToUtf8Bytes(value, LongSoakJsonOptions);
        if (image.Length is <= 0 or > 1_048_576)
        {
            throw new InvalidDataException("A long-soak terminal file exceeded its byte bound.");
        }
        using (var stream = new FileStream(
                   path,
                   FileMode.CreateNew,
                   FileAccess.Write,
                   FileShare.None,
                   bufferSize: 4096,
                   FileOptions.WriteThrough))
        {
            stream.Write(image);
            stream.WriteByte((byte)'\n');
            stream.Flush(flushToDisk: true);
        }
        return CaptureLongSoakFileIdentity(root, relativePath);
    }

    private static LongSoakFileIdentity CaptureLongSoakFileIdentity(
        string evidenceRoot,
        string relativePath)
    {
        var root = ValidateScoreOnlyPerformanceEvidenceRoot(evidenceRoot);
        var path = Path.GetFullPath(Path.Combine(root, relativePath));
        var file = new FileInfo(path);
        if (!file.Exists
            || file.Attributes.HasFlag(FileAttributes.ReparsePoint)
            || !string.Equals(Path.GetDirectoryName(path), root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("A long-soak terminal file is missing or redirected.");
        }
        return new LongSoakFileIdentity(
            relativePath.Replace('\\', '/'),
            file.Length,
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
    }

    private static void WriteLongSoakLoopReceipt(
        string evidenceRoot,
        LongSoakEvidencePlan plan,
        LongSoakProfileDefinition profile,
        LongSoakRuntimeIdentity runtimeIdentity,
        LongSoakClockAssessment clock,
        LongSoakAssessment assessment,
        LongSoakShardIndex shardIndex,
        LongSoakFileIdentity planIdentity,
        LongSoakFileIdentity indexIdentity,
        LongSoakFileIdentity assessmentIdentity,
        HostManagerSmartCoordinatorLifecycleState lifecycleBefore,
        HostManagerSmartCoordinatorLifecycleState lifecycleAfter,
        IReadOnlyList<HostManagerSmartCoordinatorShutdownPoint> shutdownPoints,
        TimeSpan stopElapsed,
        ulong deadlineVersionBeforeStop,
        LongSoakBoundedSnapshotSource source,
        LongSoakCompactSampleObserver observer,
        NativeSmartCoordinatorCapacity capacity,
        HostedScoreOnlyEffectCounterSnapshot effectCounters,
        ulong rollbackIncarnationBefore,
        ulong rollbackIncarnationAfter,
        string durableBeforeSha256,
        string durableAfterSha256,
        int durableMutationCount,
        long uptimeAtEnd,
        long bootEstimateAtEnd)
    {
        var zeroEffectCanonical = effectCounters.ToCanonicalString();
        var zeroEffectSha256 = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(zeroEffectCanonical)));
        WriteLongSoakJsonFile(
            evidenceRoot,
            "loop-receipt.json",
            new
            {
                schemaVersion = 1,
                contract = LongSoakLoopReceiptContract,
                evidenceRunId = plan.EvidenceRunId,
                profile = plan.Profile,
                durationQualified = profile.IsFormal,
                requestedDurationQualification = plan.RequestedDurationQualification,
                allowLongRunning = plan.AllowLongRunning,
                scope = "one persistent S0 testhost, production NonOwningHostedService scheduled loop, bounded scripted providers, and fail-fast deny-all effects",
                measurementBoundary = LongSoakMeasurementBoundary,
                resourceStabilization = new
                {
                    mode = "blocking-compacting-full-gc-finalizer-drain-second-full-gc-at-trend-window-starts",
                    earlySequence = profile.WarmupCycleCount + 1,
                    lateSequence = profile.MinimumCycleCount - profile.TrendWindowSize + 1,
                    productionProfilerExcluded = true
                },
                runtimeIdentity,
                runtimeContinuity = new
                {
                    uptimeAtEndMilliseconds = uptimeAtEnd,
                    bootEstimateAtEndUtcTicks = bootEstimateAtEnd,
                    processIdAtEnd = Environment.ProcessId,
                    processContinuous = Environment.ProcessId == runtimeIdentity.TestHostProcessId,
                    bootContinuous = Math.Abs(
                        bootEstimateAtEnd - runtimeIdentity.BootEstimateUtcTicks)
                        <= 5 * TimeSpan.TicksPerSecond
                },
                clock,
                lifecycle = new
                {
                    startedViaNonOwningHostedService = true,
                    before = lifecycleBefore.ToString(),
                    after = lifecycleAfter.ToString(),
                    shutdownPoints = shutdownPoints.Select(static point => point.ToString()).ToArray(),
                    stopElapsedMilliseconds = stopElapsed.TotalMilliseconds,
                    maximumStopElapsedMilliseconds = 10_000D,
                    exactWorkerCompleted = true
                },
                opportunities = new
                {
                    scheduled = source.HardwareCaptureCount,
                    cycleEntered = source.HardwareCaptureCount,
                    providerPairs = source.ProcessCaptureCount,
                    profilerMaterialized = observer.MaterializedRecordCount,
                    writerAdmitted = observer.WriterAdmissionCount,
                    shardCommitted = observer.CommittedSampleCount,
                    deadlineVersionBeforeStop,
                    explicitlyFailed = 0,
                    missed = 0,
                    catchUp = false
                },
                workspace = new
                {
                    stableAcrossRun = true,
                    inputRowCapacity = capacity.InputRowCapacity,
                    actionCapacity = capacity.ActionCapacity,
                    feedbackCapacity = capacity.FeedbackCapacity,
                    snapshotRowCapacity = capacity.SnapshotRowCapacity
                },
                zeroEffects = new
                {
                    canonical = zeroEffectCanonical,
                    sha256 = zeroEffectSha256,
                    perSampleSha256 = zeroEffectSha256,
                    allZero = effectCounters.AllZero
                },
                durableState = new
                {
                    beforeSha256 = durableBeforeSha256,
                    afterSha256 = durableAfterSha256,
                    identical = durableBeforeSha256 == durableAfterSha256,
                    mutationCount = durableMutationCount,
                    rollbackIncarnationBefore,
                    rollbackIncarnationAfter,
                    rollbackIncarnationIdentical =
                        rollbackIncarnationBefore == rollbackIncarnationAfter
                },
                files = new
                {
                    plan = planIdentity,
                    shardIndex = indexIdentity,
                    assessment = assessmentIdentity,
                    shardCount = shardIndex.Shards.Length,
                    shardRecordCount = shardIndex.RecordCount,
                    shardBytes = shardIndex.TotalBytes
                },
                assessment,
                satisfied = assessment.Passed
                    && effectCounters.AllZero
                    && durableBeforeSha256 == durableAfterSha256
                    && durableMutationCount == 0
            });
    }

    private sealed class LongSoakBoundedSnapshotSource(
        LongSoakProfileDefinition profile,
        LongSoakCyclePermit[] permits)
        : IMetricSampler,
          ISchedulingProcessFactSource,
          IMetricSnapshotObservationSource,
          ISchedulingProcessFactObservationSource
    {
        private readonly object sync = new();
        private readonly HostedHardwareObservationCache hardwareCache = new();
        private readonly LongSoakOpportunity?[] opportunities =
            new LongSoakOpportunity?[permits.Length];
        private LongSoakClockReading started;
        private string? ledgerId;
        private int completedCount;
        private int pendingSequence;
        private Exception? failure;

        internal void ConfigureHistory(CompiledDataHistoryPlan history)
            => hardwareCache.Configure(history);

        internal CpuCoreResidencySnapshot? ReadCpuCoreResidency()
        {
            lock (sync)
            {
                return completedCount == 0 ? null : CpuCoreResidencyTestValues.OneCore(permits[completedCount - 1].ProcessSnapshot);
            }
        }

        internal int HardwareCaptureCount { get; private set; }
        internal int ProcessCaptureCount { get; private set; }
        internal int CompletedCount
        {
            get
            {
                lock (sync)
                {
                    return completedCount;
                }
            }
        }

        internal void Arm(LongSoakClockReading startedClock, string opportunityLedgerId)
        {
            lock (sync)
            {
                if (ledgerId is not null || completedCount != 0 || pendingSequence != 0)
                {
                    throw new InvalidOperationException(
                        "The long-soak opportunity source can only be armed once while idle.");
                }
                started = startedClock;
                ledgerId = opportunityLedgerId;
            }
        }

        internal void ThrowIfFaulted()
        {
            lock (sync)
            {
                if (failure is not null)
                {
                    throw new InvalidDataException(
                        "The long-soak opportunity source failed.",
                        failure);
                }
            }
        }

        internal LongSoakOpportunity[] AssertCompleteAndCapture()
        {
            lock (sync)
            {
                ThrowIfFaulted();
                if (pendingSequence != 0
                    || HardwareCaptureCount != ProcessCaptureCount
                    || completedCount != ProcessCaptureCount
                    || completedCount < profile.MinimumCycleCount
                    || completedCount > profile.MaximumCycleCount)
                {
                    throw new InvalidDataException(
                        $"The long-soak provider opportunity ledger is incomplete or out of bounds: " +
                        $"pending={pendingSequence}; hardware={HardwareCaptureCount}; " +
                        $"process={ProcessCaptureCount}; completed={completedCount}; " +
                        $"expected={profile.MinimumCycleCount}..{profile.MaximumCycleCount}.");
                }
                return opportunities
                    .Take(completedCount)
                    .Select(static value => value
                        ?? throw new InvalidDataException(
                            "The long-soak opportunity ledger contains a gap."))
                    .ToArray();
            }
        }

        internal LongSoakOpportunity GetCompletedOpportunity(int sequence)
        {
            lock (sync)
            {
                if (sequence < 1 || sequence > completedCount)
                {
                    throw new InvalidDataException(
                        "The profiler record does not have a completed schedule opportunity.");
                }
                return opportunities[sequence - 1]
                    ?? throw new InvalidDataException(
                        "The completed schedule opportunity is unavailable.");
            }
        }

        public Task<HardwareMetricSnapshot> GetSnapshotAsync(
            CancellationToken cancellationToken)
            => throw new InvalidOperationException(
                "The long-soak source requires the production capture path.");

        public Task<HardwareMetricSnapshot> GetSnapshotAsync(
            MetricSampleRequest request,
            CancellationToken cancellationToken)
            => throw new InvalidOperationException(
                "The long-soak source requires the production capture path.");

        public Task<HardwareMetricSnapshot> CaptureSnapshotAsync(
            MetricSampleRequest request,
            CancellationToken cancellationToken)
            => throw new InvalidOperationException(
                "The long-soak source forbids direct hardware capture.");

        public Task<SchedulingProcessFactSnapshot> CaptureAsync(
            SchedulingProcessFactRequest request,
            CancellationToken cancellationToken)
            => throw new InvalidOperationException(
                "The long-soak source forbids direct process capture.");

        public IDisposable AcquireSubscription(
            string subscriptionId,
            MetricSampleRequest request,
            TimeSpan refreshInterval)
            => Subscription.Instance;

        public IDisposable AcquireSubscription(
            string subscriptionId,
            SchedulingProcessMetricMask metricMask,
            TimeSpan refreshInterval)
            => Subscription.Instance;

        public HardwareMetricSnapshot? ReadLatest(MetricSampleRequest request)
        {
            lock (sync)
            {
                try
                {
                    if (ledgerId is null
                        || pendingSequence != 0
                        || completedCount >= permits.Length)
                    {
                        throw new InvalidDataException(
                            "The long-soak scheduled opportunity is unarmed, overlapping, or over its bound.");
                    }
                    var sequence = completedCount + 1;
                    var clock = CaptureLongSoakClock();
                    var planned = checked(
                        started.ActiveTime100Nanoseconds
                        + checked((ulong)(sequence - 1)
                            * (ulong)profile.NormalIntervalMilliseconds
                            * 10_000UL));
                    if (clock.ActiveTime100Nanoseconds + 250_000UL < planned)
                    {
                        throw new InvalidDataException(
                            "The production scheduled loop entered before its fixed opportunity grid.");
                    }
                    var lateness = clock.ActiveTime100Nanoseconds <= planned
                        ? 0D
                        : (clock.ActiveTime100Nanoseconds - planned) / 10_000D;
                    if (lateness > profile.MaximumOpportunityLatenessMilliseconds)
                    {
                        throw new InvalidDataException(
                            "The production scheduled loop exceeded its opportunity lateness bound.");
                    }
                    opportunities[sequence - 1] = new LongSoakOpportunity(
                        sequence,
                        clock,
                        planned,
                        lateness);
                    pendingSequence = sequence;
                    HardwareCaptureCount++;
                    return hardwareCache.Publish(
                        permits[sequence - 1].HardwareSnapshot,
                        request);
                }
                catch (Exception exception)
                {
                    failure ??= exception;
                    throw;
                }
            }
        }

        public SchedulingProcessFactSnapshot? ReadLatest(
            SchedulingProcessFactRequest request)
        {
            lock (sync)
            {
                try
                {
                    if (pendingSequence != completedCount + 1)
                    {
                        throw new InvalidDataException(
                            "The long-soak process capture is not paired with its schedule opportunity.");
                    }
                    var permit = permits[pendingSequence - 1];
                    pendingSequence = 0;
                    completedCount++;
                    ProcessCaptureCount++;
                    return SchedulingProcessFactTestProjection.Project(
                        permit.ProcessSnapshot,
                        request);
                }
                catch (Exception exception)
                {
                    failure ??= exception;
                    throw;
                }
            }
        }

        private sealed class Subscription : IDisposable
        {
            internal static Subscription Instance { get; } = new();
            public void Dispose()
            {
            }
        }
    }

    private sealed class LongSoakCompactSampleObserver(
        LongSoakEvidencePlan plan,
        LongSoakProfileDefinition profile,
        LongSoakBoundedSnapshotSource source,
        ScoreOnlyCoordinatorFixture fixture,
        LongSoakSampleShardWriter shardWriter,
        LongSoakRuntimeIdentity runtimeIdentity)
        : IScoreOnlyDiagnosticRecordObserver,
          IDisposable
    {
        private readonly object sync = new();
        private readonly Process process = Process.GetCurrentProcess();
        private readonly LongSoakCompactSample?[] samples =
            new LongSoakCompactSample?[profile.MaximumCycleCount];
        private Exception? failure;
        private string? producerInstanceId;
        private long previousCompletedQpcTicks;

        internal int MaterializedRecordCount { get; private set; }
        internal int WriterAdmissionCount { get; private set; }
        internal int CommittedSampleCount { get; private set; }

        public void Observe(DebugDiagnosticLogRecord record)
        {
            try
            {
                lock (sync)
                {
                    if (failure is not null)
                    {
                        return;
                    }
                    MaterializedRecordCount++;
                    var sequence = checked((int)ReadLong(record.Properties, "cycleSequence"));
                    if (sequence != MaterializedRecordCount
                        || sequence > profile.MaximumCycleCount)
                    {
                        throw new InvalidDataException(
                            "The long-soak profiler sequence is duplicate, skipped, or over its bound.");
                    }
                    var producer = ReadString(record.Properties, "producerInstanceId");
                    var outcome = ReadString(record.Properties, "outcome");
                    var trigger = ReadString(record.Properties, "trigger");
                    var scoreOnly = ReadBool(record.Properties, "scoreOnly");
                    var mode = ReadString(record.Properties, "mode");
                    producerInstanceId ??= producer;
                    if (!string.Equals(producerInstanceId, producer, StringComparison.Ordinal)
                        || record.Category != "smart-optimization"
                        || record.EventName != "cycle-performance"
                        || outcome != "completed"
                        || trigger != "scheduled"
                        || !scoreOnly
                        || mode != "smart")
                    {
                        throw new InvalidDataException(
                            "The long-soak profiler record identity or outcome is invalid: " +
                            FormattableString.Invariant(
                                $"expectedProducer={producerInstanceId}; actualProducer={producer}; category={record.Category}; eventName={record.EventName}; outcome={outcome}; trigger={trigger}; scoreOnly={scoreOnly}; mode={mode}; message={ReadString(record.Properties, "message")}"));
                    }
                    var opportunity = source.GetCompletedOpportunity(sequence);
                    var shape = ResolveLongSoakShape(plan.Profile, sequence);
                    var startedQpc = ReadLong(record.Properties, "cycleStartedAtQpcTicks");
                    var completedQpc = ReadLong(record.Properties, "cycleCompletedAtQpcTicks");
                    var qpcFrequency = ReadLong(record.Properties, "qpcFrequency");
                    if (qpcFrequency != Stopwatch.Frequency
                        || startedQpc <= 0
                        || completedQpc < startedQpc
                        || startedQpc < previousCompletedQpcTicks)
                    {
                        throw new InvalidDataException(
                            "The long-soak profiler QPC interval is invalid or overlapping.");
                    }
                    previousCompletedQpcTicks = completedQpc;
                    var counts = ReadDictionary(record.Properties, "counts");
                    foreach (var name in new[]
                             {
                                  "sampleTargets",
                                  "sampleProcesses",
                                  "scoredProcesses",
                                 "policyProcesses"
                             })
                    {
                        if (ReadInt(counts, name) != shape.ProcessCount)
                        {
                            throw new InvalidDataException(
                                "The long-soak profiler process topology is not exact.");
                        }
                    }
                    if (ReadInt(counts, "schedulingTargets") != 2 * shape.ProcessCount)
                    {
                        throw new InvalidDataException(
                            "The long-soak native scheduling topology is not exact.");
                    }
                    var policyChanges = ReadInt(counts, "policyChanges");
                    var pendingChanges = ReadInt(counts, "pendingChanges");
                    var resourceQueueActions = ReadInt(counts, "resourceQueueActions");
                    var changedCount = ReadInt(counts, "changedCount");
                    var appliedTargets = ReadInt(counts, "appliedTargets");
                    var appliedPlacements = ReadInt(counts, "appliedPlacements");
                    if (policyChanges != 0
                        || pendingChanges != 0
                        || resourceQueueActions != 0
                        || changedCount != 0
                        || appliedTargets != 0
                        || appliedPlacements != 0)
                    {
                        throw new InvalidDataException(
                            "The long-soak score-only cycle produced an action or pending effect.");
                    }
                    var guards = ReadDictionary(record.Properties, "guards");
                    if (!ReadBool(guards, "sampling")
                        || ReadBool(guards, "scoring") != (shape.ProcessCount > 0)
                        || !ReadBool(guards, "policyExecution")
                        || ReadBool(guards, "hardwarePlacement"))
                    {
                        throw new InvalidDataException(
                            "The long-soak production guards are not exact.");
                    }
                    var details = ReadDictionary(record.Properties, "details");
                    if (ReadUInt(details, "nativeInputCount")
                            != checked((uint)(2 * shape.ProcessCount))
                        || ReadUInt(details, "nativeSoftwareCount")
                            != checked((uint)shape.ProcessCount)
                        || ReadUInt(details, "nativeInvalidFactCount") != 0
                        || ReadUInt(details, "nativeFeedbackCount") != 0)
                    {
                        throw new InvalidDataException(
                            "The long-soak native result envelope is invalid.");
                    }
                    var nativeSnapshot = fixture.Workspace.Snapshot;
                    if (nativeSnapshot.CycleSequence != checked((ulong)sequence)
                        || nativeSnapshot.ProcessCount != checked((uint)shape.ProcessCount)
                        || nativeSnapshot.SoftwareCount != checked((uint)shape.ProcessCount)
                        || nativeSnapshot.ActionCount != 0)
                    {
                        throw new InvalidDataException(
                            "The long-soak native workspace readback is not exact and action-free.");
                    }
                    var gcCollections = ReadDictionary(
                        record.Properties,
                        "hostGcCollections");
                    var effectCounters = fixture.CaptureHostedScoreOnlyEffectCounters();
                    if (!effectCounters.AllZero)
                    {
                        throw new InvalidDataException(
                            "A long-soak deny-all effect surface was attempted.");
                    }
                    var zeroEffectCanonical = effectCounters.ToCanonicalString();
                    var zeroEffectSha256 = Convert.ToHexString(SHA256.HashData(
                        Encoding.UTF8.GetBytes(zeroEffectCanonical)));
                    var resourceStabilization = ResolveLongSoakResourceStabilization(
                        profile,
                        sequence);
                    if (resourceStabilization == "trend-window-start")
                    {
                        StabilizeLongSoakResourceSnapshot();
                    }
                    var sampledClock = CaptureLongSoakClock();
                    process.Refresh();
                    var gcInfo = GC.GetGCMemoryInfo();
                    var sample = new LongSoakCompactSample(
                        1,
                        LongSoakSampleContract,
                        plan.EvidenceRunId,
                        plan.Profile,
                        sequence,
                        producer,
                        runtimeIdentity.OpportunityLedgerId,
                        runtimeIdentity.WriterInstanceId,
                        runtimeIdentity.TestHostInstanceId,
                        runtimeIdentity.TestHostProcessId,
                        runtimeIdentity.TestHostStartUtcTicks,
                        runtimeIdentity.BootIdentitySha256,
                        "scheduled",
                        shape.Id,
                        shape.ProcessCount,
                        opportunity.PlannedActiveTime100Nanoseconds,
                        opportunity.Clock.ActiveTime100Nanoseconds,
                        opportunity.Clock.QpcTicks,
                        opportunity.Clock.UtcTicks,
                        opportunity.LatenessMilliseconds,
                        startedQpc,
                        completedQpc,
                        qpcFrequency,
                        sampledClock.ActiveTime100Nanoseconds,
                        sampledClock.QpcTicks,
                        sampledClock.UtcTicks,
                        ReadDouble(record.Properties, "elapsedMs"),
                        ReadDouble(record.Properties, "hostProcessCpuMs"),
                        ReadLong(record.Properties, "hostProcessAllocatedBytes"),
                        ReadInt(gcCollections, "gen0"),
                        ReadInt(gcCollections, "gen1"),
                        ReadInt(gcCollections, "gen2"),
                        policyChanges,
                        pendingChanges,
                        resourceQueueActions,
                        changedCount,
                        appliedTargets,
                        appliedPlacements,
                        EffectsAllZero: true,
                        zeroEffectSha256,
                        nativeSnapshot.ActionCount,
                        nativeSnapshot.ProcessCount,
                        nativeSnapshot.SoftwareCount,
                        ResourceStabilization: resourceStabilization,
                        GC.GetTotalMemory(forceFullCollection: false),
                        gcInfo.HeapSizeBytes,
                        gcInfo.FragmentedBytes,
                        process.PrivateMemorySize64,
                        process.WorkingSet64,
                        process.HandleCount,
                        process.Threads.Count);
                    WriterAdmissionCount++;
                    shardWriter.Append(sample);
                    CommittedSampleCount++;
                    samples[sequence - 1] = sample;
                }
            }
            catch (Exception exception)
            {
                lock (sync)
                {
                    failure ??= exception;
                }
            }
        }

        internal void ThrowIfFaulted()
        {
            lock (sync)
            {
                if (failure is not null)
                {
                    throw new InvalidDataException(
                        "The long-soak compact observer failed.",
                        failure);
                }
            }
        }

        internal LongSoakCompactSample[] AssertCompleteAndCapture(int expectedCount)
        {
            lock (sync)
            {
                ThrowIfFaulted();
                if (MaterializedRecordCount != expectedCount
                    || WriterAdmissionCount != expectedCount
                    || CommittedSampleCount != expectedCount)
                {
                    throw new InvalidDataException(
                        "The long-soak observer stage ledger is incomplete.");
                }
                return samples
                    .Take(expectedCount)
                    .Select(static value => value
                        ?? throw new InvalidDataException(
                            "The long-soak compact sample corpus contains a gap."))
                    .ToArray();
            }
        }

        public void Dispose() => process.Dispose();
    }

    private sealed class LongSoakDurableMutationProbe : IDisposable
    {
        private readonly object sync = new();
        private readonly FileSystemWatcher watcher;
        private readonly string root;
        private readonly List<string> mutations = [];
        private Exception? failure;
        private int mutationCount;
        private bool stopped;

        internal LongSoakDurableMutationProbe(string root)
        {
            this.root = root;
            watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName
                    | NotifyFilters.DirectoryName
                    | NotifyFilters.CreationTime
                    | NotifyFilters.LastWrite
                    | NotifyFilters.Size,
                InternalBufferSize = 16 * 1024
            };
            watcher.Changed += ObserveMutation;
            watcher.Created += ObserveMutation;
            watcher.Deleted += ObserveMutation;
            watcher.Renamed += ObserveMutation;
            watcher.Error += (_, args) =>
            {
                lock (sync)
                {
                    failure ??= args.GetException();
                }
            };
            watcher.EnableRaisingEvents = true;
        }

        internal int MutationCount
        {
            get
            {
                lock (sync)
                {
                    return mutationCount;
                }
            }
        }

        internal void StopAndAssertNoMutations()
        {
            lock (sync)
            {
                if (!stopped)
                {
                    watcher.EnableRaisingEvents = false;
                    stopped = true;
                }
                if (failure is not null || mutationCount != 0)
                {
                    throw new InvalidDataException(
                        "The long-soak durable mutation ledger observed a change or overflow: " +
                        string.Join(", ", mutations),
                        failure);
                }
            }
        }

        private void ObserveMutation(object sender, FileSystemEventArgs args)
        {
            if (args.FullPath.EndsWith(".lock", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            if (args.ChangeType == WatcherChangeTypes.Changed
                && Directory.Exists(args.FullPath))
            {
                return;
            }
            lock (sync)
            {
                mutationCount = checked(mutationCount + 1);
                if (mutations.Count < 16)
                {
                    mutations.Add(
                        $"{args.ChangeType}:{Path.GetRelativePath(root, args.FullPath)}");
                }
            }
        }

        public void Dispose()
        {
            watcher.Dispose();
        }
    }

    private sealed record LongSoakCyclePermit(
        int Sequence,
        LongSoakShape Shape,
        HardwareMetricSnapshot HardwareSnapshot,
        SchedulingProcessFactSnapshot ProcessSnapshot);

    private sealed record LongSoakFileIdentity(
        string Path,
        long Bytes,
        string Sha256);
}
