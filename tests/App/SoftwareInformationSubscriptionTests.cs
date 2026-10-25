using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Application.DeviceTopology;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.ReadWrite;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.Software;
using ResourceManager.ReadWrite;

namespace Resource_Manager_APP.Tests;

public sealed class SoftwareInformationSubscriptionTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ServiceFieldDeliveryInterval = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan SoftwareFieldDeliveryInterval = TimeSpan.FromSeconds(2);
    private static TimeSpan SlowInterval => TimeSpan.FromMinutes(1);

    [Fact]
    public async Task ServiceFieldsReuseRoleTwoItemMergeActiveConfigurationAndKeepOwnerAcrossStopRestart()
    {
        var includeConfiguration = false;
        var serviceQueries = 0;
        using var services = new ResourceManager.ReadWrite.Windows.Services.WindowsServiceInformationOwner(
            ResourceManager.ReadWrite.Windows.Services.WindowsServiceScope.Win32, (include, _) =>
            {
                includeConfiguration = include;
                Interlocked.Increment(ref serviceQueries);
                return new(true, [new("Svc", "Original service", 0x10, 1, 0, include ? 3u : null)]);
            });
        using var fixture = new Fixture(services: services);
        await using var cpu = new CpuTopologyProviderTestFixture();
        var processApi = new ProcessBasicFieldCollectorTests.Api();
        using var process = ProcessBasicFieldCollectorTests.Fixture(processApi);
        var metrics = new HostReadWriteBackend(null!, null!, new DashboardMonitoringCatalogState(), null!, null!);
        var composed = new HostFieldBackends(metrics, new HostFieldSubscriptionBackend(metrics, null!, null!),
            fixture.Provider, cpu.Reader, new HostProcessFieldBackend(process.Sampler));
        await fixture.Provider.CaptureServiceInformationAsync(default);
        Assert.False(includeConfiguration);
        Assert.Equal(1, serviceQueries);
        var start = Assert.Single(composed.Reads.Catalog.Fields, field => field.Field.FieldName == "service_all_start_type_configured");
        var state = Assert.Single(composed.Reads.Catalog.Fields, field => field.Field.FieldName == "service_all_state_actual");
        using var timeout = new CancellationTokenSource(Deadline);
        await using var stream = composed.Subscriptions.SubscribeAsync("configured-slow", [start.Field], ServiceFieldDeliveryInterval, timeout.Token).GetAsyncEnumerator();
        var firstDelivery = stream.MoveNextAsync().AsTask();
        IDisposable? fast = composed.Subscriptions.Acquire("state-fast", [state.Field], TimeSpan.FromSeconds(1));
        var demand = fixture.ReadDemand();
        Assert.Equal(2u, demand.Sources);
        Assert.Equal(1u, demand.Items);
        Assert.Equal(1_000UL, demand.Interval);
        await fixture.Provider.StartAsync(timeout.Token);
        try
        {
            // Existing current is not replayed as a subscription event. The four-second field trigger is served
            // while the second source keeps the same item sampled every second.
            Assert.True(await firstDelivery.WaitAsync(Deadline));
            Assert.True(includeConfiguration);
            Assert.True(serviceQueries >= 2); // one explicit direct capture plus an actual worker source query
            Assert.NotNull(stream.Current[0].ObservedAt);
            Assert.Equal(MetricInteger.Parse("3"), stream.Current[0].Value!.Integer);
            FieldReadingAssert.CurrentOrNewer(stream.Current[0], composed.Reads.ReadCached(start.Field));

            fast.Dispose(); fast = null;
            Assert.Equal((1u, 1u, 1u, (ulong)ServiceFieldDeliveryInterval.TotalMilliseconds), fixture.ReadDemand());
            await Task.Delay(200, timeout.Token); // let a collection already accepted before removal settle
            var slowerCount = serviceQueries;
            await Task.Delay(1_300, timeout.Token); // longer than the removed one-second rate, shorter than four seconds
            Assert.Equal(slowerCount, serviceQueries);
            Assert.Equal(0, fixture.Scans + fixture.Topology.Reads + fixture.Acpi.Reads + fixture.Store.Saves
                + processApi.Opens + cpu.Sampler.CaptureCount);
        }
        finally
        {
            fast?.Dispose();
            await fixture.Provider.StopAsync(CancellationToken.None).WaitAsync(Deadline);
        }
        // The supplied service owner remains externally owned and directly usable after the provider stops.
        var beforeStopCapture = serviceQueries;
        var afterStop = await services.CaptureInformationAsync();
        Assert.Equal(beforeStopCapture + 1, serviceQueries);
        Assert.False(includeConfiguration);
        Assert.NotNull(afterStop.ObservedAt);
        Assert.Equal(afterStop, services.ReadInformation());
        await fixture.Provider.StartAsync(timeout.Token);
        try
        {
            var beforeRestartCapture = serviceQueries;
            var afterRestart = await services.CaptureInformationAsync();
            Assert.Equal(beforeRestartCapture + 1, serviceQueries);
            Assert.NotNull(afterRestart.ObservedAt);
        }
        finally { await fixture.Provider.StopAsync(CancellationToken.None).WaitAsync(Deadline); }
    }

    [Fact]
    public async Task RealWorkerSharesOneSoftwareItemUsesHighestDemandAndRestoresSlowerFrequency()
    {
        using var fixture = new Fixture();
        using var timeout = new CancellationTokenSource(Deadline);
        var slowInterval = SlowInterval;
        await using var stream = fixture.Provider.SubscribeSoftwareAsync("slow", slowInterval, timeout.Token).GetAsyncEnumerator();
        Assert.True(await stream.MoveNextAsync()); Assert.Null(stream.Current.ObservedAt); Assert.Equal(0, fixture.Scans);
        var first = stream.MoveNextAsync().AsTask();
        await fixture.Provider.StartAsync(timeout.Token);
        IDisposable? medium = null; IDisposable? fast = null;
        try
        {
            Assert.True(await first.WaitAsync(Deadline));
            Assert.Same(fixture.Inventory.ReadInformation(), stream.Current); Assert.Equal(1, fixture.Scans);
            Assert.Equal("deviceTopology.software", DeviceTopologySnapshotProvider.SoftwareItemId);
            Assert.Equal((1u, 1u, 1u, (ulong)slowInterval.TotalMilliseconds), fixture.ReadDemand());
            medium = fixture.Provider.AcquireSoftwareInformationSubscription("medium", TimeSpan.FromSeconds(2));
            fast = fixture.Provider.AcquireSoftwareInformationSubscription("fast", TimeSpan.FromSeconds(1));
            Assert.Equal((3u, 3u, 1u, 1000UL), fixture.ReadDemand());
            Assert.True(await stream.MoveNextAsync().AsTask().WaitAsync(Deadline));
            Assert.True(await stream.MoveNextAsync().AsTask().WaitAsync(Deadline));
            Assert.InRange(fixture.Scans, 3, 4);
            fast.Dispose(); fast = null;
            Assert.Equal((2u, 2u, 1u, 2000UL), fixture.ReadDemand());
            medium.Dispose(); medium = null;
            Assert.Equal((1u, 1u, 1u, (ulong)slowInterval.TotalMilliseconds), fixture.ReadDemand());
            // Settle a capture already accepted before the lease change, then span a fast period.
            await Task.Delay(200, timeout.Token);
            var count = fixture.Scans; var current = fixture.Inventory.ReadInformation();
            await Task.Delay(1300, timeout.Token);
            Assert.Equal(count, fixture.Scans); Assert.Same(current, fixture.Inventory.ReadInformation());
            Assert.Equal(0, fixture.Topology.Reads + fixture.Acpi.Reads + fixture.Store.Saves);
        }
        finally
        {
            fast?.Dispose(); medium?.Dispose();
            await timeout.CancelAsync();
            await fixture.Provider.StopAsync(default).WaitAsync(Deadline);
        }
    }

    [Fact]
    public async Task LastSubscriptionCancellationClearsDemandAndStopsSubsequentScans()
    {
        using var fixture = new Fixture(); using var cancellation = new CancellationTokenSource(Deadline);
        var stream = fixture.Provider.SubscribeSoftwareAsync("last", TimeSpan.FromSeconds(1), cancellation.Token).GetAsyncEnumerator();
        Task<bool>? pending = null;
        await fixture.Provider.StartAsync(default);
        try
        {
            Assert.True(await stream.MoveNextAsync()); // The original inventory signal initially yields its current.
            if (stream.Current.ObservedAt is null) Assert.True(await stream.MoveNextAsync().AsTask().WaitAsync(Deadline));
            Assert.Same(fixture.Inventory.ReadInformation(), stream.Current);
            pending = stream.MoveNextAsync().AsTask();
            await cancellation.CancelAsync();
            // A publication accepted before cancellation may already have completed this move.
            try { await pending.WaitAsync(Deadline); } catch (OperationCanceledException) { }
            await stream.DisposeAsync();
            Assert.Equal(0u, fixture.ReadDemand().Sources);
            await Task.Delay(200);
            var count = fixture.Scans; var current = fixture.Inventory.ReadInformation();
            await Task.Delay(1300);
            Assert.Equal(count, fixture.Scans); Assert.Same(current, fixture.Inventory.ReadInformation());
            Assert.Equal(0, fixture.Topology.Reads + fixture.Acpi.Reads + fixture.Store.Saves);
        }
        finally
        {
            await cancellation.CancelAsync();
            if (pending is not null) { try { await pending.WaitAsync(Deadline); } catch (OperationCanceledException) { } }
            await stream.DisposeAsync();
            await fixture.Provider.StopAsync(default).WaitAsync(Deadline);
        }
    }

    [Fact]
    public async Task ActualOwnerRetirementRejectsAnInFlightExplicitCommitAndRetainsOriginalDisplayAndSignal()
    {
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var block = false;
        using var fixture = new Fixture((scan, _) =>
        {
            if (block) { entered.Set(); if (!release.Wait(Deadline)) throw new TimeoutException("Software capture was not released."); }
            return [Entry("scan-" + scan)];
        });
        var first = await fixture.Provider.CaptureSoftwareInformationAsync(default);
        var originalEntries = await fixture.Inventory.GetInstalledSoftwareAsync(default);
        using var timeout = new CancellationTokenSource(Deadline);
        // Observe the inventory's original signal without retaining a demand lease after owner shutdown.
        await using var stream = fixture.Inventory.SubscribeInformationAsync(timeout.Token).GetAsyncEnumerator();
        Assert.True(await stream.MoveNextAsync()); Assert.Same(first, stream.Current);
        var pending = stream.MoveNextAsync().AsTask();
        block = true;
        var inFlight = Task.Run(async () => await fixture.Provider.CaptureSoftwareInformationAsync(default));
        try
        {
            Assert.True(entered.Wait(Deadline));
            using var tokenLease = fixture.Sampling.Owner.Acquire(2);
            await fixture.Sampling.Owner.StopAsync(default).WaitAsync(Deadline);
            Assert.False(tokenLease.OwnerToken.IsActive);
            release.Set(); Assert.Same(first, await inFlight.WaitAsync(Deadline));
            Assert.Same(first, fixture.Inventory.ReadInformation());
            Assert.Same(originalEntries, await fixture.Inventory.GetInstalledSoftwareAsync(default));
            Assert.False(pending.IsCompleted); Assert.Equal(2, fixture.Scans);
        }
        finally
        {
            release.Set(); await inFlight.WaitAsync(Deadline);
            await timeout.CancelAsync();
            try { await pending.WaitAsync(Deadline); } catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task HostCompositionRoutesSoftwareWorkerDirectReadsAndNotificationsThroughOneCurrent()
    {
        using var fixture = new Fixture();
        await using var cpu = new CpuTopologyProviderTestFixture();
        var processApi = new ProcessBasicFieldCollectorTests.Api();
        using var process = ProcessBasicFieldCollectorTests.Fixture(processApi);
        var metrics = new HostReadWriteBackend(null!, null!, new DashboardMonitoringCatalogState(), null!, null!);
        var composed = new HostFieldBackends(metrics, new HostFieldSubscriptionBackend(metrics, null!, null!),
            fixture.Provider, cpu.Reader, new HostProcessFieldBackend(process.Sampler));
        Assert.Empty(composed.Reads.Catalog.Fields); Assert.Equal(0, fixture.Scans);
        await fixture.Provider.CaptureSoftwareInformationAsync(default);
        var name = Assert.Single(composed.Reads.Catalog.Fields, item => item.Field.FieldName == "software_all_name_declared");
        Assert.Equal(4, composed.Reads.Catalog.Fields.Length);
        Assert.True(name.Read.Supported); Assert.False(name.Write.Supported); Assert.Empty(name.Read.Dependencies);
        Assert.Equal("string", name.Definition.ValueKind);
        Assert.Equal("windows-uninstall|" + Entry("unused").RegistryPath, name.Field.DeviceIdentity);
        Assert.Equal("scan-1", composed.Reads.ReadCached(name.Field).Value?.Text);
        Assert.Equal(1, fixture.Scans);
        using var timeout = new CancellationTokenSource(Deadline);
        await using var stream = composed.Subscriptions.SubscribeAsync("software-public", [name.Field], SoftwareFieldDeliveryInterval,
            new ResourceManager.Subscription.FieldSubscriptionOptions(ResourceManager.Subscription.FieldSubscriptionSamplingMode.FreshOnTrigger), timeout.Token).GetAsyncEnumerator();
        var firstDelivery = stream.MoveNextAsync().AsTask();
        Assert.Equal(1u, fixture.ReadDemand().Sources);
        await fixture.Provider.StartAsync(timeout.Token);
        try
        {
            // The worker's first sample fills the original current. The first stream result arrives on its
            // own two-second trigger, after the inventory has been queried again by that same worker.
            Assert.True(await firstDelivery.WaitAsync(Deadline));
            var firstPeriodic = Assert.Single(stream.Current);
            Assert.True(fixture.Scans >= 2);
            Assert.NotEqual("scan-1", firstPeriodic.Value?.Text);
            Assert.Equal(firstPeriodic, composed.Reads.ReadCached(name.Field));

            var nextPeriodic = stream.MoveNextAsync().AsTask();
            var beforeDirect = fixture.Scans;
            var readings = await composed.Reads.CollectAsync([name.Field], timeout.Token);
            var directReading = Assert.Single(readings);
            Assert.Equal(beforeDirect + 1, fixture.Scans);
            Assert.Equal(directReading, composed.Reads.ReadCached(name.Field));
            Assert.False(nextPeriodic.IsCompleted); // a direct query updates current but is not a field trigger

            Assert.True(await nextPeriodic.WaitAsync(Deadline));
            var nextWorkerReading = Assert.Single(stream.Current);
            Assert.Equal(beforeDirect + 2, fixture.Scans);
            Assert.NotEqual(directReading.Value?.Text, nextWorkerReading.Value?.Text);
            Assert.Equal(nextWorkerReading, composed.Reads.ReadCached(name.Field));
            Assert.Same(fixture.Inventory, fixture.Provider.SoftwareInventory);
            Assert.Equal(fixture.Inventory.ReadInformation().ObservedAt, nextWorkerReading.ObservedAt);
            _ = await fixture.Inventory.GetInstalledSoftwareAsync(timeout.Token);
            _ = composed.Reads.Catalog; _ = composed.Reads.ReadCached(name.Field); Assert.Equal(beforeDirect + 2, fixture.Scans);
            Assert.Equal(0, fixture.Topology.Reads + fixture.Acpi.Reads + fixture.Store.Saves + processApi.Opens + cpu.Sampler.CaptureCount);
        }
        finally { await timeout.CancelAsync(); await fixture.Provider.StopAsync(default).WaitAsync(Deadline); }
    }

    private static InstalledSoftwareEntry Entry(string name) => new("recorded-id", name, "1.2.3", "Publisher", @"D:\Recorded\App",
        [@"D:\Recorded\App"], null, @"LocalMachine\Registry64\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Recorded");

    private sealed class Fixture : IDisposable
    {
        public HostManagerSamplingSubscriptionTestFixture Sampling { get; } = new();
        public TopologyReader Topology { get; } = new();
        public AcpiReader Acpi { get; } = new();
        public Store Store { get; } = new();
        public WindowsInstalledSoftwareInventory Inventory { get; }
        public DeviceTopologySnapshotProvider Provider { get; }
        private int scans;
        public int Scans => Volatile.Read(ref scans);
        public Fixture(Func<int, CancellationToken, IReadOnlyList<InstalledSoftwareEntry>>? read = null,
            ResourceManager.ReadWrite.Windows.Services.WindowsServiceInformationOwner? services = null)
        {
            Inventory = new(token => { var scan = Interlocked.Increment(ref scans); return read?.Invoke(scan, token) ?? [Entry("scan-" + scan)]; });
            Provider = new(Topology, Store, new(), Sampling.Owner, NullLogger<DeviceTopologySnapshotProvider>.Instance, Acpi, Inventory, services);
        }
        public (uint Sources, uint Persistent, uint Items, ulong Interval) ReadDemand() => Sampling.Owner.Execute(2, session =>
        {
            var header = new NativeSamplingSubscriptionSnapshotHeader { AbiVersion = NativeSamplingSubscriptionAbi.Version,
                StructSize = checked((uint)Marshal.SizeOf<NativeSamplingSubscriptionSnapshotHeader>()) };
            var sources = new NativeSamplingSubscriptionSourceView[session.Capacity.SnapshotSourceCapacity];
            var items = new NativeSamplingSubscriptionItemState[session.Capacity.SnapshotItemCapacity];
            Assert.Equal(NativeSamplingSubscriptionStatus.Ok, session.Snapshot(ref header, sources, items));
            return (header.ActiveSourceCount, header.PersistentSourceCount, header.KnownItemCount,
                header.ItemOutputCount == 0 ? 0 : items[0].EffectiveIntervalMilliseconds);
        });
        public void Dispose() { Provider.Dispose(); Sampling.Dispose(); }
    }

    private sealed class TopologyReader : IDeviceTopologyReader
    {
        public int Reads;
        public DeviceTopologySnapshot ReadSnapshot() { Interlocked.Increment(ref Reads); throw new InvalidOperationException("Unexpected topology capture."); }
    }
    private sealed class AcpiReader : IAcpiFirmwareTableReader
    {
        public int Reads;
        public IReadOnlyList<AcpiFirmwareTable>? ReadTables() { Interlocked.Increment(ref Reads); throw new InvalidOperationException("Unexpected ACPI capture."); }
    }
    private sealed class Store : IDeviceTopologySnapshotStore
    {
        public int Saves;
        public Task<DeviceTopologyPersistedSnapshot?> LoadAsync(CancellationToken token) => Task.FromResult<DeviceTopologyPersistedSnapshot?>(null);
        public Task SaveAsync(DeviceTopologyPersistedSnapshot snapshot, CancellationToken token) { Interlocked.Increment(ref Saves); return Task.CompletedTask; }
    }
}

