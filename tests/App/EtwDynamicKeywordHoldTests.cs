using System.Collections.Frozen;
using Resource_Manager_APP.Tests.KernelEtwFixtures;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Infrastructure.SystemHealth.Interrupts;
using ResourceManager.App.Infrastructure.Telemetry.Etw;
using ResourceManager.ReadWrite.Windows.Processes;

namespace Resource_Manager_APP.Tests;

public sealed class EtwDynamicKeywordHoldTests
{
    private const KernelTraceEventParser.Keywords Metadata = KernelTraceEventParser.Keywords.Process | KernelTraceEventParser.Keywords.Thread;
    private const KernelTraceEventParser.Keywords Switch = KernelTraceEventParser.Keywords.ContextSwitch;
    private static EtwCpuResidencyCaptureOwner Capture(Broker broker) => new(broker,
        () => new(50, 64, new Dictionary<int, CpuResidencyCoreAccounting> { [0] = new("0", "", 1, 1) }.ToFrozenDictionary(), "fixture", 10),
        new KernelEtwDiagnostics(), null, static (_, _) => { });

    [Fact]
    public void ResidencyHoldsMetadataAtEntryAndSwitchesOnlyInWindows()
    {
        using var broker = new Broker();
        using var capture = Capture(broker);
        Assert.Empty(broker.Holders);
        capture.EnsureStarted();
        Assert.Equal(Metadata, broker.Keywords);
        for (var i = 0; i < 2; i++)
        {
            capture.BeginWindow();
            Assert.Equal(Metadata | Switch, broker.Keywords);
            capture.Collect();
            capture.PauseWindow();
            Assert.Equal(Metadata, broker.Keywords);
            Assert.Equal(Metadata, broker.Holders.Single().Keywords);
        }
        capture.BeginWindow();
        capture.Collect();
        capture.BeginWindow();
        Assert.Equal(2, broker.Holders.Length); // continuous capture keeps the same second hold
        capture.PauseWindow();
        Assert.Equal(Metadata, broker.Keywords);
        capture.BeginWindow();
        Assert.Equal(Metadata | Switch, broker.Keywords);
        capture.Stop();
        Assert.Empty(broker.Holders);
        Assert.All(broker.History, holders => Assert.Contains(holders, h => h.Keywords == Metadata));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WindowAcquisitionCanBindOnAnotherThreadAndPauseOrDisposeBeforeReturn(bool dispose)
    {
        using var broker = new Broker();
        using var capture = Capture(broker);
        capture.EnsureStarted();
        broker.RebindExisting = true;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        broker.BeforeReturn = request =>
        {
            if (request.Keywords != Switch) return;
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
        };
        var window = Task.Run(capture.BeginWindow);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            if (dispose) capture.Dispose(); else capture.PauseWindow();
        }
        finally { release.Set(); }
        await window.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(dispose ? KernelTraceEventParser.Keywords.None : Metadata, broker.Keywords);
        capture.Dispose();
        Assert.Empty(broker.Holders);
    }

    [Fact]
    public void DeniedOrLostContextSwitchCannotBeHiddenByRunningMetadata()
    {
        using var broker = new Broker { DeniedKeywords = Switch };
        using var capture = Capture(broker);
        capture.EnsureStarted();
        capture.BeginWindow();
        Assert.False(capture.Collect());
        Assert.Equal("cpu-residency-session-unavailable",
            Assert.IsType<ResourceManager.App.Domain.CpuTopology.CpuCoreResidencyUnavailable>(capture.ReadObservation()).ReasonCode);
        broker.DeniedKeywords = KernelTraceEventParser.Keywords.None;
        Assert.True(capture.Collect()); // available, but still warming without boundary events
        broker.DeniedKeywords = Switch;
        Assert.False(capture.Collect());
        Assert.Equal("cpu-residency-session-unavailable",
            Assert.IsType<ResourceManager.App.Domain.CpuTopology.CpuCoreResidencyUnavailable>(capture.ReadObservation()).ReasonCode);
        broker.DeniedKeywords = Metadata;
        Assert.False(capture.Collect());
    }

