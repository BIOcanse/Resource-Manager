using System.Diagnostics;
using ResourceManager.App.Domain.Diagnostics;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.Optimization;

public sealed partial class HostManagerSmartCoordinator
{
    private const string HostManagerSmartCoordinatorDiagnosticsCategory = "smart-optimization";
    private const string HostManagerSmartCoordinatorPerformanceEventName = "cycle-performance";
    private string? hostManagerPerformanceProducerInstanceId;
    private long hostManagerPerformanceCycleSequence;

    private sealed class HostManagerSmartCoordinatorCycleProfiler
    {
        private readonly string producerInstanceId;
        private readonly long cycleSequence;
        private readonly DateTimeOffset startedAtUtc = DateTimeOffset.UtcNow;
        private readonly long startedAtQpcTicks = Stopwatch.GetTimestamp();
        private readonly Stopwatch stopwatch = Stopwatch.StartNew();
        private readonly List<HostManagerSmartCoordinatorPhaseTiming> phases = [];
        private readonly long startAllocatedBytes = GC.GetTotalAllocatedBytes(precise: false);
        private readonly int startGen0 = GC.CollectionCount(0);
        private readonly int startGen1 = GC.CollectionCount(1);
        private readonly int startGen2 = GC.CollectionCount(2);
        private readonly TimeSpan startProcessCpu = ReadProcessCpuTime();
        private readonly string runId = Guid.NewGuid().ToString("N");
        private long lastTicks;

        private HostManagerSmartCoordinatorCycleProfiler(
            string producerInstanceId,
            long cycleSequence)
        {
            this.producerInstanceId = producerInstanceId;
            this.cycleSequence = cycleSequence;
        }

        public static HostManagerSmartCoordinatorCycleProfiler? Create(
            HostManagerSmartCoordinator owner,
            CompiledDiagnosticsPlan diagnostics)
        {
            if (!diagnostics.HostManagerSmartCoordinatorPerformanceLogEnabled)
            {
                return null;
            }

            var producerInstanceId = Volatile.Read(
                ref owner.hostManagerPerformanceProducerInstanceId);
            if (producerInstanceId is null)
            {
                var candidate = Guid.NewGuid().ToString("N");
                producerInstanceId = Interlocked.CompareExchange(
                    ref owner.hostManagerPerformanceProducerInstanceId,
                    candidate,
                    null) ?? candidate;
            }
            var cycleSequence = Interlocked.Increment(
                ref owner.hostManagerPerformanceCycleSequence);
            if (cycleSequence <= 0)
            {
                throw new InvalidOperationException(
                    "The Host Manager performance cycle sequence was exhausted.");
            }

            return new HostManagerSmartCoordinatorCycleProfiler(
                producerInstanceId,
                cycleSequence);
        }

        internal string ProducerInstanceId => producerInstanceId;

        internal long CycleSequence => cycleSequence;

        public void Mark(string phase)
        {
            var currentTicks = stopwatch.ElapsedTicks;
            phases.Add(new HostManagerSmartCoordinatorPhaseTiming(
                phase,
                TicksToMilliseconds(currentTicks - lastTicks),
                TicksToMilliseconds(currentTicks)));
            lastTicks = currentTicks;
        }

