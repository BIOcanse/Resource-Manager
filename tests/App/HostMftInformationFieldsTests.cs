using ResourceManager.App.Application.DiskUsage;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Application.DeviceTopology;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Domain.DiskUsage;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;
using ResourceManager.App.Infrastructure.DiskUsage;
using ResourceManager.App.Infrastructure.Adaptation;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.ReadWrite;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Windows.Devices;
using ResourceManager.ReadWrite.Windows.Devices.Models;
using ResourceManager.Subscription.Native;

namespace Resource_Manager_APP.Tests;

public sealed class HostMftInformationFieldsTests
{
    [Fact]
    public async Task ProductReadsAndSubscriptionsUseTheOriginalCommittedTreeWithoutScanning()
    {
        using var ledger = new ResourceManagerLedgerOwner();
        var store = new DiskUsageTreeStore(ledger);
        DiskUsageScanResult Result(long bytes, DateTimeOffset time)
        {
            var builder = new DiskUsageTreeBuilder();
            var root = builder.Add(-1, "C:\\", true, 0, 0, new(7, 5, 1));
            builder.Add(root, "recorded", false, bytes, 0, new(7, 10, 1));
            return new(builder.Build(), new(DiskUsageScanScopes.Volume, DiskUsageScanModes.Fast,
                "C:", ["C:\\"], DiskUsageScanKinds.MasterFileTable, [], time, 0, 0, 0, 1, 1, 0));
        }
        var time = new DateTimeOffset(2026, 10, 8, 0, 0, 0, TimeSpan.Zero);
        store.Replace(Result(9, time));
        var originalSummary = store.CurrentSummary;
        using var sampling = new HostManagerSamplingSubscriptionTestFixture();
        var topology = new Topology();
        var journal = new Journal();
        var acpi = new Acpi();
        using var devices = new DeviceTopologySnapshotProvider(topology, journal, new(),
            sampling.Owner, NullLogger<DeviceTopologySnapshotProvider>.Instance, acpi);
        await using var cpu = new CpuTopologyProviderTestFixture();
        using var process = ProcessBasicFieldCollectorTests.Fixture(new ProcessBasicFieldCollectorTests.Api());
        var metrics = new HostReadWriteBackend(null!, null!, new DashboardMonitoringCatalogState(), null!, null!);
        var host = new HostFieldBackends(metrics, new HostFieldSubscriptionBackend(metrics, null!, null!),
            devices, cpu.Reader, new HostProcessFieldBackend(process.Sampler), store);
        var field = host.Reads.Catalog.Fields.Single(item => item.Field.FieldName == "directory_all_logical_size_aggregated").Field;
        var first = (await host.Reads.CollectAsync([field], default)).Single();
        Assert.Equal("9", first.Value?.Integer);
        Assert.Equal(first, host.Reads.ReadCached(field));
        Assert.Same(originalSummary, store.CurrentSummary);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var streamCancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        await using var stream = host.Subscriptions.SubscribeAsync("committed-tree", [field],
            TimeSpan.FromSeconds(1), streamCancellation.Token).GetAsyncEnumerator(streamCancellation.Token);

        var started = false;
        try
        {
            // Register the original periodic demand, then start its existing device worker.
            var initial = stream.MoveNextAsync().AsTask();
            await devices.StartAsync(timeout.Token);
            started = true;
            Assert.True(await initial.WaitAsync(TimeSpan.FromSeconds(10)));
            var firstPeriodic = Assert.Single(stream.Current);
            Assert.Equal(first, firstPeriodic);
            Assert.Equal("9", firstPeriodic.Value?.Integer);
            Assert.Equal(FieldObservationStatus.Current, firstPeriodic.Status);
            Assert.NotNull(firstPeriodic.ObservedAt);
            Assert.Equal(time, firstPeriodic.ObservedAt.Value);

            var replacement = Result(11, time.AddSeconds(1));
            var replacementRound = stream.MoveNextAsync().AsTask();
            store.Replace(replacement);
            Assert.True(await replacementRound.WaitAsync(TimeSpan.FromSeconds(10)));
            var updated = Assert.Single(stream.Current);
            Assert.Equal("11", updated.Value?.Integer);
            Assert.Equal(FieldObservationStatus.Current, updated.Status);
            Assert.NotNull(updated.ObservedAt);
            Assert.Equal(replacement.Summary.CompletedAt, updated.ObservedAt.Value);
            Assert.Equal(host.Reads.ReadCached(field), updated);
            Assert.Same(replacement.Summary, store.CurrentSummary);

            var clearedRound = stream.MoveNextAsync().AsTask();
            store.Clear();
            Assert.True(await clearedRound.WaitAsync(TimeSpan.FromSeconds(10)));
            var unavailable = Assert.Single(stream.Current);
            Assert.Equal(field, unavailable.Field);
            Assert.Equal(FieldObservationStatus.Unavailable, unavailable.Status);
            Assert.Null(unavailable.Value);
            Assert.Null(unavailable.ObservedAt);
            Assert.Null(store.CurrentSummary);
            Assert.DoesNotContain(host.Reads.Catalog.Fields,
                item => item.Field.DeviceIdentity == field.DeviceIdentity);

            // An outstanding channel read is canceled and drained before stopping the worker.
            var canceledRead = stream.MoveNextAsync().AsTask();
            await streamCancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledRead);
            var persistentSources = sampling.Owner.Execute(2, session =>
            {
                var header = new NativeSamplingSubscriptionSnapshotHeader
                {
                    AbiVersion = NativeSamplingSubscriptionAbi.Version,
                    StructSize = checked((uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeSamplingSubscriptionSnapshotHeader>())
                };
                var sources = new NativeSamplingSubscriptionSourceView[session.Capacity.SnapshotSourceCapacity];
                var items = new NativeSamplingSubscriptionItemState[session.Capacity.SnapshotItemCapacity];
                Assert.Equal(NativeSamplingSubscriptionStatus.Ok, session.Snapshot(ref header, sources, items));
                return header.PersistentSourceCount;
            });
            Assert.Equal(0u, persistentSources);
        }
        finally
        {
            if (started) await devices.StopAsync(CancellationToken.None);
        }

        Assert.Equal(0, topology.ReadCount);
        Assert.Equal(0, acpi.ReadCount);
        Assert.Equal(1, journal.LoadCount); // StartAsync loads persisted state once; no collection writes it.
        Assert.Equal(0, journal.SaveCount);
    }
    private sealed class Topology : IDeviceTopologyReader
    {
        private int readCount;
        internal int ReadCount => Volatile.Read(ref readCount);

