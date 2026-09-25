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
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Hosting;
using ResourceManager.App.Infrastructure.Diagnostics;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerSmartCoordinatorScoreOnlyCompositionTests
{
    private const int HostedScoreOnlyMaximumProcessCount = 256;
    private const int HostedScoreOnlyWarmupCycleCount = 9;
    private const int HostedScoreOnlyMeasuredCycleCount = 128;
    private const int HostedScoreOnlyNormalIntervalMilliseconds = 60_000;
    private const int HostedScoreOnlyEventIntervalMilliseconds = 25;
    private const int HostedScoreOnlyFirstMeasuredSequence = 10;
    private const int HostedScoreOnlyLastMeasuredSequence = 137;
    private const long HostedScoreOnlyMaximumRawLogBytes = 32L * 1024 * 1024;
    private const string HostedScoreOnlyEvidenceRootEnvironmentVariable =
        "RESOURCE_MANAGER_HOSTED_SCORE_ONLY_PERFORMANCE_EVIDENCE_ROOT";
    private const string HostedScoreOnlyEpochPlanEnvironmentVariable =
        "RESOURCE_MANAGER_HOSTED_SCORE_ONLY_PERFORMANCE_EPOCH_PLAN";
    private const string HostedScoreOnlyEpochPlanContract =
        "non-adapted-hosted-score-only-performance-epoch-plan-v1";
    private const string HostedScoreOnlyMeasurementBoundary =
        "cycle-profiler-after-plan-effect-admission-validation-scope-and-producer-sequence-capture-before-diagnostic-materialization-and-jsonl-transport";

    [Fact]
    public async Task HostedScoreOnlyLoopExercisesDynamicShapesWithoutEffects()
    {
        var evidenceRoot = Environment.GetEnvironmentVariable(
            HostedScoreOnlyEvidenceRootEnvironmentVariable);
        var epochPlanPath = Environment.GetEnvironmentVariable(
            HostedScoreOnlyEpochPlanEnvironmentVariable);
        var plan = LoadHostedScoreOnlyEpochPlan(evidenceRoot, epochPlanPath);
        var permits = CreateHostedScoreOnlyPermits(plan);
        var source = new HostedScoreOnlyEpochSource();
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true,
            performanceLogEnabled: true,
            smartCoordinatorMaximumProcesses: HostedScoreOnlyMaximumProcessCount,
            metricSamplerOverride: source,
            processFactsOverride: source,
            cpuCoreReader: new RecordingCpuCoreResidencyReader(source.ReadCpuCoreResidency),
            normalIntervalMilliseconds: HostedScoreOnlyNormalIntervalMilliseconds,
            eventIntervalMilliseconds: HostedScoreOnlyEventIntervalMilliseconds,
            failFastEffects: true);
        source.ConfigureHistory(fixture.RuntimePlan.HostManager.DataHistory);
        var deadline = ReadPrivateField<HostManagerVersionedWakeDeadline>(
            fixture.Coordinator,
            "schedulingWakeDeadline")
            ?? throw new InvalidDataException(
                "The production hosted-loop wake deadline is unavailable.");
        var shutdownProbe = new HostedScoreOnlyShutdownProbe();
        fixture.Coordinator.TransitionProbe = shutdownProbe;
        fixture.DebugLogWriter.PrepareRecordCapacity(
            HostedScoreOnlyWarmupCycleCount + HostedScoreOnlyMeasuredCycleCount);
        var nativeIdentityCapture = new HostedNativeProcessedIdentityCapture(
            fixture.Workspace,
            HostedScoreOnlyLastMeasuredSequence,
            HostedScoreOnlyMaximumProcessCount);
        fixture.DebugLogWriter.AttachRecordObserver(nativeIdentityCapture);

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
        Assert.Equal(HostManagerSmartCoordinatorLifecycleState.Created, lifecycleBefore);
        Assert.Equal(default, deadline.Snapshot);

        JsonDebugDiagnosticLogWriter? evidenceWriter = null;
        IHost? host = null;
        var forwardingWriterAttached = false;
        var recordObserverAttached = true;
        var hostStopped = false;
        var stopElapsed = TimeSpan.Zero;
        try
        {
            if (!string.IsNullOrWhiteSpace(evidenceRoot))
            {
                evidenceWriter = CreateScoreOnlyPerformanceEvidenceWriter(evidenceRoot);
                await evidenceWriter.StartAsync(CancellationToken.None);
            }

            host = BuildHostedScoreOnlyTestHost(fixture.Coordinator);
            var concrete = host.Services.GetRequiredService<HostManagerSmartCoordinator>();
            var contract = host.Services.GetRequiredService<IHostManagerSmartCoordinator>();
            var hostedOwner = Assert.Single(host.Services.GetServices<IHostedService>());
            var alias = Assert.IsType<NonOwningHostedService<HostManagerSmartCoordinator>>(
                hostedOwner);
            Assert.Same(fixture.Coordinator, concrete);
            Assert.Same(concrete, contract);
            Assert.Same(concrete, alias.Service);

            source.Enqueue(permits[0]);
            using (var startCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            {
                await host.StartAsync(startCancellation.Token);
            }
            Assert.Equal(
                HostManagerSmartCoordinatorLifecycleState.Running,
                fixture.Coordinator.LifecycleState);
            await WaitForHostedScoreOnlyDeadlineVersionAsync(
                deadline,
                expectedVersion: 1,
                TimeSpan.FromSeconds(5));

            for (var sequence = 2; sequence <= HostedScoreOnlyWarmupCycleCount; sequence += 2)
            {
                source.Enqueue(permits[sequence - 1]);
                source.Enqueue(permits[sequence]);
                await fixture.Coordinator.RunOnceAsync(CancellationToken.None);
                await WaitForHostedScoreOnlyDeadlineVersionAsync(
                    deadline,
                    checked((ulong)(sequence + 1)),
                    TimeSpan.FromSeconds(5));
            }

            Assert.Equal(HostedScoreOnlyWarmupCycleCount, source.CompletedCount);
            Assert.Equal(HostedScoreOnlyWarmupCycleCount, fixture.DebugLogWriter.Records.Count);
            Assert.Equal(
                checked((ulong)HostedScoreOnlyWarmupCycleCount),
                deadline.Snapshot.Version);
            fixture.DebugLogWriter.Records.Clear();
            if (evidenceWriter is not null)
            {
                fixture.DebugLogWriter.AttachForwardingWriter(evidenceWriter);
                forwardingWriterAttached = true;
            }

            var measuredStarted = Stopwatch.GetTimestamp();
            for (var sequence = HostedScoreOnlyFirstMeasuredSequence;
                 sequence <= HostedScoreOnlyLastMeasuredSequence;
                 sequence += 2)
            {
                source.Enqueue(permits[sequence - 1]);
                source.Enqueue(permits[sequence]);
                await fixture.Coordinator.RunOnceAsync(CancellationToken.None);
                await WaitForHostedScoreOnlyDeadlineVersionAsync(
                    deadline,
                    checked((ulong)(sequence + 1)),
                    TimeSpan.FromSeconds(5));
            }
            var measuredElapsed = Stopwatch.GetElapsedTime(measuredStarted);
            Assert.InRange(
                measuredElapsed,
                TimeSpan.Zero,
                TimeSpan.FromSeconds(45));

            var records = fixture.DebugLogWriter.Records.ToArray();
            var completedPermits = source.CompletedPermits;
            Assert.Equal(
                checked((ulong)HostedScoreOnlyLastMeasuredSequence),
                deadline.Snapshot.Version);
            Assert.Same(workspaceBefore, fixture.Workspace);
            Assert.Equal(capacityBefore, fixture.Workspace.Capacity);
            Assert.Equal(0U, fixture.Workspace.Snapshot.ProcessCount);
            Assert.Equal(0U, fixture.Workspace.Snapshot.SoftwareCount);
            Assert.Equal(0U, fixture.Workspace.Snapshot.ActionCount);
            Assert.Equal(durableBefore, CaptureDurableFiles(fixture.Root));
            Assert.Equal(
                normalReleaseEvidenceBefore,
                CapturePrivateState(
                    fixture.Coordinator,
                    "hostPublicResourceNormalReleaseEvidence"));

            var stopStarted = Stopwatch.GetTimestamp();
            using (var stopCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            {
                await host.StopAsync(stopCancellation.Token);
            }
            stopElapsed = Stopwatch.GetElapsedTime(stopStarted);
            hostStopped = true;
            Assert.InRange(stopElapsed, TimeSpan.Zero, TimeSpan.FromSeconds(10));
            Assert.Equal(
                HostManagerSmartCoordinatorLifecycleState.Closed,
                fixture.Coordinator.LifecycleState);
            shutdownProbe.AssertExactOrder();
            fixture.DebugLogWriter.DetachRecordObserver(nativeIdentityCapture);
            recordObserverAttached = false;
            var nativeReadbacks = nativeIdentityCapture.AssertCompleteAndExact(
                completedPermits);
            var schedulingAuthority = fixture.Coordinator.SchedulingAuthority;
            Assert.Null(schedulingAuthority.Compute?.Cpu);
            AssertHostedScoreOnlyCorpus(plan, permits, completedPermits, records);
            fixture.AssertNoMemoryCleanupAttemptEntries();
            var effectCounters = fixture.CaptureHostedScoreOnlyEffectCounters();
            effectCounters.AssertAllZero();
            var rollbackIncarnationAfter =
                fixture.StateStore.Current.NativeHostSessionIncarnation;
            Assert.Equal(rollbackIncarnationBefore, rollbackIncarnationAfter);
            Assert.Equal(
                normalReleaseEvidenceBefore,
                CapturePrivateState(
                    fixture.Coordinator,
                    "hostPublicResourceNormalReleaseEvidence"));
            var durableAfterStop = CaptureDurableFiles(fixture.Root);
            Assert.Equal(durableBefore, durableAfterStop);

            if (evidenceWriter is not null)
            {
                await SealHostedScoreOnlyEvidenceAsync(
                    evidenceRoot!,
                    evidenceWriter,
                    records);
                fixture.DebugLogWriter.DetachForwardingWriter(evidenceWriter);
                forwardingWriterAttached = false;
                using var writerStopCancellation =
                    new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await evidenceWriter.StopAsync(writerStopCancellation.Token);

                WriteHostedScoreOnlyLoopReceipt(
                    evidenceRoot!,
                    epochPlanPath!,
                    plan,
                    completedPermits,
                    nativeReadbacks,
                    records,
                    measuredElapsed,
                    stopElapsed,
                    lifecycleBefore,
                    fixture.Coordinator.LifecycleState,
                    shutdownProbe.Points,
                    capacityBefore,
                    effectCounters,
                    rollbackIncarnationBefore,
                    rollbackIncarnationAfter,
                    durableBeforeSha256,
                    ComputeHostedScoreOnlyIdentitySha256(durableAfterStop));
            }
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
            if (forwardingWriterAttached && evidenceWriter is not null)
            {
                try
                {
                    fixture.DebugLogWriter.DetachForwardingWriter(evidenceWriter);
                }
                catch
                {
                }
            }
            if (recordObserverAttached)
            {
                try
                {
                    fixture.DebugLogWriter.DetachRecordObserver(nativeIdentityCapture);
                }
                catch
                {
                }
            }
            if (evidenceWriter is not null)
            {
                try
                {
                    using var writerStopCancellation =
                        new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await evidenceWriter.StopAsync(writerStopCancellation.Token);
                }
                catch
                {
                }
                evidenceWriter.Dispose();
            }
            host?.Dispose();
        }
    }

    private static IHost BuildHostedScoreOnlyTestHost(
        HostManagerSmartCoordinator coordinator)
        => new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton(coordinator);
                services.AddSingleton<IHostManagerSmartCoordinator>(static provider =>
                    provider.GetRequiredService<HostManagerSmartCoordinator>());
                services.AddHostedServiceAlias<HostManagerSmartCoordinator>();
            })
            .Build();

    private static async Task WaitForHostedScoreOnlyDeadlineVersionAsync(
        HostManagerVersionedWakeDeadline deadline,
        ulong expectedVersion,
        TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            while (true)
            {
                var snapshot = deadline.Snapshot;
                if (snapshot.Version >= expectedVersion)
                {
                    Assert.Equal(expectedVersion, snapshot.Version);
                    Assert.True(snapshot.HasDeadline);
                    Assert.True(snapshot.DeadlineTimestamp > 0);
                    return;
                }
                await Task.Delay(1, cancellation.Token);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"The hosted score-only deadline did not publish version {expectedVersion}.");
        }
    }

    private static HostedScoreOnlyEpochPlan LoadHostedScoreOnlyEpochPlan(
        string? evidenceRoot,
        string? epochPlanPath)
    {
        if (string.IsNullOrWhiteSpace(evidenceRoot)
            && string.IsNullOrWhiteSpace(epochPlanPath))
        {
            var defaultPlan = CreateDefaultHostedScoreOnlyEpochPlan("in-process-test");
            ValidateHostedScoreOnlyEpochPlan(defaultPlan, expectedRunId: null);
            return defaultPlan;
        }
        if (string.IsNullOrWhiteSpace(evidenceRoot)
            || string.IsNullOrWhiteSpace(epochPlanPath))
        {
            throw new InvalidDataException(
                "Hosted score-only evidence root and epoch plan must be supplied together.");
        }

        var root = ValidateScoreOnlyPerformanceEvidenceRoot(evidenceRoot);
        var expectedPath = Path.Combine(root, "epoch-plan.json");
        var fullPath = Path.GetFullPath(epochPlanPath);
        if (!string.Equals(fullPath, expectedPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The hosted score-only epoch plan must be the run-root epoch-plan.json file.");
        }
        var file = new FileInfo(fullPath);
        if (!file.Exists
            || file.Attributes.HasFlag(FileAttributes.ReparsePoint)
            || file.Length is <= 0 or > 1_048_576)
        {
            throw new InvalidDataException(
                "The hosted score-only epoch plan is missing, redirected, empty, or oversized.");
        }
        var image = File.ReadAllBytes(fullPath);
        var plan = JsonSerializer.Deserialize<HostedScoreOnlyEpochPlan>(
            image,
            ScoreOnlyPerformanceJsonOptions)
            ?? throw new InvalidDataException(
                "The hosted score-only epoch plan is invalid.");
        ValidateHostedScoreOnlyEpochPlan(plan, new DirectoryInfo(root).Name);
        return plan;
    }

    private static HostedScoreOnlyEpochPlan CreateDefaultHostedScoreOnlyEpochPlan(
        string evidenceRunId)
    {
        var baseIdentities = CreateHostedScoreOnlyIdentitySet(
            "base-256",
            HostedScoreOnlyMaximumProcessCount,
            firstProcessId: 30_000,
            firstProcessStartKey: 132_537_600_000_000_000,
            softwarePrefix: "software:hosted-base");
        var replacement = CreateHostedScoreOnlyIdentitySet(
            "replacement-64",
            64,
            firstProcessId: 40_000,
            firstProcessStartKey: 132_537_700_000_000_000,
            softwarePrefix: "software:hosted-replacement");
        var pidReuse = replacement with
        {
            Id = "pid-reuse-64",
            Processes = replacement.Processes
                .Select(static process => process with
                {
                    ProcessStartKey = checked(process.ProcessStartKey + 50_000_000UL)
                })
                .ToArray()
        };
        return new HostedScoreOnlyEpochPlan(
            SchemaVersion: 1,
            Contract: HostedScoreOnlyEpochPlanContract,
            EvidenceRunId: evidenceRunId,
            NormalIntervalMilliseconds: HostedScoreOnlyNormalIntervalMilliseconds,
            EventIntervalMilliseconds: HostedScoreOnlyEventIntervalMilliseconds,
            WarmupCycleCount: HostedScoreOnlyWarmupCycleCount,
            MeasuredCycleCount: HostedScoreOnlyMeasuredCycleCount,
            BaseObservedAtUtcTicks: 638901408000000000L,
            IdentitySets: [baseIdentities, replacement, pidReuse],
            Epochs:
            [
                new("warm-256", 1, 3, "base-256", 256, false),
                new("warm-1", 4, 9, "base-256", 1, false),
                new("stable-1", 10, 25, "base-256", 1, true),
                new("grow-16", 26, 41, "base-256", 16, true),
                new("grow-64", 42, 57, "base-256", 64, true),
                new("grow-256", 58, 73, "base-256", 256, true),
                new("shrink-64", 74, 89, "base-256", 64, true),
                new("replace-64", 90, 105, "replacement-64", 64, true),
                new("pid-reuse-64", 106, 121, "pid-reuse-64", 64, true),
                new("drain-0", 122, 137, "base-256", 0, true)
            ]);
    }

    private static HostedScoreOnlyIdentitySetPlan CreateHostedScoreOnlyIdentitySet(
        string id,
        int count,
        int firstProcessId,
        ulong firstProcessStartKey,
        string softwarePrefix)
        => new(
            id,
            Enumerable.Range(0, count)
                .Select(index => new HostedScoreOnlyProcessPlan(
                    ProcessId: checked(firstProcessId + index),
                    ProcessStartKey: checked(
                        firstProcessStartKey + checked((ulong)index * 10_000_000UL)),
                    SoftwareId: $"{softwarePrefix}:{index:D4}",
                    BaseScore: 35D + index % 60,
                    CpuUsagePercent: 10D + index % 80,
                    MemoryUsagePercent: 5D + index % 90))
                .ToArray());

    private static void ValidateHostedScoreOnlyEpochPlan(
        HostedScoreOnlyEpochPlan plan,
        string? expectedRunId)
    {
        var expectedPlan = CreateDefaultHostedScoreOnlyEpochPlan(plan.EvidenceRunId);
        Assert.Equal(1, plan.SchemaVersion);
        Assert.Equal(HostedScoreOnlyEpochPlanContract, plan.Contract);
        Assert.Matches("^[a-zA-Z0-9][a-zA-Z0-9._-]{0,127}$", plan.EvidenceRunId);
        if (expectedRunId is not null)
        {
            Assert.Equal(expectedRunId, plan.EvidenceRunId);
        }
        Assert.Equal(HostedScoreOnlyNormalIntervalMilliseconds, plan.NormalIntervalMilliseconds);
        Assert.Equal(HostedScoreOnlyEventIntervalMilliseconds, plan.EventIntervalMilliseconds);
        Assert.Equal(HostedScoreOnlyWarmupCycleCount, plan.WarmupCycleCount);
        Assert.Equal(HostedScoreOnlyMeasuredCycleCount, plan.MeasuredCycleCount);
        Assert.Equal(expectedPlan.BaseObservedAtUtcTicks, plan.BaseObservedAtUtcTicks);

        var expectedEpochs = expectedPlan.Epochs;
        Assert.Equal(expectedEpochs, plan.Epochs);
        Assert.Equal(
            ["base-256", "replacement-64", "pid-reuse-64"],
            plan.IdentitySets.Select(static set => set.Id).ToArray());
        Assert.Equal(
            [256, 64, 64],
            plan.IdentitySets.Select(static set => set.Processes.Length).ToArray());
        for (var setIndex = 0; setIndex < plan.IdentitySets.Length; setIndex++)
        {
            var set = plan.IdentitySets[setIndex];
            var expectedSet = expectedPlan.IdentitySets[setIndex];
            Assert.Equal(expectedSet.Id, set.Id);
            Assert.Equal(
                set.Processes.Length,
                set.Processes.Select(static process => process.ProcessId).Distinct().Count());
            Assert.Equal(
                set.Processes.Length,
                set.Processes.Select(static process => process.ProcessStartKey).Distinct().Count());
            Assert.Equal(
                set.Processes.Length,
                set.Processes.Select(static process => process.SoftwareId)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.All(set.Processes, static process =>
            {
                Assert.InRange(process.ProcessId, 1, int.MaxValue);
                Assert.InRange(process.ProcessStartKey, 1UL, ulong.MaxValue);
                Assert.False(string.IsNullOrWhiteSpace(process.SoftwareId));
                Assert.InRange(process.BaseScore, 0D, 100D);
                Assert.InRange(process.CpuUsagePercent, 0D, 100D);
                Assert.InRange(process.MemoryUsagePercent, 0D, 100D);
            });
            Assert.Equal(expectedSet.Processes, set.Processes);
        }

        var baseSet = plan.IdentitySets[0].Processes;
        var replacement = plan.IdentitySets[1].Processes;
        var pidReuse = plan.IdentitySets[2].Processes;
        Assert.Empty(baseSet.Select(static process => process.ProcessId)
            .Intersect(replacement.Select(static process => process.ProcessId)));
        Assert.Empty(baseSet.Select(static process => process.ProcessStartKey)
            .Intersect(replacement.Select(static process => process.ProcessStartKey)));
        Assert.Empty(baseSet.Select(static process => process.SoftwareId)
            .Intersect(replacement.Select(static process => process.SoftwareId),
                StringComparer.OrdinalIgnoreCase));
        Assert.Equal(
            replacement.Select(static process => process.ProcessId),
            pidReuse.Select(static process => process.ProcessId));
        Assert.Equal(
            replacement.Select(static process => process.SoftwareId),
            pidReuse.Select(static process => process.SoftwareId));
        Assert.All(
            replacement.Zip(pidReuse),
            static pair => Assert.NotEqual(
                pair.First.ProcessStartKey,
                pair.Second.ProcessStartKey));
    }

    private static HostedScoreOnlyCyclePermit[] CreateHostedScoreOnlyPermits(
        HostedScoreOnlyEpochPlan plan)
    {
        var identitySets = plan.IdentitySets.ToDictionary(
            static set => set.Id,
            StringComparer.Ordinal);
        var permits = new List<HostedScoreOnlyCyclePermit>(
            HostedScoreOnlyWarmupCycleCount + HostedScoreOnlyMeasuredCycleCount);
        foreach (var epoch in plan.Epochs)
        {
            var identitySet = identitySets[epoch.IdentitySetId];
            var processPlans = identitySet.Processes.Take(epoch.ProcessCount).ToArray();
            for (var sequence = epoch.FirstSequence;
                 sequence <= epoch.LastSequence;
                 sequence++)
            {
                var generation = checked((ulong)sequence);
                var observedAtUtcTicks = checked(plan.BaseObservedAtUtcTicks + sequence);
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
                var processes = processPlans
                    .Select(process => new SchedulingProcessFact(
                        process.ProcessId,
                        process.ProcessStartKey,
                        $"hosted-{process.ProcessId}",
                        $@"c:\tests\hosted-{process.ProcessId}.exe",
                        process.SoftwareId,
                        $"Hosted {process.ProcessId}",
                        "general",
                        "General",
                        process.BaseScore,
                        metrics,
                        process.CpuUsagePercent,
                        process.MemoryUsagePercent,
                        generation,
                        []))
                    .ToArray();
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
                permits.Add(new HostedScoreOnlyCyclePermit(
                    sequence,
                    ResolveHostedScoreOnlyTrigger(sequence),
                    epoch.Id,
                    epoch.Measured,
                    generation,
                    observedAtUtcTicks,
                    hardware,
                    processSnapshot,
                    ComputeHostedScoreOnlyProcessIdentitySha256(processes)));
            }
        }
        Assert.Equal(HostedScoreOnlyLastMeasuredSequence, permits.Count);
        Assert.Equal(
            Enumerable.Range(1, HostedScoreOnlyLastMeasuredSequence),
            permits.Select(static permit => permit.Sequence));
        return [.. permits];
    }

    private static string ResolveHostedScoreOnlyTrigger(int sequence)
        => sequence == 1 || (sequence & 1) != 0 ? "scheduled" : "manual";

    private static string ComputeHostedScoreOnlyProcessIdentitySha256(
        IReadOnlyList<SchedulingProcessFact> processes)
    {
        var canonical = string.Join(
            "\n",
            processes
                .OrderBy(static process => process.ProcessId)
                .ThenBy(static process => process.ProcessStartKey)
                .ThenBy(static process => process.SoftwareId, StringComparer.Ordinal)
                .Select(static process => FormattableString.Invariant(
                    $"{process.ProcessId}|{process.ProcessStartKey}|{process.SoftwareId}")));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private sealed class HostedNativeProcessedIdentityCapture(
        NativeSmartCoordinatorWorkspace workspace,
        int maximumSequence,
        int maximumProcessCount) : IScoreOnlyDiagnosticRecordObserver
    {
        private readonly object sync = new();
        private readonly HostedNativeProcessIdentity[] identities =
            new HostedNativeProcessIdentity[checked(maximumSequence * maximumProcessCount)];
        private readonly uint[] processCounts = new uint[maximumSequence];
        private readonly uint[] softwareCounts = new uint[maximumSequence];
        private readonly uint[] snapshotRowCounts = new uint[maximumSequence];
        private readonly ulong[] nativeCycleSequences = new ulong[maximumSequence];
        private readonly int[] written = new int[maximumSequence];
        private Exception? failure;

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
                    CaptureCore(record);
                }
            }
            catch (Exception exception)
            {
                Interlocked.CompareExchange(ref failure, exception, null);
            }
        }

        private void CaptureCore(DebugDiagnosticLogRecord record)
        {
            if (!record.Properties.TryGetValue("cycleSequence", out var sequenceValue)
                || sequenceValue is not long sequence
                || sequence < 1
                || sequence > maximumSequence)
            {
                throw new InvalidDataException(
                    "A hosted native readback has an invalid producer sequence.");
            }
            var slot = checked((int)sequence - 1);
            if (Volatile.Read(ref written[slot]) != 0)
            {
                throw new InvalidDataException(
                    $"Hosted native readback sequence {sequence} was observed more than once.");
            }

            var snapshot = workspace.Snapshot;
            if (snapshot.ProcessCount > maximumProcessCount
                || snapshot.SoftwareCount != snapshot.ProcessCount
                || snapshot.SnapshotRowCount
                    != checked(snapshot.ProcessCount + snapshot.SoftwareCount))
            {
                throw new InvalidDataException(
                    $"Hosted native readback sequence {sequence} has an invalid topology envelope.");
            }
            var rows = workspace.CurrentSnapshotRows;
            if (rows.Length != checked((int)snapshot.SnapshotRowCount))
            {
                throw new InvalidDataException(
                    $"Hosted native readback sequence {sequence} has an invalid row span.");
            }
            if (snapshot.InvalidFactCount != 0)
            {
                var invalidRows = string.Join(
                    "; ",
                    rows.ToArray()
                        .Where(static row => row.ReasonMask != NativeSmartCoordinatorReason.None)
                        .Take(8)
                        .Select(static row => FormattableString.Invariant(
                            $"kind={row.RowKind},pid={row.ProcessId},valid={row.ValidMask},reason={row.ReasonMask},cpu={row.CpuScore}")));
                throw new InvalidDataException(
                    FormattableString.Invariant(
                        $"Hosted native readback sequence {sequence} has {snapshot.InvalidFactCount} invalid facts; snapshotReason={snapshot.ReasonMask}; rows=[{invalidRows}]."));
            }
            var offset = checked(slot * maximumProcessCount);
            var processCount = checked((int)snapshot.ProcessCount);
            for (var index = 0; index < processCount; index++)
            {
                var row = rows[index];
                if (row.RowKind != NativeSmartCoordinatorSnapshotRowKind.Process)
                {
                    throw new InvalidDataException(
                        $"Hosted native readback sequence {sequence} has a non-process row in its process prefix.");
                }
                identities[offset + index] = new HostedNativeProcessIdentity(
                    row.TargetKey,
                    row.SoftwareKey,
                    row.ProcessStartKey,
                    row.ProcessId);
            }
            processCounts[slot] = snapshot.ProcessCount;
            softwareCounts[slot] = snapshot.SoftwareCount;
            snapshotRowCounts[slot] = snapshot.SnapshotRowCount;
            nativeCycleSequences[slot] = snapshot.CycleSequence;
            Volatile.Write(ref written[slot], 1);
        }

        internal HostedScoreOnlyNativeReadback[] AssertCompleteAndExact(
            IReadOnlyList<HostedScoreOnlyCyclePermit> permits)
        {
            lock (sync)
            {
                if (failure is not null)
                {
                    throw new InvalidDataException(
                        "The hosted native readback observer failed.",
                        failure);
                }
                Assert.Equal(maximumSequence, permits.Count);
                var results = new HostedScoreOnlyNativeReadback[maximumSequence];
                for (var slot = 0; slot < maximumSequence; slot++)
                {
                    var sequence = slot + 1;
                    Assert.Equal(1, Volatile.Read(ref written[slot]));
                    var permit = permits[slot];
                    Assert.Equal(sequence, permit.Sequence);
                    Assert.Equal(checked((ulong)sequence), nativeCycleSequences[slot]);
                    var expectedProcesses = permit.ProcessSnapshot.Processes;
                    Assert.Equal(expectedProcesses.Count, checked((int)processCounts[slot]));
                    Assert.Equal(expectedProcesses.Count, checked((int)softwareCounts[slot]));
                    Assert.Equal(
                        checked(2 * expectedProcesses.Count),
                        checked((int)snapshotRowCounts[slot]));

                    var actual = new HostedNativeProcessIdentity[expectedProcesses.Count];
                    Array.Copy(
                        identities,
                        checked(slot * maximumProcessCount),
                        actual,
                        0,
                        actual.Length);
                    var expected = new HostedNativeProcessIdentity[expectedProcesses.Count];
                    var softwareIds = new Dictionary<ulong, string>(expectedProcesses.Count);
                    for (var index = 0; index < expectedProcesses.Count; index++)
                    {
                        var process = expectedProcesses[index];
                        var softwareKey = NativeStableIdentity.CreateCaseInsensitiveKey(
                            process.SoftwareId);
                        Assert.True(softwareIds.TryAdd(softwareKey, process.SoftwareId));
                        expected[index] = new HostedNativeProcessIdentity(
                            NativeStableIdentity.CreateCaseInsensitiveKey(
                                HostManagerTargetIdentity.CreateProcessTargetId(
                                    process.ProcessId,
                                    process.ProcessStartKey)),
                            softwareKey,
                            process.ProcessStartKey,
                            checked((uint)process.ProcessId));
                    }
                    Array.Sort(actual, HostedNativeProcessIdentityComparer.Instance);
                    Array.Sort(expected, HostedNativeProcessIdentityComparer.Instance);
                    Assert.Equal(expected, actual);

                    var canonical = string.Join(
                        "\n",
                        actual.Select(identity => FormattableString.Invariant(
                            $"{identity.ProcessId}|{identity.ProcessStartKey}|{softwareIds[identity.SoftwareKey]}")));
                    var digest = Convert.ToHexString(SHA256.HashData(
                        Encoding.UTF8.GetBytes(canonical)));
                    Assert.Equal(permit.ProcessIdentitySha256, digest);
                    results[slot] = new HostedScoreOnlyNativeReadback(
                        sequence,
                        nativeCycleSequences[slot],
                        processCounts[slot],
                        softwareCounts[slot],
                        snapshotRowCounts[slot],
                        digest);
                }
                return results;
            }
        }
    }

    private sealed class HostedNativeProcessIdentityComparer
        : IComparer<HostedNativeProcessIdentity>
    {
        internal static HostedNativeProcessIdentityComparer Instance { get; } = new();

        public int Compare(
            HostedNativeProcessIdentity left,
            HostedNativeProcessIdentity right)
        {
            var result = left.ProcessId.CompareTo(right.ProcessId);
            if (result == 0)
            {
                result = left.ProcessStartKey.CompareTo(right.ProcessStartKey);
            }
            if (result == 0)
            {
                result = left.SoftwareKey.CompareTo(right.SoftwareKey);
            }
            return result == 0
                ? left.TargetKey.CompareTo(right.TargetKey)
                : result;
        }
    }

    private static void AssertHostedScoreOnlyCorpus(
        HostedScoreOnlyEpochPlan plan,
        IReadOnlyList<HostedScoreOnlyCyclePermit> expectedPermits,
        IReadOnlyList<HostedScoreOnlyCyclePermit> completedPermits,
        IReadOnlyList<DebugDiagnosticLogRecord> records)
    {
        Assert.Equal(HostedScoreOnlyLastMeasuredSequence, completedPermits.Count);
        Assert.Equal(HostedScoreOnlyLastMeasuredSequence, expectedPermits.Count);
        for (var index = 0; index < expectedPermits.Count; index++)
        {
            Assert.Equal(expectedPermits[index], completedPermits[index]);
        }
        Assert.Equal(HostedScoreOnlyMeasuredCycleCount, records.Count);
        var measuredPermits = completedPermits.Where(static permit => permit.Measured).ToArray();
        Assert.Equal(HostedScoreOnlyMeasuredCycleCount, measuredPermits.Length);
        var producerInstanceId = Assert.IsType<string>(
            records[0].Properties["producerInstanceId"]);
        for (var index = 0; index < records.Count; index++)
        {
            var permit = measuredPermits[index];
            var record = records[index];
            AssertHostedScoreOnlyRecord(record, permit);
            Assert.Equal(
                producerInstanceId,
                Assert.IsType<string>(record.Properties["producerInstanceId"]));
            if (index > 0)
            {
                var previous = records[index - 1];
                Assert.True(
                    Assert.IsType<long>(record.Properties["cycleStartedAtQpcTicks"])
                    >= Assert.IsType<long>(previous.Properties["cycleCompletedAtQpcTicks"]));
            }
        }
        Assert.Equal(
            64,
            records.Count(static record =>
                Assert.IsType<string>(record.Properties["trigger"]) == "manual"));
        Assert.Equal(
            64,
            records.Count(static record =>
                Assert.IsType<string>(record.Properties["trigger"]) == "scheduled"));
        Assert.Equal(
            plan.Epochs.Where(static epoch => epoch.Measured).Select(static epoch => epoch.Id),
            measuredPermits.Select(static permit => permit.EpochId).Distinct());
    }

    private static void AssertHostedScoreOnlyRecord(
        DebugDiagnosticLogRecord record,
        HostedScoreOnlyCyclePermit permit)
    {
        Assert.Equal("smart-optimization", record.Category);
        Assert.Equal("cycle-performance", record.EventName);
        Assert.Equal("completed", Assert.IsType<string>(record.Properties["outcome"]));
        Assert.Equal(permit.Trigger, Assert.IsType<string>(record.Properties["trigger"]));
        Assert.Equal(permit.Sequence, Assert.IsType<long>(record.Properties["cycleSequence"]));
        Assert.True(Assert.IsType<bool>(record.Properties["scoreOnly"]));
        Assert.Equal("smart", Assert.IsType<string>(record.Properties["mode"]));
        var started = Assert.IsType<long>(record.Properties["cycleStartedAtQpcTicks"]);
        var completed = Assert.IsType<long>(record.Properties["cycleCompletedAtQpcTicks"]);
        Assert.InRange(started, 1, long.MaxValue);
        Assert.InRange(completed, started, long.MaxValue);
        Assert.Equal(
            Stopwatch.Frequency,
            Assert.IsType<long>(record.Properties["qpcFrequency"]));

        var count = permit.ProcessSnapshot.Processes.Count;
        var counts = Assert.IsType<Dictionary<string, object?>>(record.Properties["counts"]);
        Assert.Equal(count, Assert.IsType<int>(counts["sampleTargets"]));
        Assert.Equal(count, Assert.IsType<int>(counts["sampleProcesses"]));
        Assert.Equal(count, Assert.IsType<int>(counts["scoredProcesses"]));
        Assert.Equal(count, Assert.IsType<int>(counts["policyProcesses"]));
        Assert.Equal(checked(2 * count), Assert.IsType<int>(counts["schedulingTargets"]));
        foreach (var name in new[]
                 {
                     "policyChanges",
                     "pendingChanges",
                     "resourceQueueActions",
                     "changedCount",
                     "appliedTargets",
                     "appliedPlacements"
                 })
        {
            Assert.Equal(0, Assert.IsType<int>(counts[name]));
        }

        var guards = Assert.IsType<Dictionary<string, object?>>(record.Properties["guards"]);
        Assert.True(Assert.IsType<bool>(guards["sampling"]));
        Assert.Equal(count > 0, Assert.IsType<bool>(guards["scoring"]));
        Assert.True(Assert.IsType<bool>(guards["policyExecution"]));
        Assert.True(Assert.IsType<bool>(guards["hardwarePlacement"]));
        var details = Assert.IsType<Dictionary<string, object?>>(record.Properties["details"]);
        Assert.Equal(checked((uint)(2 * count)), Assert.IsType<uint>(details["nativeInputCount"]));
        Assert.Equal(checked((uint)count), Assert.IsType<uint>(details["nativeSoftwareCount"]));
        Assert.Equal(0U, Assert.IsType<uint>(details["nativeInvalidFactCount"]));
        Assert.Equal(0U, Assert.IsType<uint>(details["nativeFeedbackCount"]));
        Assert.NotEmpty(
            Assert.IsType<Dictionary<string, object?>[]>(record.Properties["phases"]));
    }

    private static async Task SealHostedScoreOnlyEvidenceAsync(
        string requestedRoot,
        JsonDebugDiagnosticLogWriter writer,
        IReadOnlyList<DebugDiagnosticLogRecord> records)
    {
        var root = ValidateScoreOnlyPerformanceEvidenceRoot(requestedRoot);
        Assert.Equal(HostedScoreOnlyMeasuredCycleCount, records.Count);
        var receipt = await writer.SealForEvidenceAsync(
            records.Count,
            TimeSpan.FromSeconds(10),
            CancellationToken.None);
        Assert.True(receipt.Satisfied);
        Assert.Equal(records.Count, receipt.AcceptedRecordCount);
        Assert.Equal(records.Count, receipt.WrittenRecordCount);
        Assert.Equal(0, receipt.DroppedRecordCount);
        Assert.Equal(0, receipt.FailedRecordCount);
        Assert.Equal(0, receipt.RejectedRecordCount);
        Assert.InRange(receipt.LogLength, 1, HostedScoreOnlyMaximumRawLogBytes);
        Assert.NotNull(receipt.LogSha256);
        Assert.Equal(records.Count, File.ReadLines(receipt.LogPath).Count());

        WriteHostedScoreOnlyJsonFile(
            Path.Combine(root, "writer-receipt.json"),
            new
            {
                schemaVersion = 1,
                contract = "non-adapted-hosted-score-only-performance-writer-receipt-v1",
                expectedRecordCount = records.Count,
                acceptedRecordCount = receipt.AcceptedRecordCount,
                writtenRecordCount = receipt.WrittenRecordCount,
                droppedRecordCount = receipt.DroppedRecordCount,
                failedRecordCount = receipt.FailedRecordCount,
                rejectedRecordCount = receipt.RejectedRecordCount,
                logFile = "Config/Diagnostics/DebugLogs/debug-log.jsonl",
                logLength = receipt.LogLength,
                logSha256 = receipt.LogSha256,
                satisfied = true
            });
    }

    private static void WriteHostedScoreOnlyLoopReceipt(
        string requestedRoot,
        string epochPlanPath,
        HostedScoreOnlyEpochPlan plan,
        IReadOnlyList<HostedScoreOnlyCyclePermit> permits,
        IReadOnlyList<HostedScoreOnlyNativeReadback> nativeReadbacks,
        IReadOnlyList<DebugDiagnosticLogRecord> records,
        TimeSpan measuredElapsed,
        TimeSpan stopElapsed,
        HostManagerSmartCoordinatorLifecycleState lifecycleBefore,
        HostManagerSmartCoordinatorLifecycleState lifecycleAfter,
        IReadOnlyList<HostManagerSmartCoordinatorShutdownPoint> shutdownPoints,
        NativeSmartCoordinatorCapacity capacity,
        HostedScoreOnlyEffectCounterSnapshot effectCounters,
        ulong rollbackIncarnationBefore,
        ulong rollbackIncarnationAfter,
        string durableBeforeSha256,
        string durableAfterSha256)
    {
        var root = ValidateScoreOnlyPerformanceEvidenceRoot(requestedRoot);
        var planImage = File.ReadAllBytes(epochPlanPath);
        var producerInstanceId = Assert.IsType<string>(
            records[0].Properties["producerInstanceId"]);
        var qpcFrequency = Assert.IsType<long>(records[0].Properties["qpcFrequency"]);
        Assert.Equal(permits.Count, nativeReadbacks.Count);
        var zeroEffectCanonical = effectCounters.ToCanonicalString();
        WriteHostedScoreOnlyJsonFile(
            Path.Combine(root, "hosted-loop-receipt.json"),
            new
            {
                schemaVersion = 1,
                contract = "non-adapted-hosted-score-only-performance-loop-receipt-v1",
                evidenceRunId = plan.EvidenceRunId,
                scope = "production coordinator hosted loop in a dedicated S0 test host with scripted read providers and fail-fast deny-all effects",
                measurementBoundary = HostedScoreOnlyMeasurementBoundary,
                epochPlan = new
                {
                    path = "epoch-plan.json",
                    bytes = planImage.LongLength,
                    sha256 = Convert.ToHexString(SHA256.HashData(planImage))
                },
                timing = new
                {
                    normalIntervalMilliseconds = plan.NormalIntervalMilliseconds,
                    eventIntervalMilliseconds = plan.EventIntervalMilliseconds,
                    measuredElapsedMilliseconds = measuredElapsed.TotalMilliseconds,
                    maximumMeasuredElapsedMilliseconds = 45_000D,
                    stopElapsedMilliseconds = stopElapsed.TotalMilliseconds,
                    maximumStopElapsedMilliseconds = 10_000D
                },
                lifecycle = new
                {
                    startedViaNonOwningHostedService = true,
                    before = lifecycleBefore.ToString(),
                    after = lifecycleAfter.ToString(),
                    exactWorkerCompleted = true,
                    shutdownPoints = shutdownPoints.Select(static point => point.ToString()).ToArray()
                },
                counts = new
                {
                    warmupCycles = HostedScoreOnlyWarmupCycleCount,
                    measuredCycles = HostedScoreOnlyMeasuredCycleCount,
                    totalProviderPairs = permits.Count,
                    providerHardwareCaptures = permits.Count,
                    providerProcessCaptures = permits.Count,
                    nativeReadbacks = nativeReadbacks.Count,
                    writerRecords = records.Count,
                    manualRecords = records.Count(static record =>
                        Assert.IsType<string>(record.Properties["trigger"]) == "manual"),
                    scheduledRecords = records.Count(static record =>
                        Assert.IsType<string>(record.Properties["trigger"]) == "scheduled"),
                    remainingPermits = 0,
                    pendingProviderPair = false
                },
                sequences = new
                {
                    bootstrap = 1,
                    warmupLast = HostedScoreOnlyWarmupCycleCount,
                    measuredFirst = HostedScoreOnlyFirstMeasuredSequence,
                    measuredLast = HostedScoreOnlyLastMeasuredSequence,
                    deadlineVersionBeforeStart = 0,
                    deadlineVersionAfterWarmup = HostedScoreOnlyWarmupCycleCount,
                    deadlineVersionAfterMeasurement = HostedScoreOnlyLastMeasuredSequence,
                    producerInstanceId,
                    qpcFrequency
                },
                workspace = new
                {
                    stableAcrossCorpus = true,
                    inputRowCapacity = capacity.InputRowCapacity,
                    actionCapacity = capacity.ActionCapacity,
                    feedbackCapacity = capacity.FeedbackCapacity,
                    snapshotRowCapacity = capacity.SnapshotRowCapacity
                },
                zeroEffects = new
                {
                    counters = new
                    {
                        rollbackReserveAttempts = effectCounters.RollbackReserveAttempts,
                        rollbackReserveCalls = effectCounters.RollbackReserveCalls,
                        rollbackSaveAttempts = effectCounters.RollbackSaveAttempts,
                        rollbackSaveCalls = effectCounters.RollbackSaveCalls,
                        processPolicyTotalCalls = effectCounters.ProcessPolicyTotalCalls,
                        processPolicyRecoveryReadCalls = effectCounters.ProcessPolicyRecoveryReadCalls,
                        processPolicyBatchCalls = effectCounters.ProcessPolicyBatchCalls,
                        processPolicyBatchRequestCount = effectCounters.ProcessPolicyBatchRequestCount,
                        adapterTotalCalls = effectCounters.AdapterTotalCalls,
                        memoryCleanupPlanAttempts = effectCounters.MemoryCleanupPlanAttempts,
                        memoryCleanupPlanCalls = effectCounters.MemoryCleanupPlanCalls,
                        memoryCleanupCompleteAttempts = effectCounters.MemoryCleanupCompleteAttempts,
                        memoryCleanupCompleteCalls = effectCounters.MemoryCleanupCompleteCalls,
                        memoryCleanupCompletionHistoryCount = effectCounters.MemoryCleanupCompletionHistoryCount,
                        publicResourceTickAttempts = effectCounters.PublicResourceTickAttempts,
                        publicResourceTickCalls = effectCounters.PublicResourceTickCalls,
                        publicResourceNewEffectAttemptCount = effectCounters.PublicResourceNewEffectAttemptCount,
                        graphicsTotalCalls = effectCounters.GraphicsTotalCalls
                    },
                    canonical = zeroEffectCanonical,
                    sha256 = Convert.ToHexString(SHA256.HashData(
                        Encoding.UTF8.GetBytes(zeroEffectCanonical))),
                    satisfied = effectCounters.AllZero
                },
                durableState = new
                {
                    beforeSha256 = durableBeforeSha256,
                    afterSha256 = durableAfterSha256,
                    identical = durableBeforeSha256 == durableAfterSha256,
                    rollbackIncarnationBefore,
                    rollbackIncarnationAfter,
                    rollbackIncarnationIdentical =
                        rollbackIncarnationBefore == rollbackIncarnationAfter
                },
                cycles = permits.Select((permit, index) => new
                {
                    sequence = permit.Sequence,
                    trigger = permit.Trigger,
                    epochId = permit.EpochId,
                    measured = permit.Measured,
                    sourceGeneration = permit.SourceGeneration,
                    observedAtUtcTicks = permit.ObservedAtUtcTicks,
                    processCount = permit.ProcessSnapshot.Processes.Count,
                    softwareCount = permit.ProcessSnapshot.Processes.Count,
                    nativeInputCount = checked(2 * permit.ProcessSnapshot.Processes.Count),
                    processIdentitySha256 = permit.ProcessIdentitySha256,
                    nativeCycleSequence = nativeReadbacks[index].NativeCycleSequence,
                    nativeProcessCount = nativeReadbacks[index].ProcessCount,
                    nativeSoftwareCount = nativeReadbacks[index].SoftwareCount,
                    nativeSnapshotRowCount = nativeReadbacks[index].SnapshotRowCount,
                    nativeProcessIdentitySha256 =
                        nativeReadbacks[index].ProcessIdentitySha256,
                    nativeIdentityExact =
                        nativeReadbacks[index].ProcessIdentitySha256
                        == permit.ProcessIdentitySha256
                }).ToArray(),
                satisfied = true
            });
    }

    private static void WriteHostedScoreOnlyJsonFile(string path, object value)
    {
        var image = JsonSerializer.SerializeToUtf8Bytes(
            value,
            ScoreOnlyPerformanceJsonOptions);
        if (image.Length > 1_048_576)
        {
            throw new InvalidDataException(
                $"The hosted score-only receipt exceeded its byte bound: {path}");
        }
        using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.WriteThrough);
        stream.Write(image);
        stream.WriteByte((byte)'\n');
        stream.Flush(flushToDisk: true);
    }

    private static string ComputeHostedScoreOnlyIdentitySha256(object value)
        => Convert.ToHexString(SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(value, ScoreOnlyPerformanceJsonOptions)));

    private sealed class HostedScoreOnlyEpochSource
        : IMetricSampler,
          ISchedulingProcessFactSource,
          IMetricSnapshotObservationSource,
          ISchedulingProcessFactObservationSource
    {
        private readonly object sync = new();
        private readonly HostedHardwareObservationCache hardwareCache = new();
        private readonly Queue<HostedScoreOnlyCyclePermit> permits = new();
        private readonly List<HostedScoreOnlyCyclePermit> completed = [];
        private HostedScoreOnlyCyclePermit? pending;
        private int hardwareReadCount;
        private int processReadCount;

        internal void ConfigureHistory(CompiledDataHistoryPlan history)
            => hardwareCache.Configure(history);

        internal int CompletedCount
        {
            get
            {
                lock (sync)
                {
                    return completed.Count;
                }
            }
        }

        internal HostedScoreOnlyCyclePermit[] CompletedPermits
        {
            get
            {
                lock (sync)
                {
                    Assert.Equal(completed.Count, hardwareReadCount);
                    Assert.Equal(completed.Count, processReadCount);
                    Assert.Empty(permits);
                    Assert.Null(pending);
                    return [.. completed];
                }
            }
        }

        internal void Enqueue(HostedScoreOnlyCyclePermit permit)
        {
            lock (sync)
            {
                permits.Enqueue(permit);
            }
        }

        public Task<HardwareMetricSnapshot> GetSnapshotAsync(
            CancellationToken cancellationToken)
            => throw new InvalidOperationException(
                "The hosted score-only corpus requires the production capture path.");

        public Task<HardwareMetricSnapshot> GetSnapshotAsync(
            MetricSampleRequest request,
            CancellationToken cancellationToken)
            => throw new InvalidOperationException(
                "The hosted score-only corpus requires the production capture path.");

        public Task<HardwareMetricSnapshot> CaptureSnapshotAsync(
            MetricSampleRequest request,
            CancellationToken cancellationToken)
            => throw new InvalidOperationException(
                "The hosted score-only corpus forbids direct hardware capture.");

        public Task<SchedulingProcessFactSnapshot> CaptureAsync(
            SchedulingProcessFactRequest request,
            CancellationToken cancellationToken)
            => throw new InvalidOperationException(
                "The hosted score-only corpus forbids direct process capture.");

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
                if (pending is not null)
                {
                    throw new InvalidOperationException(
                        "A hosted score-only hardware snapshot was requested before its paired process snapshot.");
                }
                if (permits.Count == 0)
                {
                    throw new InvalidOperationException(
                        "The production hosted loop requested an unplanned score-only cycle.");
                }
                pending = permits.Dequeue();
                hardwareReadCount++;
                return hardwareCache.Publish(pending.HardwareSnapshot, request);
            }
        }

        internal CpuCoreResidencySnapshot? ReadCpuCoreResidency()
        {
            lock (sync)
            {
                // The corpus declares one physical core; reading it does not advance a permit.
                return completed.Count == 0 ? null : CpuCoreResidencyTestValues.OneCore(completed[^1].ProcessSnapshot);
            }
        }

        public SchedulingProcessFactSnapshot? ReadLatest(
            SchedulingProcessFactRequest request)
        {
            lock (sync)
            {
                var current = pending
                    ?? throw new InvalidOperationException(
                        "A hosted score-only process snapshot was requested without its paired hardware snapshot.");
                pending = null;
                processReadCount++;
                completed.Add(current);
                return SchedulingProcessFactTestProjection.Project(
                    current.ProcessSnapshot,
                    request);
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

    private sealed class HostedScoreOnlyShutdownProbe
        : IHostManagerSmartCoordinatorTransitionProbe
    {
        private readonly object sync = new();
        private readonly List<HostManagerSmartCoordinatorShutdownPoint> points = [];

        internal HostManagerSmartCoordinatorShutdownPoint[] Points
        {
            get
            {
                lock (sync)
                {
                    return [.. points];
                }
            }
        }

        public void Reach(HostManagerSmartCoordinatorTransitionPoint point)
        {
        }

        public void ObserveShutdown(HostManagerSmartCoordinatorShutdownPoint point)
        {
            lock (sync)
            {
                points.Add(point);
            }
        }

        internal void AssertExactOrder()
            => Assert.Equal(
                [
                    HostManagerSmartCoordinatorShutdownPoint.AdmissionClosed,
                    HostManagerSmartCoordinatorShutdownPoint.ExactWorkerJoined,
                    HostManagerSmartCoordinatorShutdownPoint.GateDrained,
                    HostManagerSmartCoordinatorShutdownPoint.OwnedResourcesDisposed
                ],
                Points);
    }

    private readonly record struct HostedNativeProcessIdentity(
        ulong TargetKey,
        ulong SoftwareKey,
        ulong ProcessStartKey,
        uint ProcessId);

    private sealed record HostedScoreOnlyNativeReadback(
        int Sequence,
        ulong NativeCycleSequence,
        uint ProcessCount,
        uint SoftwareCount,
        uint SnapshotRowCount,
        string ProcessIdentitySha256);

    private sealed record HostedScoreOnlyEffectCounterSnapshot(
        int RollbackReserveAttempts,
        int RollbackReserveCalls,
        int RollbackSaveAttempts,
        int RollbackSaveCalls,
        int ProcessPolicyTotalCalls,
        int ProcessPolicyRecoveryReadCalls,
        int ProcessPolicyBatchCalls,
        int ProcessPolicyBatchRequestCount,
        int AdapterTotalCalls,
        int MemoryCleanupPlanAttempts,
        int MemoryCleanupPlanCalls,
        int MemoryCleanupCompleteAttempts,
        int MemoryCleanupCompleteCalls,
        int MemoryCleanupCompletionHistoryCount,
        int PublicResourceTickAttempts,
        int PublicResourceTickCalls,
        uint PublicResourceNewEffectAttemptCount,
        int GraphicsTotalCalls)
    {
        internal bool AllZero =>
            RollbackReserveAttempts == 0
            && RollbackReserveCalls == 0
            && RollbackSaveAttempts == 0
            && RollbackSaveCalls == 0
            && ProcessPolicyTotalCalls == 0
            && ProcessPolicyRecoveryReadCalls == 0
            && ProcessPolicyBatchCalls == 0
            && ProcessPolicyBatchRequestCount == 0
            && AdapterTotalCalls == 0
            && MemoryCleanupPlanAttempts == 0
            && MemoryCleanupPlanCalls == 0
            && MemoryCleanupCompleteAttempts == 0
            && MemoryCleanupCompleteCalls == 0
            && MemoryCleanupCompletionHistoryCount == 0
            && PublicResourceTickAttempts == 0
            && PublicResourceTickCalls == 0
            && PublicResourceNewEffectAttemptCount == 0
            && GraphicsTotalCalls == 0;

        internal void AssertAllZero() => Assert.True(AllZero);

        internal string ToCanonicalString()
            => string.Join(
                ";",
                [
                    $"rollbackReserveAttempts={RollbackReserveAttempts}",
                    $"rollbackReserveCalls={RollbackReserveCalls}",
                    $"rollbackSaveAttempts={RollbackSaveAttempts}",
                    $"rollbackSaveCalls={RollbackSaveCalls}",
                    $"processPolicyTotalCalls={ProcessPolicyTotalCalls}",
                    $"processPolicyRecoveryReadCalls={ProcessPolicyRecoveryReadCalls}",
                    $"processPolicyBatchCalls={ProcessPolicyBatchCalls}",
                    $"processPolicyBatchRequestCount={ProcessPolicyBatchRequestCount}",
                    $"adapterTotalCalls={AdapterTotalCalls}",
                    $"memoryCleanupPlanAttempts={MemoryCleanupPlanAttempts}",
                    $"memoryCleanupPlanCalls={MemoryCleanupPlanCalls}",
                    $"memoryCleanupCompleteAttempts={MemoryCleanupCompleteAttempts}",
                    $"memoryCleanupCompleteCalls={MemoryCleanupCompleteCalls}",
                    $"memoryCleanupCompletionHistoryCount={MemoryCleanupCompletionHistoryCount}",
                    $"publicResourceTickAttempts={PublicResourceTickAttempts}",
                    $"publicResourceTickCalls={PublicResourceTickCalls}",
                    $"publicResourceNewEffectAttemptCount={PublicResourceNewEffectAttemptCount}",
                    $"graphicsTotalCalls={GraphicsTotalCalls}"
                ]);
    }

    private sealed record HostedScoreOnlyEpochPlan(
        int SchemaVersion,
        string Contract,
        string EvidenceRunId,
        int NormalIntervalMilliseconds,
        int EventIntervalMilliseconds,
        int WarmupCycleCount,
        int MeasuredCycleCount,
        long BaseObservedAtUtcTicks,
        HostedScoreOnlyIdentitySetPlan[] IdentitySets,
        HostedScoreOnlyEpochRangePlan[] Epochs);

    private sealed record HostedScoreOnlyIdentitySetPlan(
        string Id,
        HostedScoreOnlyProcessPlan[] Processes);

    private sealed record HostedScoreOnlyProcessPlan(
        int ProcessId,
        ulong ProcessStartKey,
        string SoftwareId,
        double BaseScore,
        double CpuUsagePercent,
        double MemoryUsagePercent);

    private sealed record HostedScoreOnlyEpochRangePlan(
        string Id,
        int FirstSequence,
        int LastSequence,
        string IdentitySetId,
        int ProcessCount,
        bool Measured);

    private sealed record HostedScoreOnlyCyclePermit(
        int Sequence,
        string Trigger,
        string EpochId,
        bool Measured,
        ulong SourceGeneration,
        long ObservedAtUtcTicks,
        HardwareMetricSnapshot HardwareSnapshot,
        SchedulingProcessFactSnapshot ProcessSnapshot,
        string ProcessIdentitySha256);
}