        public DebugDiagnosticLogRecord CreateRecord(
            DateTimeOffset timestamp,
            long runtimePlanVersion,
            string trigger,
            string mode,
            bool scoreOnly,
            string outcome,
            string message,
            IReadOnlyDictionary<string, object?> counts,
            IReadOnlyDictionary<string, object?> guards,
            IReadOnlyDictionary<string, object?>? details = null)
        {
            stopwatch.Stop();
            var completedAtQpcTicks = Stopwatch.GetTimestamp();
            var elapsedMs = stopwatch.Elapsed.TotalMilliseconds;
            var endProcessCpu = ReadProcessCpuTime();
            var processAllocatedBytes = Math.Max(0, GC.GetTotalAllocatedBytes(precise: false) - startAllocatedBytes);
            var gen0Collections = Math.Max(0, GC.CollectionCount(0) - startGen0);
            var gen1Collections = Math.Max(0, GC.CollectionCount(1) - startGen1);
            var gen2Collections = Math.Max(0, GC.CollectionCount(2) - startGen2);
            return new DebugDiagnosticLogRecord(
                timestamp,
                HostManagerSmartCoordinatorDiagnosticsCategory,
                HostManagerSmartCoordinatorPerformanceEventName,
                runtimePlanVersion,
                new Dictionary<string, object?>
                {
                    ["runId"] = runId,
                    ["producerInstanceId"] = producerInstanceId,
                    ["cycleSequence"] = cycleSequence,
                    ["cycleStartedAtUtc"] = startedAtUtc,
                    ["cycleStartedAtQpcTicks"] = startedAtQpcTicks,
                    ["cycleCompletedAtQpcTicks"] = completedAtQpcTicks,
                    ["qpcFrequency"] = Stopwatch.Frequency,
                    ["trigger"] = trigger,
                    ["mode"] = mode,
                    ["scoreOnly"] = scoreOnly,
                    ["outcome"] = outcome,
                    ["message"] = message,
                    ["elapsedMs"] = RoundMilliseconds(elapsedMs),
                    ["hostProcessCpuMs"] = RoundMilliseconds(Math.Max(0, (endProcessCpu - startProcessCpu).TotalMilliseconds)),
                    ["hostProcessAllocatedBytes"] = processAllocatedBytes,
                    ["hostGcCollections"] = new Dictionary<string, object?>
                    {
                        ["gen0"] = gen0Collections,
                        ["gen1"] = gen1Collections,
                        ["gen2"] = gen2Collections
                    },
                    ["phases"] = phases
                        .Select(static phase => new Dictionary<string, object?>
                        {
                            ["name"] = phase.Name,
                            ["elapsedMs"] = RoundMilliseconds(phase.ElapsedMs),
                            ["totalMs"] = RoundMilliseconds(phase.TotalMs)
                        })
                        .ToArray(),
                    ["counts"] = counts,
                    ["guards"] = guards,
                    ["details"] = details ?? new Dictionary<string, object?>()
                });
        }

        private static double TicksToMilliseconds(long ticks)
        {
            return ticks * 1000d / Stopwatch.Frequency;
        }

        private static double RoundMilliseconds(double milliseconds)
        {
            return Math.Round(milliseconds, 3);
        }

        private static TimeSpan ReadProcessCpuTime()
        {
            using var process = Process.GetCurrentProcess();
            return process.TotalProcessorTime;
        }
    }

    private sealed class HostManagerSmartCoordinatorCycleDiagnostics : IDisposable
    {
        private readonly HostManagerSmartCoordinator owner;
        private readonly HostManagerSmartCoordinatorCycleProfiler profiler;
        private readonly CompiledRuntimePlan runtimePlan;
        private readonly string trigger;
        private readonly bool realtimeCycle;
        private readonly HostManagerSmartCoordinatorOuterLoopCycleContext?
            outerLoopContext;
        private readonly CancellationToken cancellationToken;
        private readonly bool scoreOnly;
        private bool policyExecution;
        private bool hardwarePlacement;
        private bool samplingComplete;
        private bool scoringComplete;
        private bool disabled;
        private bool disposed;
        private string outcome = "failed";
        private string message = "The cycle exited before reporting a terminal outcome.";
        private int sampleTargets;
        private int sampleProcesses;
        private int scoredProcesses;
        private int policyProcesses;
        private int schedulingTargets;
        private int policyChanges;
        private int pendingChanges;
        private int resourceQueueActions;
        private int changedCount;
        private int appliedTargets;
        private uint nativeInputCount;
        private uint nativeSoftwareCount;
        private uint nativeInvalidFactCount;
        private uint nativeFeedbackCount;

