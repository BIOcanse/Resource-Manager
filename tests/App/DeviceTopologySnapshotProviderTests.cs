using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Application.DeviceTopology;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Domain.Messages;
using ResourceManager.App.Infrastructure.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;
using Resource_Manager_APP.Tests;

namespace ResourceManager.App.Tests;

public sealed class DeviceTopologySnapshotProviderTests
{
    [Fact]
    public async Task MachineIdentityUsesExistingStateSubscriptionAndOnlyChangesStructuralGenerationWhenFactsChange()
    {
        var first = CreateSnapshot(DateTimeOffset.UtcNow) with {
            MachineIdentity = DeviceTopologyMachineIdentityTests.Resolve([10], true)
        };
        var changed = first with { MachineIdentity = DeviceTopologyMachineIdentityTests.Resolve([7], false, ["Intel Xeon W-2495X"]) };
        var store = new FakeStore();
        using var fixture = CreateProvider(new FakeReader(first, first with { CapturedAt = first.CapturedAt.AddSeconds(1) }, changed), store);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var subscription = fixture.Provider.SubscribeAsync("identity-test", TimeSpan.FromSeconds(1), cancellation.Token).GetAsyncEnumerator();
        Assert.True(await subscription.MoveNextAsync());
        for (var capture = 0; capture < 3; capture++)
        {
            var next = subscription.MoveNextAsync().AsTask();
            await fixture.Provider.SampleNowAsync(cancellation.Token);
            Assert.True(await next);
            Assert.Equal(capture == 2 ? "workstation" : "laptop", subscription.Current.Snapshot!.MachineIdentity.FormFactor);
            Assert.Equal(capture == 2 ? 2 : 1, subscription.Current.ContentGeneration);
        }
        Assert.Equal(2, store.SaveCount);
    }

    [Fact]
    public async Task SharedSmbiosFailureClearsSlotBiosDateAndDimmWhileKeepingDeclarationsThenRecovers()
    {
        var first = CreateSnapshot(DateTimeOffset.UtcNow) with {
            FirmwareDeclarations = SmbiosFirmwareInformationCollectorTests.Declarations(),
            MemoryModules = [DeviceInformationTestData.Module("dimm")]
        };
        var failed = first with { CapturedAt = first.CapturedAt.AddSeconds(1), FirmwareDeclarations = null, MemoryModules = [] };
        var empty = first with { CapturedAt = first.CapturedAt.AddSeconds(2), FirmwareDeclarations = SmbiosFirmwareDeclarations.Empty, MemoryModules = [] };
        using var fixture = CreateProvider(new FakeReader(first, failed, empty, first with { CapturedAt = first.CapturedAt.AddSeconds(3) }), new FakeStore());
        await fixture.Provider.SampleNowAsync(CancellationToken.None);
        Assert.Equal("2026-09-30", DeviceInformationTestData.Field(fixture.Provider.ReadState().InformationFields, "bios", "bios_all_release_date").Value!.Value.GetString());
        await fixture.Provider.SampleNowAsync(CancellationToken.None);
        var failure = fixture.Provider.ReadState().InformationFields;
        Assert.Null(DeviceInformationTestData.Field(failure, "bios", "bios_all_release_date").Value);
        Assert.All(failure.Devices.Where(d => d.ObjectType is "motherboard_slot" or "memory_dimm" or "cpu_socket").SelectMany(d => d.Fields), field => Assert.Null(field.Value));
        Assert.NotNull(DeviceInformationTestData.Field(failure, "machine", "machine_all_manufacturer").Value);
        await fixture.Provider.SampleNowAsync(CancellationToken.None);
        Assert.DoesNotContain(fixture.Provider.ReadState().InformationFields.Devices, d => d.ObjectType is "motherboard_slot" or "memory_dimm" or "cpu_socket");
        await fixture.Provider.SampleNowAsync(CancellationToken.None);
        Assert.Equal("SLOT", DeviceInformationTestData.Field(fixture.Provider.ReadState().InformationFields, "smbios-slot|1234", "motherboard_all_slot_designation").Value!.Value.GetString());
        Assert.Equal("CPU0", DeviceInformationTestData.Field(fixture.Provider.ReadState().InformationFields, "smbios-cpu-socket|4567", "cpu_all_socket_designation").Value!.Value.GetString());
    }