    [Fact]
    public async Task InterruptRunHoldsMetadataAndEventsAndStopReturnsBoth()
    {
        using var sampling = new HostManagerSamplingSubscriptionTestFixture();
        using var broker = new Broker();
        using var source = new EtwSystemInterruptSnapshotSource(broker, sampling.Owner, NullLogger<EtwSystemInterruptSnapshotSource>.Instance);
        Assert.Empty(broker.Holders);
        await source.StartAsync(default);
        var events = KernelTraceEventParser.Keywords.Interrupt | KernelTraceEventParser.Keywords.DeferedProcedureCalls;
        await UntilAsync(() => broker.Keywords == (KernelTraceEventParser.Keywords.ImageLoad | events)
            && HasInterruptLease(source));
        Assert.Equal(KernelTraceEventParser.Keywords.ImageLoad | events, broker.Keywords);
        await source.StopAsync(default);
        Assert.Empty(broker.Holders);
        await source.StartAsync(default);
        await UntilAsync(() => broker.Holders.Length == 2);
        source.Dispose();
        Assert.Empty(broker.Holders);
    }

    private static bool HasInterruptLease(EtwSystemInterruptSnapshotSource source)
    {
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.NonPublic;
        var gate = typeof(EtwSystemInterruptSnapshotSource).GetField("subscriptionGate", flags)!.GetValue(source)!;
        lock (gate)
            return typeof(EtwSystemInterruptSnapshotSource).GetField("durationSubscription", flags)!.GetValue(source) is not null;
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(5, timeout.Token);
    }

    private sealed class Broker : IKernelEtwSessionBroker, INativeKernelEtwTestResourceBroker, IDisposable
    {
        private readonly object gate = new();
        private readonly List<KernelEtwSubscriptionRequest> holders = [];
        public NativeKernelEtwTestResource Resource { get; } = new(NativeKernelEtwTestResource.SmallConfiguration());
        public List<KernelEtwSubscriptionRequest[]> History { get; } = [];
        public Action<KernelEtwSubscriptionRequest>? BeforeReturn;
        public bool RebindExisting;
        public KernelTraceEventParser.Keywords DeniedKeywords;
        public KernelEtwSubscriptionRequest[] Holders { get { lock (gate) return holders.ToArray(); } }
        public KernelTraceEventParser.Keywords Keywords => Holders.Aggregate(KernelTraceEventParser.Keywords.None, (v, h) => v | h.Keywords);
        public IKernelEtwSubscription Acquire(KernelEtwSubscriptionRequest request)
        {
            lock (gate) { holders.Add(request); History.Add(holders.ToArray()); }
            IKernelEtwSubscription? native = null;
            try
            {
                if (RebindExisting) Resource.Rebind();
                native = Resource.Acquire(request with { Bind = (source, context) =>
                {
                    var binding = Task.Factory.StartNew(() => request.Bind(source, context),
                        CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                    Assert.True(binding.Wait(TimeSpan.FromSeconds(2)), "Broker binding deadlocked.");
                }});
                BeforeReturn?.Invoke(request);
                return new Lease(this, request, native);
            }
            catch
            {
                native?.Dispose();
                lock (gate) holders.Remove(request);
                throw;
            }
        }
        public KernelEtwSessionSnapshot GetSnapshot() => Resource.GetSnapshot();
        public void Dispose() => Resource.Dispose();
        private sealed class Lease(Broker owner, KernelEtwSubscriptionRequest request, IKernelEtwSubscription native) : IKernelEtwSubscription
        {
            private int disposed;
            public KernelEtwSessionSnapshot GetSnapshot()
            {
                var snapshot = native.GetSnapshot();
                return (owner.DeniedKeywords & request.Keywords) == 0 ? snapshot
                    : snapshot with { State = "Unavailable", Message = "fixture: holder keywords denied" };
            }
            public void Dispose()
            {
                if (Interlocked.Exchange(ref disposed, 1) != 0) return;
                try { native.Dispose(); }
                finally { lock (owner.gate) owner.holders.Remove(request); }
            }
        }
    }
}
