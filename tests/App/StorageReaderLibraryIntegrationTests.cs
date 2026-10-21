using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;
using ResourceManager.ReadWrite.Information;

namespace Resource_Manager_APP.Tests;

public sealed class StorageReaderLibraryIntegrationTests
{
    [Fact]
    public void ActualReadersModelsAndProductProjectionsShareOneLibrary()
    {
        var owner = typeof(WindowsDiskLayoutInformationReader).Assembly;
        Assert.Equal(typeof(DeviceInformationSnapshot).Assembly, owner);
        Assert.All(new[] { typeof(WindowsStorageIdentifyReader), typeof(WindowsAtaSmartReader), typeof(WindowsNvmeHealthReader),
            typeof(WindowsPciResourceInformationReader), typeof(WindowsDeviceTopologyNativeReader), typeof(DeviceTopologyNativeDevice),
            typeof(DeviceTopologyDiskLayoutInformation), typeof(DeviceTopologyStorageInformation) }, type => Assert.Equal(owner, type.Assembly));
        var disk = new DeviceTopologyDiskLayoutInformation("windows-pnp|RECORDED-DISK")
        {
            OsDiskNumberReported = uint.MaxValue,
            Layout = new(DiskPartitionStyle.Gpt, null, new(Guid.NewGuid(), 4096, ulong.MaxValue - 4096, 128), []),
            AccessAlignment = new(0, 0, 512, 4096, 0), BusTypeReported = 17
        };
        var raw = DeviceInformationTestData.Snapshot() with { DiskLayoutInformation = [disk], VolumeSpaceInformation = [], StorageInformation = [] };
        var product = DeviceTopologyInformationProjection.Create(raw, raw.CapturedAt);
        var independent = WindowsInformationFieldProjection.Disks([disk], [], [], raw.CapturedAt);
        Assert.Equal(20, independent.Devices[0].Fields.Count);
        foreach (var field in independent.Devices[0].Fields)
        {
            var actual = product.FindField(new(disk.DeviceIdentity, field.Definition.Name));
            Assert.NotNull(actual);
            Assert.Equal(field.Definition, actual.Definition);
            Assert.Equal(field.Value?.GetRawText(), actual.Value?.GetRawText());
        }
        Assert.All(WindowsInformationFieldProjection.Disks([disk], [], [], null).Devices[0].Fields, field => Assert.Null(field.Value));
    }

    [Fact]
    public void PublicDiskReadDoesOneOriginalCaptureAndOnlyUsesExplicitRelationInputs()
    {
        var api = new Disks();
        var reader = new WindowsDiskLayoutInformationReader(api);
        Assert.Equal(0, api.Reads); Assert.Equal(0, api.Enumerations);
        var isolated = reader.ReadFields();
        Assert.Equal(1, api.Reads); Assert.Equal(1, api.Enumerations);
        Assert.Null(isolated.FindField(new("windows-pnp|RECORDED-DISK", "storage_all_volume_relation_actual"))!.Value);
        var emptyInventory = reader.ReadFields([]);
        Assert.Equal(2, api.Reads); Assert.Equal(2, api.Enumerations);
        var relation = emptyInventory.FindField(new("windows-pnp|RECORDED-DISK", "storage_all_volume_relation_actual"))!;
        Assert.Equal(0, relation.Value!.Value.GetArrayLength());
        api.Directory = null;
        var failed = reader.ReadFields([]);
        Assert.Null(failed.ObservedAt); Assert.Empty(failed.Devices);
        Assert.Equal(2, api.Reads); Assert.Equal(3, api.Enumerations);
    }

    private sealed class Disks : IWindowsDiskLayoutInformationApi
    {
        internal int Reads, Enumerations;
        internal IReadOnlyList<WindowsDiskInterface>? Directory = [new("recorded-path", "recorded-disk")];
        public IReadOnlyList<WindowsDiskInterface>? Enumerate() { Enumerations++; return Directory; }
        public WindowsDiskLayoutCapture? Read(WindowsDiskInterface device)
        { Reads++; return new(2, new(DiskPartitionStyle.Raw, null, null, [])); }
    }
}
