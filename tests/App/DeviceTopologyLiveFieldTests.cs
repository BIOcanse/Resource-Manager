using System.Text.Json;
using ResourceManager.App.Application.DeviceTopology;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;
using ResourceManager.ReadWrite.Windows.Devices.Snapshots;

namespace Resource_Manager_APP.Tests;

public sealed class DeviceTopologyLiveFieldTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MobileReadingsChangeFieldsWithoutChangingStructure(bool battery)
    {
        var smart = new DeviceTopologySmartDeviceCapabilities("phone", "maker", "model", "serial", "firmware",
            "mtp", "usb", 80, [new("Internal", 1000, 500, "fs")], DeviceInformationTestData.Message);
        var port = DeviceInformationTestData.Port(" phone ") with { BusKind = DeviceBusKinds.Usb, SmartDevice = smart };
        var next = DeviceInformationTestData.Snapshot(port);
        var reader = new Reader(() => next);
        var policy = new DeviceTopologyCapturePolicy(reader, new DeviceTopologySemanticComparer().ComputeHash,
            DeviceTopologyInformationProjection.Project, static (_, _) => Task.CompletedTask,
            static _ => new("failed", []), static (_, _, _, _) => { }, static _ => { }, static () => { });
        using var owner = new DeviceTopologyCaptureOwner(new Firmware(), policy);
        await owner.CaptureInformationAsync(default);
        var first = owner.ReadChangeState();
        var fields = owner.ReadState().InformationFields;
        Assert.Equal("windows-pnp|PHONE", first.Snapshot!.Ports[0].InformationIdentity);
        Assert.Equal(first.Snapshot.Ports[0].InformationIdentity,
            Assert.Single(fields.Devices, d => d.ObjectType == "mobile_device").DeviceIdentity);
        using var change = owner.SubscribeTopologyChanges();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pending = change.WaitAsync(timeout.Token).AsTask();
        smart = battery ? smart with { BatteryPercent = 79 }
            : smart with { Storages = [smart.Storages[0] with { FreeBytes = 450 }] };
        next = next with { Ports = [port with { SmartDevice = smart }], CapturedAt = next.CapturedAt.AddSeconds(1) };
        await owner.CaptureInformationAsync(default);
        Assert.Same(first, owner.ReadChangeState());
        Assert.False(pending.IsCompleted);
        Assert.Null(first.Snapshot!.Ports[0].SmartDevice!.BatteryPercent);
        Assert.Null(first.Snapshot.Ports[0].SmartDevice!.Storages[0].FreeBytes);
        var before = Assert.Single(fields.Devices, d => d.DeviceIdentity == "windows-pnp|PHONE");
        var after = Assert.Single(owner.ReadState().InformationFields.Devices, d => d.DeviceIdentity == before.DeviceIdentity);
        var name = battery ? "mobile_all_battery_percent_reported" : "mobile_all_storage_free_bytes_reported";
        Assert.NotEqual(JsonSerializer.Serialize(Assert.Single(before.Fields, f => f.Definition.Name == name).Value),
            JsonSerializer.Serialize(Assert.Single(after.Fields, f => f.Definition.Name == name).Value));
        await timeout.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        next = next with { Ports = [] };
        await owner.CaptureInformationAsync(default);
        Assert.DoesNotContain(owner.ReadState().InformationFields.Devices, device => device.DeviceIdentity == "windows-pnp|PHONE");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiskPortReferenceFollowsProjectionInsteadOfFallbackObject(bool duplicate)
    {
        var port = DeviceInformationTestData.Port(" disk ");
        var snapshot = DeviceInformationTestData.Snapshot(duplicate ? [port, port with { Id = "other" }] : [port]) with
        { DiskLayoutInformation = [new("windows-pnp|DISK") { OsDiskNumberReported = 2 }] };
        using var owner = Owner(snapshot);
        await owner.CaptureInformationAsync(default);
        var state = owner.ReadState();
        Assert.Single(state.InformationFields.Devices, d => d.DeviceIdentity == "windows-pnp|DISK");
        Assert.All(state.Snapshot!.Ports, p => Assert.Equal(duplicate ? null : "windows-pnp|DISK", p.InformationIdentity));
    }

    [Fact]
    public async Task PlainAndDisplayOnlyPortsHaveNoInformationReference()
    {
        var plain = DeviceInformationTestData.Port("plain") with
        { BusKind = DeviceBusKinds.Unknown, InformationIdentity = "obsolete-reference" };
        var display = plain with { Id = "display", DeviceId = "display", Display = new(
            null, DeviceDisplayOutputTechnologies.Hdmi, "monitor", null, null, true, true, false, 0, "monitor-path") };
        using var owner = Owner(DeviceInformationTestData.Snapshot(plain, display));
        await owner.CaptureInformationAsync(default);
        Assert.All(owner.ReadState().Snapshot!.Ports, p => Assert.Null(p.InformationIdentity));
        Assert.DoesNotContain(owner.ReadState().InformationFields.Devices, d => d.DeviceIdentity == "windows-pnp|PLAIN");
        Assert.Contains(owner.ReadState().InformationFields.Devices, d => d.ObjectType == "display_path");
    }

    [Fact]
    public async Task UnchangedPortKeepsStructureAndReferenceParticipatesInHash()
    {
        var snapshot = DeviceInformationTestData.Snapshot(DeviceInformationTestData.Port("pci"));
        using var owner = Owner(snapshot);
        await owner.CaptureInformationAsync(default);
        var first = owner.ReadChangeState();
        Assert.Equal("windows-pnp|PCI", first.Snapshot!.Ports[0].InformationIdentity);
        var firstSnapshot = owner.ReadState().Snapshot;
        await owner.CaptureInformationAsync(default);
        Assert.Same(first, owner.ReadChangeState());
        Assert.Same(firstSnapshot, owner.ReadState().Snapshot);
        var comparer = new DeviceTopologySemanticComparer();
        var withoutReference = first.Snapshot with
        { Ports = [first.Snapshot.Ports[0] with { InformationIdentity = null }] };
        Assert.NotEqual(comparer.ComputeHash(first.Snapshot), comparer.ComputeHash(withoutReference));
        Assert.Equal(first.ContentGeneration, owner.ReadState().ContentGeneration);
    }

    private static DeviceTopologyCaptureOwner Owner(DeviceTopologySnapshot snapshot)
        => new(new Firmware(), new(new Reader(() => snapshot), new DeviceTopologySemanticComparer().ComputeHash,
            DeviceTopologyInformationProjection.Project, static (_, _) => Task.CompletedTask,
            static _ => new("failed", []), static (_, _, _, _) => { }, static _ => { }, static () => { }));

    [Fact]
    public async Task PersistedPortGetsReferenceWithoutInventingAStructureChangeOnNextCapture()
    {
        var snapshot = DeviceInformationTestData.Snapshot(DeviceInformationTestData.Port("pci")) with { NpuInformation = [] };
        using var owner = Owner(snapshot);
        owner.InitializePersisted(snapshot, new DeviceTopologySemanticComparer().ComputeHash(snapshot), 7);
        var persisted = owner.ReadState().Snapshot!;
        Assert.Equal("windows-pnp|PCI", persisted.Ports[0].InformationIdentity);
        Assert.All(owner.ReadState().InformationFields.Devices.SelectMany(d => d.Fields), f => Assert.Null(f.Value));
        await owner.CaptureInformationAsync(default);
        Assert.Equal(7, owner.ReadState().ContentGeneration);
        Assert.Same(persisted, owner.ReadState().Snapshot);
    }

    [Fact]
    public void BlankDeviceIdCannotReferenceAnInformationObjectEvenWhenPortHasFields()
    {
        var snapshot = DeviceInformationTestData.Snapshot(DeviceInformationTestData.Port(" "));
        var result = DeviceTopologyInformationProjection.Project(snapshot, snapshot.CapturedAt);
        Assert.Null(result.Snapshot!.Ports[0].InformationIdentity);
        Assert.DoesNotContain(result.Information.Devices, d => d.DeviceIdentity.StartsWith("windows-pnp|", StringComparison.Ordinal));
    }

    private sealed class Reader(Func<DeviceTopologySnapshot> read) : IDeviceTopologyReader
    { public DeviceTopologySnapshot ReadSnapshot() => read(); }
    private sealed class Firmware : IAcpiFirmwareTableReader
    { public IReadOnlyList<AcpiFirmwareTable>? ReadTables() => []; }
}
