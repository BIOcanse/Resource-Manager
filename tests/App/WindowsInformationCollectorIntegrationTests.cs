using System.Text.Json;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;
using ResourceManager.ReadWrite.Information;

namespace Resource_Manager_APP.Tests;

public sealed class WindowsInformationCollectorIntegrationTests
{
    [Fact]
    public void ProductAndIndependentCollectorsUseTheSameCanonicalFieldsAndModels()
    {
        var owner = typeof(WindowsPageFileInformationReader).Assembly;
        Assert.Equal(typeof(DeviceInformationSnapshot).Assembly, owner);
        Assert.All(new[] { typeof(WindowsNetworkInterfaceInformationReader), typeof(WindowsNetworkEndpointInformationReader),
            typeof(WindowsVolumeSpaceInformationReader), typeof(DeviceTopologyVolumeSpaceInformation),
            typeof(DeviceTopologyNetworkInterfaceInformation), typeof(WindowsInformationFieldProjection) },
            type => Assert.Equal(owner, type.Assembly));
        var page = PageFileInformationCollectorTests.Sample();
        var volume = WindowsVolumeSpaceInformationReader.Create(VolumeSpaceInformationCollectorTests.IdA,
            VolumeSpaceInformationCollectorTests.Sample(), "S-1-5-18");
        var network = new DeviceTopologyNetworkInterfaceInformation("network-recorded", "GUID", ulong.MaxValue, uint.MaxValue, [])
            { Routes = [] };
        var endpoints = new DeviceTopologyNetworkEndpointInformation(null, [], [], null);
        var raw = DeviceInformationTestData.Snapshot() with { PageFileInformation = [page], VolumeSpaceInformation = [volume],
            NetworkInterfaceInformation = [network], NetworkEndpointInformation = endpoints };
        var at = raw.CapturedAt;
        var product = DeviceTopologyInformationProjection.Create(raw, at);
        var independent = new[] { WindowsInformationFieldProjection.PageFiles([page], at),
            WindowsInformationFieldProjection.Volumes([volume], at), WindowsInformationFieldProjection.NetworkInterfaces([network], at) };
        foreach (var device in independent.SelectMany(frame => frame.Devices))
        foreach (var field in device.Fields)
        {
            var actual = product.FindField(new(device.DeviceIdentity, field.Definition.Name));
            Assert.NotNull(actual);
            Assert.Equal(field.Definition, actual.Definition);
            Assert.Equal(field.Value?.GetRawText(), actual.Value?.GetRawText());
        }
        // Connection definitions belong exclusively to the independent Sampled item.
        foreach (var field in WindowsInformationFieldProjection.NetworkEndpointFields(endpoints))
            Assert.Null(product.FindField(new("machine", field.Definition.Name)));
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var restored = JsonSerializer.Deserialize<DeviceInformationSnapshot>(JsonSerializer.Serialize(independent[0], options), options)!;
        Assert.Equal(independent[0].Devices[0].Fields[1].Value!.Value.GetString(), restored.Devices[0].Fields[1].Value!.Value.GetString());
    }

    [Fact]
    public void CanonicalReadIsExplicitAndFailureIsDistinctFromSuccessfulEmptyEnumeration()
    {
        var source = new PageFiles();
        var reader = new WindowsPageFileInformationReader(source);
        Assert.Equal(0, source.Reads);
        var empty = reader.ReadFields();
        Assert.Equal(1, source.Reads); Assert.NotNull(empty.ObservedAt); Assert.Empty(empty.Devices);
        source.Result = null;
        var failed = reader.ReadFields();
        Assert.Equal(2, source.Reads); Assert.Null(failed.ObservedAt); Assert.Empty(failed.Devices);
        source.Result = [PageFileInformationCollectorTests.Sample()];
        var recovered = reader.ReadFields();
        Assert.Equal(3, source.Reads); Assert.NotNull(recovered.ObservedAt);
        Assert.Single(recovered.Devices);
        Assert.All(recovered.Devices[0].Fields, field => Assert.NotNull(field.Value));
        Assert.All(WindowsInformationFieldProjection.PageFiles(source.Result, null).Devices[0].Fields,
            field => Assert.Null(field.Value));
    }

    private sealed class PageFiles : IWindowsPageFileInformationApi
    {
        internal int Reads;
        internal IReadOnlyList<DeviceTopologyPageFileInformation>? Result = [];
        public IReadOnlyList<DeviceTopologyPageFileInformation>? Read() { Reads++; return Result; }
    }
}