        private HostManagerSmartCoordinatorCycleDiagnostics(
            HostManagerSmartCoordinator owner,
            HostManagerSmartCoordinatorCycleProfiler profiler,
            CompiledRuntimePlan runtimePlan,
            string trigger,
            bool realtimeCycle,
            bool scoreOnly,
            HostManagerSmartCoordinatorOuterLoopCycleContext? outerLoopContext,
            CancellationToken cancellationToken)
        {
            this.owner = owner;
            this.profiler = profiler;
            this.runtimePlan = runtimePlan;
            this.trigger = trigger;
            this.realtimeCycle = realtimeCycle;
            this.scoreOnly = scoreOnly;
            this.outerLoopContext = outerLoopContext;
            this.cancellationToken = cancellationToken;
        }

        internal string ProducerInstanceId => profiler.ProducerInstanceId;

        internal long CycleSequence => profiler.CycleSequence;

        public static HostManagerSmartCoordinatorCycleDiagnostics? Create(
            HostManagerSmartCoordinator owner,
            CompiledRuntimePlan runtimePlan,
            string trigger,
            bool realtimeCycle,
            bool scoreOnly,
            HostManagerSmartCoordinatorOuterLoopCycleContext? outerLoopContext,
            CancellationToken cancellationToken)
        {
            try
            {
                var profiler = HostManagerSmartCoordinatorCycleProfiler.Create(
                    owner,
                    runtimePlan.Diagnostics);
                return profiler is null
                    ? null
                    : new HostManagerSmartCoordinatorCycleDiagnostics(
                        owner,
                        profiler,
                        runtimePlan,
                        trigger,
                        realtimeCycle,
                        scoreOnly,
                        outerLoopContext,
                        cancellationToken);
            }
            catch (Exception exception)
            {
                owner.LogHostManagerSmartCoordinatorDiagnosticsFailure(exception);
                return null;
            }
        }

        public void Mark(string phase)
        {
            if (disabled)
            {
                return;
            }

            try
            {
                profiler.Mark(phase);
            }
            catch (Exception exception)
            {
                disabled = true;
                owner.LogHostManagerSmartCoordinatorDiagnosticsFailure(exception);
            }
        }

        public void CaptureSample(HostManagerSample sample)
        {
            sampleTargets = sample.Targets.Count;
            sampleProcesses = sample.ProcessFacts.Processes.Count;
            samplingComplete = sample.ProcessFacts.IsInventoryCurrentComplete();
        }

        public void CaptureScoring(HostManagerComputeScoringCycleResult? compute)
        {
            scoredProcesses = compute?.Cpu?.Scores.Count(static score =>
                score.Kind == NativeComputeScoringOutputKind.ProcessCpu) ?? 0;
            scoringComplete = compute?.Cpu is not null;
        }

        public void CaptureGuards(bool policyExecutionEnabled, bool hardwarePlacementEnabled)
        {
            policyExecution = policyExecutionEnabled;
            hardwarePlacement = hardwarePlacementEnabled;
        }

        public void CaptureMemoryTransactions(uint transactionCount)
        {
            resourceQueueActions = checked((int)transactionCount);
        }

        public void CaptureProjection(uint inputCount)
        {
            nativeInputCount = inputCount;
        }

        public void CaptureNativePlanSnapshot(
            NativeSmartCoordinatorSnapshot snapshot,
            uint plannedPolicyChangeCount)
        {
            CaptureNativeState(snapshot);
            policyChanges = checked((int)plannedPolicyChangeCount);
        }

        public void CaptureNativeTerminalSnapshot(NativeSmartCoordinatorSnapshot snapshot)
        {
            CaptureNativeState(snapshot);
        }

        private void CaptureNativeState(NativeSmartCoordinatorSnapshot snapshot)
        {
            policyProcesses = checked((int)snapshot.ProcessCount);
            nativeSoftwareCount = snapshot.SoftwareCount;
            schedulingTargets = checked((int)(snapshot.ProcessCount + snapshot.SoftwareCount));
            pendingChanges = checked((int)snapshot.PendingCount);
            nativeInvalidFactCount = snapshot.InvalidFactCount;
        }

        public void CaptureExecution(uint feedbackCount, uint successfulFeedbackCount)
        {
            nativeFeedbackCount = feedbackCount;
            appliedTargets = checked((int)successfulFeedbackCount);
            changedCount = appliedTargets;
        }

