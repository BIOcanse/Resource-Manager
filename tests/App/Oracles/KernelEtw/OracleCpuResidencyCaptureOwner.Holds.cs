using Microsoft.Diagnostics.Tracing.Parsers;
using ResourceManager.App.Infrastructure.Telemetry.Etw;

namespace Resource_Manager_APP.Tests.Oracles.KernelEtw;

internal sealed partial class OracleCpuResidencyCaptureOwner
{
    // Broker calls can invoke metadata binding on another reconciliation flow.
    // Never hold subscriptionGate across an acquire/return, and metadata binding
    // never takes this resource-operation gate.
    private readonly object durationResourceGate = new();
    private IOracleKernelEtwSubscription? durationSubscription;
    private bool windowRequested = true;
    // Cadence of the requested continuous window; null = finite window. Every binding re-issues it.
    private TimeSpan? windowCadence;

    private void SynchronizeDurationHold()
    {
        bool required;
        IOracleKernelEtwSubscription? release = null;
        lock (subscriptionGate)
        {
            required = !stopped && kernelSubscription is not null && windowRequested
                && (activeSession is null || activeSession.WindowActive);
            if (!required) { release = durationSubscription; durationSubscription = null; }
            else if (durationSubscription is not null)
            {
                return;
            }
        }
        release?.Dispose();
        if (!required) return;
        var created = kernelEtwBroker.Acquire(new OracleKernelEtwSubscriptionRequest("cpu-core-residency-duration",
            KernelTraceEventParser.Keywords.ContextSwitch, BindDurationEvents));
        var accepted = false;
        try
        {
            lock (subscriptionGate)
            {
                if (!stopped && kernelSubscription is not null && windowRequested
                    && (activeSession is null || activeSession.WindowActive))
                {
                    durationSubscription = created;
                    accepted = true;
                }
            }
        }
        finally { if (!accepted) { lock (subscriptionGate) if (ReferenceEquals(durationSubscription, created)) durationSubscription = null; created.Dispose(); } }
    }

    private void BindDurationEvents(OracleKernelEtwEventSource source, KernelEtwSessionContext context)
    {
        ResidencySessionState? session;
        lock (subscriptionGate) session = activeSession;
        if (session is null || session.Generation != context.Generation) return;
        var processors = session.Plan.PhysicalCoreByLogicalProcessor.Keys.Order().ToArray();
        BindContextSwitchCallbacks(source, session.Buffer,
            () => IsCurrentSession(session, session.BindingEpoch) && source.IsActive,
            index => ResolveEventLogicalProcessorId(index, processors),
            observeContextSwitch: (qpc, at, processor, oldPid, oldTid, pid, name, tid)
                => ObserveContextSwitch(session, qpc, at, processor, oldPid, oldTid, pid, name, tid));
    }

    private IOracleKernelEtwSubscription? DetachDurationHoldCore()
    {
        var held = durationSubscription;
        durationSubscription = null;
        return held;
    }

    private KernelEtwSessionSnapshot ReadBrokerSnapshot()
        => Volatile.Read(ref durationSubscription)?.GetSnapshot()
            ?? Volatile.Read(ref kernelSubscription)?.GetSnapshot() ?? kernelEtwBroker.GetSnapshot();
}
