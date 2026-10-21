using System.Buffers.Binary;
using System.Text.Json;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology.Pci;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;

namespace Resource_Manager_APP.Tests;

public sealed partial class PciResourceInformationCollectorTests
{
    [Fact]
    public void OfficialSdkFramesPreserveExactInclusiveEndpointsAndIndependentKinds()
    {
        using var sdk = Sdk(); var metadata = sdk.RootElement;
        Assert.Equal(IntPtr.Size, metadata.GetProperty("pointerSize").GetInt32());
        Assert.Equal(2U, metadata.GetProperty("allocatedFlag").GetUInt32());
        Assert.Equal(WindowsPciResourceInformationReader.NoMoreConfigurations, metadata.GetProperty("noMoreConfigurations").GetUInt32());
        Assert.Equal(WindowsPciResourceInformationReader.NoMoreResources, metadata.GetProperty("noMoreResources").GetUInt32());
        var api = new Api(); var ranges = Read(api)!; Assert.Equal(new uint[] { 1, 7, 2, 6 }, ranges.Select(value => value.ResourceTypeReported));
        Assert.Equal("0", ranges[0].StartReported); Assert.Equal("18446744073709551615", ranges[0].EndReported); Assert.Equal("18446744073709551616", ranges[0].LengthDerived);
        Assert.Equal(0xA1B2C3D4U, ranges[0].FlagsReported); Assert.Equal("9007199254740993", ranges[1].StartReported);
        Assert.Equal("4096", ranges[2].LengthDerived); Assert.Equal("4294967296", ranges[3].LengthDerived); Assert.Equal(0x76543210U, ranges[3].FlagsReported);
        Assert.Equal(2, api.IdentityCalls); Assert.Equal(4, api.DataCalls);
        Assert.Equal(new[] { (false, (nuint)1003), (false, (nuint)1002), (false, (nuint)1001), (false, (nuint)1000), (true, (nuint)10) }, api.Released);
    }

    [Theory]
    [InlineData("first")] [InlineData("next-config")] [InlineData("next-resource")]
    [InlineData("size")] [InlineData("data")] [InlineData("throw-size")]
    [InlineData("identity-before")] [InlineData("identity-after")]
    [InlineData("release-config")] [InlineData("release-resource")] [InlineData("throw-release")]
    [InlineData("loop-config")] [InlineData("loop-resource")]
    public void FailedIncompleteChangedOrRepeatedHandlesNeverPublishPartialRanges(string fault)
    {
        var api = new Api { Fault = fault }; Assert.Null(Read(api));
        Assert.Equal(api.Released.Count, api.Released.Distinct().Count());
        if (fault == "identity-before") Assert.Equal(0, api.Opens);
        if (fault != "first" && fault != "identity-before") Assert.Contains((true, (nuint)10), api.Released);
        if (fault is "release-resource" or "throw-release") Assert.Equal(5, api.Released.Count);
    }

    [Theory]
    [InlineData(0U)] [InlineData(1048577U)]
    public void InvalidDescriptorSizesDoNotReadDataAndStillReleaseHandles(uint size)
    {
        var api = new Api { SizeOverride = size }; Assert.Null(Read(api)); Assert.Equal(0, api.DataCalls); Assert.Equal(5, api.Released.Count);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(32)] [InlineData(33)]
    public void ConfigurationDirectoryIsCompleteAndBoundedIncludingSuccessfulAbsence(int count)
    {
        var api = new Api { ConfigurationCount = count, Descriptors = [] }; var value = Read(api);
        if (count <= 32) Assert.Empty(value!); else Assert.Null(value);
        Assert.Equal(count, api.Released.Count); Assert.Equal(0, api.DataCalls); Assert.Equal(count <= 32 ? 2 : 1, api.IdentityCalls);
    }

    [Theory]
    [InlineData(4096)] [InlineData(4097)]
    public void NonRangeDescriptorsAreNotInventedAsRangesButStillCountTowardsBound(int count)
    {
        var api = new Api { Descriptors = Enumerable.Repeat((5U, Array.Empty<byte>()), count).ToList() };
        var value = Read(api); if (count == 4096) Assert.Empty(value!); else Assert.Null(value);
        Assert.Equal(count + 1, api.Released.Count); Assert.Equal(0, api.SizeCalls); Assert.Equal(0, api.DataCalls);
    }