    [Fact]
    public void SlotOrderIsNotSemanticButDeclarationChangesAre()
    {
        var firmware = SmbiosFirmwareInformationCollectorTests.Declarations();
        firmware = firmware with { Slots = [firmware.Slots[0], firmware.Slots[0] with { DeviceIdentity = "smbios-slot|2222" }] };
        var first = CreateSnapshot(DateTimeOffset.UtcNow) with { FirmwareDeclarations = firmware };
        var comparer = new DeviceTopologySemanticComparer();
        Assert.Equal(comparer.ComputeHash(first), comparer.ComputeHash(first with { FirmwareDeclarations = firmware with { Slots = firmware.Slots.Reverse().ToArray() } }));
        Assert.NotEqual(comparer.ComputeHash(first), comparer.ComputeHash(first with { FirmwareDeclarations = firmware with { BiosReleaseDate = null } }));
    }

    [Fact]
    public async Task OptionalStorageFailureKeepsDeclarationsClearsCurrentAndRecoversAfterRemoval()
    {
        var first = CreateSnapshot(DateTimeOffset.UtcNow) with { StorageInformation = StorageIdentifyInformationCollectorTests.Declarations() };
        var failed = first with { CapturedAt = first.CapturedAt.AddSeconds(1), StorageInformation = null };
        var empty = first with { CapturedAt = first.CapturedAt.AddSeconds(2), StorageInformation = [] };
        using var fixture = CreateProvider(new FakeReader(first, failed, empty, first with { CapturedAt = first.CapturedAt.AddSeconds(3) }), new FakeStore());
        await fixture.Provider.SampleNowAsync(CancellationToken.None);
        Assert.Equal(2, fixture.Provider.ReadState().InformationFields.Devices.Count(d => d.ObjectType == "physical_disk"));
        await fixture.Provider.SampleNowAsync(CancellationToken.None);
        var failure = fixture.Provider.ReadState();
        Assert.Equal(DeviceTopologySnapshotStatus.Ready, failure.State);
        Assert.All(failure.InformationFields.Devices.Where(d => d.ObjectType == "physical_disk").SelectMany(d => d.Fields), field => Assert.Null(field.Value));
        Assert.NotNull(failure.InformationFields.Devices.Single(d => d.DeviceIdentity == "machine").Fields.First().Value);
        await fixture.Provider.SampleNowAsync(CancellationToken.None);
        Assert.DoesNotContain(fixture.Provider.ReadState().InformationFields.Devices, d => d.ObjectType == "physical_disk");
        await fixture.Provider.SampleNowAsync(CancellationToken.None);
        Assert.Equal("NVMe Model", DeviceInformationTestData.Field(fixture.Provider.ReadState().InformationFields, "nvme", "storage_all_nvme_subsystem_model_declared").Value!.Value.GetString());
    }

    [Fact]
    public void StorageOrderIsNotSemanticAndProtocolOrRawChangesAreSemantic()
    {
        var first = CreateSnapshot(DateTimeOffset.UtcNow) with { StorageInformation = StorageIdentifyInformationCollectorTests.Declarations() };
        var comparer = new DeviceTopologySemanticComparer();
        Assert.Equal(comparer.ComputeHash(first), comparer.ComputeHash(first with { StorageInformation = first.StorageInformation!.Reverse().ToArray() }));
        Assert.NotEqual(comparer.ComputeHash(first), comparer.ComputeHash(first with { StorageInformation = null }));
        Assert.NotEqual(comparer.ComputeHash(first), comparer.ComputeHash(first with { StorageInformation = first.StorageInformation!.Select(d => d with { Ata = null, Nvme = null }).ToArray() }));
    }

