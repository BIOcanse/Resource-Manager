using System.Net;
using Microsoft.Diagnostics.Tracing.Parsers;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Infrastructure.ResourceBreakdown;
using ResourceManager.App.Infrastructure.Telemetry.Etw;
using ResourceManager.ReadWrite.Windows.Devices;
using Resource_Manager_APP.Tests.KernelEtwFixtures;

namespace Resource_Manager_APP.Tests;

public sealed class EtwResourceOwnerLibraryIntegrationTests
{
    private static NativeKernelEtwTestResource NewResource()
    {
        var configuration = NativeKernelEtwTestResource.SmallConfiguration();
        configuration.StartedTickMilliseconds = 0;
        return new(configuration);
    }

    [Fact]
    public void IndependentOwnersAndContractsBelongToSdkAndReadsDoNotAcquireEtw()
    {
        var sdk = typeof(ProcessCpuDeltaTracker).Assembly;
        Assert.Equal(sdk, typeof(KernelEtwSessionOwner).Assembly);
        Assert.Equal(sdk, typeof(EtwNetworkTcpIpCaptureOwner).Assembly);
        Assert.Equal(sdk, typeof(NetworkAttributionSnapshot).Assembly);
        Assert.Equal(sdk, typeof(KernelEtwEventSource).Assembly);
        using var broker = NewResource();
        using var network = new EtwNetworkTcpIpCaptureOwner(broker);
        using var disk = new ResourceManager.ReadWrite.Windows.Processes.EtwPhysicalDiskIoCaptureOwner(broker);
        Assert.Empty(network.Read(true).RawProcessValues); Assert.Empty(disk.Read(true).Processes);
        Assert.Equal(0, broker.Creations); Assert.Equal(0, broker.GetSnapshot().SubscriptionCount);
    }

    [Fact]
    public void NetworkBorrowerSharesTheOriginalUnionAndDisposesOnlyItsLease()
    {
        using var broker = NewResource();
        using var cpu = broker.Acquire(new("cpu", KernelTraceEventParser.Keywords.ContextSwitch,
            static (source, _) => Assert.NotNull(source.NativeSession)) { NativeOutputs = KernelEtwNativeOutputs.Residency });
        var network = new EtwNetworkTcpIpCaptureOwner(broker);
        network.Start(); network.Read();
        Assert.Equal(1, broker.Creations); Assert.Equal(2, broker.GetSnapshot().SubscriptionCount);
        Assert.Equal(KernelTraceEventParser.Keywords.ContextSwitch
            | KernelTraceEventParser.Keywords.Thread | KernelTraceEventParser.Keywords.NetworkTCPIP, broker.GetSnapshot().EnabledKeywords);
        network.Dispose();
        Assert.Equal(1, broker.GetSnapshot().SubscriptionCount); Assert.Equal(0, broker.Stops);
        Assert.Equal(KernelTraceEventParser.Keywords.ContextSwitch, broker.GetSnapshot().EnabledKeywords);
        Assert.Throws<ObjectDisposedException>(() => network.Read());
        cpu.Dispose(); Assert.Equal(1, broker.Stops);
    }

    [Fact]
    public void RecordedWindowUsesOriginalBirthVerificationAndReleasesEveryIdentityLease()
    {
        using var broker = NewResource(); broker.Identities[41] = new(100);
        long tick = 0;
        using var network = new EtwNetworkTcpIpCaptureOwner(broker, new(), () => tick);
        network.Start(); Assert.Null(network.Read(true).ObservedTraffic);
        broker.Network(1500, 41, 4000, true, false, IPAddress.Loopback, 99);
        broker.Network(1600, 41, 2000, true, false, IPAddress.Loopback, 100);
        tick = 2001; broker.Publish(tick);
        var window = network.Read(true).ObservedTraffic!;
        Assert.Equal(2000, window.WindowMilliseconds);
        var row = Assert.Single(window.Processes);
        Assert.Equal("100", row.CreationWindowsFileTime100Ns); Assert.Equal("2000", row.SendBytes);
        Assert.Equal(1000d, row.SendBytesPerSecond);
        var identity = broker.Identities[41];
        Assert.Equal(1, identity.Opens); Assert.Equal(2, identity.Reads); Assert.Equal(1, identity.Closes);
        Assert.Equal(0, broker.ActiveIdentityHandles);
    }

