using ResourceManager.App.Infrastructure.Telemetry.Etw;

namespace ResourceManager.ControlDecisionAcceptance;

internal static partial class Program
{
    private sealed record ActualUseEventCounts(long ContextSwitch, long ImageRundown, long ImageLoad,
        long ImageUnload, long Isr, long Dpc, long ContextSwitchBindings, long ImageBindings,
        bool ContextSwitchSourceActive, bool ImageSourceActive);

    // Reads native cumulative physical counters without per-event managed callbacks.
    // This decorator never takes a hold or changes its keywords or outputs.
    private sealed class ActualUseEtwCounters(KernelEtwSessionBroker inner) : IKernelEtwSessionBroker
    {
        private long contextSwitchBindings, imageBindings;
        private readonly Dictionary<long, Dictionary<string, long>> physicalByGeneration = [];
        private KernelEtwEventSource? contextSwitchSource, imageSource;
        private readonly object registrationsGate = new();
        private readonly Dictionary<long, KernelRegistrationEvidence> registrations = [];
        private long nextRegistration;
        public KernelEtwSessionSnapshot GetSnapshot() => inner.GetSnapshot();
        public IKernelEtwSubscription Acquire(KernelEtwSubscriptionRequest request)
        {
            long registration;
            lock (registrationsGate)
            {
                registration = ++nextRegistration;
                registrations.Add(registration, new(request.Id, request.Keywords));
            }
            try
            {
                var forwarded = request.Id is "cpu-core-residency" or "system-interrupts"
                    ? request with { Bind = (source, context) => BindCounters(request, source, context) } : request;
                // Tracking locks never span production subscription, binding or disposal.
                return new TrackedSubscription(this, registration, inner.Acquire(forwarded));
            }
            catch { Forget(registration); throw; }
        }
        private void BindCounters(KernelEtwSubscriptionRequest request, KernelEtwEventSource source, KernelEtwSessionContext context)
        {
            request.Bind(source, context);
            if (request.Id == "cpu-core-residency")
            {
                Interlocked.Increment(ref contextSwitchBindings);
                Volatile.Write(ref contextSwitchSource, source);
            }
            else
            {
                Interlocked.Increment(ref imageBindings);
                Volatile.Write(ref imageSource, source);
            }
        }
        internal KernelRegistrationEvidence[] ReadRegistrations()
        {
            lock (registrationsGate) return registrations.Values.ToArray();
        }
        private void Forget(long registration)
        {
            lock (registrationsGate) registrations.Remove(registration);
        }
        private sealed class TrackedSubscription(ActualUseEtwCounters owner, long registration,
            IKernelEtwSubscription subscription) : IKernelEtwSubscription
        {
            private int disposed;
            public KernelEtwSessionSnapshot GetSnapshot() => subscription.GetSnapshot();
            public void Dispose()
            {
                if (Interlocked.Exchange(ref disposed, 1) != 0) return;
                owner.Read();
                try { subscription.Dispose(); }
                finally { owner.Read(); owner.Forget(registration); }
            }
        }
        internal ActualUseEventCounts Read()
        {
            var name = ResourceManagerEtwSessionNames.Create(ResourceManagerEtwSessionNames.KernelPrefix,
                ResourceManagerEtwSessionNames.CurrentOwner);
            var session = EtwEventDropDiagnostics.Read().Sessions.SingleOrDefault(value => value.Name == name);
            lock (registrationsGate)
            {
                if (session is not null)
                    physicalByGeneration[session.Generation] = session.Kinds
                        .Where(value => !value.Kind.StartsWith("holder/", StringComparison.Ordinal))
                        .ToDictionary(value => value.Kind, value => value.Received, StringComparer.Ordinal);
                long Total(string kind) => physicalByGeneration.Values.Sum(value => value.GetValueOrDefault(kind));
                return new(Total("ThreadCSwitch"), Total("ImageDCStart"), Total("ImageLoad"), Total("ImageUnload"),
                    Total("PerfInfoISR"), Total("PerfInfoDPC") + Total("PerfInfoThreadedDPC") + Total("PerfInfoTimerDPC"),
                    Interlocked.Read(ref contextSwitchBindings), Interlocked.Read(ref imageBindings),
                    Volatile.Read(ref contextSwitchSource)?.IsActive == true, Volatile.Read(ref imageSource)?.IsActive == true);
            }
        }
    }

    private sealed record ActualUseEventRateInterval(string Phase, string WindowState, double Seconds,
        long ContextSwitch, long ImageRundown, long ImageLoad, long ImageUnload, long Isr, long Dpc,
        bool ContextSwitchCounterBound, bool ImageCounterBound);
    private static ActualUseEventRateInterval EventInterval(string phase, string windowState, double seconds,
        ActualUseEventCounts before, ActualUseEventCounts after)
        => new(phase, windowState, seconds, after.ContextSwitch - before.ContextSwitch,
            after.ImageRundown - before.ImageRundown, after.ImageLoad - before.ImageLoad,
            after.ImageUnload - before.ImageUnload, after.Isr - before.Isr, after.Dpc - before.Dpc,
            before.ContextSwitchSourceActive && after.ContextSwitchSourceActive,
            before.ImageSourceActive && after.ImageSourceActive);

    private sealed record ActualUseEventRateSummary(string Phase, string WindowState, double Seconds,
        long ContextSwitch, long ImageRundown, long ImageLoad, long ImageUnload, long Isr, long Dpc,
        double? ContextSwitchPerSecond, double? ImageRundownPerSecond, double? ImageLoadPerSecond,
        double? ImageUnloadPerSecond, double? IsrPerSecond, double? DpcPerSecond,
        double? CountedEventKindsPerSecond);
    private static ActualUseEventRateSummary[] SummarizeEventRates(IEnumerable<ActualUseEventRateInterval> intervals)
        => intervals.GroupBy(value => (value.Phase, value.WindowState)).Select(group =>
        {
            var seconds = group.Sum(value => value.Seconds);
            var contextSwitch = group.Sum(value => value.ContextSwitch);
            var rundown = group.Sum(value => value.ImageRundown);
            var load = group.Sum(value => value.ImageLoad);
            var unload = group.Sum(value => value.ImageUnload);
            var isr = group.Sum(value => value.Isr);
            var dpc = group.Sum(value => value.Dpc);
            var cpuBound = group.All(value => value.ContextSwitchCounterBound);
            var imageBound = group.All(value => value.ImageCounterBound);
            return new ActualUseEventRateSummary(group.Key.Phase, group.Key.WindowState, seconds,
                contextSwitch, rundown, load, unload, isr, dpc,
                cpuBound ? contextSwitch / seconds : null, imageBound ? rundown / seconds : null,
                imageBound ? load / seconds : null, imageBound ? unload / seconds : null,
                imageBound ? isr / seconds : null, imageBound ? dpc / seconds : null,
                cpuBound && imageBound ? (contextSwitch + rundown + load + unload + isr + dpc) / seconds : null);
        }).ToArray();
}