    [Fact]
    public async Task OptionalBatteryFailureClearsValuesKeepsIdentityAndSuccessfulEmptyRemovesIt()
    {
        var first = CreateSnapshot(DateTimeOffset.UtcNow) with
        { Batteries = [new("battery-a", "manufacturer", "serial", "X123")] };
        var failed = first with { CapturedAt = first.CapturedAt.AddSeconds(1), Batteries = null };
        var empty = first with { CapturedAt = first.CapturedAt.AddSeconds(2), Batteries = [] };
        var restored = first with { CapturedAt = first.CapturedAt.AddSeconds(3) };
        using var fixture = CreateProvider(new FakeReader(first, failed, empty, restored), new FakeStore());
        await fixture.Provider.SampleNowAsync(CancellationToken.None);
        Assert.Equal("manufacturer", fixture.Provider.ReadState().InformationFields.Devices.Single(d => d.DeviceIdentity == "battery-a")
            .Fields.Single(f => f.Definition.Name == "battery_all_manufacturer_declared").Value!.Value.GetString());
        await fixture.Provider.SampleNowAsync(CancellationToken.None);
        var failure = fixture.Provider.ReadState();
        Assert.Equal(DeviceTopologySnapshotStatus.Ready, failure.State);
        Assert.All(failure.InformationFields.Devices.Single(d => d.DeviceIdentity == "battery-a").Fields, f => Assert.Null(f.Value));
        Assert.NotNull(failure.InformationFields.Devices.Single(d => d.DeviceIdentity == "machine")
            .Fields.Single(f => f.Definition.Name == "machine_all_manufacturer").Value);
        await fixture.Provider.SampleNowAsync(CancellationToken.None);
        Assert.DoesNotContain(fixture.Provider.ReadState().InformationFields.Devices, d => d.ObjectType == "battery_device");
        await fixture.Provider.SampleNowAsync(CancellationToken.None);
        Assert.Equal("serial", fixture.Provider.ReadState().InformationFields.Devices.Single(d => d.DeviceIdentity == "battery-a")
            .Fields.Single(f => f.Definition.Name == "battery_all_serial_declared").Value!.Value.GetString());
    }

    [Fact]
    public void BatteryDeclarationOrderingDoesNotChangeSemanticContent()
    {
        var first = CreateSnapshot(DateTimeOffset.UtcNow) with
        { Batteries = [new("battery-b", "B", null, "LION"), new("battery-a", "A", null, "NiMH")] };
        var second = first with { CapturedAt = first.CapturedAt.AddSeconds(1), Batteries = first.Batteries!.Reverse().ToArray() };
        var comparer = new DeviceTopologySemanticComparer();
        Assert.Equal(comparer.ComputeHash(first), comparer.ComputeHash(second));
        Assert.NotEqual(comparer.ComputeHash(first), comparer.ComputeHash(first with { Batteries = null }));
    }

    [Fact]
    public async Task ReadStateDoesNotTrackDemandOrReadTopology()
    {
        var reader = new FakeReader(CreateSnapshot(DateTimeOffset.UtcNow));
        using var fixture = CreateProvider(reader, new FakeStore());
        var provider = fixture.Provider;

        await provider.StartAsync(CancellationToken.None);
        var state = provider.ReadState();
        await Task.Delay(50);
        await provider.StopAsync(CancellationToken.None);

        Assert.Equal(DeviceTopologySnapshotStatus.Warming, state.State);
        Assert.Null(state.Snapshot);
        Assert.Equal(0, reader.ReadCount);
    }

    [Fact]
    public async Task SemanticMatchKeepsPublishedSnapshotAndGeneration()
    {
        var first = CreateSnapshot(DateTimeOffset.UtcNow);
        var second = CreateSnapshot(first.CapturedAt.AddSeconds(1));
        var reader = new FakeReader(first, second);
        var store = new FakeStore();
        using var fixture = CreateProvider(reader, store);
        var provider = fixture.Provider;

        await provider.SampleNowAsync(CancellationToken.None);
        var initial = provider.ReadState();
        await provider.SampleNowAsync(CancellationToken.None);
        var refreshed = provider.ReadState();

        Assert.Equal(DeviceTopologySnapshotStatus.Ready, refreshed.State);
        Assert.Equal(1, refreshed.ContentGeneration);
        Assert.Same(initial.Snapshot, refreshed.Snapshot);
        Assert.Equal(1, store.SaveCount);
        Assert.Equal(2, reader.ReadCount);
        Assert.Equal(second.CapturedAt, refreshed.InformationFields.ObservedAt);
        Assert.NotEqual(initial.InformationFields.ObservedAt, refreshed.InformationFields.ObservedAt);
    }

