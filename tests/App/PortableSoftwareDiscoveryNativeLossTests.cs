using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Infrastructure.SoftwareDiscovery;
using ResourceManager.App.Infrastructure.Telemetry.Etw;

namespace Resource_Manager_APP.Tests;

public sealed class PortableSoftwareDiscoveryNativeLossTests
{
    [Fact]
    public async Task NativePublicationAndHealthCheckRefillEachLossOnlyOnce()
    {
        using var fixture = new KernelEtwZigIoEquivalenceTests.SharedFixture();
        var broker = new HeldBroker(fixture.Session);
        var scans = 0;
        using var service = new PortableSoftwareDiscoveryService(broker, null!, null!, null!,
            NullLogger<PortableSoftwareDiscoveryService>.Instance,
            () => { Interlocked.Increment(ref scans); return []; });
        // The already-existing SharedFixture owns native memory. Attach the same
        // boundary-only wake contract as the production session, without a provider.
        PublicationSignal signal = _ =>
        {
            foreach (var action in (Action[])typeof(NativeKernelEtwSession)
                .GetField("publicationSignals", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Session)!)
                action();
        };
        using (var read = fixture.Session.AcquireReadLease())
            Assert.Equal(0, SetCallback(read.OwnerHandle, Marshal.GetFunctionPointerForDelegate(signal), 0));
        try
        {
            await service.StartAsync(default);
            Assert.Equal(1, Volatile.Read(ref scans));
            fixture.Publish(1000);
            await WaitForNativePublication(service, PublishedSequence(fixture.Session));

            InjectLoss(fixture, 1100, 1);
            await WaitForNativePublication(service, PublishedSequence(fixture.Session));
            Assert.Equal(2, Volatile.Read(ref scans));
            service.RefreshInventoryAfterLoss(broker.GetSnapshot());
            Assert.Equal(2, Volatile.Read(ref scans)); // RED: current code refills the same loss a second time.

            fixture.Publish(1200);
            await WaitForNativePublication(service, PublishedSequence(fixture.Session));
            Assert.Equal(2, Volatile.Read(ref scans));
            InjectLoss(fixture, 1300, 2);
            await WaitForNativePublication(service, PublishedSequence(fixture.Session));
            Assert.Equal(3, Volatile.Read(ref scans));
            service.RefreshInventoryAfterLoss(broker.GetSnapshot());
            Assert.Equal(3, Volatile.Read(ref scans));
        }
        finally
        {
            await service.StopAsync(default);
            using var read = fixture.Session.AcquireReadLease();
            Assert.Equal(0, SetCallback(read.OwnerHandle, 0, 0));
            GC.KeepAlive(signal);
        }
        Assert.False(broker.Source.IsActive);
        Assert.Equal(1, broker.Acquires);
        Assert.Equal(1, broker.Releases);
    }

    private static void InjectLoss(KernelEtwZigIoEquivalenceTests.SharedFixture fixture, long tick, ulong cumulative)
    {
        var wrapper = (nint)typeof(KernelEtwZigIoEquivalenceTests.SharedFixture)
            .GetField("wrapper", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture)!;
        Assert.Equal(0, Loss(wrapper, tick, cumulative));
        fixture.Publish(tick); // Real immutable native notification view, not a fabricated EventsLost field.
    }

    private static ulong PublishedSequence(NativeKernelEtwSession session)
    {
        var cursor = new KernelEtwNativeNotifications.Cursor();
        Assert.True(KernelEtwNativeNotifications.Read(session, ref cursor, static (_, _) => { }));
        return cursor.Publication;
    }

    private static async Task WaitForNativePublication(PortableSoftwareDiscoveryService service, ulong sequence)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var binding = typeof(PortableSoftwareDiscoveryService)
                .GetField("nativeBinding", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service);
            if (binding is not null && ((KernelEtwNativeNotifications.Cursor)binding.GetType()
                    .GetField("Cursor", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(binding)!).Publication >= sequence) return;
            await Task.Delay(1, timeout.Token);
        }
    }

    private sealed class HeldBroker(NativeKernelEtwSession session) : IKernelEtwSessionBroker
    {
        internal KernelEtwEventSource Source { get; private set; } = null!;
        internal int Acquires, Releases;
        public KernelEtwSessionSnapshot GetSnapshot() => new("Running", "actual native test hold", 1,
            DateTimeOffset.UnixEpoch, KernelTraceEventParser.Keywords.Process, Acquires - Releases, session.EventsLost);
        public IKernelEtwSubscription Acquire(KernelEtwSubscriptionRequest request)
        {
            Acquires++;
            var source = Source = new KernelEtwEventSource(session, request.Id, request.NativeOutputs, 1,
                static (name, read, supplement) => new(name, read, supplement));
            request.Bind(source, new(1, DateTimeOffset.UnixEpoch));
            source.Activate();
            var observation = session.ObservePublications(() => source.DispatchPublication());
            return new Held(this, source, observation);
        }
        private sealed class Held(HeldBroker owner, KernelEtwEventSource source, IDisposable observation) : IKernelEtwSubscription
        {
            private int disposed;
            public KernelEtwSessionSnapshot GetSnapshot() => owner.GetSnapshot();
            public void Dispose()
            {
                if (Interlocked.Exchange(ref disposed, 1) != 0) return;
                observation.Dispose();
                owner.Releases++; source.Cancel();
            }
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void PublicationSignal(nint context);
    [DllImport("ResourceManager.NativeCore", EntryPoint = "rm_kernel_etw_shared_set_publication_callback", CallingConvention = CallingConvention.Cdecl)]
    private static extern int SetCallback(nint owner, nint callback, nint context);
    [DllImport("ResourceManager.NativeCore", EntryPoint = "rm_kernel_etw_io_replay_shared_loss", CallingConvention = CallingConvention.Cdecl)]
    private static extern int Loss(nint wrapper, long tick, ulong cumulative);
}
