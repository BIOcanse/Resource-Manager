using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Application.DeviceTopology;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.ReadWrite;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Windows.SystemInformation;
using ResourceManager.Subscription;

namespace Resource_Manager_APP.Tests;

public sealed class OperatingSystemUptimeOwnerTests
{
    [Fact]
    public void PrimitivePreservesOriginalElapsedAndFloorWithValidZeroAndTypedFailure()
    {
        Assert.Equal(0UL, WindowsMachineUptimeReader.FromTickCountMilliseconds(0)!.Seconds);
        Assert.Equal(0UL, WindowsMachineUptimeReader.FromTickCountMilliseconds(999)!.Seconds);
        var reading = WindowsMachineUptimeReader.FromTickCountMilliseconds(1999)!;
        Assert.Equal(1UL, reading.Seconds);
        Assert.Equal(TimeSpan.FromMilliseconds(1999), reading.Elapsed);
        Assert.Null(WindowsMachineUptimeReader.FromTickCountMilliseconds(-1));
        Assert.Null(WindowsMachineUptimeReader.FromTickCountMilliseconds(long.MaxValue));
    }

    [Fact]
    public void Unsigned64InformationBindingPreservesFullWidthAndRejectsOutOfRange()
    {
        var definition = DeviceInformationFieldRegistry.OsAllUptimeActual;
        Assert.Equal("unsigned64", InformationFieldProjection.Describe(definition).ValueKind);
        Assert.Equal("s", definition.Unit);
        var current = InformationFieldProjection.Read("machine", new(definition,
            JsonSerializer.SerializeToElement(ulong.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture))), DateTimeOffset.UtcNow);
        Assert.Equal(MetricInteger.Parse("18446744073709551615"), current.Value!.Integer);
        Assert.Null(current.Value.Number);
        var invalid = InformationFieldProjection.Read("machine", new(definition,
            JsonSerializer.SerializeToElement("18446744073709551616")), DateTimeOffset.UtcNow);
        Assert.Equal(FieldObservationStatus.Invalid, invalid.Status);
        Assert.Null(invalid.Value);
    }

    [Fact]
    public async Task ConstructorCachedReadsAndQueryDoNotReadAndFailureClearsToNullNotZero()
    {
        var reads = 0;
        long ticks = 0;
        using var owner = new WindowsOperatingSystemInformationOwner(() =>
        { reads++; return WindowsMachineUptimeReader.FromTickCountMilliseconds(ticks); });
        var backend = owner.CreateBackend();
        Assert.Empty(backend.Catalog.Fields); Assert.Equal(0, reads);
        var catalog = await backend.DiscoverAsync(default);
        var capability = Assert.Single(catalog.Fields.Where(static field => field.Definition.Name == "os_all_uptime_actual"));
        Assert.Equal(WindowsOperatingSystemInformationOwner.MachineIdentity, capability.Field.DeviceIdentity);
        Assert.Equal(MetricInteger.Parse("0"), backend.ReadCached(capability.Field).Value!.Integer);
        _ = backend.Catalog.Require(capability.Field); _ = backend.ReadCached(capability.Field);
        Assert.Equal(1, reads);
        ticks = -1;
        var failed = Assert.Single(await backend.CollectAsync([capability.Field], default));
        Assert.Equal(FieldObservationStatus.Unavailable, failed.Status);
        Assert.Null(failed.Value); Assert.Null(failed.ObservedAt);
        Assert.Equal(catalog.Fields, backend.Catalog.Fields);
    }

    [Fact]
    public async Task DirectAndBorrowShareCurrentAndSignalsAndRevokeCancelRetainDisplay()
    {
        long ticks = 4000;
        using var owner = new WindowsOperatingSystemInformationOwner(() => WindowsMachineUptimeReader.FromTickCountMilliseconds(ticks));
        var backend = owner.CreateBackend();
        var field = Assert.Single((await backend.DiscoverAsync(default)).Fields.Where(static field => field.Definition.Name == "os_all_uptime_actual")).Field;
        var previous = owner.ReadInformation();
        using var borrow = owner.BorrowSubscriptionCapture();
        _ = borrow.Bind([field]);
        using var publication = borrow.Publications.Subscribe([WindowsOperatingSystemInformationOwner.CollectionItemId]);
        ticks = 9000;
        Assert.False((await borrow.CollectAsync([field], new Permit(false))).Committed);
        Assert.Same(previous, owner.ReadInformation());
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await borrow.CollectAsync([field], new Permit(), cancellation.Token));
        Assert.Same(previous, owner.ReadInformation());
        var committed = await borrow.CollectAsync([field], new Permit());
        Assert.True(committed.Committed);
        Assert.Equal(committed.Readings[0], backend.ReadCached(field));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await publication.WaitAsync(timeout.Token);
        var next = publication.WaitAsync(timeout.Token).AsTask();
        ticks = 11000;
        _ = await backend.CollectAsync([field], default);
        await next;
        Assert.Equal(MetricInteger.Parse("11"), backend.ReadCached(field).Value!.Integer);
        borrow.Dispose();
        Assert.Equal(MetricInteger.Parse("11"), (await backend.CollectAsync([field], default))[0].Value!.Integer);
    }

    [Fact]
    public async Task OriginalNativeCompositionReadsTheSameUptimeCurrentAndLeavesDirectOwnerAvailable()
    {
        using var owner = new WindowsOperatingSystemInformationOwner(() => WindowsMachineUptimeReader.FromTickCountMilliseconds(7000));
        var backend = owner.CreateBackend();
        var field = Assert.Single((await backend.DiscoverAsync(default)).Fields.Where(static field => field.Definition.Name == "os_all_uptime_actual")).Field;
        var configuration = DefaultFieldSubscriptionCompositionTests.Configuration();
        await using var composition = new NativeFieldSubscriptionComposition(owner.BorrowSubscriptionCapture(),
            DefaultFieldSubscriptionCompositionTests.Settings(), in configuration,
            failure => throw new InvalidOperationException("Unexpected original worker failure", failure.Error));
        await composition.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var stream = composition.SubscribeAsync("uptime", [field], TimeSpan.FromSeconds(1), timeout.Token).GetAsyncEnumerator();
        Assert.True(await stream.MoveNextAsync());
        Assert.Equal(MetricInteger.Parse("7"), stream.Current[0].Value!.Integer);
        FieldReadingAssert.CurrentOrNewer(stream.Current[0], backend.ReadCached(field));
        await composition.StopAsync();
        Assert.False(await stream.MoveNextAsync());
        Assert.Equal(MetricInteger.Parse("7"), (await backend.CollectAsync([field], default))[0].Value!.Integer);
    }

    [Fact]
    public async Task HostRoleTwoUsesOneFastestItemSameSdkCurrentAndDoesNotDisposeBorrowedOwnerOnStop()
    {
        var reads = 0;
        using var os = new WindowsOperatingSystemInformationOwner(() =>
            WindowsMachineUptimeReader.FromTickCountMilliseconds(Interlocked.Increment(ref reads) * 1000));
        using var sampling = new HostManagerSamplingSubscriptionTestFixture();
        var topology = new Topology(); var acpi = new Acpi(); var store = new Store();
        using var provider = new DeviceTopologySnapshotProvider(topology, store, new(), sampling.Owner,
            NullLogger<DeviceTopologySnapshotProvider>.Instance, acpi, operatingSystemInventory: os);
        await using var cpu = new CpuTopologyProviderTestFixture();
        var api = new ProcessBasicFieldCollectorTests.Api(); using var process = ProcessBasicFieldCollectorTests.Fixture(api);
        var metrics = new HostReadWriteBackend(null!, null!, new HardwareMetricCatalogState(), null!, null!);
        var fields = new HostFieldBackends(metrics, new HostFieldSubscriptionBackend(metrics, null!, null!), provider,
            cpu.Reader, new HostProcessFieldBackend(process.Sampler));
        Assert.Empty(fields.Reads.Catalog.Fields); Assert.Equal(0, reads);
        await provider.CaptureOperatingSystemInformationAsync(default);
        var field = Assert.Single(fields.Reads.Catalog.Fields.Where(static field => field.Definition.Name == "os_all_uptime_actual")).Field;
        var slow = TimeSpan.FromSeconds(3);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var nextOwnerPublication = os.Publications.Subscribe([WindowsOperatingSystemInformationOwner.CollectionItemId]);
        await using (var stream = fields.Subscriptions.SubscribeAsync("slow", [field], slow, timeout.Token).GetAsyncEnumerator())
        {
            var slowPending = stream.MoveNextAsync().AsTask();
            using (var fast = fields.Subscriptions.Acquire("fast", [field], TimeSpan.FromSeconds(1)))
            {
                sampling.Owner.Execute(2, session =>
                {
                    var header = new ResourceManager.Subscription.Native.NativeSamplingSubscriptionSnapshotHeader
                    { AbiVersion = ResourceManager.Subscription.Native.NativeSamplingSubscriptionAbi.Version,
                        StructSize = (uint)Marshal.SizeOf<ResourceManager.Subscription.Native.NativeSamplingSubscriptionSnapshotHeader>() };
                    var sources = new ResourceManager.Subscription.Native.NativeSamplingSubscriptionSourceView[session.Capacity.SnapshotSourceCapacity];
                    var items = new ResourceManager.Subscription.Native.NativeSamplingSubscriptionItemState[session.Capacity.SnapshotItemCapacity];
                    Assert.Equal(ResourceManager.Subscription.Native.NativeSamplingSubscriptionStatus.Ok, session.Snapshot(ref header, sources, items));
                    Assert.Equal(2u, header.ActiveSourceCount); Assert.Equal(1u, header.KnownItemCount);
                    Assert.Equal(1000UL, items[0].EffectiveIntervalMilliseconds);
                    return 0;
                });
                Assert.False(slowPending.IsCompleted); // registration and cached current alone do not replay a field round
                Assert.Equal(1, Volatile.Read(ref reads)); // only the explicit baseline capture has queried uptime

                await provider.StartAsync(timeout.Token);
                try
                {
                    // This owner signal is only a finite test synchronization point. The field stream must still wait
                    // for its own 3-second trigger; a faster committed query cannot act as its delivery notification.
                    await nextOwnerPublication.WaitAsync(timeout.Token);
                    Assert.True(Volatile.Read(ref reads) >= 2);
                    Assert.False(slowPending.IsCompleted);
                    var fastCurrent = fields.Reads.ReadCached(field);
                    Assert.Equal(FieldObservationStatus.Current, fastCurrent.Status);
                    Assert.NotNull(fastCurrent.ObservedAt);
                    var fastValue = Assert.IsType<MetricInteger>(fastCurrent.Value?.Integer).ToSigned64();
                    Assert.True(fastValue >= 2);

                    Assert.True(await slowPending.WaitAsync(timeout.Token));
                    var slowCurrent = Assert.Single(stream.Current);
                    Assert.Equal(FieldObservationStatus.Current, slowCurrent.Status);
                    // The faster owner keeps publishing, so the cache may already hold a later uptime.
                    FieldReadingAssert.CurrentOrNewer(slowCurrent, fields.Reads.ReadCached(field));
                    Assert.True(Volatile.Read(ref reads) >= 3);
                    Assert.Equal(0, topology.Reads);
                    Assert.Equal(0, acpi.Reads);
                    Assert.Equal(0, store.Saves);
                    Assert.Equal(0, api.Opens + api.TimeCalls + api.MemoryCalls + api.IoCalls
                        + api.HandleCalls + api.Closes + api.ImageCalls + api.SidCalls + api.ParentCalls);
                }
                finally { await provider.StopAsync(CancellationToken.None); }

                var beforeRestart = fields.Reads.ReadCached(field);
                var resumed = stream.MoveNextAsync().AsTask();
                Assert.False(resumed.IsCompleted); // The consumer retains its stream while the producer is stopped.
                await provider.StartAsync(timeout.Token);
                try
                {
                    Assert.True(await resumed.WaitAsync(timeout.Token));
                    var resumedCurrent = Assert.Single(stream.Current);
                    Assert.Equal(FieldObservationStatus.Current, resumedCurrent.Status);
                    Assert.True(resumedCurrent.ObservedAt > beforeRestart.ObservedAt);
                    Assert.True(Assert.IsType<MetricInteger>(resumedCurrent.Value?.Integer).ToSigned64()
                        > Assert.IsType<MetricInteger>(beforeRestart.Value?.Integer).ToSigned64());
                    FieldReadingAssert.CurrentOrNewer(resumedCurrent, fields.Reads.ReadCached(field));
                }
                finally { await provider.StopAsync(CancellationToken.None); }
            }
        }
        Assert.Same(os, provider.OperatingSystemInventory);
        var readsAfterFirstStop = Volatile.Read(ref reads);
        _ = await os.CaptureInformationAsync();
        Assert.Equal(readsAfterFirstStop + 1, Volatile.Read(ref reads));
        await provider.StartAsync(timeout.Token);
        await provider.StopAsync(CancellationToken.None);
        provider.Dispose();
        var readsAfterProviderDispose = Volatile.Read(ref reads);
        _ = await os.CaptureInformationAsync(); // Final host disposal does not own an explicit external owner.
        Assert.Equal(readsAfterProviderDispose + 1, Volatile.Read(ref reads));
    }

    private sealed class Permit(bool active = true) : IMetricPublicationPermit
    {
        public ulong WorkspaceIncarnation => 1;
        public ulong ConfigurationGeneration => 1;
        public bool IsActive => active;
        public bool TryPublish(Action commit) { if (!active) return false; commit(); return true; }
    }
    private sealed class Topology : IDeviceTopologyReader
    {
        internal int Reads;
        public DeviceTopologySnapshot ReadSnapshot() { Reads++; throw new InvalidOperationException("Unexpected topology capture."); }
    }
    private sealed class Acpi : IAcpiFirmwareTableReader
    {
        internal int Reads;
        public IReadOnlyList<AcpiFirmwareTable>? ReadTables() { Reads++; throw new InvalidOperationException("Unexpected ACPI capture."); }
    }
    private sealed class Store : IDeviceTopologySnapshotStore
    {
        internal int Saves;
        public Task<DeviceTopologyPersistedSnapshot?> LoadAsync(CancellationToken token) => Task.FromResult<DeviceTopologyPersistedSnapshot?>(null);
        public Task SaveAsync(DeviceTopologyPersistedSnapshot value, CancellationToken token) { Saves++; return Task.CompletedTask; }
    }
}


