using System.Collections.Immutable;
using System.Text.Json;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Windows.Devices.Models;
using ResourceManager.ReadWrite.Windows.Devices.Snapshots;
using ResourceManager.Subscription;

namespace Resource_Manager_APP.Tests;

public sealed class NpuInformationOwnerTests
{
    private const string Identity = @"windows-npu|PCI\NPU_A";

    [Fact]
    public void DefaultFactoryIsPassiveAndUsesTheOriginalOwnerBackend()
    {
        using var owner = DeviceTopologyCaptureOwner.CreateNpuInformation();
        var backend = owner.CreateBackend();
        Assert.Same(backend, owner.CreateBackend());
        Assert.Equal(DeviceTopologySnapshotStatus.Warming, owner.ReadState().State);
        Assert.Empty(backend.Catalog.Fields);
        Assert.Throws<ArgumentException>(() => backend.ReadCached(new(Identity, "npu_all_name")));
        Assert.Equal(DeviceTopologySnapshotStatus.Warming, owner.ReadState().State);
    }

    [Fact]
    public async Task OriginalCurrentRetainsOnCancelAndRevokeClearsEveryDeclarationOnUnavailableAndRemovesOnEmpty()
    {
        var native = new DeviceTopologyNativeSnapshot(new Dictionary<string, DeviceTopologyNativeDevice>(), null);
        var reads = 0;
        IReadOnlyList<DeviceTopologyNpuInformation>? next = [Row()];
        using var owner = DeviceTopologyCaptureOwner.CreateNpuInformation(() => { reads++; return native; }, captured =>
        { Assert.Same(native, captured); return next; });
        var backend = owner.CreateBackend();
        Assert.Throws<ArgumentException>(() => backend.ReadCached(new(Identity, "npu_all_name")));
        Assert.Empty(backend.Catalog.Fields);
        Assert.Equal(0, reads);
        var catalog = await backend.DiscoverAsync(default);
        Assert.Equal(1, reads);
        Assert.Equal(5, catalog.Fields.Length);
        Assert.All(catalog.Fields, field => Assert.False(field.Write.Supported));
        var fields = catalog.Fields.Select(static field => field.Field).ToImmutableArray();
        var name = fields.Single(static field => field.FieldName == "npu_all_name");
        var paths = fields.Single(static field => field.FieldName == "npu_all_location_paths_declared");
        var before = owner.ReadState();
        using var borrow = owner.BorrowSubscriptionCapture();
        Assert.All(borrow.Bind(fields), binding => Assert.Equal(DeviceTopologyCaptureOwner.InformationCollectionItemId,
            Assert.Single(binding.CollectionItemIds)));
        next = [Row() with { NameDeclared = "replacement", LocationPathsDeclared = [] }];
        Assert.False((await borrow.CollectAsync(fields, new Permit(false))).Committed);
        Assert.Same(before, owner.ReadState());
        Assert.Equal(1, reads);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await borrow.CollectAsync(fields, new Permit(), canceled.Token));
        Assert.Same(before, owner.ReadState());
        var result = await borrow.CollectAsync(fields, new Permit());
        Assert.True(result.Committed);
        Assert.Equal("replacement", backend.ReadCached(name).Value!.Text);
        Assert.Equal(backend.ReadCached(name), result.Readings.Single(reading => reading.Field == name));
        Assert.Equal(FieldObservationStatus.Current, backend.ReadCached(paths).Status);
        Assert.NotNull(backend.ReadCached(paths).Value); // A successful empty location path array is a value.
        next = null;
        _ = await backend.CollectAsync(fields, default);
        Assert.Equal(DeviceTopologySnapshotStatus.Failed, owner.ReadState().State);
        Assert.Equal("npu-directory-unavailable", owner.ReadState().FailureCode);
        Assert.Equal(5, backend.Catalog.Fields.Length);
        var retained = Assert.Single(owner.ReadState().Snapshot!.NpuInformation!);
        Assert.Equal(Identity, retained.DeviceIdentity);
        Assert.All(fields, field =>
        {
            var reading = backend.ReadCached(field);
            Assert.Equal(FieldObservationStatus.Unavailable, reading.Status);
            Assert.Null(reading.Value);
            Assert.Null(reading.ObservedAt);
        });
        next = [];
        _ = await owner.CaptureInformationAsync(default);
        Assert.Empty(backend.Catalog.Fields);
        var retired = await borrow.CollectAsync(fields, new Permit());
        Assert.True(retired.Committed);
        Assert.All(retired.Readings, reading => Assert.Null(reading.Value));
    }

    [Fact]
    public async Task FirstUnavailableDirectoryFailsWhileSuccessfulEmptyDirectoryIsReady()
    {
        IReadOnlyList<DeviceTopologyNpuInformation>? rows = null;
        using var owner = DeviceTopologyCaptureOwner.CreateNpuInformation(
            () => new(new Dictionary<string, DeviceTopologyNativeDevice>(), null), _ => rows);
        var backend = owner.CreateBackend();
        Assert.Empty((await backend.DiscoverAsync(default)).Fields);
        Assert.Equal(DeviceTopologySnapshotStatus.Failed, owner.ReadState().State);
        Assert.Equal("npu-directory-unavailable", owner.ReadState().FailureCode);
        Assert.Null(owner.ReadState().Snapshot);
        Assert.Null(owner.ReadState().InformationFields.ObservedAt);
        rows = [];
        Assert.Empty((await backend.DiscoverAsync(default)).Fields);
        Assert.Equal(DeviceTopologySnapshotStatus.Ready, owner.ReadState().State);
        Assert.Null(owner.ReadState().FailureCode);
        Assert.Empty(owner.ReadState().Snapshot!.NpuInformation!);
        Assert.NotNull(owner.ReadState().InformationFields.ObservedAt);
    }

    [Fact]
    public async Task CancellationAndRetirementAfterSourceReadCannotReplaceDisplayedCurrent()
    {
        Action? afterRead = null;
        using var owner = DeviceTopologyCaptureOwner.CreateNpuInformation(
            () => new(new Dictionary<string, DeviceTopologyNativeDevice>(), null),
            _ => { afterRead?.Invoke(); return [Row() with { NameDeclared = "new declaration" }]; });
        _ = await owner.CreateBackend().DiscoverAsync(default);
        var previous = owner.ReadState();
        using var cancellation = new CancellationTokenSource();
        afterRead = cancellation.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await owner.CaptureInformationAsync(cancellation.Token));
        Assert.Same(previous, owner.ReadState());
        var active = true;
        afterRead = () => active = false;
        var permit = new DeviceTopologyCapturePermission(() => active,
            commit => { if (!active) return false; commit(); return true; }, () => true);
        _ = await owner.CaptureInformationAsync(default, permit);
        Assert.Same(previous, owner.ReadState());
    }

    [Fact]
    public async Task SourceFailureUsesOriginalFailurePublicationAndRetainsOnlyDefinitions()
    {
        var failNative = false;
        var failDirectory = false;
        var directoryCalls = 0;
        using var owner = DeviceTopologyCaptureOwner.CreateNpuInformation(
            () => new(new Dictionary<string, DeviceTopologyNativeDevice>(), failNative ? "denied" : null),
            _ => { directoryCalls++; if (failDirectory) throw new InvalidOperationException("DXCore failed"); return [Row()]; });
        var backend = owner.CreateBackend();
        var fields = (await backend.DiscoverAsync(default)).Fields.Select(static field => field.Field).ToImmutableArray();
        failNative = true;
        _ = await backend.CollectAsync(fields, default);
        Assert.Equal(1, directoryCalls);
        Assert.Equal(DeviceTopologySnapshotStatus.Failed, owner.ReadState().State);
        Assert.Equal(5, backend.Catalog.Fields.Length);
        Assert.All(fields, field => Assert.Null(backend.ReadCached(field).Value));
        failNative = false;
        failDirectory = true;
        _ = await backend.CollectAsync(fields, default);
        Assert.Equal(2, directoryCalls);
        Assert.Equal(DeviceTopologySnapshotStatus.Failed, owner.ReadState().State);
        Assert.All(fields, field => Assert.Null(backend.ReadCached(field).Value));
    }

    [Fact]
    public void ProductProjectionAndSemanticHashRespectCurrentDeclarationsAndPersistedUnavailability()
    {
        var a = Row();
        var b = Row() with { DeviceIdentity = @"windows-npu|ACPI\NPU_B", PnpInstanceId = @"ACPI\NPU_B" };
        var snapshot = Snapshot([a, b]);
        var live = DeviceTopologyInformationProjection.Create(snapshot, snapshot.CapturedAt);
        var devices = live.Devices.Where(static device => device.ObjectType == "npu").ToArray();
        Assert.Equal(2, devices.Length);
        Assert.All(devices, device => Assert.Equal(5, device.Fields.Count));
        var persisted = DeviceTopologyInformationProjection.Create(snapshot, null);
        Assert.All(persisted.Devices.SelectMany(static device => device.Fields), field => Assert.Null(field.Value));
        var comparer = new DeviceTopologySemanticComparer();
        var hash = comparer.ComputeHash(snapshot);
        Assert.Equal(hash, comparer.ComputeHash(snapshot with { CapturedAt = snapshot.CapturedAt.AddDays(1),
            NpuInformation = [b, a with { LocationPathsDeclared = a.LocationPathsDeclared!.Reverse().ToArray() }] }));
        foreach (var changed in new[] { a with { PnpInstanceId = @"PCI\changed" }, a with { NameDeclared = "changed" },
            a with { DriverVersionDeclared = "changed" }, a with { LocationInformationReported = "changed" },
            a with { LocationPathsDeclared = [] } })
            Assert.NotEqual(hash, comparer.ComputeHash(snapshot with { NpuInformation = [changed, b] }));
        Assert.NotEqual(comparer.ComputeHash(snapshot with { NpuInformation = null }),
            comparer.ComputeHash(snapshot with { NpuInformation = [] }));
        Assert.Null(new DeviceTopologySnapshot(DateTimeOffset.UtcNow, new("", "", "", "", null, null, null), [], []).NpuInformation);
        var json = JsonSerializer.Serialize(Snapshot(null));
        using var document = JsonDocument.Parse(json);
        var oldJson = "{" + string.Join(",", document.RootElement.EnumerateObject()
            .Where(static property => property.Name != "NpuInformation")
            .Select(static property => JsonSerializer.Serialize(property.Name) + ":" + property.Value.GetRawText())) + "}";
        Assert.Null(JsonSerializer.Deserialize<DeviceTopologySnapshot>(oldJson)!.NpuInformation);
        Assert.Empty(WindowsInformationFieldProjection.Npus([a, a], snapshot.CapturedAt).Devices);
    }

    [Fact]
    public async Task OriginalNativeCompositionSamplesSharedNpuAtFastestDemandAndStopLeavesDirectCaptureUsable()
    {
        var sequence = 0;
        using var owner = DeviceTopologyCaptureOwner.CreateNpuInformation(
            () => new(new Dictionary<string, DeviceTopologyNativeDevice>(), null),
            _ => [Row() with { DriverVersionDeclared = Interlocked.Increment(ref sequence).ToString() }]);
        var backend = owner.CreateBackend();
        var field = (await backend.DiscoverAsync(default)).Fields.Single(static field =>
            field.Field.FieldName == "npu_all_driver_version_declared").Field;
        var configuration = DefaultFieldSubscriptionCompositionTests.Configuration();
        await using var composition = new NativeFieldSubscriptionComposition(owner.BorrowSubscriptionCapture(),
            DefaultFieldSubscriptionCompositionTests.Settings(), in configuration,
            failure => throw new InvalidOperationException("Unexpected worker failure", failure.Error), TimeSpan.FromMilliseconds(1));
        await composition.StartAsync();
        using var slow = composition.Acquire("slow-npu", [field], TimeSpan.FromMinutes(1));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var fast = composition.SubscribeAsync("fast-npu", [field], TimeSpan.FromMilliseconds(20),
            timeout.Token).GetAsyncEnumerator();
        Assert.True(await fast.MoveNextAsync());
        var first = Assert.Single(fast.Current);
        FieldReading latest;
        do
        {
            Assert.True(await fast.MoveNextAsync());
            latest = Assert.Single(fast.Current);
        } while (latest.Value?.Text == first.Value?.Text);
        Assert.NotNull(latest.Value);
        var current = backend.ReadCached(field);
        Assert.True(int.Parse(current.Value!.Text!) >= int.Parse(latest.Value!.Text!));
        Assert.True(current.ObservedAt >= latest.ObservedAt);
        await fast.DisposeAsync();
        await composition.StopAsync();
        Assert.NotNull(backend.ReadCached(field).Value);
        Assert.Equal(FieldObservationStatus.Current, (await backend.CollectAsync([field], default)).Single().Status);
    }

    private static DeviceTopologyNpuInformation Row() => new(Identity, @"PCI\NPU_A", "Declared NPU", "1.2.3",
        "reported location", ["PCIROOT(0)#PCI(0100)", "ACPI(_SB_)#NPU(0)"]);
    private static DeviceTopologySnapshot Snapshot(IReadOnlyList<DeviceTopologyNpuInformation>? rows)
        => new(DateTimeOffset.UtcNow, new("", "", "", "", null, null, null), [], []) { NpuInformation = rows };
    private sealed class Permit(bool active = true) : IMetricPublicationPermit
    {
        public ulong WorkspaceIncarnation => 1;
        public ulong ConfigurationGeneration => 1;
        public bool IsActive => active;
        public bool TryPublish(Action commit) { if (!active) return false; commit(); return true; }
    }
}
