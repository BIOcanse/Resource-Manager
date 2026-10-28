using System.Text.Json;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Application.DeviceTopology;
using ResourceManager.App.Application.DiskUsage;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Domain.DiskUsage;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;
using ResourceManager.App.Infrastructure.DiskUsage;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.Software;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Windows.Devices;
using ResourceManager.ReadWrite.Windows.Devices.Models;
using ResourceManager.ReadWrite.Windows.Services;
using ResourceManager.ReadWrite.Windows.SystemInformation;
using ResourceManager.Subscription;
using ResourceManager.Subscription.Native;

namespace Resource_Manager_APP.Tests;

public sealed class HostDevicePeriodicSubscriptionTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task DeviceFieldStreamSurvivesAProducerFaultAndControllerRestart()
    {
        await using var fixture = new DeviceFixture();
        await fixture.Provider.SampleNowAsync(CancellationToken.None);
        var field = fixture.Backend.Catalog.Fields.Single(static capability =>
            capability.Field.FieldName == "machine_all_manufacturer").Field;
        var failReads = 0;
        var backend = new InformationReadWriteBackend(() =>
        {
            if (Volatile.Read(ref failReads) != 0)
                throw new InvalidOperationException(ProducerSubscriptionCurrentBackend.FailureMessage);
            return fixture.Provider.ReadState().InformationFields;
        }, fixture.Provider.CaptureInformationAsync, static _ => new(true, null, []));
        using var timeout = new CancellationTokenSource(Deadline);
        await using var stream = fixture.Provider.SubscribeFieldsAsync("held-through-fault", [field],
            TimeSpan.FromMilliseconds(200), FieldSubscriptionOptions.Default, backend,
            DeviceTopologyFieldScope.Snapshot, timeout.Token).GetAsyncEnumerator(timeout.Token);
        var pending = stream.MoveNextAsync().AsTask();
        Volatile.Write(ref failReads, 1); // Registration uses the real typed backend before the recorded run fault is armed.
        await using var controller = await ProducerSubscriptionRestartHost<DeviceTopologySnapshotProvider>
            .StartAsync(fixture.Provider, static producer => producer.CaptureSamplingWorkerTask(),
                () => Volatile.Write(ref failReads, 0));
        var firstRun = await controller.FirstRun;
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => firstRun.WaitAsync(Deadline));
        Assert.Equal(ProducerSubscriptionCurrentBackend.FailureMessage, failure.Message);
        await controller.AssertControllerRestartedAsync();
        Assert.Equal((1u, 1u), fixture.ReadDemand());
        Assert.True(await pending.WaitAsync(Deadline));
        Assert.Equal(FieldObservationStatus.Current, Assert.Single(stream.Current).Status);
        Assert.True(fixture.Topology.ReadCount >= 3); // Discovery, failed run, restarted run.
    }

    [Fact]
    public async Task DeviceHighestFrequencyKeepsIndependentPeriodsOnTheOriginalCaptureCurrent()
    {
        await using var fixture = new DeviceFixture();
        Assert.Equal(DeviceTopologySampleCompletion.Sampled,
            await fixture.Provider.SampleNowAsync(CancellationToken.None));
        var field = fixture.Backend.Catalog.Fields.Single(static capability =>
            capability.Field.FieldName == "machine_all_manufacturer").Field;
        var sourceReadsBeforeWorker = fixture.Topology.ReadCount;

        using var timeout = new CancellationTokenSource(Deadline);
        await using var fast = fixture.Provider.SubscribeFieldsAsync("device-fast", [field],
            TimeSpan.FromMilliseconds(200), FieldSubscriptionOptions.Default, fixture.Backend,
            DeviceTopologyFieldScope.Snapshot, timeout.Token).GetAsyncEnumerator(timeout.Token);
        await using var slow = fixture.Provider.SubscribeFieldsAsync("device-slow", [field],
            TimeSpan.FromMilliseconds(1500), FieldSubscriptionOptions.Default, fixture.Backend,
            DeviceTopologyFieldScope.Snapshot, timeout.Token).GetAsyncEnumerator(timeout.Token);

        var fastFirst = fast.MoveNextAsync().AsTask();
        var slowFirst = slow.MoveNextAsync().AsTask();
        await fixture.Provider.StartAsync(timeout.Token);
        Assert.True(await fastFirst.WaitAsync(Deadline));
        Assert.True(await slowFirst.WaitAsync(Deadline));
        var firstFast = Assert.Single(fast.Current);
        var firstSlow = Assert.Single(slow.Current);
        Assert.Equal(field, firstFast.Field);
        Assert.Equal(field, firstSlow.Field);
        Assert.Equal(FieldObservationStatus.Current, firstFast.Status);
        Assert.Equal(FieldObservationStatus.Current, firstSlow.Status);
        Assert.NotNull(firstFast.ObservedAt);
        Assert.NotNull(firstSlow.ObservedAt);

        var slowSecond = slow.MoveNextAsync().AsTask();
        Assert.False(slowSecond.IsCompleted,
            "The slower subscriber keeps its own 1500 ms delivery cadence while the shared source follows the 200 ms demand.");
        Assert.True(await fast.MoveNextAsync().AsTask().WaitAsync(Deadline));
        var secondFast = Assert.Single(fast.Current);
        Assert.True(await fast.MoveNextAsync().AsTask().WaitAsync(Deadline));
        var thirdFast = Assert.Single(fast.Current);
        Assert.True(await slowSecond.WaitAsync(Deadline));
        var secondSlow = Assert.Single(slow.Current);

        Assert.True(fixture.Topology.ReadCount - sourceReadsBeforeWorker >= 3,
            "The original topology reader must sample at the fastest subscribed period, not once per slow delivery.");
        Assert.NotEqual(firstFast.Value?.Text, thirdFast.Value?.Text);
        Assert.NotNull(secondFast.ObservedAt);
        Assert.NotNull(thirdFast.ObservedAt);
        // Shared-mode deliveries read the latest completed cache; a delivery can
        // race the next periodic capture and therefore repeat its observation.
        Assert.True(firstFast.ObservedAt.Value <= secondFast.ObservedAt.Value);
        Assert.True(secondFast.ObservedAt.Value <= thirdFast.ObservedAt.Value);
        Assert.True(firstFast.ObservedAt.Value < thirdFast.ObservedAt.Value);
        Assert.Equal(field, secondSlow.Field);
        Assert.Equal(FieldObservationStatus.Current, secondSlow.Status);
        Assert.NotNull(secondSlow.ObservedAt);
        Assert.Equal(fixture.Backend.Catalog.Require(field).ReadSource, secondSlow.SourceId);
        // Each item is checked against its own observation. The worker may already have advanced
        // the original current again by the time this slower stream is read.
    }

    [Fact]
    public async Task AlignedFreshDeviceFieldsShareEachOriginalSnapshotQuery()
    {
        await using var fixture = new DeviceFixture();
        Assert.Equal(DeviceTopologySampleCompletion.Sampled,
            await fixture.Provider.SampleNowAsync(CancellationToken.None));
        var fields = fixture.Backend.Catalog.Fields;
        var manufacturer = fields.Single(static capability => capability.Field.FieldName == "machine_all_manufacturer").Field;
        var model = fields.Single(static capability => capability.Field.FieldName == "machine_all_model_declared").Field;
        var readsBeforeWorker = fixture.Topology.ReadCount;
        var alignedFresh = new FieldSubscriptionOptions(FieldSubscriptionSamplingMode.FreshOnTrigger,
            AlignSameItemPhase: true);

        using var timeout = new CancellationTokenSource(Deadline);
        await using var manufacturerStream = fixture.Provider.SubscribeFieldsAsync("aligned-maker", [manufacturer],
            TimeSpan.FromMilliseconds(400), alignedFresh, fixture.Backend, DeviceTopologyFieldScope.Snapshot,
            timeout.Token).GetAsyncEnumerator(timeout.Token);
        await using var modelStream = fixture.Provider.SubscribeFieldsAsync("aligned-model", [model],
            TimeSpan.FromMilliseconds(400), alignedFresh, fixture.Backend, DeviceTopologyFieldScope.Snapshot,
            timeout.Token).GetAsyncEnumerator(timeout.Token);

        var firstManufacturer = manufacturerStream.MoveNextAsync().AsTask();
        var firstModel = modelStream.MoveNextAsync().AsTask();
        await fixture.Provider.StartAsync(timeout.Token);
        Assert.True(await firstManufacturer.WaitAsync(Deadline));
        Assert.True(await firstModel.WaitAsync(Deadline));
        var makerOne = Assert.Single(manufacturerStream.Current);
        var modelOne = Assert.Single(modelStream.Current);
        Assert.Equal(FieldObservationStatus.Current, makerOne.Status);
        Assert.Equal(FieldObservationStatus.Current, modelOne.Status);
        Assert.Equal(makerOne.ObservedAt, modelOne.ObservedAt);
        Assert.Equal(Suffix(makerOne.Value!.Text!, "maker-"), Suffix(modelOne.Value!.Text!, "model-"));

        var nextManufacturer = manufacturerStream.MoveNextAsync().AsTask();
        var nextModel = modelStream.MoveNextAsync().AsTask();
        Assert.True(await nextManufacturer.WaitAsync(Deadline));
        Assert.True(await nextModel.WaitAsync(Deadline));
        var makerTwo = Assert.Single(manufacturerStream.Current);
        var modelTwo = Assert.Single(modelStream.Current);

        Assert.True(fixture.Topology.ReadCount > readsBeforeWorker,
            "Fresh triggers must execute the original recorded topology source.");
        Assert.Equal(makerTwo.ObservedAt, modelTwo.ObservedAt);
        Assert.Equal(Suffix(makerTwo.Value!.Text!, "maker-"), Suffix(modelTwo.Value!.Text!, "model-"));
        Assert.NotNull(makerOne.ObservedAt);
        Assert.NotNull(makerTwo.ObservedAt);
        Assert.True(makerOne.ObservedAt.Value < makerTwo.ObservedAt.Value);
    }

    [Fact]
    public async Task FreshOsFieldExcludedByTheRecordedComponentScopeIsUnavailableWithoutReusingCurrent()
    {
        var componentReader = new RecordedComponentReader();
        using var operatingSystem = new WindowsOperatingSystemInformationOwner(
            new WindowsOperatingSystemComponentInformationScope(WindowsOperatingSystemInformationFields.ComponentPackages),
            componentReader, TimeSpan.FromSeconds(1));
        await using var fixture = new DeviceFixture(operatingSystemInventory: operatingSystem);

        var observedAt = new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
        var definition = DeviceInformationFieldRegistry.OsAllFeatureStateActual;
        var field = new FieldReference("windows-os-component|feature:Feature.One", definition.Name);
        var recordedCurrent = new DeviceInformationSnapshot(observedAt,
        [
            new(field.DeviceIdentity, "os_feature",
            [
                new(definition, JsonSerializer.SerializeToElement(1U)) { ObservedAt = observedAt }
            ])
        ]);
        var backend = new InformationReadWriteBackend(() => recordedCurrent,
            _ => ValueTask.FromResult(recordedCurrent), static _ => new(true, null, []));

        using var timeout = new CancellationTokenSource(Deadline);
        await using var stream = fixture.Provider.SubscribeFieldsAsync("os-excluded-feature", [field],
            TimeSpan.FromMilliseconds(200), new(FieldSubscriptionSamplingMode.FreshOnTrigger), backend,
            DeviceTopologyFieldScope.OperatingSystem, timeout.Token).GetAsyncEnumerator(timeout.Token);
        var first = stream.MoveNextAsync().AsTask();
        await fixture.Provider.StartAsync(timeout.Token);

        Assert.True(await first.WaitAsync(Deadline));
        var reading = Assert.Single(stream.Current);
        Assert.Equal(field, reading.Field);
        Assert.Equal(FieldObservationStatus.Unavailable, reading.Status);
        Assert.Null(reading.Value);
        Assert.Null(reading.ObservedAt);
        Assert.Empty(componentReader.Calls);
        Assert.Equal(0, fixture.Topology.ReadCount);
    }

    [Fact]
    public async Task StopDiscardsAnInFlightDeviceCaptureAndTheHeldStreamResumesOnRestart()
    {
        var topology = new BlockingTopologyReader();
        await using var fixture = new DeviceFixture(topology);
        Assert.Equal(DeviceTopologySampleCompletion.Sampled,
            await fixture.Provider.SampleNowAsync(CancellationToken.None));
        var committedBeforeStop = fixture.Provider.ReadState().Snapshot;
        var field = fixture.Backend.Catalog.Fields.Single(static capability =>
            capability.Field.FieldName == "machine_all_manufacturer").Field;

        using var timeout = new CancellationTokenSource(Deadline);
        await using var stream = fixture.Provider.SubscribeFieldsAsync("device-stop-in-flight", [field],
            TimeSpan.FromMilliseconds(200), new(FieldSubscriptionSamplingMode.FreshOnTrigger), fixture.Backend,
            DeviceTopologyFieldScope.Snapshot, timeout.Token).GetAsyncEnumerator(timeout.Token);
        var pendingRound = stream.MoveNextAsync().AsTask();
        await fixture.Provider.StartAsync(timeout.Token);
        await topology.BlockedReadEntered.Task.WaitAsync(Deadline);

        var stop = fixture.Provider.StopAsync(CancellationToken.None);
        Assert.False(stop.IsCompleted, "Stop waits for the synchronous recorded source call to drain.");
        Assert.False(pendingRound.IsCompleted);
        topology.ReleaseBlockedRead.TrySetResult();
        await stop.WaitAsync(Deadline);

        Assert.Same(committedBeforeStop, fixture.Provider.ReadState().Snapshot);
        Assert.Equal(2, topology.ReadCount);
        Assert.False(pendingRound.IsCompleted);
        Assert.Equal((1u, 1u), fixture.ReadDemand());
        await fixture.Provider.StartAsync(CancellationToken.None);
        Assert.True(await pendingRound.WaitAsync(Deadline));
        Assert.Equal(FieldObservationStatus.Current, Assert.Single(stream.Current).Status);
        Assert.NotEqual(committedBeforeStop!.CapturedAt, Assert.Single(stream.Current).ObservedAt);
    }

    [Fact]
    public async Task PassiveMftCurrentUpdatesPeriodicallyWhileFreshRemainsUnsupportedWithoutStartingTopologyReads()
    {
        var store = new DiskUsageTreeStore();
        var timestamp = new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
        store.Replace(MftResult(9, timestamp));
        var field = store.Backend.Catalog.Fields.Single(static capability =>
            capability.Field.FieldName == "directory_all_logical_size_aggregated").Field;
        var topology = new RecordedTopologyReader();
        await using var fixture = new DeviceFixture(topology);

        using var timeout = new CancellationTokenSource(Deadline);
        await using var periodic = fixture.Provider.SubscribeFieldsAsync("mft-current", [field],
            TimeSpan.FromMilliseconds(1000), FieldSubscriptionOptions.Default, store.Backend,
            DeviceTopologyFieldScope.PassiveCurrent, timeout.Token).GetAsyncEnumerator(timeout.Token);
        var firstPeriodic = periodic.MoveNextAsync().AsTask();
        await fixture.Provider.StartAsync(timeout.Token);
        Assert.True(await firstPeriodic.WaitAsync(Deadline));
        var first = Assert.Single(periodic.Current);
        Assert.Equal(MetricInteger.Parse("9"), first.Value?.Integer);
        Assert.Equal(FieldObservationStatus.Current, first.Status);

        var nextPeriodic = periodic.MoveNextAsync().AsTask();
        var replacement = MftResult(21, timestamp.AddSeconds(1));
        store.Replace(replacement);
        Assert.True(await nextPeriodic.WaitAsync(Deadline));
        var updated = Assert.Single(periodic.Current);
        Assert.Equal(MetricInteger.Parse("21"), updated.Value?.Integer);
        Assert.NotNull(first.ObservedAt);
        Assert.NotNull(updated.ObservedAt);
        Assert.True(first.ObservedAt.Value < updated.ObservedAt.Value);

        await using var fresh = fixture.Provider.SubscribeFieldsAsync("mft-fresh", [field],
            TimeSpan.FromMilliseconds(1000), new(FieldSubscriptionSamplingMode.FreshOnTrigger), store.Backend,
            DeviceTopologyFieldScope.PassiveCurrent, timeout.Token).GetAsyncEnumerator(timeout.Token);
        Assert.True(await fresh.MoveNextAsync().AsTask().WaitAsync(Deadline));
        var unsupported = Assert.Single(fresh.Current);
        Assert.Equal(FieldObservationStatus.Unsupported, unsupported.Status);
        Assert.Null(unsupported.Value);
        Assert.Null(unsupported.ObservedAt);
        using (var reading = store.TryAcquire())
        {
            Assert.NotNull(reading);
            Assert.Same(replacement.Tree, reading.Result.Tree);
            Assert.Same(replacement.Summary, reading.Result.Summary);
        }
        Assert.Equal(0, topology.ReadCount);
    }

    [Fact]
    public async Task DisposeBeforeStartClearsTypedInformationDemandsFromTheOriginalTracker()
    {
        var softwareInventory = new WindowsInstalledSoftwareInventory(_ => []);
        await using var fixture = new DeviceFixture(softwareInventory: softwareInventory);
        var interval = TimeSpan.FromSeconds(1);
        var snapshot = fixture.Provider.AcquireInformationSubscription("prestart-snapshot", interval);
        var software = fixture.Provider.AcquireSoftwareInformationSubscription("prestart-software", interval);
        var services = fixture.Provider.AcquireServiceInformationSubscription("prestart-services", interval, includeConfiguration: false);
        var operatingSystem = fixture.Provider.AcquireOperatingSystemInformationSubscription("prestart-os", interval,
            fields: WindowsOperatingSystemInformationFields.None);

        Assert.Equal((4u, 4u), fixture.ReadDemand());
        Assert.Equal(0, fixture.Topology.ReadCount);

        fixture.Provider.Dispose();

        Assert.Equal((0u, 0u), fixture.ReadDemand());
        snapshot.Dispose();
        software.Dispose();
        services.Dispose();
        operatingSystem.Dispose();
    }

    [Fact]
    public async Task StopRetainsHeldTypedSubscriptionsAndAcceptsNewSourcesForRestart()
    {
        var softwareInventory = new WindowsInstalledSoftwareInventory(_ => []);
        using var serviceInventory = new WindowsServiceInformationOwner(WindowsServiceScope.Win32,
            (_, _) => new(true, []));
        await using var fixture = new DeviceFixture(softwareInventory: softwareInventory, serviceInventory: serviceInventory);
        var interval = TimeSpan.FromSeconds(1);
        var snapshot = fixture.Provider.AcquireInformationSubscription("stop-snapshot", interval);
        var software = fixture.Provider.AcquireSoftwareInformationSubscription("stop-software", interval);
        var services = fixture.Provider.AcquireServiceInformationSubscription("stop-services", interval, includeConfiguration: false);
        var operatingSystem = fixture.Provider.AcquireOperatingSystemInformationSubscription("stop-os", interval,
            fields: WindowsOperatingSystemInformationFields.None);
        Assert.Equal((4u, 4u), fixture.ReadDemand());

        await fixture.Provider.StartAsync(CancellationToken.None);
        await fixture.Provider.StopAsync(CancellationToken.None);

        Assert.Equal((4u, 4u), fixture.ReadDemand());
        using var lateSnapshot = fixture.Provider.AcquireInformationSubscription("late-snapshot", interval);
        using var lateSoftware = fixture.Provider.AcquireSoftwareInformationSubscription("late-software", interval);
        using var lateServices = fixture.Provider.AcquireServiceInformationSubscription("late-services", interval, includeConfiguration: false);
        using var lateOperatingSystem = fixture.Provider.AcquireOperatingSystemInformationSubscription(
            "late-os", interval, fields: WindowsOperatingSystemInformationFields.None);
        Assert.Equal((8u, 8u), fixture.ReadDemand());

        snapshot.Dispose();
        software.Dispose();
        services.Dispose();
        operatingSystem.Dispose();
        Assert.Equal((4u, 4u), fixture.ReadDemand());
        await fixture.Provider.StartAsync(CancellationToken.None);
        Assert.Equal((4u, 4u), fixture.ReadDemand());
        await fixture.Provider.StopAsync(CancellationToken.None);
        lateSnapshot.Dispose();
        lateSoftware.Dispose();
        lateServices.Dispose();
        lateOperatingSystem.Dispose();
        Assert.Equal((0u, 0u), fixture.ReadDemand());
    }

    private static string Suffix(string value, string prefix)
    {
        Assert.StartsWith(prefix, value);
        return value[prefix.Length..];
    }

    private static DiskUsageScanResult MftResult(long bytes, DateTimeOffset time)
    {
        var builder = new DiskUsageTreeBuilder();
        var root = builder.Add(-1, "C:\\", true, 0, 0, new(7, 5, 1));
        builder.Add(root, "recorded", false, bytes, 0, new(7, 10, 1));
        return new(builder.Build(), new(DiskUsageScanScopes.Volume, DiskUsageScanModes.Fast,
            "C:", ["C:\\"], DiskUsageScanKinds.MasterFileTable, [], time, 0, 0, 0, 1, 1, 0));
    }

    private sealed class DeviceFixture : IAsyncDisposable
    {
        private readonly HostManagerSamplingSubscriptionTestFixture sampling = new();

        internal DeviceFixture(RecordedTopologyReader? topology = null,
            WindowsOperatingSystemInformationOwner? operatingSystemInventory = null,
            WindowsInstalledSoftwareInventory? softwareInventory = null,
            WindowsServiceInformationOwner? serviceInventory = null)
        {
            Topology = topology ?? new RecordedTopologyReader();
            Provider = new(Topology, new RecordedStore(), new DeviceTopologySemanticComparer(), sampling.Owner,
                NullLogger<DeviceTopologySnapshotProvider>.Instance, new RecordedAcpi(),
                softwareInventory: softwareInventory, serviceInventory: serviceInventory,
                operatingSystemInventory: operatingSystemInventory);
            Backend = new(() => Provider.ReadState().InformationFields, Provider.CaptureInformationAsync,
                static _ => new(true, null, []));
        }

        internal RecordedTopologyReader Topology { get; }
        internal DeviceTopologySnapshotProvider Provider { get; }
        internal InformationReadWriteBackend Backend { get; }

        internal (uint ActiveSources, uint PersistentSources) ReadDemand() => sampling.Owner.Execute(2, session =>
        {
            var header = new NativeSamplingSubscriptionSnapshotHeader
            {
                AbiVersion = NativeSamplingSubscriptionAbi.Version,
                StructSize = checked((uint)Marshal.SizeOf<NativeSamplingSubscriptionSnapshotHeader>())
            };
            var sources = new NativeSamplingSubscriptionSourceView[session.Capacity.SnapshotSourceCapacity];
            var items = new NativeSamplingSubscriptionItemState[session.Capacity.SnapshotItemCapacity];
            Assert.Equal(NativeSamplingSubscriptionStatus.Ok, session.Snapshot(ref header, sources, items));
            return (header.ActiveSourceCount, header.PersistentSourceCount);
        });

        public async ValueTask DisposeAsync()
        {
            await Provider.StopAsync(CancellationToken.None);
            Provider.Dispose();
            sampling.Dispose();
        }
    }

    private class RecordedTopologyReader : IDeviceTopologyReader
    {
        protected int readCount;
        internal int ReadCount => Volatile.Read(ref readCount);

        public virtual DeviceTopologySnapshot ReadSnapshot()
        {
            var sequence = Interlocked.Increment(ref readCount);
            return Snapshot(sequence);
        }
    }

    private sealed class BlockingTopologyReader : RecordedTopologyReader
    {
        internal TaskCompletionSource BlockedReadEntered { get; } = NewSignal();
        internal TaskCompletionSource ReleaseBlockedRead { get; } = NewSignal();

        public override DeviceTopologySnapshot ReadSnapshot()
        {
            var sequence = Interlocked.Increment(ref readCount);
            if (sequence == 2)
            {
                BlockedReadEntered.TrySetResult();
                if (!ReleaseBlockedRead.Task.Wait(Deadline))
                    throw new TimeoutException("The recorded in-flight topology read was not released.");
            }
            return Snapshot(sequence);
        }
    }

    private sealed class RecordedAcpi : IAcpiFirmwareTableReader
    {
        public IReadOnlyList<AcpiFirmwareTable>? ReadTables() => throw new InvalidOperationException("Unexpected ACPI read.");
    }

    private sealed class RecordedStore : IDeviceTopologySnapshotStore
    {
        public Task<DeviceTopologyPersistedSnapshot?> LoadAsync(CancellationToken cancellationToken)
            => Task.FromResult<DeviceTopologyPersistedSnapshot?>(null);

        public Task SaveAsync(DeviceTopologyPersistedSnapshot snapshot, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class RecordedComponentReader : IWindowsOperatingSystemComponentInformationReader
    {
        private readonly List<WindowsOperatingSystemInformationFields> calls = [];
        internal IReadOnlyList<WindowsOperatingSystemInformationFields> Calls => calls.ToArray();

        public WindowsOperatingSystemComponentInformationReadResult Read(
            WindowsOperatingSystemComponentInformationScope scope,
            WindowsOperatingSystemInformationFields selected,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            calls.Add(selected);
            return new(null, null, null);
        }
    }

    private static DeviceTopologySnapshot Snapshot(int sequence)
    {
        var capturedAt = new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero)
            .AddMilliseconds(sequence * 250L);
        return new(capturedAt, new DeviceTopologySystemIdentity(
            $"maker-{sequence}", $"model-{sequence}", "recorded-brand", "REC", "1.0", "recorded-board-vendor", "recorded-board"),
            [], []);
    }

    private static TaskCompletionSource NewSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
