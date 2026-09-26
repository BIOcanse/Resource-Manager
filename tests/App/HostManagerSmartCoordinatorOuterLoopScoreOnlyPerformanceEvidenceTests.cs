using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using ResourceManager.App.Infrastructure.Diagnostics;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerSmartCoordinatorScoreOnlyCompositionTests
{
    private const string OuterLoopScoreOnlyEvidenceRootEnvironmentVariable =
        "RESOURCE_MANAGER_OUTER_LOOP_SCORE_ONLY_PERFORMANCE_EVIDENCE_ROOT";
    private const string OuterLoopScoreOnlyEpochPlanEnvironmentVariable =
        "RESOURCE_MANAGER_OUTER_LOOP_SCORE_ONLY_PERFORMANCE_EPOCH_PLAN";

    [Fact]
    public async Task OuterLoopScoreOnlyCapturesHostedPhasesWithoutEffects()
    {
        var evidenceRoot = Environment.GetEnvironmentVariable(
            OuterLoopScoreOnlyEvidenceRootEnvironmentVariable);
        var epochPlanPath = Environment.GetEnvironmentVariable(
            OuterLoopScoreOnlyEpochPlanEnvironmentVariable);
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
                "The production outer-loop wake deadline is unavailable.");
        var traceProbe = new OuterLoopTraceProbe();
        var shutdownProbe = new TimestampedOuterLoopShutdownProbe();
        fixture.Coordinator.OuterLoopProbe = traceProbe;
        fixture.Coordinator.TransitionProbe = shutdownProbe;
        fixture.DebugLogWriter.DisableRecordRetention();

        var ownsWriterRoot = string.IsNullOrWhiteSpace(evidenceRoot);
        var writerRoot = ownsWriterRoot
            ? Path.Combine(
                Path.GetTempPath(),
                "resource-manager-outer-loop-score-only",
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
        var lifecycleBefore = fixture.Coordinator.LifecycleState;
        Assert.Equal(HostManagerSmartCoordinatorLifecycleState.Created, lifecycleBefore);
        Assert.Equal(default, deadline.Snapshot);

        IHost? host = null;
        var writerStarted = false;
        var forwardingWriterAttached = false;
        var hostStopped = false;
        var writerStopped = false;
        JsonDebugDiagnosticLogSealReceipt? writerReceipt = null;
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

            source.Enqueue(permits[0]);
            hostStartRequestedAtQpcTicks = Stopwatch.GetTimestamp();
            using (var startCancellation =
                   new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            {
                await host.StartAsync(startCancellation.Token);
            }
            hostStartReturnedAtQpcTicks = Stopwatch.GetTimestamp();
            Assert.Equal(
                HostManagerSmartCoordinatorLifecycleState.Running,
                fixture.Coordinator.LifecycleState);
            await WaitForHostedScoreOnlyDeadlineVersionAsync(
                deadline,
                expectedVersion: 1,
                TimeSpan.FromSeconds(5));

            for (var sequence = 2;
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

            Assert.Equal(HostedScoreOnlyLastMeasuredSequence, source.CompletedCount);
            Assert.Equal(
                checked((ulong)HostedScoreOnlyLastMeasuredSequence),
                deadline.Snapshot.Version);
            Assert.Same(workspaceBefore, fixture.Workspace);
            Assert.Equal(capacityBefore, fixture.Workspace.Capacity);
            Assert.Equal(0U, fixture.Workspace.Snapshot.ProcessCount);
            Assert.Equal(0U, fixture.Workspace.Snapshot.SoftwareCount);
            Assert.Equal(0U, fixture.Workspace.Snapshot.ActionCount);

            hostStopRequestedAtQpcTicks = Stopwatch.GetTimestamp();
            using (var stopCancellation =
                   new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            {
                await host.StopAsync(stopCancellation.Token);
            }
            hostStopReturnedAtQpcTicks = Stopwatch.GetTimestamp();
            hostStopped = true;
            Assert.Equal(
                HostManagerSmartCoordinatorLifecycleState.Closed,
                fixture.Coordinator.LifecycleState);
            shutdownProbe.AssertExactOrder(
                hostStopRequestedAtQpcTicks,
                hostStopReturnedAtQpcTicks);

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

            writerReceipt = await evidenceWriter.SealForEvidenceAsync(
                HostedScoreOnlyLastMeasuredSequence,
                TimeSpan.FromSeconds(10),
                CancellationToken.None);
            AssertOuterLoopWriterReceipt(writerReceipt);
            fixture.DebugLogWriter.DetachForwardingWriter(evidenceWriter);
            forwardingWriterAttached = false;
            using (var writerStopCancellation =
                   new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            {
                await evidenceWriter.StopAsync(writerStopCancellation.Token);
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
            Assert.True(writerReceipt.SealStartedAtQpcTicks >= hostStopReturnedAtQpcTicks);

            if (!string.IsNullOrWhiteSpace(evidenceRoot))
            {
                WriteOuterLoopEvidenceReceipts(
                    evidenceRoot!,
                    plan,
                    completedPermits,
                    traces,
                    rawRecords,
                    writerReceipt,
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
            if (ownsWriterRoot && Directory.Exists(writerRoot))
            {
                Directory.Delete(writerRoot, recursive: true);
            }
        }
    }

    private static void AssertOuterLoopWriterReceipt(
        JsonDebugDiagnosticLogSealReceipt receipt)
    {
        Assert.True(receipt.Satisfied);
        Assert.Equal(HostedScoreOnlyLastMeasuredSequence, receipt.AcceptedRecordCount);
        Assert.Equal(HostedScoreOnlyLastMeasuredSequence, receipt.WrittenRecordCount);
        Assert.Equal(0, receipt.DroppedRecordCount);
        Assert.Equal(0, receipt.FailedRecordCount);
        Assert.Equal(0, receipt.RejectedRecordCount);
        Assert.InRange(receipt.LogLength, 1, HostedScoreOnlyMaximumRawLogBytes);
        Assert.NotNull(receipt.LogSha256);
        Assert.True(receipt.QpcFrequency > 0);
        Assert.InRange(
            receipt.AdmissionClosedAtQpcTicks,
            receipt.SealStartedAtQpcTicks,
            long.MaxValue);
        Assert.InRange(
            receipt.DrainCompletedAtQpcTicks,
            receipt.AdmissionClosedAtQpcTicks,
            long.MaxValue);
        Assert.NotNull(receipt.FlushStartedAtQpcTicks);
        Assert.NotNull(receipt.FlushCompletedAtQpcTicks);
        Assert.NotNull(receipt.HashCompletedAtQpcTicks);
        Assert.InRange(
            receipt.FlushStartedAtQpcTicks!.Value,
            receipt.DrainCompletedAtQpcTicks,
            long.MaxValue);
        Assert.InRange(
            receipt.FlushCompletedAtQpcTicks!.Value,
            receipt.FlushStartedAtQpcTicks.Value,
            long.MaxValue);
        Assert.InRange(
            receipt.HashCompletedAtQpcTicks!.Value,
            receipt.FlushCompletedAtQpcTicks.Value,
            long.MaxValue);
        Assert.InRange(
            receipt.SealCompletedAtQpcTicks,
            receipt.HashCompletedAtQpcTicks.Value,
            long.MaxValue);
        Assert.Equal(
            HostedScoreOnlyLastMeasuredSequence,
            File.ReadLines(receipt.LogPath).Count());
    }

    private static OuterLoopRawRecordIdentity[] ReadOuterLoopRawRecordIdentities(
        string logPath)
        => File.ReadLines(logPath)
            .Select(static line =>
            {
                using var document = JsonDocument.Parse(line);
                var properties = document.RootElement.GetProperty("properties");
                return new OuterLoopRawRecordIdentity(
                    properties.GetProperty("cycleSequence").GetInt64(),
                    properties.GetProperty("producerInstanceId").GetString()
                        ?? throw new InvalidDataException("The producer identity is missing."),
                    properties.GetProperty("runId").GetString()
                        ?? throw new InvalidDataException("The diagnostic run identity is missing."),
                    properties.GetProperty("trigger").GetString()
                        ?? throw new InvalidDataException("The diagnostic trigger is missing."),
                    properties.GetProperty("scoreOnly").GetBoolean(),
                    properties.GetProperty("outcome").GetString()
                        ?? throw new InvalidDataException("The diagnostic outcome is missing."),
                    properties.GetProperty("qpcFrequency").GetInt64());
            })
            .ToArray();

    private static void WriteOuterLoopEvidenceReceipts(
        string requestedRoot,
        HostedScoreOnlyEpochPlan plan,
        IReadOnlyList<HostedScoreOnlyCyclePermit> permits,
        IReadOnlyList<OuterLoopTrace> traces,
        IReadOnlyList<OuterLoopRawRecordIdentity> rawRecords,
        JsonDebugDiagnosticLogSealReceipt writerReceipt,
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
            Path.Combine(root, "outer-loop-receipt.json"),
            new
            {
                schemaVersion = 1,
                contract = "non-adapted-outer-loop-score-only-trace-receipt-v1",
                evidenceRunId = plan.EvidenceRunId,
                measurementContract =
                    "non-adapted-hosted-outer-loop-score-only-performance-v1",
                timingScope =
                    "steady-per-cycle-permit-through-writer-admission-plus-corpus-seal-and-dedicated-host-lifecycle",
                qpcFrequency = Stopwatch.Frequency,
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
                    events = trace.Events.Select(static value => new
                    {
                        phase = value.Phase.ToString(),
                        value.QpcTicks,
                        value.QpcFrequency,
                        value.ProviderTimestamp,
                        value.ProviderTimestampFrequency,
                        consumedWake = value.ConsumedWake,
                        publishedDeadline = value.PublishedDeadline,
                        value.NativeCycleSequence,
                        value.DiagnosticCycleSequence,
                        value.ProducerInstanceId,
                        value.DiagnosticRunId,
                        value.WriterAccepted,
                        value.Outcome
                    }).ToArray()
                }).ToArray(),
                satisfied = true
            });
        WriteOuterLoopJsonFile(
            Path.Combine(root, "writer-receipt.json"),
            new
            {
                schemaVersion = 1,
                contract = "non-adapted-outer-loop-score-only-writer-receipt-v1",
                expectedRecordCount = HostedScoreOnlyLastMeasuredSequence,
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
                contract = "non-adapted-outer-loop-score-only-assessment-v1",
                evidenceRunId = plan.EvidenceRunId,
                coldSequence = 1,
                warmupFirstSequence = 2,
                warmupLastSequence = 9,
                steadyFirstSequence = 10,
                steadyLastSequence = 137,
                steadyManualCount = 64,
                steadyScheduledCount = 64,
                integritySatisfied = true,
                performanceQualified = false,
                budgetSource = (string?)null,
                thresholds = new Dictionary<string, double>()
            });
    }

    private static void WriteOuterLoopJsonFile(string path, object value)
    {
        var image = JsonSerializer.SerializeToUtf8Bytes(
            value,
            ScoreOnlyPerformanceJsonOptions);
        if (image.Length > 4 * 1_048_576)
        {
            throw new InvalidDataException(
                $"The outer-loop receipt exceeded its byte bound: {path}");
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

    private sealed class OuterLoopTraceProbe
        : IHostManagerSmartCoordinatorOuterLoopProbe
    {
        private readonly object sync = new();
        private readonly List<HostManagerSmartCoordinatorOuterLoopEvent> events = [];

        public void Observe(in HostManagerSmartCoordinatorOuterLoopEvent value)
        {
            lock (sync)
            {
                events.Add(value);
            }
        }

        internal OuterLoopTrace[] AssertCompleteAndExact(
            IReadOnlyList<HostedScoreOnlyCyclePermit> permits,
            IReadOnlyList<OuterLoopRawRecordIdentity> rawRecords)
        {
            HostManagerSmartCoordinatorOuterLoopEvent[] snapshot;
            lock (sync)
            {
                snapshot = [.. events];
            }
            Assert.Equal(HostedScoreOnlyLastMeasuredSequence, permits.Count);
            Assert.Equal(HostedScoreOnlyLastMeasuredSequence, rawRecords.Count);
            var groups = snapshot
                .GroupBy(static value => value.AttemptId)
                .Select(static group => new OuterLoopTrace(
                    group.Key,
                    group.Max(static value => value.DispatchSequence),
                    Assert.Single(group.Select(static value => value.Trigger).Distinct()),
                    group.ToArray()))
                .OrderBy(static trace => trace.DispatchSequence)
                .ToArray();
            Assert.Equal(HostedScoreOnlyLastMeasuredSequence, groups.Length);
            Assert.Equal(
                Enumerable.Range(1, HostedScoreOnlyLastMeasuredSequence)
                    .Select(static value => (long)value),
                groups.Select(static trace => trace.DispatchSequence));
            Assert.Equal(
                HostedScoreOnlyLastMeasuredSequence,
                groups.Select(static trace => trace.AttemptId).Distinct().Count());

            var producerIds = new HashSet<string>(StringComparer.Ordinal);
            var runIds = new HashSet<string>(StringComparer.Ordinal);
            var publishedDeadlines = groups
                .SelectMany(static trace => trace.Events)
                .Where(static value =>
                    value.Phase ==
                    HostManagerSmartCoordinatorOuterLoopPhase.NextDeadlinePublished)
                .Select(static value => value.PublishedDeadline
                    ?? throw new InvalidDataException(
                        "An outer-loop publication is missing its exact deadline identity."))
                .ToDictionary(static value => value.Version);
            var consumedDeadlineVersions = new HashSet<ulong>();
            ulong previousConsumedDeadlineVersion = 0;
            for (var index = 0; index < groups.Length; index++)
            {
                var sequence = index + 1;
                var trace = groups[index];
                var permit = permits[index];
                var raw = rawRecords[index];
                var expectedTrigger = sequence == 1
                    ? HostManagerSmartCoordinatorOuterLoopTrigger.ScheduledBootstrap
                    : sequence % 2 == 0
                        ? HostManagerSmartCoordinatorOuterLoopTrigger.Manual
                        : HostManagerSmartCoordinatorOuterLoopTrigger.Scheduled;
                Assert.Equal(expectedTrigger, trace.Trigger);
                Assert.Equal(sequence, permit.Sequence);
                Assert.Equal(checked((ulong)sequence), permit.SourceGeneration);
                Assert.All(trace.Events, static value =>
                {
                    Assert.InRange(value.QpcTicks, 1, long.MaxValue);
                    Assert.Equal(Stopwatch.Frequency, value.QpcFrequency);
                });
                Assert.Equal(
                    trace.Events.Length,
                    trace.Events.Select(static value => value.Phase).Distinct().Count());

                HostManagerSmartCoordinatorOuterLoopPhase[] ordered = expectedTrigger switch
                {
                    HostManagerSmartCoordinatorOuterLoopTrigger.ScheduledBootstrap =>
                    [
                        HostManagerSmartCoordinatorOuterLoopPhase.GateWaitStarted,
                        HostManagerSmartCoordinatorOuterLoopPhase.GateAcquired,
                        HostManagerSmartCoordinatorOuterLoopPhase.DispatchStarted
                    ],
                    HostManagerSmartCoordinatorOuterLoopTrigger.Scheduled =>
                    [
                        HostManagerSmartCoordinatorOuterLoopPhase.WaitEntered,
                        HostManagerSmartCoordinatorOuterLoopPhase.WakeReturned,
                        HostManagerSmartCoordinatorOuterLoopPhase.GateWaitStarted,
                        HostManagerSmartCoordinatorOuterLoopPhase.GateAcquired,
                        HostManagerSmartCoordinatorOuterLoopPhase.DispatchStarted
                    ],
                    _ =>
                    [
                        HostManagerSmartCoordinatorOuterLoopPhase.ManualRequest,
                        HostManagerSmartCoordinatorOuterLoopPhase.ForegroundAdmissionCompleted,
                        HostManagerSmartCoordinatorOuterLoopPhase.GateWaitStarted,
                        HostManagerSmartCoordinatorOuterLoopPhase.GateAcquired,
                        HostManagerSmartCoordinatorOuterLoopPhase.DispatchStarted
                    ]
                };
                var common = new[]
                {
                    HostManagerSmartCoordinatorOuterLoopPhase.CoreEntered,
                    HostManagerSmartCoordinatorOuterLoopPhase.ProfilerCreated,
                    HostManagerSmartCoordinatorOuterLoopPhase.NativeSequenceAssigned,
                    HostManagerSmartCoordinatorOuterLoopPhase.CoreTerminal,
                    HostManagerSmartCoordinatorOuterLoopPhase.DiagnosticDisposeStarted,
                    HostManagerSmartCoordinatorOuterLoopPhase.ProfilerStopped,
                    HostManagerSmartCoordinatorOuterLoopPhase.RecordMaterialized,
                    HostManagerSmartCoordinatorOuterLoopPhase.WriterAdmissionStarted,
                    HostManagerSmartCoordinatorOuterLoopPhase.WriterAdmissionCompleted,
                    HostManagerSmartCoordinatorOuterLoopPhase.NextDeadlinePublished,
                    HostManagerSmartCoordinatorOuterLoopPhase.WrapperCompleted,
                    HostManagerSmartCoordinatorOuterLoopPhase.GateReleaseStarted,
                    HostManagerSmartCoordinatorOuterLoopPhase.GateReleased
                };
                var expectedPhases = ordered.Concat(common).ToList();
                if (expectedTrigger == HostManagerSmartCoordinatorOuterLoopTrigger.Manual)
                {
                    expectedPhases.Add(
                        HostManagerSmartCoordinatorOuterLoopPhase.ManualApiReturned);
                }
                Assert.Equal(expectedPhases, trace.Events.Select(static value => value.Phase));
                for (var eventIndex = 1; eventIndex < trace.Events.Length; eventIndex++)
                {
                    Assert.True(
                        trace.Events[eventIndex].QpcTicks >=
                        trace.Events[eventIndex - 1].QpcTicks);
                }

                var native = Assert.Single(trace.Events, static value =>
                    value.Phase ==
                    HostManagerSmartCoordinatorOuterLoopPhase.NativeSequenceAssigned);
                Assert.Equal((ulong)sequence, native.NativeCycleSequence);
                var profiler = Assert.Single(trace.Events, static value =>
                    value.Phase == HostManagerSmartCoordinatorOuterLoopPhase.ProfilerCreated);
                Assert.Equal((long)sequence, profiler.DiagnosticCycleSequence);
                Assert.False(string.IsNullOrWhiteSpace(profiler.ProducerInstanceId));
                producerIds.Add(profiler.ProducerInstanceId!);
                var writer = Assert.Single(trace.Events, static value =>
                    value.Phase ==
                    HostManagerSmartCoordinatorOuterLoopPhase.WriterAdmissionCompleted);
                Assert.True(writer.WriterAccepted is true);
                Assert.Equal((long)sequence, writer.DiagnosticCycleSequence);
                Assert.False(string.IsNullOrWhiteSpace(writer.DiagnosticRunId));
                Assert.True(runIds.Add(writer.DiagnosticRunId!));
                Assert.Equal(raw.CycleSequence, writer.DiagnosticCycleSequence);
                Assert.Equal(raw.ProducerInstanceId, writer.ProducerInstanceId);
                Assert.Equal(raw.DiagnosticRunId, writer.DiagnosticRunId);
                Assert.Equal(permit.Trigger, raw.Trigger);
                Assert.True(raw.ScoreOnly);
                Assert.Equal("completed", raw.Outcome);
                Assert.Equal(Stopwatch.Frequency, raw.QpcFrequency);

                var published = Assert.Single(trace.Events, static value =>
                    value.Phase ==
                    HostManagerSmartCoordinatorOuterLoopPhase.NextDeadlinePublished);
                Assert.NotNull(published.PublishedDeadline);
                Assert.Equal((ulong)sequence, published.PublishedDeadline.Value.Version);
                Assert.True(published.PublishedDeadline.Value.HasDeadline);
                Assert.Equal(
                    Stopwatch.Frequency,
                    published.ProviderTimestampFrequency);
                if (expectedTrigger == HostManagerSmartCoordinatorOuterLoopTrigger.Scheduled)
                {
                    var wake = Assert.Single(trace.Events, static value =>
                        value.Phase == HostManagerSmartCoordinatorOuterLoopPhase.WakeReturned);
                    Assert.NotNull(wake.ConsumedWake);
                    var consumedWake = wake.ConsumedWake.Value;
                    Assert.InRange(consumedWake.Version, 1UL, checked((ulong)sequence - 1));
                    Assert.True(consumedWake.Version > previousConsumedDeadlineVersion);
                    Assert.True(consumedDeadlineVersions.Add(consumedWake.Version));
                    Assert.True(publishedDeadlines.TryGetValue(
                        consumedWake.Version,
                        out var publishedWake));
                    Assert.Equal(publishedWake, consumedWake);
                    previousConsumedDeadlineVersion = consumedWake.Version;
                    Assert.NotNull(wake.ProviderTimestamp);
                    Assert.True(
                        wake.ProviderTimestamp.Value >=
                        consumedWake.DeadlineTimestamp);
                }
                else
                {
                    Assert.All(trace.Events, static value => Assert.Null(value.ConsumedWake));
                }
            }
            Assert.Single(producerIds);
            Assert.Equal(HostedScoreOnlyLastMeasuredSequence, runIds.Count);
            Assert.Equal(
                groups.Count(static trace =>
                    trace.Trigger == HostManagerSmartCoordinatorOuterLoopTrigger.Scheduled),
                consumedDeadlineVersions.Count);
            return groups;
        }
    }

    private sealed class TimestampedOuterLoopShutdownProbe
        : IHostManagerSmartCoordinatorTransitionProbe
    {
        private readonly object sync = new();
        private readonly List<TimestampedShutdownPoint> points = [];

        internal TimestampedShutdownPoint[] Points
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
                points.Add(new TimestampedShutdownPoint(
                    point,
                    Stopwatch.GetTimestamp()));
            }
        }

        internal void AssertExactOrder(long stopRequested, long stopReturned)
        {
            var snapshot = Points;
            Assert.Equal(
                [
                    HostManagerSmartCoordinatorShutdownPoint.AdmissionClosed,
                    HostManagerSmartCoordinatorShutdownPoint.ExactWorkerJoined,
                    HostManagerSmartCoordinatorShutdownPoint.GateDrained,
                    HostManagerSmartCoordinatorShutdownPoint.OwnedResourcesDisposed
                ],
                snapshot.Select(static value => value.Point));
            Assert.True(snapshot[0].QpcTicks >= stopRequested);
            for (var index = 1; index < snapshot.Length; index++)
            {
                Assert.True(snapshot[index].QpcTicks >= snapshot[index - 1].QpcTicks);
            }
            Assert.True(stopReturned >= snapshot[^1].QpcTicks);
        }
    }

    private sealed record OuterLoopTrace(
        long AttemptId,
        long DispatchSequence,
        HostManagerSmartCoordinatorOuterLoopTrigger Trigger,
        HostManagerSmartCoordinatorOuterLoopEvent[] Events);

    private sealed record OuterLoopRawRecordIdentity(
        long CycleSequence,
        string ProducerInstanceId,
        string DiagnosticRunId,
        string Trigger,
        bool ScoreOnly,
        string Outcome,
        long QpcFrequency);

    private sealed record TimestampedShutdownPoint(
        HostManagerSmartCoordinatorShutdownPoint Point,
        long QpcTicks);
}
