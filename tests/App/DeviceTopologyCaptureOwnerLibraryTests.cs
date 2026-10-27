using ResourceManager.App.Application.DeviceTopology;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Domain.Messages;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Windows.Devices.Snapshots;
using ResourceManager.Subscription;

namespace Resource_Manager_APP.Tests;

public sealed class DeviceTopologyCaptureOwnerLibraryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PersistedTopologyCannotReplaceTheOnceReadAcpiPublicationWithUnreadEmpty(bool failed)
    {
        var firmware = new FirmwareReader(() => failed ? null : AcpiFirmwareTestData.Tables());
        using var owner = new DeviceTopologyCaptureOwner(firmware, Policy(() => Snapshot()));
        var value = await owner.CaptureAcpiInformationAsync(default);
        owner.InitializePersisted(Snapshot(), "persisted", 1);
        Assert.Same(value, owner.ReadState().AcpiInformationFields);
        Assert.Same(value, await owner.CaptureAcpiInformationAsync(default));
        Assert.Equal(1, firmware.ReadCount);
    }

    [Fact]
    public async Task LiveReadingsPublishWithoutReplacingOrPersistingUnchangedStructure()
    {
        var route = new NetworkRoute("ipv4", "0.0.0.0", 0, null, "192.0.2.1", null,
            0, 600, 300, 1, 3, false, false, false, false, 10, 0);
        var address = new NetworkUnicastAddress("ipv4", "192.0.2.2", 24, null, 0, 1, 1, 4,
            600, 300, false, "134116992000000000");
        var first = Snapshot() with
        {
            NetworkInterfaceInformation = [new("network", "guid", 1, 1, [address]) { Routes = [route] }],
            NetworkEndpointInformation = new([], [], [], []),
            VolumeSpaceInformation = [new("volume") { TotalBytes = 1000, AvailableBytes = 500, UsedBytes = 500 }],
            PageFileInformation = [new("pagefile") { UsedPages = 10, PeakUsedPages = 20 }]
        };
        var second = first with
        {
            CapturedAt = first.CapturedAt.AddSeconds(1),
            NetworkInterfaceInformation = [first.NetworkInterfaceInformation[0] with
            { UnicastAddresses = [address with { ValidLifetimeSeconds = 599, PreferredLifetimeSeconds = 299 }],
                Routes = [route with { AgeSeconds = 11, ValidLifetimeSeconds = 599 }] }],
            NetworkEndpointInformation = new([new("ipv4", new("192.0.2.2", null, 123), null, 2, 5)], [], [], []),
            VolumeSpaceInformation = [first.VolumeSpaceInformation[0] with { AvailableBytes = 450, UsedBytes = 550 }],
            PageFileInformation = [first.PageFileInformation[0] with { UsedPages = 11, PeakUsedPages = 21 }]
        };
        var read = first;
        var persisted = new List<DeviceTopologyChangedCapture>();
        using var owner = new DeviceTopologyCaptureOwner(new FirmwareReader(() => []), Policy(() => read, persisted.Add));
        await owner.CaptureInformationAsync(default);
        var original = owner.ReadState();
        read = second;
        await owner.CaptureInformationAsync(default);
        var current = owner.ReadState();
        Assert.Same(original.Snapshot, current.Snapshot);
        Assert.Equal(original.ContentGeneration, current.ContentGeneration);
        var durable = Assert.Single(persisted).Snapshot;
        Assert.Null(durable.VolumeSpaceInformation![0].AvailableBytes);
        Assert.Null(durable.NetworkInterfaceInformation![0].Routes);
        Assert.Null(durable.NetworkEndpointInformation.TcpIpv4);
        Assert.Null(durable.PageFileInformation![0].UsedPages);
        var expected = DeviceTopologyInformationProjection.Create(second, second.CapturedAt);
        foreach (var device in expected.Devices)
            Assert.Equal(System.Text.Json.JsonSerializer.Serialize(device.Fields),
                System.Text.Json.JsonSerializer.Serialize(current.InformationFields.Devices.Single(row => row.DeviceIdentity == device.DeviceIdentity).Fields));
        Assert.Equal(second.CapturedAt, current.InformationFields.ObservedAt);
        read = second with { VolumeSpaceInformation = [second.VolumeSpaceInformation[0] with { MountPaths = ["D:\\"] }] };
        await owner.CaptureInformationAsync(default);
        Assert.Equal(original.ContentGeneration + 1, owner.ReadState().ContentGeneration);
        Assert.Equal(2, persisted.Count);
    }

    [Fact]
    public async Task LocalSourceFailureStillPublishesHealthyCurrentInformationAndDiagnostics()
    {
        var diagnostic = new DeviceTopologySourceDiagnostic("usb-hub-ioctl", DeviceTopologySourceDiagnosticStatus.OptionalDegraded,
            "usb-unavailable", BackendMessage.Create(BackendMessageDomains.DeviceTopology, BackendMessageCodes.DeviceTopology.UsbHubIoctlFailed, "removed"));
        var snapshot = Snapshot() with { SourceDiagnostics = [diagnostic], Notes = [diagnostic.MessageCode] };
        using var owner = new DeviceTopologyCaptureOwner(new FirmwareReader(() => []), Policy(() => snapshot));
        await owner.CaptureInformationAsync(default);
        var current = owner.ReadState();
        Assert.Equal(DeviceTopologySnapshotStatus.Ready, current.State);
        Assert.Equal(diagnostic, Assert.Single(current.AttemptDiagnostics));
        Assert.Equal(snapshot.CapturedAt, current.InformationFields.ObservedAt);
        Assert.Contains(current.InformationFields.Devices.Single(row => row.DeviceIdentity == "machine").Fields,
            field => field.Definition.Name == "machine_all_manufacturer" && field.Value?.GetString() == "manufacturer");
    }

    [Fact]
    public async Task SharedOwnerPreservesBothFramesAndSignalsOnlyTheCapturedBranch()
    {
        var acpi = new FirmwareReader(() => AcpiFirmwareTestData.Tables());
        var persisted = 0;
        using var owner = new DeviceTopologyCaptureOwner(acpi, Policy(() => Snapshot(), _ => persisted++));
        Assert.Equal(typeof(ReadWriteCore).Assembly, typeof(DeviceTopologyCaptureOwner).Assembly);
        Assert.Equal(typeof(ReadWriteCore).Assembly, typeof(DeviceTopologySnapshot).Assembly);
        Assert.Equal(typeof(ReadWriteCore).Assembly, typeof(IDeviceTopologyReader).Assembly);
        Assert.Equal(typeof(ReadWriteCore).Assembly, typeof(BackendMessage).Assembly);
        Assert.Equal(0, acpi.ReadCount);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var topologySignal = owner.SubscribeTopologyPublications();
        using var acpiSignal = owner.SubscribeAcpiPublications();
        var topologyWait = topologySignal.WaitAsync(timeout.Token).AsTask();
        var acpiWait = acpiSignal.WaitAsync(timeout.Token).AsTask();
        var firmware = await owner.CaptureAcpiInformationAsync(timeout.Token);
        await acpiWait;
        Assert.False(topologyWait.IsCompleted);
        var topology = await owner.CaptureInformationAsync(timeout.Token);
        await topologyWait;
        Assert.Same(firmware, owner.ReadState().AcpiInformationFields);
        Assert.Same(topology, owner.ReadState().InformationFields);
        Assert.Equal(1, persisted);
        acpiWait = acpiSignal.WaitAsync(timeout.Token).AsTask();
        await owner.CaptureInformationAsync(timeout.Token);
        Assert.False(acpiWait.IsCompleted);
        Assert.Same(firmware, owner.ReadState().AcpiInformationFields);
        await timeout.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => acpiWait);
    }

    [Fact]
    public async Task RetiredOwnerCannotCommitAfterAnAlreadyStartedSynchronousFirmwareRead()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var retirementGate = new object();
        var active = true;
        var permission = new DeviceTopologyCapturePermission(
            () => { lock (retirementGate) return active; },
            commit => { lock (retirementGate) { if (!active) return false; commit(); return true; } },
            () => true);
        using var owner = new DeviceTopologyCaptureOwner(new FirmwareReader(() => {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
            return AcpiFirmwareTestData.Tables();
        }));
        var original = owner.ReadState();
        var capture = Task.Run(() => owner.SampleAcpiAsync(permission, CancellationToken.None));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
            lock (retirementGate) active = false;
        }
        finally { release.Set(); }
        Assert.Equal(DeviceTopologySampleCompletion.Canceled, await capture);
        Assert.Same(original, owner.ReadState());
    }

    [Fact]
    public async Task TopologyAndAcpiExplicitCaptureUseTheSameGateAndCanceledWaitReadsNothing()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var acpi = new FirmwareReader(() => AcpiFirmwareTestData.Tables());
        using var owner = new DeviceTopologyCaptureOwner(acpi, Policy(() => {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
            return Snapshot();
        }));
        var topology = Task.Run(async () => await owner.CaptureInformationAsync(default));
        Task<DeviceInformationSnapshot>? firmware = null;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
            firmware = owner.CaptureAcpiInformationAsync(default).AsTask();
            Assert.False(firmware.IsCompleted);
            using var cancellation = new CancellationTokenSource();
            var canceled = owner.CaptureAcpiInformationAsync(cancellation.Token).AsTask();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
            Assert.Equal(0, acpi.ReadCount);
        }
        finally { release.Set(); }
        var topologyFrame = await topology;
        var firmwareFrame = await firmware!;
        Assert.Same(topologyFrame, owner.ReadState().InformationFields);
        Assert.Same(firmwareFrame, owner.ReadState().AcpiInformationFields);
        Assert.Equal(1, acpi.ReadCount);
    }

    [Fact]
    public async Task ExternalSubscriptionProjectsTheSameCanonicalCapturePublication()
    {
        using var owner = new DeviceTopologyCaptureOwner(new FirmwareReader(() => AcpiFirmwareTestData.Tables()));
        var backend = new InformationReadWriteBackend(() => owner.ReadState().AcpiInformationFields,
            token => owner.CaptureAcpiInformationAsync(token), static _ => new(true, null, []));
        await backend.DiscoverAsync(default);
        var field = backend.Catalog.Fields.Single(f => f.Field.FieldName == "firmware_all_acpi_table_revision").Field;
        var subscriptions = new InformationFieldSubscriptionBackend(backend,
            static (_, _) => new EmptyLease(), (_, _, token) => Stream(owner, token));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var stream = subscriptions.SubscribeAsync("selected", [field], TimeSpan.FromSeconds(1), timeout.Token).GetAsyncEnumerator();
        var next = stream.MoveNextAsync().AsTask();
        var explicitResult = (await backend.CollectAsync([field], timeout.Token)).Single();
        Assert.True(await next);
        Assert.Equal(explicitResult, Assert.Single(stream.Current));
    }

    [Fact]
    public async Task CanonicalBackendRetainsTheOriginalFixedAcpiFrame()
    {
        var count = 0;
        using var owner = new DeviceTopologyCaptureOwner(new FirmwareReader(() => ++count switch {
            1 => AcpiFirmwareTestData.Tables(),
            2 => null,
            3 => AcpiFirmwareTestData.Tables(7),
            _ => []
        }));
        var backend = new InformationReadWriteBackend(() => owner.ReadState().AcpiInformationFields,
            token => owner.CaptureAcpiInformationAsync(token), static _ => new(true, null, []));
        await backend.DiscoverAsync(default);
        var known = owner.ReadState().AcpiInformationFields;
        var field = backend.Catalog.Fields.Single(f => f.Field.FieldName == "firmware_all_acpi_table_revision").Field;
        Assert.Equal(MetricInteger.Parse("6"), (await backend.CollectAsync([field], default)).Single().Value!.Integer);
        Assert.Same(known, owner.ReadState().AcpiInformationFields);
        Assert.Equal(MetricInteger.Parse("6"), (await backend.CollectAsync([field], default)).Single().Value!.Integer);
        await owner.CaptureAcpiInformationAsync(default);
        Assert.Same(known, owner.ReadState().AcpiInformationFields);
        Assert.Equal(1, count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FixedAcpiFailureIsLoggedAndQueriedOnceAndNewOwnerCanReadAgain(bool throws)
    {
        var unavailable = true;
        var reader = new FirmwareReader(() => !unavailable ? AcpiFirmwareTestData.Tables(7)
            : throws ? throw new InvalidOperationException("firmware unavailable") : null);
        var logs = 0; var queries = 0;
        using (var owner = new DeviceTopologyCaptureOwner(reader, logAcpiFailure: _ => logs++))
        {
            for (var index = 0; index < 100; index++)
                Assert.Equal(DeviceTopologySampleCompletion.Failed,
                    await owner.SampleAcpiAsync(null, default, sourceQueryStarted: () => queries++));
            Assert.Equal(1, reader.ReadCount);
            Assert.Equal(1, queries);
            Assert.Equal(throws ? 1 : 0, logs);
            Assert.Null(owner.ReadState().AcpiInformationFields.ObservedAt);
        }
        unavailable = false;
        using var secondOwner = new DeviceTopologyCaptureOwner(reader);
        var recovered = await secondOwner.CaptureAcpiInformationAsync(default);
        Assert.Equal("7", AcpiFirmwareTestData.Field(recovered, "firmware_all_acpi_table_revision").Value!.Value.GetString());
        Assert.Equal(2, reader.ReadCount);
    }

    [Fact]
    public async Task FixedAcpiRawBytesAndObservationTimeAreCapturedBeforePublicationAndRemainImmutable()
    {
        var tables = AcpiFirmwareTestData.Tables();
        var expectedRaw = tables[0].FirstTable!.Raw.ToArray();
        var reader = new FirmwareReader(() => tables);
        using var owner = new DeviceTopologyCaptureOwner(reader);
        var queries = 0;
        await owner.SampleAcpiAsync(new DeviceTopologyCapturePermission(() => true, _ => false, () => true),
            default, sourceQueryStarted: () => queries++);
        Assert.Null(owner.ReadState().AcpiInformationFields.ObservedAt);
        tables[0].FirstTable!.Raw[8] = 99;
        var frame = await owner.CaptureAcpiInformationAsync(default);
        Assert.Equal(expectedRaw, AcpiFirmwareTestData.Field(frame, "firmware_all_acpi_table_raw").Value!.Value.GetBytesFromBase64());
        for (var index = 0; index < 100; index++)
            Assert.Same(frame, await owner.CaptureAcpiInformationAsync(default));
        Assert.Equal(1, reader.ReadCount); Assert.Equal(1, queries);
    }

    private static DeviceTopologyCapturePolicy Policy(Func<DeviceTopologySnapshot> read,
        Action<DeviceTopologyChangedCapture>? persist = null)
        => new(new TopologyReader(read), new DeviceTopologySemanticComparer().ComputeHash,
            DeviceTopologyInformationProjection.Create,
            (changed, _) => { persist?.Invoke(changed); return Task.CompletedTask; },
            static _ => new("device-topology-read-failed", []),
            static (_, _, _, _) => { }, static _ => { }, static () => { });

    private static DeviceTopologySnapshot Snapshot()
        => new(DateTimeOffset.UtcNow, new("manufacturer", "model", "brand", "logo", null, null, null), [], []);

    private sealed class TopologyReader(Func<DeviceTopologySnapshot> read) : IDeviceTopologyReader
    { public DeviceTopologySnapshot ReadSnapshot() => read(); }

    private sealed class FirmwareReader(Func<IReadOnlyList<AcpiFirmwareTable>?> read) : IAcpiFirmwareTableReader
    {
        public int ReadCount { get; private set; }
        public IReadOnlyList<AcpiFirmwareTable>? ReadTables() { ReadCount++; return read(); }
    }

    private static async IAsyncEnumerable<DeviceInformationSnapshot> Stream(DeviceTopologyCaptureOwner owner,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var publication = owner.SubscribeAcpiPublications();
        while (!cancellationToken.IsCancellationRequested)
        {
            await publication.WaitAsync(cancellationToken);
            yield return owner.ReadState().AcpiInformationFields;
        }
    }

    private sealed class EmptyLease : IDisposable { public void Dispose() { } }
}
