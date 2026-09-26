using System.Diagnostics;

namespace ResourceManager.App.Infrastructure.Optimization;

internal enum HostManagerSmartCoordinatorOuterLoopTrigger
{
    ScheduledBootstrap = 0,
    Scheduled = 1,
    Manual = 2
}

internal enum HostManagerSmartCoordinatorOuterLoopPhase
{
    ManualRequest = 0,
    ForegroundAdmissionCompleted = 1,
    WaitEntered = 2,
    WakeReturned = 3,
    GateWaitStarted = 4,
    GateAcquired = 5,
    DispatchStarted = 6,
    CoreEntered = 7,
    ProfilerCreated = 8,
    NativeSequenceAssigned = 9,
    CoreTerminal = 10,
    DiagnosticDisposeStarted = 11,
    ProfilerStopped = 12,
    RecordMaterialized = 13,
    WriterAdmissionStarted = 14,
    WriterAdmissionCompleted = 15,
    NextDeadlinePublished = 16,
    WrapperCompleted = 17,
    GateReleaseStarted = 18,
    GateReleased = 19,
    ManualApiReturned = 20
}

internal readonly record struct HostManagerSmartCoordinatorOuterLoopEvent(
    long AttemptId,
    long DispatchSequence,
    HostManagerSmartCoordinatorOuterLoopTrigger Trigger,
    HostManagerSmartCoordinatorOuterLoopPhase Phase,
    long QpcTicks,
    long QpcFrequency,
    HostManagerWakeDeadlineSnapshot? ConsumedWake,
    long? ProviderTimestamp,
    long? ProviderTimestampFrequency,
    HostManagerWakeDeadlineSnapshot? PublishedDeadline,
    ulong? NativeCycleSequence,
    long? DiagnosticCycleSequence,
    string? ProducerInstanceId,
    string? DiagnosticRunId,
    bool? WriterAccepted,
    string? Outcome);

internal interface IHostManagerSmartCoordinatorOuterLoopProbe
{
    void Observe(in HostManagerSmartCoordinatorOuterLoopEvent value);
}

internal sealed class HostManagerSmartCoordinatorOuterLoopCycleContext(
    IHostManagerSmartCoordinatorOuterLoopProbe probe,
    long attemptId,
    HostManagerSmartCoordinatorOuterLoopTrigger trigger)
{
    internal IHostManagerSmartCoordinatorOuterLoopProbe Probe { get; } = probe;

    internal long AttemptId { get; } = attemptId;

    internal HostManagerSmartCoordinatorOuterLoopTrigger Trigger { get; } = trigger;

    internal long DispatchSequence { get; set; }

    internal HostManagerWakeDeadlineSnapshot? ConsumedWake { get; set; }
}

public sealed partial class HostManagerSmartCoordinator
{
    private IHostManagerSmartCoordinatorOuterLoopProbe? outerLoopProbe;
    private long outerLoopAttemptId;
    private long outerLoopDispatchSequence;

    internal IHostManagerSmartCoordinatorOuterLoopProbe? OuterLoopProbe
    {
        get => Volatile.Read(ref outerLoopProbe);
        set => Volatile.Write(ref outerLoopProbe, value);
    }

    private HostManagerSmartCoordinatorOuterLoopCycleContext? BeginOuterLoopAttempt(
        HostManagerSmartCoordinatorOuterLoopTrigger trigger)
    {
        var probe = Volatile.Read(ref outerLoopProbe);
        if (probe is null)
        {
            return null;
        }

        var attemptId = Interlocked.Increment(ref outerLoopAttemptId);
        if (attemptId <= 0)
        {
            throw new InvalidOperationException(
                "The Host Manager outer-loop attempt sequence was exhausted.");
        }

        return new HostManagerSmartCoordinatorOuterLoopCycleContext(
            probe,
            attemptId,
            trigger);
    }

    private void AssignOuterLoopDispatchSequence(
        HostManagerSmartCoordinatorOuterLoopCycleContext? context)
    {
        if (context is null)
        {
            return;
        }

        var dispatchSequence = Interlocked.Increment(ref outerLoopDispatchSequence);
        if (dispatchSequence <= 0)
        {
            throw new InvalidOperationException(
                "The Host Manager outer-loop dispatch sequence was exhausted.");
        }
        context.DispatchSequence = dispatchSequence;
    }

    private static void ObserveOuterLoop(
        HostManagerSmartCoordinatorOuterLoopCycleContext? context,
        HostManagerSmartCoordinatorOuterLoopPhase phase,
        long? qpcTicks = null,
        long? providerTimestamp = null,
        long? providerTimestampFrequency = null,
        HostManagerWakeDeadlineSnapshot? publishedDeadline = null,
        ulong? nativeCycleSequence = null,
        long? diagnosticCycleSequence = null,
        string? producerInstanceId = null,
        string? diagnosticRunId = null,
        bool? writerAccepted = null,
        string? outcome = null)
    {
        if (context is null)
        {
            return;
        }

        var value = new HostManagerSmartCoordinatorOuterLoopEvent(
            context.AttemptId,
            context.DispatchSequence,
            context.Trigger,
            phase,
            qpcTicks ?? Stopwatch.GetTimestamp(),
            Stopwatch.Frequency,
            context.ConsumedWake,
            providerTimestamp,
            providerTimestampFrequency,
            publishedDeadline,
            nativeCycleSequence,
            diagnosticCycleSequence,
            producerInstanceId,
            diagnosticRunId,
            writerAccepted,
            outcome);
        context.Probe.Observe(in value);
    }
}