        public void Defer(string reason)
        {
            outcome = "deferred";
            message = reason;
        }

        public void Complete()
        {
            outcome = "completed";
            message = "The smart coordinator cycle completed.";
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            if (disabled)
            {
                return;
            }

            if (outcome == "failed" && cancellationToken.IsCancellationRequested)
            {
                outcome = "canceled";
                message = "The smart coordinator cycle was canceled.";
            }

            ObserveOuterLoop(
                outerLoopContext,
                HostManagerSmartCoordinatorOuterLoopPhase.CoreTerminal,
                diagnosticCycleSequence: profiler.CycleSequence,
                producerInstanceId: profiler.ProducerInstanceId,
                outcome: outcome);

            ObserveOuterLoop(
                outerLoopContext,
                HostManagerSmartCoordinatorOuterLoopPhase.DiagnosticDisposeStarted,
                diagnosticCycleSequence: profiler.CycleSequence,
                producerInstanceId: profiler.ProducerInstanceId,
                outcome: outcome);

            try
            {
                owner.WriteHostManagerSmartCoordinatorPerformanceLog(
                    profiler,
                    DateTimeOffset.UtcNow,
                    runtimePlan,
                    trigger,
                    scoreOnly,
                    outcome,
                    message,
                    outerLoopContext,
                    CreateHostManagerSmartCoordinatorCounts(
                        sampleTargets: sampleTargets,
                        sampleProcesses: sampleProcesses,
                        scoredProcesses: scoredProcesses,
                        policyProcesses: policyProcesses,
                        schedulingTargets: schedulingTargets,
                        policyChanges: policyChanges,
                        pendingChanges: pendingChanges,
                        resourceQueueActions: resourceQueueActions,
                        changedCount: changedCount,
                        appliedTargets: appliedTargets),
                    CreateHostManagerSmartCoordinatorGuards(
                        sampling: samplingComplete,
                        scoring: scoringComplete,
                        policyExecution: policyExecution,
                        hardwarePlacement: hardwarePlacement),
                    new Dictionary<string, object?>
                    {
                        ["realtimeCycle"] = realtimeCycle,
                        ["nativeInputCount"] = nativeInputCount,
                        ["nativeSoftwareCount"] = nativeSoftwareCount,
                        ["nativeInvalidFactCount"] = nativeInvalidFactCount,
                        ["nativeFeedbackCount"] = nativeFeedbackCount
                    });
            }
            catch (Exception exception)
            {
                owner.LogHostManagerSmartCoordinatorDiagnosticsFailure(exception);
            }
        }
    }

    private sealed record HostManagerSmartCoordinatorPhaseTiming(
        string Name,
        double ElapsedMs,
        double TotalMs);

    private void WriteHostManagerSmartCoordinatorPerformanceLog(
        HostManagerSmartCoordinatorCycleProfiler profiler,
        DateTimeOffset now,
        CompiledRuntimePlan runtimePlan,
        string trigger,
        bool scoreOnly,
        string outcome,
        string message,
        HostManagerSmartCoordinatorOuterLoopCycleContext? outerLoopContext,
        IReadOnlyDictionary<string, object?> counts,
        IReadOnlyDictionary<string, object?> guards,
        IReadOnlyDictionary<string, object?>? details = null)
    {
        var record = profiler.CreateRecord(
            now,
            runtimePlan.Version,
            trigger,
            runtimePlan.OptimizationMode.Mode,
            scoreOnly,
            outcome,
            message,
            counts,
            guards,
            details);
        var completedAtQpcTicks = (long)record.Properties["cycleCompletedAtQpcTicks"]!;
        var producerInstanceId = (string)record.Properties["producerInstanceId"]!;
        var diagnosticCycleSequence = (long)record.Properties["cycleSequence"]!;
        var diagnosticRunId = (string)record.Properties["runId"]!;
        ObserveOuterLoop(
            outerLoopContext,
            HostManagerSmartCoordinatorOuterLoopPhase.ProfilerStopped,
            qpcTicks: completedAtQpcTicks,
            diagnosticCycleSequence: diagnosticCycleSequence,
            producerInstanceId: producerInstanceId,
            diagnosticRunId: diagnosticRunId,
            outcome: outcome);
        ObserveOuterLoop(
            outerLoopContext,
            HostManagerSmartCoordinatorOuterLoopPhase.RecordMaterialized,
            diagnosticCycleSequence: diagnosticCycleSequence,
            producerInstanceId: producerInstanceId,
            diagnosticRunId: diagnosticRunId,
            outcome: outcome);
        ObserveOuterLoop(
            outerLoopContext,
            HostManagerSmartCoordinatorOuterLoopPhase.WriterAdmissionStarted,
            diagnosticCycleSequence: diagnosticCycleSequence,
            producerInstanceId: producerInstanceId,
            diagnosticRunId: diagnosticRunId,
            outcome: outcome);
        var accepted = debugDiagnosticLogWriter.TryWrite(record);
        ObserveOuterLoop(
            outerLoopContext,
            HostManagerSmartCoordinatorOuterLoopPhase.WriterAdmissionCompleted,
            diagnosticCycleSequence: diagnosticCycleSequence,
            producerInstanceId: producerInstanceId,
            diagnosticRunId: diagnosticRunId,
            writerAccepted: accepted,
            outcome: outcome);
    }

