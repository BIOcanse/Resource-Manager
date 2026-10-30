using Microsoft.Diagnostics.Tracing.Parsers;
using ResourceManager.App.Infrastructure.Telemetry.Etw;

namespace ResourceManager.ReadWrite.Windows.Processes;

public sealed partial class EtwCpuResidencyCaptureOwner
{
    // Short state transitions may use this gate; broker calls never do.
    private readonly object durationResourceGate = new();
    private IKernelEtwSubscription? durationSubscription;
    private bool windowRequested, acquiringDuration;
    private long durationEpoch;

    private void SynchronizeDurationHold()
    {
        IKernelEtwSubscription? release = null;
        long epoch = 0;
        lock (subscriptionGate)
        {
            var required = !stopped && kernelSubscription is not null && windowRequested
                && (activeSession is null || activeSession.WindowActive);
            if (!required) release = DetachDurationHoldCore();
            else if (durationSubscription is null && !acquiringDuration)
            {
                acquiringDuration = true;
                epoch = checked(++durationEpoch);
            }
        }
        release?.Dispose();
        if (epoch == 0) return;
        IKernelEtwSubscription? created = null;
        var accepted = false;
        try
        {
            created = kernelEtwBroker.Acquire(new KernelEtwSubscriptionRequest("cpu-core-residency-duration",
                KernelTraceEventParser.Keywords.ContextSwitch, BindDurationEvents)
                { NativeOutputs = KernelEtwNativeOutputs.Residency });
            var availability = created.GetSnapshot();
            lock (durationResourceGate)
            lock (subscriptionGate)
            {
                if (durationEpoch == epoch && !stopped && kernelSubscription is not null && windowRequested
                    && (activeSession is null || activeSession.WindowActive))
                {
                    durationSubscription = created;
                    // The keyword union is installed before native duration intake.
                    if (availability.IsRunning && activeSession is { WindowActive: true } session && availability.Generation == session.Generation)
                        session.Native.SetEnabled(true);
                    accepted = true;
                }
            }
        }
        finally
        {
            lock (subscriptionGate)
            {
                if (durationEpoch == epoch) acquiringDuration = false;
                if (!accepted && ReferenceEquals(durationSubscription, created)) durationSubscription = null;
            }
            if (!accepted) created?.Dispose();
        }
    }

    private void BindDurationEvents(KernelEtwEventSource source, KernelEtwSessionContext context)
    {
        ResidencySessionState? session;
        lock (subscriptionGate) session = activeSession;
        if (session is null || session.Generation != context.Generation) return;
        if (source.NativeSession is null)
            throw new InvalidOperationException("CPU residency duration requires a native kernel ETW session.");
        lock (subscriptionGate)
            if (durationSubscription is not null && IsCurrentSessionCore(session) && session.WindowActive)
                session.Native.SetEnabled(true);
    }

    private IKernelEtwSubscription? DetachDurationHoldCore()
    {
        var held = durationSubscription;
        durationSubscription = null;
        durationEpoch = checked(durationEpoch + 1);
        acquiringDuration = false;
        return held;
    }

    private KernelEtwSessionSnapshot ReadBrokerSnapshot()
    {
        var metadata = Volatile.Read(ref kernelSubscription)?.GetSnapshot() ?? kernelEtwBroker.GetSnapshot();
        if (!metadata.IsRunning || Volatile.Read(ref activeSession) is not { WindowActive: true }) return metadata;
        return Volatile.Read(ref durationSubscription)?.GetSnapshot()
            ?? metadata with { State = "Unavailable", Message = "CPU residency context-switch keywords are unavailable." };
    }
}
