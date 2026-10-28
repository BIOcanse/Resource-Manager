using System.Net;
using Microsoft.Diagnostics.Tracing.Parsers;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Infrastructure.ResourceBreakdown;
using ResourceManager.App.Infrastructure.Telemetry.Etw;
using ResourceManager.ReadWrite.Windows.Devices;

namespace Resource_Manager_APP.Tests;

public sealed class EtwResourceOwnerLibraryIntegrationTests
{
    [Fact]
    public void IndependentOwnersAndContractsBelongToSdkAndReadsDoNotAcquireEtw()
    {
        var sdk = typeof(ProcessCpuDeltaTracker).Assembly;
        Assert.Equal(sdk, typeof(KernelEtwSessionOwner).Assembly);
        Assert.Equal(sdk, typeof(EtwNetworkTcpIpCaptureOwner).Assembly);
        Assert.Equal(sdk, typeof(NetworkAttributionSnapshot).Assembly);
        Assert.Equal(sdk, typeof(KernelEtwEventSource).Assembly);
        var factory = new Factory();
        using var broker = new KernelEtwSessionOwner(factory);
        using var network = new EtwNetworkTcpIpCaptureOwner(broker);
        using var disk = new ResourceManager.ReadWrite.Windows.Processes.EtwPhysicalDiskIoCaptureOwner(broker);
        Assert.Empty(network.Read(true).RawProcessValues);
        Assert.Empty(disk.Read(true).Processes);
        Assert.Equal(0, factory.Created);
        Assert.Equal(0, broker.GetSnapshot().SubscriptionCount);
    }

    [Fact]
    public void NetworkBorrowerSharesTheOriginalUnionAndDisposesOnlyItsLease()
    {
        var factory = new Factory();
        using var broker = new KernelEtwSessionOwner(factory);
        using var cpu = broker.Subscribe(new("cpu", KernelTraceEventParser.Keywords.ContextSwitch, static (_, _) => { }));
        var network = new EtwNetworkTcpIpCaptureOwner(broker);
        network.Start();
        network.Read();
        Assert.Equal(1, factory.Created);
        Assert.Equal(2, broker.GetSnapshot().SubscriptionCount);
        Assert.Equal(KernelTraceEventParser.Keywords.ContextSwitch | KernelTraceEventParser.Keywords.NetworkTCPIP,
            broker.GetSnapshot().EnabledKeywords);
        network.Dispose();
        Assert.Equal(1, broker.GetSnapshot().SubscriptionCount);
        Assert.Equal(0, factory.Current!.Stops);
        Assert.Equal(KernelTraceEventParser.Keywords.ContextSwitch, broker.GetSnapshot().EnabledKeywords);
        Assert.Throws<ObjectDisposedException>(() => network.Read());
        cpu.Dispose();
        Assert.Equal(1, factory.Current.Stops);
    }

    [Fact]
    public void RecordedWindowUsesOriginalBirthVerificationAndReleasesEveryIdentityLease()
    {
        var factory = new Factory();
        using var broker = new KernelEtwSessionOwner(factory);
        long tick = 0;
        var handles = new List<ProcessLease>();
        using var network = new EtwNetworkTcpIpCaptureOwner(broker, static () => true, new(), () => tick,
            pid => { var lease = new ProcessLease(pid, 100); handles.Add(lease); return lease; });
        network.Start();
        Assert.Null(network.Read(true).ObservedTraffic);
        var runtime = factory.Current!;
        network.ObserveBoundNetworkTransfer(runtime.NetworkSource!, runtime.Context!, 41, 4000, true, false,
            IPAddress.Loopback, 1500, 99); // PID reused after this event.
        network.ObserveBoundNetworkTransfer(runtime.NetworkSource!, runtime.Context!, 41, 2000, true, false,
            IPAddress.Loopback, 1600, 100);
        tick = 2001;
        var window = network.Read(true).ObservedTraffic!;
        Assert.Equal(2000, window.WindowMilliseconds);
        var row = Assert.Single(window.Processes);
        Assert.Equal("100", row.CreationWindowsFileTime100Ns);
        Assert.Equal("2000", row.SendBytes);
        Assert.Equal(1000d, row.SendBytesPerSecond);
        var handle = Assert.Single(handles);
        Assert.Equal(2, handle.Reads);
        Assert.Equal(1, handle.Disposals);
    }