    private void LogHostManagerSmartCoordinatorDiagnosticsFailure(Exception exception)
    {
        try
        {
            logger.LogDebug(
                exception,
                "Host Manager smart coordinator cycle diagnostics failed without affecting scheduling.");
        }
        catch
        {
        }
    }

    private static IReadOnlyDictionary<string, object?> CreateHostManagerSmartCoordinatorCounts(
        int sampleTargets = 0,
        int sampleProcesses = 0,
        int scoredProcesses = 0,
        int policyProcesses = 0,
        int schedulingTargets = 0,
        int policyChanges = 0,
        int pendingChanges = 0,
        int adapterResourceTargets = 0,
        int softwareModeTargets = 0,
        int automaticProcessTargets = 0,
        int gpuPlacementTargets = 0,
        int cpuPlacementTargets = 0,
        int cpuAvoidanceTargets = 0,
        int resourceCandidates = 0,
        int resourceQueueActions = 0,
        int resourceDangerLines = 0,
        int resourceReplans = 0,
        int changedCount = 0,
        int appliedTargets = 0,
        int appliedPlacements = 0)
    {
        return new Dictionary<string, object?>
        {
            ["sampleTargets"] = sampleTargets,
            ["sampleProcesses"] = sampleProcesses,
            ["scoredProcesses"] = scoredProcesses,
            ["policyProcesses"] = policyProcesses,
            ["schedulingTargets"] = schedulingTargets,
            ["policyChanges"] = policyChanges,
            ["pendingChanges"] = pendingChanges,
            ["adapterResourceTargets"] = adapterResourceTargets,
            ["softwareModeTargets"] = softwareModeTargets,
            ["automaticProcessTargets"] = automaticProcessTargets,
            ["gpuPlacementTargets"] = gpuPlacementTargets,
            ["cpuPlacementTargets"] = cpuPlacementTargets,
            ["cpuAvoidanceTargets"] = cpuAvoidanceTargets,
            ["resourceCandidates"] = resourceCandidates,
            ["resourceQueueActions"] = resourceQueueActions,
            ["resourceDangerLines"] = resourceDangerLines,
            ["resourceReplans"] = resourceReplans,
            ["changedCount"] = changedCount,
            ["appliedTargets"] = appliedTargets,
            ["appliedPlacements"] = appliedPlacements
        };
    }

    private static IReadOnlyDictionary<string, object?> CreateHostManagerSmartCoordinatorGuards(
        bool coordinator = true,
        bool sampling = true,
        bool scoring = true,
        bool policyExecution = true,
        bool hardwarePlacement = true)
    {
        return new Dictionary<string, object?>
        {
            ["coordinator"] = coordinator,
            ["sampling"] = sampling,
            ["scoring"] = scoring,
            ["policyExecution"] = policyExecution,
            ["hardwarePlacement"] = hardwarePlacement
        };
    }
}
