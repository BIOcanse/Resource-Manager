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
        var port = DeviceInformationTestData.Port("phone") with { SmartDevice = smart };
        var next = DeviceInformationTestData.Snapshot(port);
        var reader = new Reader(() => next);
        var policy = new DeviceTopologyCapturePolicy(reader, new DeviceTopologySemanticComparer().ComputeHash,
            DeviceTopologyInformationProjection.Create, static (_, _) => Task.CompletedTask,
            static _ => new("failed", []), static (_, _, _, _) => { }, static _ => { }, static () => { });
        using var owner = new DeviceTopologyCaptureOwner(new Firmware(), policy);
        await owner.CaptureInformationAsync(default);
        var first = owner.ReadChangeState();
        var fields = owner.ReadState().InformationFields;
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
    private sealed class Reader(Func<DeviceTopologySnapshot> read) : IDeviceTopologyReader
    { public DeviceTopologySnapshot ReadSnapshot() => read(); }
    private sealed class Firmware : IAcpiFirmwareTableReader
    { public IReadOnlyList<AcpiFirmwareTable>? ReadTables() => []; }
}