    [Fact]
    public async Task FailedRefreshKeepsLastGoodSnapshot()
    {
        var snapshot = CreateSnapshot(DateTimeOffset.UtcNow);
        var reader = new FakeReader(snapshot, new InvalidOperationException("test failure"));
        using var fixture = CreateProvider(reader, new FakeStore());
        var provider = fixture.Provider;

        var firstCompletion = await provider.SampleNowAsync(CancellationToken.None);
        var lastGood = provider.ReadState().Snapshot;
        var failedCompletion = await provider.SampleNowAsync(CancellationToken.None);
        var failed = provider.ReadState();

        Assert.Equal(DeviceTopologySampleCompletion.Sampled, firstCompletion);
        Assert.Equal(DeviceTopologySampleCompletion.Failed, failedCompletion);
        Assert.Equal(DeviceTopologySnapshotStatus.Failed, failed.State);
        Assert.Same(lastGood, failed.Snapshot);
        Assert.Equal("device-topology-read-failed", failed.FailureCode);
        Assert.Empty(failed.AttemptDiagnostics);
        Assert.Null(failed.InformationFields.ObservedAt);
        Assert.All(failed.InformationFields.Devices.SelectMany(device => device.Fields), field => Assert.Null(field.Value));
    }

    [Fact]
    public async Task IncompleteSourcePublishesOrderedDiagnosticsAndKeepsLastGood()
    {
        var snapshot = CreateSnapshot(DateTimeOffset.UtcNow);
        var diagnostics = new[]
        {
            new DeviceTopologySourceDiagnostic(
                "display-coordinator",
                DeviceTopologySourceDiagnosticStatus.RequiredIncomplete,
                "device-topology-display-coordinator-incomplete",
                BackendMessage.Create(
                    BackendMessageDomains.DeviceTopology,
                    BackendMessageCodes.DeviceTopology.DisplayCoordinatorNotReady,
                    "Warming")),
            new DeviceTopologySourceDiagnostic(
                "storage-capabilities",
                DeviceTopologySourceDiagnosticStatus.RequiredIncomplete,
                "device-topology-storage-capabilities-incomplete",
                BackendMessage.Create(
                    BackendMessageDomains.DeviceTopology,
                    BackendMessageCodes.DeviceTopology.StorageCapabilitiesIncomplete,
                    "fixture"))
        };
        var reader = new FakeReader(
            snapshot,
            new DeviceTopologyIncompleteSourcesException(diagnostics));
        var store = new FakeStore();
        using var fixture = CreateProvider(reader, store);
        var provider = fixture.Provider;

        await provider.SampleNowAsync(CancellationToken.None);
        var lastGood = provider.ReadState();
        await provider.SampleNowAsync(CancellationToken.None);
        var failed = provider.ReadState();

        Assert.Same(lastGood.Snapshot, failed.Snapshot);
        Assert.Equal(lastGood.ContentGeneration, failed.ContentGeneration);
        Assert.Equal(diagnostics, failed.AttemptDiagnostics);
        Assert.Equal(diagnostics[0].Code, failed.FailureCode);
        Assert.Equal(1, store.SaveCount);
    }