    [Fact]
    public void RecordedLossAndFreezeDiscardIncompleteWindowsAndStableLossCanRecover()
    {
        var factory = new Factory();
        using var broker = new KernelEtwSessionOwner(factory);
        long tick = 0;
        var allowed = true;
        using var network = new EtwNetworkTcpIpCaptureOwner(broker, () => allowed, new(), () => tick);
        network.Start();
        network.Read(true);
        tick = 2001;
        Assert.NotNull(network.Read(true).ObservedTraffic); // Complete empty is a valid window.
        factory.Current!.Lost = 1;
        tick = 2100;
        Assert.Null(network.Read(true).ObservedTraffic);
        tick = 4101;
        Assert.NotNull(network.Read(true).ObservedTraffic);
        allowed = false;
        Assert.Null(network.Read(true).ObservedTraffic);
        allowed = true;
        tick = 6102;
        Assert.NotNull(network.Read(true).ObservedTraffic);
    }

    [Fact]
    public void UnconfirmedOriginalRuntimeReleaseBlocksEveryLaterRestart()
    {
        var factory = new Factory();
        using var broker = new KernelEtwSessionOwner(factory);
        var lease = broker.Subscribe(new("network", KernelTraceEventParser.Keywords.NetworkTCPIP, static (_, _) => { }));
        factory.Current!.UnconfirmedStop = true;
        lease.Dispose();
        Assert.Equal("StopFailed", broker.GetSnapshot().State);
        using var next = broker.Subscribe(new("process", KernelTraceEventParser.Keywords.Process, static (_, _) => { }));
        broker.Reconcile();
        Assert.Equal(1, factory.Created);
        Assert.Equal("StopFailed", broker.GetSnapshot().State);
    }

    private sealed class ProcessLease(uint pid, ulong creation) : IWindowsNetworkEndpointProcessLease
    {
        public uint ProcessId => pid;
        internal int Reads;
        internal int Disposals;
        public bool TryReadCreationFileTime(out ulong result) { Reads++; result = creation; return true; }
        public void Dispose() => Disposals++;
    }
    private sealed class Factory : IKernelEtwSessionRuntimeFactory
    {
        internal int Created;
        internal Runtime? Current;
        public bool TryGetAvailability(out string state, out string message) { state = "Available"; message = "recorded"; return true; }
        public IKernelEtwSessionRuntime Create(string name, KernelTraceEventParser.Keywords keywords,
            KernelEtwSessionContext context, IReadOnlyList<KernelEtwSubscriptionRegistration> subscriptions)
        {
            Created++;
            Current = new Runtime();
            Current.UpdateSubscriptions(subscriptions, context);
            return Current;
        }
    }
    private sealed class Runtime : IKernelEtwSessionRuntime
    {
        private readonly Dictionary<long, KernelEtwEventSource> sources = [];
        internal KernelEtwEventSource? NetworkSource;
        internal KernelEtwSessionContext? Context;
        internal int Stops;
        internal int Lost;
        internal bool UnconfirmedStop;
        public bool IsProcessing { get; private set; }
        public int EventsLost => Lost;
        public void StartProcessing(Action<Exception?> completed) => IsProcessing = true;
        public void UpdateSubscriptions(IReadOnlyList<KernelEtwSubscriptionRegistration> subscriptions, KernelEtwSessionContext context)
        {
            Context = context;
            var retained = subscriptions.Select(static item => item.LeaseId).ToHashSet();
            foreach (var id in sources.Keys.Where(id => !retained.Contains(id)).ToArray()) { sources[id].Cancel(); sources.Remove(id); }
            foreach (var item in subscriptions)
            {
                if (sources.ContainsKey(item.LeaseId)) continue;
                var source = new KernelEtwEventSource();
                item.Bind(source, context);
                source.Activate();
                sources.Add(item.LeaseId, source);
                if (item.Keywords.HasFlag(KernelTraceEventParser.Keywords.NetworkTCPIP)) NetworkSource = source;
            }
        }
        public KernelEtwSessionStopResult Stop(TimeSpan timeout)
        {
            Stops++;
            foreach (var source in sources.Values) source.Cancel();
            IsProcessing = false;
            return UnconfirmedStop ? new(false, true, true, ["recorded unconfirmed release"]) : KernelEtwSessionStopResult.Released;
        }
        public void Dispose() { }
    }
}