    [Fact]
    public void RecordedLossDiscardsIncompleteWindowsAndStableLossCanRecover()
    {
        using var broker = NewResource(); long tick = 0;
        using var network = new EtwNetworkTcpIpCaptureOwner(broker, new(), () => tick);
        network.Start(); network.Read(true);
        tick = 2001; broker.Publish(tick); Assert.NotNull(network.Read(true).ObservedTraffic);
        tick = 2100; broker.Loss(tick, 1); broker.Publish(tick); Assert.Null(network.Read(true).ObservedTraffic);
        tick = 4101; broker.Publish(tick); Assert.NotNull(network.Read(true).ObservedTraffic);
        Assert.Equal(1, broker.GetSnapshot().SubscriptionCount);
        Assert.Equal(KernelTraceEventParser.Keywords.Thread | KernelTraceEventParser.Keywords.NetworkTCPIP, broker.GetSnapshot().EnabledKeywords);
        tick = 6102; broker.Publish(tick); Assert.NotNull(network.Read(true).ObservedTraffic);
    }

    [Fact]
    public void UnconfirmedOriginalRuntimeReleaseBlocksEveryLaterRestart()
    {
        using var native = NewResource();
        var factory = new RetainedFactory(native.RuntimeFactory);
        using var broker = new KernelEtwSessionOwner(factory);
        try
        {
            var lease = broker.Acquire(new("network", KernelTraceEventParser.Keywords.NetworkTCPIP,
                static (source, _) => Assert.NotNull(source.NativeSession)) { NativeOutputs = KernelEtwNativeOutputs.Network });
            factory.RetainStop = true; lease.Dispose();
            Assert.Equal("StopFailed", broker.GetSnapshot().State);
            using var next = broker.Acquire(new("process", KernelTraceEventParser.Keywords.Process,
                static (source, _) => Assert.NotNull(source.NativeSession)) { NativeOutputs = KernelEtwNativeOutputs.Network });
            broker.Reconcile();
            Assert.Equal(1, native.Creations); Assert.Equal("StopFailed", broker.GetSnapshot().State);
        }
        finally { factory.RetainStop = false; }
    }

    // Test-only release fault over an actual native runtime; no event/source emulator.
    private sealed class RetainedFactory(IKernelEtwSessionRuntimeFactory inner) : IKernelEtwSessionRuntimeFactory
    {
        internal bool RetainStop;
        public void ValidateRequest(KernelEtwSubscriptionRequest request) => inner.ValidateRequest(request);
        public bool TryGetAvailability(out string state, out string message) => inner.TryGetAvailability(out state, out message);
        public IKernelEtwSessionRuntime Create(string name, KernelTraceEventParser.Keywords keywords, KernelEtwSessionContext context,
            IReadOnlyList<KernelEtwSubscriptionRegistration> subscriptions) => new Runtime(this, inner.Create(name, keywords, context, subscriptions));
        private sealed class Runtime(RetainedFactory owner, IKernelEtwSessionRuntime original) : IKernelEtwSessionRuntime
        {
            public bool IsProcessing => original.IsProcessing;
            public int EventsLost => original.EventsLost;
            public void StartProcessing(Action<Exception?> completed) => original.StartProcessing(completed);
            public void UpdateSubscriptions(IReadOnlyList<KernelEtwSubscriptionRegistration> subscriptions, KernelEtwSessionContext context)
                => original.UpdateSubscriptions(subscriptions, context);
            public KernelEtwSessionStopResult Stop(TimeSpan timeout) => owner.RetainStop
                ? new(false, false, false, ["controlled unconfirmed release"]) : original.Stop(timeout);
            public void Dispose() => original.Dispose();
        }
    }
}