    [Fact]
    public async Task StartLoadsPersistedSnapshotBeforeServingRequests()
    {
        var snapshot = CreateSnapshot(DateTimeOffset.UtcNow.AddMinutes(-1)) with
        { Batteries = [new("battery-persisted", "historical manufacturer", "serial", "LION")],
            StorageInformation = StorageIdentifyInformationCollectorTests.Declarations() };
        var semanticComparer = new DeviceTopologySemanticComparer();
        var store = new FakeStore(new DeviceTopologyPersistedSnapshot(
            DeviceTopologySnapshotState.CurrentSchemaVersion,
            "test",
            7,
            semanticComparer.ComputeHash(snapshot),
            snapshot));
        var reader = new FakeReader(snapshot);
        using var fixture = CreateProvider(reader, store, semanticComparer);
        var provider = fixture.Provider;

        await provider.StartAsync(CancellationToken.None);
        var state = provider.ReadState();
        await using (var subscription = provider.SubscribeAsync("test.persisted-first-frame", TimeSpan.FromHours(1), CancellationToken.None).GetAsyncEnumerator())
        {
            Assert.True(await subscription.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1)));
            Assert.Same(state, subscription.Current);
            Assert.Equal(0, reader.ReadCount);
        }
        await provider.StopAsync(CancellationToken.None);

        Assert.Equal(DeviceTopologySnapshotStatus.Ready, state.State);
        Assert.Equal(DeviceTopologySnapshotSource.Persisted, state.Source);
        Assert.Equal(7, state.ContentGeneration);
        Assert.Same(snapshot, state.Snapshot);
        Assert.Null(state.InformationFields.ObservedAt);
        Assert.NotEmpty(state.InformationFields.Devices);
        Assert.Equal(3, state.InformationFields.Devices.Single(d => d.DeviceIdentity == "battery-persisted").Fields.Count);
        Assert.Equal(13, state.InformationFields.Devices.Single(d => d.DeviceIdentity == "nvme").Fields.Count);
        Assert.All(state.InformationFields.Devices.SelectMany(device => device.Fields), field => Assert.Null(field.Value));
    }

    [Fact]
    public async Task SubscriptionReturnsCurrentBeforeWaitingForCompletedSampleAndReleasesDemand()
    {
        var snapshot = CreateSnapshot(DateTimeOffset.UtcNow) with { NpuInformation = [] };
        var reader = new FakeReader(snapshot);
        using var fixture = CreateProvider(reader, new FakeStore());
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using (var subscription = fixture.Provider.SubscribeAsync(
            "test.callback", TimeSpan.FromSeconds(1), cancellation.Token)
            .GetAsyncEnumerator())
        {
            Assert.True(await subscription.MoveNextAsync());
            Assert.Same(fixture.Provider.ReadState(), subscription.Current);
            var next = subscription.MoveNextAsync().AsTask();
            Assert.False(next.IsCompleted);
            Assert.Equal(0, reader.ReadCount);

            await fixture.Provider.SampleNowAsync(cancellation.Token);

            Assert.True(await next);
            var published = subscription.Current.Snapshot!;
            Assert.Equal(new DeviceTopologySemanticComparer().ComputeHash(snapshot),
                new DeviceTopologySemanticComparer().ComputeHash(published));
            Assert.Same(fixture.Provider.ReadState(), subscription.Current);
            next = subscription.MoveNextAsync().AsTask();
            Assert.False(next.IsCompleted);
            await fixture.Provider.SampleNowAsync(cancellation.Token);
            Assert.True(await next);
            Assert.Same(published, subscription.Current.Snapshot);
            Assert.Equal(1, subscription.Current.ContentGeneration);
            next = subscription.MoveNextAsync().AsTask();
            Assert.False(next.IsCompleted);
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => next);
        }

        await fixture.Provider.StartAsync(CancellationToken.None);
        await Task.Delay(50);
        await fixture.Provider.StopAsync(CancellationToken.None);
        Assert.Equal(2, reader.ReadCount);
    }

    [Fact]
    public async Task SamplingDoesNotPublishAnIntermediateRefreshingState()
    {
        using var reader = new BlockingReader(CreateSnapshot(DateTimeOffset.UtcNow));
        using var fixture = CreateProvider(reader, new FakeStore());
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var subscription = fixture.Provider.SubscribeAsync(
            "test.atomic", TimeSpan.FromSeconds(1), cancellation.Token)
            .GetAsyncEnumerator();
        var previous = fixture.Provider.ReadState();
        Assert.True(await subscription.MoveNextAsync());
        Assert.Same(previous, subscription.Current);
        var next = subscription.MoveNextAsync().AsTask();
        var capture = Task.Run(() => fixture.Provider.SampleNowAsync(cancellation.Token));
        try
        {
            Assert.True(reader.Entered.Wait(TimeSpan.FromSeconds(2)));
            Assert.False(next.IsCompleted);
            Assert.Same(previous, fixture.Provider.ReadState());
        }
        finally
        {
            reader.Release.Set();
            await capture;
        }
        Assert.True(await next);
        Assert.Equal(DeviceTopologySnapshotStatus.Ready, subscription.Current.State);
    }

    [Fact]
    public async Task StopClearsSubscriptionsBeforeSamplingOwnerStops()
    {
        var snapshot = CreateSnapshot(DateTimeOffset.UtcNow);
        using var fixture = CreateProvider(
            new FakeReader(snapshot),
            new FakeStore());

        await fixture.Provider.StartAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        await using var subscription = fixture.Provider.SubscribeAsync(
            "test.shutdown-order", TimeSpan.FromSeconds(1), cancellation.Token)
            .GetAsyncEnumerator();
        Assert.True(await subscription.MoveNextAsync());
        var next = subscription.MoveNextAsync().AsTask();
        await fixture.Provider.StopAsync(CancellationToken.None);
        await cancellation.CancelAsync();
        try { _ = await next; }
        catch (OperationCanceledException) { }
        await subscription.DisposeAsync();
        await fixture.Sampling.Owner.StopAsync(CancellationToken.None);

        var error = Record.Exception(fixture.Provider.Dispose);

        Assert.Null(error);
    }

    [Fact]
    public async Task CancelDuringSynchronousReadAllowsCompletionAndStopsFollowingCaptures()
    {
        using var reader = new BlockingReader(CreateSnapshot(DateTimeOffset.UtcNow));
        var store = new FakeStore();
        using var fixture = CreateProvider(reader, store);
        await fixture.Provider.StartAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var subscription = fixture.Provider.SubscribeAsync("test.cancel-in-flight", TimeSpan.FromSeconds(1), cancellation.Token)
            .GetAsyncEnumerator();
        try
        {
            Assert.True(await subscription.MoveNextAsync());
            var next = subscription.MoveNextAsync().AsTask();
            Assert.True(reader.Entered.Wait(TimeSpan.FromSeconds(2)));
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => next);
            await subscription.DisposeAsync();
            reader.Release.Set();
            await store.Saved.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await Task.Delay(1300);
            Assert.Equal(1, reader.ReadCount);
            Assert.Equal(DeviceTopologySnapshotStatus.Ready, fixture.Provider.ReadState().State);
            Assert.Equal(1, fixture.Provider.ReadState().ContentGeneration);
            Assert.NotNull(fixture.Provider.ReadState().InformationFields.ObservedAt);
        }
        finally
        {
            reader.Release.Set();
            await fixture.Provider.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task MachineClassifierIsHeldByTheRunningBlockAndReleasedWhenItStops()
    {
        var owner = new DeviceTopologyMachineClassifierOwner();
        var reader = new ClassifyingReader(owner, CreateSnapshot(DateTimeOffset.UtcNow));
        using var fixture = CreateProvider(reader, new FakeStore(), machineClassifier: owner);
        Assert.False(owner.HoldsClassifier);
        await fixture.Provider.StartAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var subscription = fixture.Provider.SubscribeAsync("test.classifier-run", TimeSpan.FromSeconds(1), cancellation.Token)
            .GetAsyncEnumerator();
        try
        {
            Assert.True(await subscription.MoveNextAsync());
            await reader.Classified.Task.WaitAsync(cancellation.Token);
            // The read inside the run built the classifier and the run keeps it for its next reads.
            Assert.True(owner.HoldsClassifier);
        }
        finally
        {
            await cancellation.CancelAsync();
            await subscription.DisposeAsync();
            await fixture.Provider.StopAsync(CancellationToken.None);
        }
        // The stopped block holds no classifier.
        Assert.False(owner.HoldsClassifier);
    }

    [Fact]
    public async Task CanceledSampleBeforeGateEntryReturnsCanceledCompletion()
    {
        using var fixture = CreateProvider(
            new FakeReader(CreateSnapshot(DateTimeOffset.UtcNow)),
            new FakeStore());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var completion = await fixture.Provider.SampleNowAsync(cancellation.Token);

        Assert.Equal(DeviceTopologySampleCompletion.Canceled, completion);
    }

    private static ProviderFixture CreateProvider(
        IDeviceTopologyReader reader,
        IDeviceTopologySnapshotStore store,
        DeviceTopologySemanticComparer? semanticComparer = null,
        DeviceTopologyMachineClassifierOwner? machineClassifier = null)
    {
        var sampling = new HostManagerSamplingSubscriptionTestFixture();
        var provider = new DeviceTopologySnapshotProvider(
            reader,
            store,
            semanticComparer ?? new DeviceTopologySemanticComparer(),
            sampling.Owner,
            NullLogger<DeviceTopologySnapshotProvider>.Instance,
            new WindowsAcpiFirmwareTableReader(),
            machineClassifier: machineClassifier);
        return new ProviderFixture(sampling, provider);
    }

    private static DeviceTopologySnapshot CreateSnapshot(DateTimeOffset capturedAt)
    {
        return new DeviceTopologySnapshot(
            capturedAt,
            new DeviceTopologySystemIdentity(
                "Test Manufacturer",
                "Test Model",
                "Test Brand",
                "TEST",
                "1.0.0",
                "Test Board Vendor",
                "Test Board"),
            [],
            [BackendMessage.Create(BackendMessageDomains.DeviceTopology, BackendMessageCodes.DeviceTopology.EnumerationOnly)]);
    }

    private sealed class FakeReader(params object[] results) : IDeviceTopologyReader
    {
        private readonly Queue<object> queue = new(results);

        public int ReadCount { get; private set; }

        public DeviceTopologySnapshot ReadSnapshot()
        {
            ReadCount += 1;
            var result = queue.Count > 1 ? queue.Dequeue() : queue.Peek();
            return result switch
            {
                DeviceTopologySnapshot snapshot => snapshot,
                Exception exception => throw exception,
                _ => throw new InvalidOperationException("Unsupported fake result.")
            };
        }
    }

    // Classifies through the block's owner the way WindowsDeviceTopologyReader does.
    private sealed class ClassifyingReader(DeviceTopologyMachineClassifierOwner owner, DeviceTopologySnapshot snapshot)
        : IDeviceTopologyReader
    {
        private readonly DeviceTopologyMachineEvidence evidence = DeviceTopologyMachineIdentityTests.Resolve([10], true).Evidence;
        public TaskCompletionSource Classified { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public DeviceTopologySnapshot ReadSnapshot()
        {
            var identity = owner.Classify(evidence);
            Classified.TrySetResult();
            return snapshot with { MachineIdentity = identity };
        }
    }

    private sealed class BlockingReader(DeviceTopologySnapshot snapshot)
        : IDeviceTopologyReader, IDisposable
    {
        private int readCount;
        public int ReadCount => Volatile.Read(ref readCount);
        public ManualResetEventSlim Entered { get; } = new();
        public ManualResetEventSlim Release { get; } = new();

        public DeviceTopologySnapshot ReadSnapshot()
        {
            Interlocked.Increment(ref readCount);
            Entered.Set();
            if (!Release.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("The test did not release the topology capture.");
            }
            return snapshot;
        }

        public void Dispose()
        {
            Entered.Dispose();
            Release.Dispose();
        }
    }

    private sealed record ProviderFixture(
        HostManagerSamplingSubscriptionTestFixture Sampling,
        DeviceTopologySnapshotProvider Provider) : IDisposable
    {
        public void Dispose()
        {
            Provider.Dispose();
            Sampling.Dispose();
        }
    }

    private sealed class FakeStore(DeviceTopologyPersistedSnapshot? persisted = null)
        : IDeviceTopologySnapshotStore
    {
        public int SaveCount { get; private set; }
        public TaskCompletionSource Saved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<DeviceTopologyPersistedSnapshot?> LoadAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(persisted);
        }

        public Task SaveAsync(
            DeviceTopologyPersistedSnapshot snapshot,
            CancellationToken cancellationToken)
        {
            SaveCount += 1;
            persisted = snapshot;
            Saved.TrySetResult();
            return Task.CompletedTask;
        }
    }
}