        public DeviceTopologySnapshot ReadSnapshot()
        {
            var sequence = Interlocked.Increment(ref readCount);
            var capturedAt = new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero).AddMilliseconds(sequence);
            return new(capturedAt, new DeviceTopologySystemIdentity("recorded-maker", "recorded-model",
                "recorded-brand", "REC", null, null, null), [], []);
        }
    }
    private sealed class Acpi : IAcpiFirmwareTableReader
    {
        private int readCount;
        internal int ReadCount => Volatile.Read(ref readCount);
        public IReadOnlyList<AcpiFirmwareTable>? ReadTables()
        {
            Interlocked.Increment(ref readCount);
            return [];
        }
    }
    private sealed class Journal : IDeviceTopologySnapshotStore
    {
        private int loadCount;
        private int saveCount;
        internal int LoadCount => Volatile.Read(ref loadCount);
        internal int SaveCount => Volatile.Read(ref saveCount);

        public Task<DeviceTopologyPersistedSnapshot?> LoadAsync(CancellationToken token)
        {
            Interlocked.Increment(ref loadCount);
            return Task.FromResult<DeviceTopologyPersistedSnapshot?>(null);
        }

        public Task SaveAsync(DeviceTopologyPersistedSnapshot value, CancellationToken token)
        {
            Interlocked.Increment(ref saveCount);
            return Task.CompletedTask;
        }
    }
}
