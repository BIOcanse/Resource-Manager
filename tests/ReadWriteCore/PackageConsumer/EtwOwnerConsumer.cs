using Microsoft.Diagnostics.Tracing.Parsers;
using ResourceManager.App.Infrastructure.Telemetry.Etw;
using ResourceManager.ReadWrite.ProcessFields;

internal static class EtwOwnerConsumer
{
    // Public packaged broker API only. No Core/friend hooks, reflection or native ETW.
    // This proves resource lifecycle, not external TCPIP window/birth injection.
    internal static void Run()
    {
        Require(typeof(KernelEtwSessionOwner).Assembly == typeof(ProcessMetricFieldRegistry).Assembly,
            "independent resource owner assembly");
        try { _ = new KernelEtwEventSource(null!); throw new InvalidOperationException("Null native binding was accepted."); }
        catch (ArgumentNullException) { }
        var factory = new ControlOnlyFactory();
        using var broker = new KernelEtwSessionOwner(factory);
        using var cpu = broker.Acquire(new("recorded-cpu", KernelTraceEventParser.Keywords.ContextSwitch, static (_, _) => { }));
        using var network = broker.Acquire(new("recorded-network", KernelTraceEventParser.Keywords.NetworkTCPIP, static (_, _) => { }));
        broker.Reconcile();
        Require(factory.Created == 1 && broker.GetSnapshot().SubscriptionCount == 2,
            "shared runtime across original reconciliation");
        Require(broker.GetSnapshot().EnabledKeywords ==
            (KernelTraceEventParser.Keywords.ContextSwitch | KernelTraceEventParser.Keywords.NetworkTCPIP),
            "original keyword union");
        network.Dispose();
        Require(broker.GetSnapshot().SubscriptionCount == 1 && factory.Runtime!.Stops == 0,
            "borrower releases only its own lease");
        cpu.Dispose();
        Require(factory.Runtime!.Stops == 1, "last lease invokes original resource release");
        broker.Dispose();
        Require(broker.GetSnapshot().State == "Stopped", "composition shutdown confirmed");
        try { broker.Acquire(new("disposed", KernelTraceEventParser.Keywords.Process, static (_, _) => { }));
            throw new InvalidOperationException("Disposed owner admitted a lease."); }
        catch (ObjectDisposedException) { }

        var failingFactory = new ControlOnlyFactory();
        using var blockedBroker = new KernelEtwSessionOwner(failingFactory);
        var first = blockedBroker.Acquire(new("recorded-first", KernelTraceEventParser.Keywords.Process, static (_, _) => { }));
        failingFactory.Runtime!.UnconfirmedStop = true;
        first.Dispose();
        Require(blockedBroker.GetSnapshot().State == "StopFailed", "unconfirmed release state");
        using var next = blockedBroker.Acquire(new("recorded-later", KernelTraceEventParser.Keywords.NetworkTCPIP, static (_, _) => { }));
        blockedBroker.Reconcile();
        Require(failingFactory.Created == 1 && blockedBroker.GetSnapshot().State == "StopFailed",
            "unconfirmed release forbids another runtime");
        Console.WriteLine("ETW public broker control-only union/reconcile/stop-block/disposal consumer passed; native output binding is required explicitly.");
    }
    private static void Require(bool value, string contract)
    {
        if (!value) throw new InvalidOperationException("ETW resource owner: " + contract);
    }
    private sealed class ControlOnlyFactory : IKernelEtwSessionRuntimeFactory
    {
        internal int Created;
        internal ControlOnlyRuntime? Runtime;
        public bool TryGetAvailability(out string state, out string message)
        { state = "Available"; message = "injected control-only runtime"; return true; }
        public IKernelEtwSessionRuntime Create(string sessionName, KernelTraceEventParser.Keywords keywords,
            KernelEtwSessionContext context, IReadOnlyList<KernelEtwSubscriptionRegistration> subscriptions)
        {
            Created++;
            Runtime = new();
            Runtime.UpdateSubscriptions(subscriptions, context);
            return Runtime;
        }
    }
    private sealed class ControlOnlyRuntime : IKernelEtwSessionRuntime
    {
        private readonly HashSet<long> bindings = [];
        internal int Stops;
        internal bool UnconfirmedStop;
        public bool IsProcessing { get; private set; }
        public int EventsLost => 0;
        public void StartProcessing(Action<Exception?> completed) => IsProcessing = true;
        public void UpdateSubscriptions(IReadOnlyList<KernelEtwSubscriptionRegistration> subscriptions,
            KernelEtwSessionContext context)
        {
            bindings.IntersectWith(subscriptions.Select(static item => item.LeaseId));
            foreach (var item in subscriptions)
                bindings.Add(item.LeaseId);
            // This public control seam never binds a reader or invents a native session. Actual native binding is covered by SDK/native fixtures.
        }
        public KernelEtwSessionStopResult Stop(TimeSpan waitTimeout)
        { Stops++; IsProcessing = false; return UnconfirmedStop
            ? new(false, true, true, ["injected unconfirmed release"]) : KernelEtwSessionStopResult.Released; }
        public void Dispose() { }
    }
}