    [Theory]
    [InlineData("short")] [InlineData("requirements")] [InlineData("type")] [InlineData("reversed")] [InlineData("unknown")]
    public void MalformedAllocatedHeaderRejectsTheDirectoryWithoutPretendingItIsARequirement(string fault)
    {
        var api = new Api(); var frame = api.Descriptors[0];
        if (fault == "short") frame = (frame.Type, frame.Bytes[..31]);
        else if (fault == "requirements") BinaryPrimitives.WriteUInt32LittleEndian(frame.Bytes, 1);
        else if (fault == "type") BinaryPrimitives.WriteUInt32LittleEndian(frame.Bytes.AsSpan(4), 35);
        else if (fault == "reversed") { BinaryPrimitives.WriteUInt64LittleEndian(frame.Bytes.AsSpan(8), 2); BinaryPrimitives.WriteUInt64LittleEndian(frame.Bytes.AsSpan(16), 1); }
        else { Assert.Null(WindowsPciResourceInformationReader.Parse(99, frame.Bytes)); return; }
        api.Descriptors[0] = frame; Assert.Null(Read(api)); Assert.Equal(5, api.Released.Count);
    }

    [Fact]
    public void ZeroEndpointSingleAddressDuplicateAndEmptyReportsRemainDistinct()
    {
        var api = new Api(); var bytes = api.Descriptors[0].Bytes; BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(16), 0);
        api.Descriptors = [(1, bytes), (1, bytes)]; var values = Read(api)!;
        Assert.Equal(2, values.Count); Assert.Equal(values[0], values[1]); Assert.Equal("1", values[0].LengthDerived); Assert.Equal("0", values[0].StartReported);
        api.Descriptors = []; Assert.Empty(Read(api)!);
    }

    [Fact]
    public void NativeDeviceMustMatchPciInstanceAndOriginalProjectionPreservesTypedRanges()
    {
        var api = new Api(); var values = Read(api)!;
        Assert.Same(values, WindowsPciResourceInformationReader.ReadForDevice(Id.ToLowerInvariant(), Native(Id, values)).Ranges);
        Assert.Equal(DeviceTopologyPciResourceInformation.Unavailable, WindowsPciResourceInformationReader.ReadForDevice(Id, Native(@"PCI\OTHER", values)));
        Assert.Equal(DeviceTopologyPciResourceInformation.Unavailable, WindowsPciResourceInformationReader.ReadForDevice(@"USB\SAME", Native(@"USB\SAME", values)));
        var raw = DeviceInformationTestData.Snapshot(DeviceInformationTestData.Port(Id) with { PciResourceRanges = values });
        var snapshot = DeviceTopologyInformationProjection.Create(raw, DateTimeOffset.UtcNow);
        var field = DeviceInformationTestData.Field(snapshot, Identity, "pci_all_resource_range");
        Assert.Equal("windows.pci-allocated-resources", field.Definition.SourceId); Assert.Equal("resource-ranges", field.Definition.ValueKind);
        Assert.Equal("18446744073709551616", field.Value!.Value[0].GetProperty("lengthDerived").GetString());
        var restored = JsonSerializer.Deserialize<DeviceTopologySnapshot>(JsonSerializer.Serialize(raw))!;
        Assert.Equal(values, restored.Ports[0].PciResourceRanges); var comparer = new DeviceTopologySemanticComparer(); Assert.Equal(comparer.ComputeHash(raw), comparer.ComputeHash(restored));
        Assert.NotEqual(comparer.ComputeHash(raw), comparer.ComputeHash(raw with { Ports = [raw.Ports[0] with { PciResourceRanges = [] }] }));
        Assert.Null(DeviceInformationTestData.Field(DeviceTopologyInformationProjection.Create(restored, null), Identity, "pci_all_resource_range").Value);
    }
}
