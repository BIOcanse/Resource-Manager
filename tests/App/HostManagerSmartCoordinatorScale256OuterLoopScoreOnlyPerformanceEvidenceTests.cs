using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Domain.Diagnostics;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Infrastructure.Diagnostics;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerSmartCoordinatorScoreOnlyCompositionTests
{
    private const int Scale256HostedProcessCount = 256;
    private const int Scale256HostedTotalCycleCount = 137;
    private const int Scale256HostedWarmupCycleCount = 9;
    private const int Scale256HostedMeasuredCycleCount = 128;
    private const int Scale256HostedFirstMeasuredSequence = 10;
    private const string Scale256HostedEvidenceRootEnvironmentVariable =
        "RESOURCE_MANAGER_SCALE256_HOSTED_OUTER_LOOP_EVIDENCE_ROOT";
    private const string Scale256HostedCyclePlanEnvironmentVariable =
        "RESOURCE_MANAGER_SCALE256_HOSTED_OUTER_LOOP_CYCLE_PLAN";
    private const string Scale256HostedCyclePlanContract =
        "non-adapted-scale-256-hosted-outer-loop-cycle-plan-v1";
    private const string Scale256HostedMeasurementContract =
        "non-adapted-scale-256-hosted-outer-loop-score-only-performance-v1";
    private const string Scale256HostedMeasurementBoundary =
        "fixed-256-hosted-cycles-with-steady-inner-profiler-budget-and-diagnostic-only-outer-loop-writer-lifecycle";
    private const double Scale256HostedMaximumElapsedP99Milliseconds = 50.0;
    private const double Scale256HostedMaximumHostCpuP99Milliseconds = 62.5;
    private const long Scale256HostedMaximumAllocatedP99Bytes = 10_000_000;
    private const long Scale256HostedMaximumAllocationP50GrowthBytes = 262_144;

    [Fact]
    public async Task Scale256HostedOuterLoopKeepsSteadyProfilerAndWriterBoundedWithoutEffects()
    {
        var evidenceRoot = Environment.GetEnvironmentVariable(
            Scale256HostedEvidenceRootEnvironmentVariable);
        var cyclePlanPath = Environment.GetEnvironmentVariable(
            Scale256HostedCyclePlanEnvironmentVariable);
        var plan = LoadScale256HostedCyclePlan(evidenceRoot, cyclePlanPath);
        var permits = CreateScale256HostedPermits(plan);
        var source = new HostedScoreOnlyEpochSource();
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true,
            performanceLogEnabled: true,
            smartCoordinatorMaximumProcesses: Scale256HostedProcessCount,
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
                "The fixed-256 hosted wake deadline is unavailable.");
        var traceProbe = new OuterLoopTraceProbe();
        var shutdownProbe = new TimestampedOuterLoopShutdownProbe();
        var capture = new Scale256HostedCycleCapture(
            fixture.Workspace,
            permits,
            fixture.RuntimePlan.HostManager.RequirePublished()
                .HotPublish.SmartCoordinator.ProcessStateMultipliers);
        fixture.Coordinator.OuterLoopProbe = traceProbe;
        fixture.Coordinator.TransitionProbe = shutdownProbe;
        fixture.DebugLogWriter.DisableRecordRetention();
        fixture.DebugLogWriter.AttachRecordObserver(capture);

        var ownsWriterRoot = string.IsNullOrWhiteSpace(evidenceRoot);
        var writerRoot = ownsWriterRoot
            ? Path.Combine(
                Path.GetTempPath(),
                "resource-manager-scale256-hosted",
                Guid.NewGuid().ToString("N"))
            : ValidateScoreOnlyPerformanceEvidenceRoot(evidenceRoot!);
        Directory.CreateDirectory(writerRoot);
        using var evidenceWriter = CreateScoreOnlyPerformanceEvidenceWriter(writerRoot);

        var durableBefore = CaptureDurableFiles(fixture.Root);
        var durableBeforeSha256 = ComputeHostedScoreOnlyIdentitySha256(durableBefore);
        var rollbackIncarnationBefore =
            fixture.StateStore.Current.NativeHostSessionIncarnation;
        var normalReleaseEvidenceBefore = CapturePrivateState(
            fixture.Coordinator,
            "hostPublicResourceNormalReleaseEvidence");
        var workspaceBefore = fixture.Workspace;
        var capacityBefore = fixture.Workspace.Capacity;
        Assert.Equal(HostManagerSmartCoordinatorLifecycleState.Created,
            fixture.Coordinator.LifecycleState);
        Assert.Equal(default, deadline.Snapshot);

        IHost? host = null;
        var writerStarted = false;
        var forwardingWriterAttached = false;
        var recordObserverAttached = true;
        var hostStopped = false;
        var writerStopped = false;
        long hostStartRequestedAtQpcTicks = 0;
        long hostStartReturnedAtQpcTicks = 0;
        long hostStopRequestedAtQpcTicks = 0;
        long hostStopReturnedAtQpcTicks = 0;
        try
        {
            await evidenceWriter.StartAsync(CancellationToken.None);
            writerStarted = true;
            fixture.DebugLogWriter.AttachForwardingWriter(evidenceWriter);
            forwardingWriterAttached = true;
            host = BuildHostedScoreOnlyTestHost(fixture.Coordinator);
            var concrete = host.Services.GetRequiredService<HostManagerSmartCoordinator>();
            Assert.Same(fixture.Coordinator, concrete);

            source.Enqueue(permits[0]);
            hostStartRequestedAtQpcTicks = Stopwatch.GetTimestamp();
            using (var cancellation =
                   new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            {
                await host.StartAsync(cancellation.Token);
            }
            hostStartReturnedAtQpcTicks = Stopwatch.GetTimestamp();
            await WaitForHostedScoreOnlyDeadlineVersionAsync(
                deadline,
                expectedVersion: 1,
                TimeSpan.FromSeconds(5));

            for (var sequence = 2;
                 sequence <= Scale256HostedTotalCycleCount;
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

            Assert.Equal(Scale256HostedTotalCycleCount, source.CompletedCount);
            Assert.Equal(
                checked((ulong)Scale256HostedTotalCycleCount),
                deadline.Snapshot.Version);
            Assert.Same(workspaceBefore, fixture.Workspace);
            Assert.Equal(capacityBefore, fixture.Workspace.Capacity);
            Assert.Equal(
                checked((uint)Scale256HostedProcessCount),
                fixture.Workspace.Snapshot.ProcessCount);
            Assert.Equal(
                checked((uint)Scale256HostedProcessCount),
                fixture.Workspace.Snapshot.SoftwareCount);
            Assert.Equal(0U, fixture.Workspace.Snapshot.ActionCount);
            Assert.Equal(0U, fixture.Workspace.Snapshot.PendingCount);
            Assert.Equal(0U, fixture.Workspace.Snapshot.InflightCount);
            Assert.Equal(0U, fixture.Workspace.Snapshot.InvalidFactCount);

            hostStopRequestedAtQpcTicks = Stopwatch.GetTimestamp();
            using (var cancellation =
                   new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            {
                await host.StopAsync(cancellation.Token);
            }
            hostStopReturnedAtQpcTicks = Stopwatch.GetTimestamp();
            hostStopped = true;
            Assert.Equal(
                HostManagerSmartCoordinatorLifecycleState.Closed,
                fixture.Coordinator.LifecycleState);
            shutdownProbe.AssertExactOrder(
                hostStopRequestedAtQpcTicks,
                hostStopReturnedAtQpcTicks);

            fixture.DebugLogWriter.DetachRecordObserver(capture);
            recordObserverAttached = false;
            var observations = capture.AssertCompleteAndExact();
            var steadyAssessment = AssertScale256HostedSteadyBudgets(observations);
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

            var writerReceipt = await evidenceWriter.SealForEvidenceAsync(
                Scale256HostedTotalCycleCount,
                TimeSpan.FromSeconds(10),
                CancellationToken.None);
            AssertOuterLoopWriterReceipt(writerReceipt);
            fixture.DebugLogWriter.DetachForwardingWriter(evidenceWriter);
            forwardingWriterAttached = false;
            using (var cancellation =
                   new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            {
                await evidenceWriter.StopAsync(cancellation.Token);
            }
            writerStopped = true;

            var completedPermits = source.CompletedPermits;
            var rawRecords = ReadOuterLoopRawRecordIdentities(writerReceipt.LogPath);
            var traces = traceProbe.AssertCompleteAndExact(completedPermits, rawRecords);
            Assert.InRange(
                hostStartReturnedAtQpcTicks,
                hostStartRequestedAtQpcTicks,
                traces[0].Events[0].QpcTicks);
            Assert.True(
                hostStopRequestedAtQpcTicks >= traces[^1].Events[^1].QpcTicks);
            Assert.True(
                writerReceipt.SealStartedAtQpcTicks >= hostStopReturnedAtQpcTicks);

            if (!string.IsNullOrWhiteSpace(evidenceRoot))
            {
                WriteScale256HostedEvidenceReceipts(
                    evidenceRoot!,
                    plan,
                    completedPermits,
                    observations,
                    traces,
                    rawRecords,
                    writerReceipt,
                    steadyAssessment,
                    shutdownProbe.Points,
                    hostStartRequestedAtQpcTicks,
                    hostStartReturnedAtQpcTicks,
                    hostStopRequestedAtQpcTicks,
                    hostStopReturnedAtQpcTicks,
                    effectCounters,
                    capacityBefore,
                    rollbackIncarnationBefore,
                    rollbackIncarnationAfter,
                    durableBeforeSha256,
                    ComputeHostedScoreOnlyIdentitySha256(durableAfterStop));
            }
        }
        finally
        {
            fixture.Coordinator.OuterLoopProbe = null;
            if (recordObserverAttached)
            {
                fixture.DebugLogWriter.DetachRecordObserver(capture);
            }
            if (host is not null && !hostStopped)
            {
                try
                {
                    using var cancellation =
                        new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await host.StopAsync(cancellation.Token);
                }
                catch
                {
                }
            }
            if (forwardingWriterAttached)
            {
                fixture.DebugLogWriter.DetachForwardingWriter(evidenceWriter);
            }
            if (writerStarted && !writerStopped)
            {
                try
                {
                    using var cancellation =
                        new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await evidenceWriter.StopAsync(cancellation.Token);
                }
                catch
                {
                }
            }
            host?.Dispose();
            if (ownsWriterRoot)
            {
                DeleteScale256HostedOwnedWriterRoot(writerRoot);
            }
        }
    }

    private static Scale256HostedCyclePlan LoadScale256HostedCyclePlan(
        string? evidenceRoot,
        string? cyclePlanPath)
    {
        if (string.IsNullOrWhiteSpace(evidenceRoot)
            && string.IsNullOrWhiteSpace(cyclePlanPath))
        {
            var fallback = CreateDefaultScale256HostedCyclePlan("in-process-test");
            ValidateScale256HostedCyclePlan(fallback, expectedRunId: null);
            return fallback;
        }
        if (string.IsNullOrWhiteSpace(evidenceRoot)
            || string.IsNullOrWhiteSpace(cyclePlanPath))
        {
            throw new InvalidDataException(
                "The fixed-256 evidence root and cycle plan must be supplied together.");
        }
        var root = ValidateScoreOnlyPerformanceEvidenceRoot(evidenceRoot);
        var expectedPath = Path.Combine(root, "cycle-plan.json");
        var fullPath = Path.GetFullPath(cyclePlanPath);
        if (!string.Equals(fullPath, expectedPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The fixed-256 cycle plan must be the run-root cycle-plan.json file.");
        }
        var file = new FileInfo(fullPath);
        if (!file.Exists
            || file.Attributes.HasFlag(FileAttributes.ReparsePoint)
            || file.Length is <= 0 or > 1_048_576)
        {
            throw new InvalidDataException(
                "The fixed-256 cycle plan is missing, redirected, empty, or oversized.");
        }
        var plan = JsonSerializer.Deserialize<Scale256HostedCyclePlan>(
            File.ReadAllBytes(fullPath),
            ScoreOnlyPerformanceJsonOptions)
            ?? throw new InvalidDataException("The fixed-256 cycle plan is invalid.");
        ValidateScale256HostedCyclePlan(plan, new DirectoryInfo(root).Name);
        return plan;
    }

    private static Scale256HostedCyclePlan CreateDefaultScale256HostedCyclePlan(
        string evidenceRunId)
        => new(
            SchemaVersion: 1,
            Contract: Scale256HostedCyclePlanContract,
            EvidenceRunId: evidenceRunId,
            NormalIntervalMilliseconds: HostedScoreOnlyNormalIntervalMilliseconds,
            EventIntervalMilliseconds: HostedScoreOnlyEventIntervalMilliseconds,
            TotalCycleCount: Scale256HostedTotalCycleCount,
            WarmupCycleCount: Scale256HostedWarmupCycleCount,
            MeasuredCycleCount: Scale256HostedMeasuredCycleCount,
            BaseObservedAtUtcTicks: 638_901_408_000_000_000L,
            Processes: CreateHostedScoreOnlyIdentitySet(
                "fixed-256",
                Scale256HostedProcessCount,
                firstProcessId: 50_000,
                firstProcessStartKey: 132_537_800_000_000_000,
                softwarePrefix: "software:scale256-hosted")
                .Processes);

    private static void ValidateScale256HostedCyclePlan(
        Scale256HostedCyclePlan plan,
        string? expectedRunId)
    {
        var expected = CreateDefaultScale256HostedCyclePlan(plan.EvidenceRunId);
        Assert.Equal(1, plan.SchemaVersion);
        Assert.Equal(Scale256HostedCyclePlanContract, plan.Contract);
        Assert.Matches("^[a-zA-Z0-9][a-zA-Z0-9._-]{0,127}$", plan.EvidenceRunId);
        if (expectedRunId is not null)
        {
            Assert.Equal(expectedRunId, plan.EvidenceRunId);
        }
        Assert.Equal(expected.NormalIntervalMilliseconds,
            plan.NormalIntervalMilliseconds);
        Assert.Equal(expected.EventIntervalMilliseconds,
            plan.EventIntervalMilliseconds);
        Assert.Equal(expected.TotalCycleCount, plan.TotalCycleCount);
        Assert.Equal(expected.WarmupCycleCount, plan.WarmupCycleCount);
        Assert.Equal(expected.MeasuredCycleCount, plan.MeasuredCycleCount);
        Assert.Equal(expected.BaseObservedAtUtcTicks, plan.BaseObservedAtUtcTicks);
        Assert.Equal(expected.Processes, plan.Processes);
        Assert.Equal(Scale256HostedProcessCount, plan.Processes.Length);
        Assert.Equal(
            plan.Processes.Length,
            plan.Processes.Select(static value => value.ProcessId).Distinct().Count());
        Assert.Equal(
            plan.Processes.Length,
            plan.Processes.Select(static value => value.ProcessStartKey).Distinct().Count());
        Assert.Equal(
            plan.Processes.Length,
            plan.Processes.Select(static value => value.SoftwareId)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    private static HostedScoreOnlyCyclePermit[] CreateScale256HostedPermits(
        Scale256HostedCyclePlan plan)
    {
        var permits = new HostedScoreOnlyCyclePermit[Scale256HostedTotalCycleCount];
        for (var sequence = 1;
             sequence <= Scale256HostedTotalCycleCount;
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
            var processes = plan.Processes
                .Select(process => new SchedulingProcessFact(
                    process.ProcessId,
                    process.ProcessStartKey,
                    $"scale256-{process.ProcessId}",
                    $@"c:\tests\scale256-{process.ProcessId}.exe",
                    process.SoftwareId,
                    $"Scale256 {process.ProcessId}",
                    "general",
                    "General",
                    process.BaseScore,
                    metrics,
                    process.CpuUsagePercent,
                    process.MemoryUsagePercent,
                    generation,
                    []))
                .ToArray();
            var snapshot = new SchedulingProcessFactSnapshot(
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
            permits[sequence - 1] = new HostedScoreOnlyCyclePermit(
                sequence,
                ResolveHostedScoreOnlyTrigger(sequence),
                "fixed-256",
                sequence >= Scale256HostedFirstMeasuredSequence,
                generation,
                observedAtUtcTicks,
                hardware,
                snapshot,
                ComputeHostedScoreOnlyProcessIdentitySha256(processes));
        }
        return permits;
    }

    private static Scale256HostedSteadyAssessment AssertScale256HostedSteadyBudgets(
        IReadOnlyList<Scale256HostedCycleObservation> observations)
    {
        Assert.Equal(Scale256HostedTotalCycleCount, observations.Count);
        var steady = observations
            .Where(static value => value.Measured)
            .OrderBy(static value => value.Sequence)
            .ToArray();
        Assert.Equal(Scale256HostedMeasuredCycleCount, steady.Length);
        Assert.Equal(
            Enumerable.Range(
                Scale256HostedFirstMeasuredSequence,
                Scale256HostedMeasuredCycleCount),
            steady.Select(static value => value.Sequence));
        var elapsedP99 = GetScale256NearestRankPercentile(
            steady.Select(static value => value.ElapsedMilliseconds), 99);
        var hostCpuP99 = GetScale256NearestRankPercentile(
            steady.Select(static value => value.HostCpuMilliseconds), 99);
        var allocatedP99 = checked((long)GetScale256NearestRankPercentile(
            steady.Select(static value => checked((double)value.AllocatedBytes)), 99));
        Assert.True(elapsedP99 <= Scale256HostedMaximumElapsedP99Milliseconds);
        Assert.True(hostCpuP99 <= Scale256HostedMaximumHostCpuP99Milliseconds);
        Assert.True(allocatedP99 <= Scale256HostedMaximumAllocatedP99Bytes);
        var early = GetScale256NearestRankPercentile(
            steady.Take(16).Select(static value => checked((double)value.AllocatedBytes)),
            50);
        var late = GetScale256NearestRankPercentile(
            steady.TakeLast(16)
                .Select(static value => checked((double)value.AllocatedBytes)),
            50);
        var growth = checked((long)(late - early));
        Assert.True(growth <= Scale256HostedMaximumAllocationP50GrowthBytes);
        return new Scale256HostedSteadyAssessment(
            elapsedP99,
            hostCpuP99,
            allocatedP99,
            checked((long)early),
            checked((long)late),
            growth);
    }

    private static double GetScale256NearestRankPercentile(
        IEnumerable<double> values,
        int percentile)
    {
        var ordered = values.Order().ToArray();
        Assert.NotEmpty(ordered);
        var index = Math.Max(
            0,
            checked((int)Math.Ceiling(ordered.Length * percentile / 100.0) - 1));
        return ordered[index];
    }

    private static void WriteScale256HostedEvidenceReceipts(
        string requestedRoot,
        Scale256HostedCyclePlan plan,
        IReadOnlyList<HostedScoreOnlyCyclePermit> permits,
        IReadOnlyList<Scale256HostedCycleObservation> observations,
        IReadOnlyList<OuterLoopTrace> traces,
        IReadOnlyList<OuterLoopRawRecordIdentity> rawRecords,
        JsonDebugDiagnosticLogSealReceipt writerReceipt,
        Scale256HostedSteadyAssessment steadyAssessment,
        IReadOnlyList<TimestampedShutdownPoint> shutdownPoints,
        long hostStartRequestedAtQpcTicks,
        long hostStartReturnedAtQpcTicks,
        long hostStopRequestedAtQpcTicks,
        long hostStopReturnedAtQpcTicks,
        HostedScoreOnlyEffectCounterSnapshot effectCounters,
        NativeSmartCoordinatorCapacity capacity,
        ulong rollbackIncarnationBefore,
        ulong rollbackIncarnationAfter,
        string durableBeforeSha256,
        string durableAfterSha256)
    {
        var root = ValidateScoreOnlyPerformanceEvidenceRoot(requestedRoot);
        WriteOuterLoopJsonFile(
            Path.Combine(root, "scale256-receipt.json"),
            new
            {
                schemaVersion = 1,
                contract = "non-adapted-scale-256-hosted-outer-loop-receipt-v1",
                evidenceRunId = plan.EvidenceRunId,
                measurementContract = Scale256HostedMeasurementContract,
                timingScope = Scale256HostedMeasurementBoundary,
                qpcFrequency = Stopwatch.Frequency,
                topology = new
                {
                    processCount = Scale256HostedProcessCount,
                    softwareCount = Scale256HostedProcessCount,
                    inputRowCount = 2 * Scale256HostedProcessCount,
                    snapshotRowCount = 2 * Scale256HostedProcessCount,
                    totalCycleCount = Scale256HostedTotalCycleCount,
                    coldSequence = 1,
                    warmupFirstSequence = 2,
                    warmupLastSequence = 9,
                    steadyFirstSequence = Scale256HostedFirstMeasuredSequence,
                    steadyLastSequence = Scale256HostedTotalCycleCount
                },
                lifecycle = new
                {
                    before = HostManagerSmartCoordinatorLifecycleState.Created.ToString(),
                    after = HostManagerSmartCoordinatorLifecycleState.Closed.ToString(),
                    hostStartRequestedAtQpcTicks,
                    hostStartReturnedAtQpcTicks,
                    hostStopRequestedAtQpcTicks,
                    hostStopReturnedAtQpcTicks,
                    shutdownPoints
                },
                workspace = new
                {
                    capacity.InputRowCapacity,
                    capacity.ActionCapacity,
                    capacity.FeedbackCapacity,
                    capacity.SnapshotRowCapacity
                },
                zeroEffects = new
                {
                    canonical = effectCounters.ToCanonicalString(),
                    satisfied = effectCounters.AllZero
                },
                durableState = new
                {
                    beforeSha256 = durableBeforeSha256,
                    afterSha256 = durableAfterSha256,
                    identical = durableBeforeSha256 == durableAfterSha256,
                    rollbackIncarnationBefore,
                    rollbackIncarnationAfter
                },
                budgets = new
                {
                    scope = "steady-inner-profiler-only",
                    maximumElapsedP99Milliseconds =
                        Scale256HostedMaximumElapsedP99Milliseconds,
                    maximumHostCpuP99Milliseconds =
                        Scale256HostedMaximumHostCpuP99Milliseconds,
                    maximumAllocatedP99Bytes =
                        Scale256HostedMaximumAllocatedP99Bytes,
                    allocationTrendWindowCycleCount = 16,
                    maximumAllocationP50GrowthBytes =
                        Scale256HostedMaximumAllocationP50GrowthBytes,
                    outerPhaseThresholds = Array.Empty<double>(),
                    actual = steadyAssessment
                },
                cycles = traces.Select((trace, index) => new
                {
                    permit = new
                    {
                        permits[index].Sequence,
                        permits[index].Trigger,
                        permits[index].EpochId,
                        permits[index].Measured,
                        permits[index].SourceGeneration,
                        processCount = permits[index].ProcessSnapshot.Processes.Count
                    },
                    trace.AttemptId,
                    trace.DispatchSequence,
                    trigger = trace.Trigger.ToString(),
                    events = trace.Events.Select(static item => new
                    {
                        phase = item.Phase.ToString(),
                        item.QpcTicks,
                        item.QpcFrequency,
                        item.ProviderTimestamp,
                        item.ProviderTimestampFrequency,
                        item.ConsumedWake,
                        item.PublishedDeadline,
                        item.NativeCycleSequence,
                        item.DiagnosticCycleSequence,
                        item.ProducerInstanceId,
                        item.DiagnosticRunId,
                        item.WriterAccepted,
                        item.Outcome
                    }).ToArray(),
                    performance = observations[index]
                }).ToArray(),
                satisfied = true
            });
        WriteOuterLoopJsonFile(
            Path.Combine(root, "writer-receipt.json"),
            new
            {
                schemaVersion = 1,
                contract = "non-adapted-scale-256-hosted-writer-receipt-v1",
                expectedRecordCount = Scale256HostedTotalCycleCount,
                writerReceipt.AcceptedRecordCount,
                writerReceipt.WrittenRecordCount,
                writerReceipt.DroppedRecordCount,
                writerReceipt.FailedRecordCount,
                writerReceipt.RejectedRecordCount,
                logFile = "Config/Diagnostics/DebugLogs/debug-log.jsonl",
                writerReceipt.LogLength,
                writerReceipt.LogSha256,
                writerReceipt.QpcFrequency,
                writerReceipt.SealStartedAtQpcTicks,
                writerReceipt.AdmissionClosedAtQpcTicks,
                writerReceipt.DrainCompletedAtQpcTicks,
                writerReceipt.FlushStartedAtQpcTicks,
                writerReceipt.FlushCompletedAtQpcTicks,
                writerReceipt.HashCompletedAtQpcTicks,
                writerReceipt.SealCompletedAtQpcTicks,
                rawRecords,
                satisfied = writerReceipt.Satisfied
            });
        WriteOuterLoopJsonFile(
            Path.Combine(root, "assessment.json"),
            new
            {
                schemaVersion = 1,
                contract = "non-adapted-scale-256-hosted-outer-loop-assessment-v1",
                evidenceRunId = plan.EvidenceRunId,
                coldSequence = 1,
                warmupFirstSequence = 2,
                warmupLastSequence = 9,
                steadyFirstSequence = Scale256HostedFirstMeasuredSequence,
                steadyLastSequence = Scale256HostedTotalCycleCount,
                steadyManualCount = 64,
                steadyScheduledCount = 64,
                integritySatisfied = true,
                performanceQualified = true,
                budgetSource = "production-inner-profiler-sequences-10-through-137",
                thresholds = new
                {
                    elapsedP99Milliseconds =
                        Scale256HostedMaximumElapsedP99Milliseconds,
                    hostCpuP99Milliseconds =
                        Scale256HostedMaximumHostCpuP99Milliseconds,
                    allocatedP99Bytes = Scale256HostedMaximumAllocatedP99Bytes,
                    allocationP50GrowthBytes =
                        Scale256HostedMaximumAllocationP50GrowthBytes
                },
                actual = steadyAssessment
            });
    }

    private sealed class Scale256HostedCycleCapture(
        NativeSmartCoordinatorWorkspace workspace,
        IReadOnlyList<HostedScoreOnlyCyclePermit> permits,
        IReadOnlyList<double> processStateMultipliers)
        : IScoreOnlyDiagnosticRecordObserver
    {
        private readonly object sync = new();
        private readonly Scale256NativeTuple[] tuples = new Scale256NativeTuple[
            Scale256HostedTotalCycleCount * Scale256HostedProcessCount];
        private readonly Scale256RecordMetric[] metrics = new Scale256RecordMetric[
            Scale256HostedTotalCycleCount];
        private readonly int[] written = new int[Scale256HostedTotalCycleCount];
        private Exception? failure;

        public void Observe(DebugDiagnosticLogRecord record)
        {
            try
            {
                lock (sync)
                {
                    if (failure is null)
                    {
                        CaptureCore(record);
                    }
                }
            }
            catch (Exception exception)
            {
                Interlocked.CompareExchange(ref failure, exception, null);
            }
        }

        private void CaptureCore(DebugDiagnosticLogRecord record)
        {
            AssertExactScoreOnlyPerformanceRecord(record);
            var sequence = Assert.IsType<long>(record.Properties["cycleSequence"]);
            Assert.InRange(sequence, 1, Scale256HostedTotalCycleCount);
            var slot = checked((int)sequence - 1);
            Assert.Equal(0, Volatile.Read(ref written[slot]));
            var snapshot = workspace.Snapshot;
            Assert.Equal(checked((ulong)sequence), snapshot.CycleSequence);
            Assert.Equal(checked((uint)Scale256HostedProcessCount), snapshot.ProcessCount);
            Assert.Equal(checked((uint)Scale256HostedProcessCount), snapshot.SoftwareCount);
            Assert.Equal(checked((uint)(2 * Scale256HostedProcessCount)),
                snapshot.SnapshotRowCount);
            Assert.Equal(0U, snapshot.ActionCount);
            Assert.Equal(0U, snapshot.PendingCount);
            Assert.Equal(0U, snapshot.InflightCount);
            Assert.Equal(0U, snapshot.InvalidFactCount);
            Assert.True(snapshot.Flags.HasFlag(NativeSmartCoordinatorSnapshotFlags.ScoreOnly));
            Assert.False(snapshot.Flags.HasFlag(
                NativeSmartCoordinatorSnapshotFlags.HasInvalidFacts));
            var rows = workspace.CurrentSnapshotRows;
            var offset = checked(slot * Scale256HostedProcessCount);
            for (var index = 0; index < Scale256HostedProcessCount; index++)
            {
                var row = rows[index];
                Assert.Equal(NativeSmartCoordinatorSnapshotRowKind.Process, row.RowKind);
                tuples[offset + index] = new Scale256NativeTuple(
                    row.TargetKey,
                    row.SoftwareKey,
                    row.ProcessStartKey,
                    row.ProcessId,
                    row.SourceIndex,
                    row.BaseScore,
                    row.CpuOccupancyPercent,
                    checked((int)row.RuntimeState),
                    row.CpuScore,
                    checked((ulong)row.ValidMask),
                    checked((ulong)row.ReasonMask));
            }
            metrics[slot] = new Scale256RecordMetric(
                checked((int)sequence),
                Assert.IsType<string>(record.Properties["trigger"]),
                Assert.IsType<string>(record.Properties["producerInstanceId"]),
                Assert.IsType<string>(record.Properties["runId"]),
                Assert.IsType<long>(record.Properties["cycleStartedAtQpcTicks"]),
                Assert.IsType<long>(record.Properties["cycleCompletedAtQpcTicks"]),
                Assert.IsType<double>(record.Properties["elapsedMs"]),
                Assert.IsType<double>(record.Properties["hostProcessCpuMs"]),
                Assert.IsType<long>(record.Properties["hostProcessAllocatedBytes"]));
            Volatile.Write(ref written[slot], 1);
        }

        internal Scale256HostedCycleObservation[] AssertCompleteAndExact()
        {
            lock (sync)
            {
                if (failure is not null)
                {
                    throw new InvalidDataException(
                        "The fixed-256 cycle observer failed.",
                        failure);
                }
                var results = new Scale256HostedCycleObservation[
                    Scale256HostedTotalCycleCount];
                string? producerInstanceId = null;
                var runIds = new HashSet<string>(StringComparer.Ordinal);
                for (var slot = 0; slot < results.Length; slot++)
                {
                    Assert.Equal(1, Volatile.Read(ref written[slot]));
                    var permit = permits[slot];
                    var metric = metrics[slot];
                    Assert.Equal(slot + 1, permit.Sequence);
                    Assert.Equal(permit.Sequence, metric.Sequence);
                    Assert.Equal(permit.Trigger, metric.Trigger);
                    producerInstanceId ??= metric.ProducerInstanceId;
                    Assert.Equal(producerInstanceId, metric.ProducerInstanceId);
                    Assert.True(runIds.Add(metric.DiagnosticRunId));
                    if (slot > 0)
                    {
                        Assert.True(
                            metric.CycleStartedAtQpcTicks >=
                            metrics[slot - 1].CycleCompletedAtQpcTicks);
                    }
                    var expected = permit.ProcessSnapshot.Processes
                        .Select((fact, index) => new
                        {
                            Fact = fact,
                            ExpectedSourceIndex = checked((uint)(index * 2))
                        })
                        .ToDictionary(
                            static value => (
                                checked((uint)value.Fact.ProcessId),
                                value.Fact.ProcessStartKey));
                    var actual = new Scale256NativeTuple[Scale256HostedProcessCount];
                    Array.Copy(
                        tuples,
                        checked(slot * Scale256HostedProcessCount),
                        actual,
                        0,
                        actual.Length);
                    Array.Sort(actual, static (left, right) =>
                    {
                        var comparison = left.ProcessId.CompareTo(right.ProcessId);
                        return comparison != 0
                            ? comparison
                            : left.ProcessStartKey.CompareTo(right.ProcessStartKey);
                    });
                    var canonical = new StringBuilder(
                        Scale256HostedProcessCount * 192);
                    for (var index = 0; index < actual.Length; index++)
                    {
                        var tuple = actual[index];
                        Assert.True(expected.Remove(
                            (tuple.ProcessId, tuple.ProcessStartKey),
                            out var expectedValue));
                        var fact = expectedValue.Fact;
                        Assert.Equal(expectedValue.ExpectedSourceIndex, tuple.SourceIndex);
                        Assert.Equal(
                            NativeStableIdentity.CreateCaseInsensitiveKey(
                                HostManagerTargetIdentity.CreateProcessTargetId(
                                    fact.ProcessId,
                                    fact.ProcessStartKey)),
                            tuple.TargetKey);
                        Assert.Equal(
                            NativeStableIdentity.CreateCaseInsensitiveKey(fact.SoftwareId),
                            tuple.SoftwareKey);
                        Assert.Equal(fact.BaseScore, tuple.BaseScore, precision: 12);
                        Assert.Equal(
                            fact.CpuUsagePercent,
                            tuple.CpuOccupancyPercent,
                            precision: 12);
                        Assert.InRange(
                            tuple.RuntimeStateValue,
                            0,
                            processStateMultipliers.Count - 1);
                        var expectedScore = fact.BaseScore
                            * processStateMultipliers[tuple.RuntimeStateValue]
                            * (fact.CpuUsagePercent / 100D);
                        Assert.InRange(
                            Math.Abs(tuple.CpuScore - expectedScore),
                            0D,
                            0.000_000_001D);
                        Assert.Equal(7679UL, tuple.ValidMaskValue);
                        var expectedReason = fact.BaseScore >= 81
                            ? NativeSmartCoordinatorReason.HighTierBlocksOptimization
                            : NativeSmartCoordinatorReason.None;
                        var actualReason = (NativeSmartCoordinatorReason)tuple.ReasonValue;
                        Assert.False(actualReason.HasFlag(
                            NativeSmartCoordinatorReason.ReservedSystemReason0));
                        Assert.Equal(expectedReason, actualReason);
                        if (index != 0)
                        {
                            canonical.Append('\n');
                        }
                        canonical.Append(tuple.ProcessId.ToString(CultureInfo.InvariantCulture));
                        canonical.Append('|');
                        canonical.Append(tuple.ProcessStartKey.ToString(
                            CultureInfo.InvariantCulture));
                        canonical.Append('|');
                        canonical.Append(tuple.TargetKey.ToString(CultureInfo.InvariantCulture));
                        canonical.Append('|');
                        canonical.Append(tuple.SoftwareKey.ToString(CultureInfo.InvariantCulture));
                        canonical.Append('|');
                        canonical.Append(tuple.SourceIndex.ToString(CultureInfo.InvariantCulture));
                        canonical.Append('|');
                        canonical.Append(tuple.BaseScore.ToString("R", CultureInfo.InvariantCulture));
                        canonical.Append('|');
                        canonical.Append(tuple.CpuOccupancyPercent.ToString(
                            "R",
                            CultureInfo.InvariantCulture));
                        canonical.Append('|');
                        canonical.Append(tuple.RuntimeStateValue.ToString(
                            CultureInfo.InvariantCulture));
                        canonical.Append('|');
                        canonical.Append(tuple.CpuScore.ToString("R", CultureInfo.InvariantCulture));
                        canonical.Append('|');
                        canonical.Append(tuple.ValidMaskValue.ToString(
                            CultureInfo.InvariantCulture));
                        canonical.Append('|');
                        canonical.Append(tuple.ReasonValue.ToString(
                            CultureInfo.InvariantCulture));
                    }
                    Assert.Empty(expected);
                    results[slot] = new Scale256HostedCycleObservation(
                        permit.Sequence,
                        permit.Trigger,
                        permit.Sequence == 1
                            ? "hosted-bootstrap-cold"
                            : permit.Measured ? "steady" : "warmup",
                        permit.Measured,
                        metric.ProducerInstanceId,
                        metric.DiagnosticRunId,
                        metric.CycleStartedAtQpcTicks,
                        metric.CycleCompletedAtQpcTicks,
                        metric.ElapsedMilliseconds,
                        metric.HostCpuMilliseconds,
                        metric.AllocatedBytes,
                        Convert.ToHexString(SHA256.HashData(
                            Encoding.UTF8.GetBytes(canonical.ToString()))));
                }
                Assert.Equal(Scale256HostedTotalCycleCount, runIds.Count);
                return results;
            }
        }
    }

    private static void DeleteScale256HostedOwnedWriterRoot(string requestedRoot)
    {
        var root = Path.GetFullPath(requestedRoot);
        var allowedPrefix = Path.GetFullPath(Path.Combine(
            Path.GetTempPath(),
            "resource-manager-scale256-hosted"))
            .TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!root.StartsWith(allowedPrefix, StringComparison.OrdinalIgnoreCase)
            || !Directory.Exists(root))
        {
            return;
        }
        var pending = new Stack<string>();
        var directories = new List<string>();
        pending.Push(root);
        while (pending.Count != 0)
        {
            if (directories.Count >= 1_024)
            {
                throw new InvalidDataException(
                    "The fixed-256 temporary evidence tree exceeded its cleanup bound.");
            }
            var current = pending.Pop();
            var info = new DirectoryInfo(current);
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidDataException(
                    "The fixed-256 temporary evidence tree contains a reparse point.");
            }
            directories.Add(current);
            foreach (var file in Directory.EnumerateFiles(current))
            {
                File.Delete(file);
            }
            foreach (var directory in Directory.EnumerateDirectories(current))
            {
                pending.Push(directory);
            }
        }
        for (var index = directories.Count - 1; index >= 0; index--)
        {
            Directory.Delete(directories[index], recursive: false);
        }
    }

    private sealed record Scale256HostedCyclePlan(
        int SchemaVersion,
        string Contract,
        string EvidenceRunId,
        int NormalIntervalMilliseconds,
        int EventIntervalMilliseconds,
        int TotalCycleCount,
        int WarmupCycleCount,
        int MeasuredCycleCount,
        long BaseObservedAtUtcTicks,
        HostedScoreOnlyProcessPlan[] Processes);

    private readonly record struct Scale256NativeTuple(
        ulong TargetKey,
        ulong SoftwareKey,
        ulong ProcessStartKey,
        uint ProcessId,
        uint SourceIndex,
        double BaseScore,
        double CpuOccupancyPercent,
        int RuntimeStateValue,
        double CpuScore,
        ulong ValidMaskValue,
        ulong ReasonValue);

    private sealed record Scale256RecordMetric(
        int Sequence,
        string Trigger,
        string ProducerInstanceId,
        string DiagnosticRunId,
        long CycleStartedAtQpcTicks,
        long CycleCompletedAtQpcTicks,
        double ElapsedMilliseconds,
        double HostCpuMilliseconds,
        long AllocatedBytes);

    private sealed record Scale256HostedSteadyAssessment(
        double ElapsedP99Milliseconds,
        double HostCpuP99Milliseconds,
        long AllocatedP99Bytes,
        long EarlyAllocatedP50Bytes,
        long LateAllocatedP50Bytes,
        long AllocationP50GrowthBytes);

    private sealed record Scale256HostedCycleObservation(
        int Sequence,
        string Trigger,
        string Partition,
        bool Measured,
        string ProducerInstanceId,
        string DiagnosticRunId,
        long CycleStartedAtQpcTicks,
        long CycleCompletedAtQpcTicks,
        double ElapsedMilliseconds,
        double HostCpuMilliseconds,
        long AllocatedBytes,
        string NativeProcessTupleSha256);
}
