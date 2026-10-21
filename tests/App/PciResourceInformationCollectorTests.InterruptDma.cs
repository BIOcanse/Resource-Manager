using System.Buffers.Binary;
using System.Text.Json;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology.Pci;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;

namespace Resource_Manager_APP.Tests;

public sealed partial class PciResourceInformationCollectorTests
{
    internal const string InterruptField = "pci_all_interrupts_allocated_reported", DmaField = "pci_all_dma_channels_allocated_reported";
    internal static (uint Type, byte[] Bytes) DetailFrame(string name)
    {
        var assembly = typeof(PciResourceInformationCollectorTests).Assembly;
        var resource = Assert.Single(assembly.GetManifestResourceNames(), item => item.EndsWith(".Fixtures.Storage.windows-sdk-pci-resource-details.json", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource)!; using var sdk = JsonDocument.Parse(stream);
        var root = sdk.RootElement; Assert.True(root.GetProperty("processorGroups").GetBoolean());
        Assert.Equal(20, root.GetProperty("irq32Size").GetInt32()); Assert.Equal(24, root.GetProperty("irq64Size").GetInt32());
        Assert.Equal(12, root.GetProperty("irqRangeSize").GetInt32()); Assert.Equal(16, root.GetProperty("dmaSize").GetInt32());
        Assert.Equal(12, root.GetProperty("dmaRangeSize").GetInt32()); Assert.Equal(0, root.GetProperty("defaultWidthFlag").GetInt32());
        var frame = Assert.Single(root.GetProperty("frames").EnumerateArray(), item => item.GetProperty("name").GetString() == name);
        return (frame.GetProperty("type").GetUInt32(), Convert.FromHexString(frame.GetProperty("hex").GetString()!));
    }
    internal static Api MixedApi() => new() { Descriptors = [.. Frames(), DetailFrame("irq64"), DetailFrame("dma0"), DetailFrame("all64"), DetailFrame("dmaMax")] };
    internal static DeviceTopologyPciResourceInformation Details(Api api) => new WindowsPciResourceInformationReader(api).Read(42, Id);
    internal static DeviceTopologySnapshot DetailSnapshot(Api api)
    {
        var value = Details(api);
        return DeviceInformationTestData.Snapshot(DeviceInformationTestData.Port(Id) with
        { PciResourceRanges = value.Ranges, PciInterrupts = value.Interrupts, PciDmaChannels = value.DmaChannels });
    }

    [Theory]
    [InlineData("irq32", 4, 0U, 3, "2147483649", 42480)]
    [InlineData("irq64", 8, uint.MaxValue, 65535, "9007199254740993", 65535)]
    [InlineData("zero", 8, 0U, 0, "0", 0)]
    [InlineData("all32", 4, 17U, 1, "4294967295", 3)]
    [InlineData("all64", 8, 18U, 2, "18446744073709551615", 3)]
    public void SdkInterruptFramesPreserveReportedNumberGroupFlagsAndLosslessMask(string name, int width, uint number, int group, string mask, int flags)
    {
        var frame = DetailFrame(name); Assert.Equal(4U, frame.Type);
        var value = WindowsPciResourceInformationReader.ParseInterrupt(width, frame.Bytes)!;
        Assert.Equal(number, value.NumberReported); Assert.Equal((ushort)group, value.ProcessorGroupReported);
        Assert.Equal(mask, value.ProcessorAffinityMaskReported); Assert.Equal((ushort)flags, value.FlagsReported);
    }

    [Theory]
    [InlineData("dma0", 0U, 0U)] [InlineData("dmaMax", uint.MaxValue, uint.MaxValue)]
    public void SdkDmaFramesPreserveChannelAndRawFlagsWithoutInferringTraffic(string name, uint channel, uint flags)
    {
        var frame = DetailFrame(name); Assert.Equal(3U, frame.Type);
        Assert.Equal(new DeviceTopologyPciDmaChannel(channel, flags), WindowsPciResourceInformationReader.ParseDmaChannel(frame.Bytes));
    }

    [Theory]
    [InlineData("short32")] [InlineData("short64")] [InlineData("requirements")] [InlineData("type")]
    [InlineData("width0")] [InlineData("width16")]
    public void InterruptParserRequiresAllocatedHeaderAndExplicitClientWidth(string fault)
    {
        var bytes = DetailFrame(fault == "short32" ? "irq32" : "irq64").Bytes; var width = fault == "short32" ? 4 : 8;
        if (fault.StartsWith("short", StringComparison.Ordinal)) bytes = bytes[..^1];
        else if (fault == "requirements") BinaryPrimitives.WriteUInt32LittleEndian(bytes, 1);
        else if (fault == "type") BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 16);
        else width = fault == "width0" ? 0 : 16;
        Assert.Null(WindowsPciResourceInformationReader.ParseInterrupt(width, bytes));
    }

    [Fact]
    public void ExtraDescriptorBytesNeverChooseTheIrqPointerFormat()
    {
        var bytes = DetailFrame("irq32").Bytes.Concat(new byte[] { 0xEF, 0xCD, 0xAB, 0x89, 0, 0, 0, 0 }).ToArray();
        Assert.Equal("2147483649", WindowsPciResourceInformationReader.ParseInterrupt(4, bytes)!.ProcessorAffinityMaskReported);
        Assert.Null(WindowsPciResourceInformationReader.ParseInterrupt(8, DetailFrame("irq32").Bytes));
        Assert.Equal(IntPtr.Size, new WindowsPciResourceInformationNativeApi().DescriptorPointerBytes);
    }

    [Theory]
    [InlineData("short")] [InlineData("requirements")] [InlineData("type")]
    public void DmaParserRejectsTruncatedAndRequirementsHeaders(string fault)
    {
        var bytes = DetailFrame("dmaMax").Bytes;
        if (fault == "short") bytes = bytes[..15];
        else BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(fault == "type" ? 4 : 0), 1);
        Assert.Null(WindowsPciResourceInformationReader.ParseDmaChannel(bytes));
    }

    [Fact]
    public void OneAllocatedDirectorySuppliesAllThreeCategoriesAndReleasesEveryHandleInReverseOrder()
    {
        var api = MixedApi(); var value = Details(api);
        Assert.Equal(4, value.Ranges!.Count); Assert.Equal(2, value.Interrupts!.Count); Assert.Equal(2, value.DmaChannels!.Count);
        Assert.Equal("9007199254740993", value.Interrupts[0].ProcessorAffinityMaskReported);
        Assert.Equal(uint.MaxValue, value.DmaChannels[1].ChannelReported);
        Assert.Equal(1, api.Opens); Assert.Equal(2, api.IdentityCalls); Assert.Equal(8, api.SizeCalls); Assert.Equal(8, api.DataCalls);
        Assert.Equal(Enumerable.Range(0, 8).Reverse().Select(index => (false, (nuint)(1000 + index))).Append((true, (nuint)10)), api.Released);
    }

    [Theory]
    [InlineData(1U, "size")] [InlineData(1U, "data")] [InlineData(1U, "throw-size")] [InlineData(1U, "throw-data")]
    [InlineData(4U, "size")] [InlineData(4U, "data")] [InlineData(4U, "throw-size")] [InlineData(4U, "throw-data")]
    [InlineData(3U, "size")] [InlineData(3U, "data")] [InlineData(3U, "throw-size")] [InlineData(3U, "throw-data")]
    public void FailedDescriptorClearsOnlyItsCategoryAndDirectoryStillCompletes(uint type, string fault)
    {
        var api = MixedApi(); var index = api.Descriptors.FindIndex(frame => frame.Type == type);
        api.Fault = fault; api.FaultResource = (nuint)(1000 + index); var value = Details(api);
        AssertCategories(value, type); Assert.Equal(9, api.Released.Count); Assert.Equal(2, api.IdentityCalls);
        Assert.Equal(type == 1 ? 5 : 7, api.SizeCalls);
        Assert.Equal(api.SizeCalls - (fault.Contains("size", StringComparison.Ordinal) ? 1 : 0), api.DataCalls);
    }

    [Theory]
    [InlineData(1U)] [InlineData(4U)] [InlineData(3U)]
    public void MalformedCategoryDoesNotEraseOtherAllocatedReports(uint type)
    {
        var api = MixedApi(); var index = api.Descriptors.FindIndex(frame => frame.Type == type);
        BinaryPrimitives.WriteUInt32LittleEndian(api.Descriptors[index].Bytes, 1);
        AssertCategories(Details(api), type); Assert.Equal(9, api.Released.Count); Assert.Equal(2, api.IdentityCalls);
    }

    [Theory]
    [InlineData(1U, 0U)] [InlineData(1U, 1048577U)] [InlineData(4U, 0U)] [InlineData(4U, 1048577U)]
    [InlineData(3U, 0U)] [InlineData(3U, 1048577U)]
    public void InvalidSizeAffectsOnlyThatCategoryWithoutAllocatingOrReadingIt(uint type, uint size)
    {
        var api = MixedApi(); api.SizeOverride = size; api.FaultResource = (nuint)(1000 + api.Descriptors.FindIndex(frame => frame.Type == type));
        AssertCategories(Details(api), type); Assert.Equal(9, api.Released.Count);
        Assert.Equal(type == 1 ? 4 : 6, api.DataCalls);
    }

    [Theory]
    [InlineData("first")] [InlineData("next-config")] [InlineData("next-resource")]
    [InlineData("identity-before")] [InlineData("identity-after")]
    [InlineData("release-config")] [InlineData("release-resource")] [InlineData("throw-release")]
    [InlineData("loop-config")] [InlineData("loop-resource")]
    public void DirectoryIdentityOrCleanupFailureInvalidatesAllAllocatedCategories(string fault)
    {
        var api = MixedApi(); api.Fault = fault; Assert.Equal(DeviceTopologyPciResourceInformation.Unavailable, Details(api));
        Assert.Equal(api.Released.Count, api.Released.Distinct().Count());
        if (fault.Contains("release", StringComparison.Ordinal)) Assert.Equal(9, api.Released.Count);
        if (fault == "identity-before") Assert.Equal(0, api.Opens);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)]
    public void SuccessfulAbsencePublishesThreeEmptyCategories(int configurations)
    {
        var api = new Api { ConfigurationCount = configurations, Descriptors = [] }; var value = Details(api);
        Assert.Empty(value.Ranges!); Assert.Empty(value.Interrupts!); Assert.Empty(value.DmaChannels!); Assert.Equal(0, api.SizeCalls);
    }

    [Fact]
    public void UnknownClientWidthClearsOnlyInterruptsAndDuplicateReportsRemainUnmodified()
    {
        var api = MixedApi(); api.DescriptorPointerBytes = 16; AssertCategories(Details(api), 4);
        api.DescriptorPointerBytes = 8; api.Descriptors = [DetailFrame("zero"), DetailFrame("zero"), DetailFrame("dma0"), DetailFrame("dma0")];
        var value = Details(api); Assert.Empty(value.Ranges!); Assert.Equal(2, value.Interrupts!.Count); Assert.Equal(value.Interrupts[0], value.Interrupts[1]);
        Assert.Equal(2, value.DmaChannels!.Count); Assert.Equal(value.DmaChannels[0], value.DmaChannels[1]);
    }

    [Fact]
    public void OriginalPciIdentityProjectionPersistenceAndSemanticComparisonIncludeBothDetailLists()
    {
        var raw = DetailSnapshot(MixedApi()); var port = raw.Ports[0];
        var value = new DeviceTopologyPciResourceInformation(port.PciResourceRanges, port.PciInterrupts, port.PciDmaChannels);
        var native = Native(Id, null) with { PciResources = value };
        Assert.Same(value, WindowsPciResourceInformationReader.ReadForDevice(Id.ToLowerInvariant(), native));
        Assert.Equal(DeviceTopologyPciResourceInformation.Unavailable, WindowsPciResourceInformationReader.ReadForDevice(@"PCI\OTHER", native));
        Assert.Equal(DeviceTopologyPciResourceInformation.Unavailable, WindowsPciResourceInformationReader.ReadForDevice(@"USB\SAME", native with { DeviceId = @"USB\SAME" }));
        var restored = JsonSerializer.Deserialize<DeviceTopologySnapshot>(JsonSerializer.Serialize(raw))!;
        Assert.Equal(port.PciInterrupts, restored.Ports[0].PciInterrupts); Assert.Equal(port.PciDmaChannels, restored.Ports[0].PciDmaChannels);
        var comparer = new DeviceTopologySemanticComparer(); Assert.Equal(comparer.ComputeHash(raw), comparer.ComputeHash(restored));
        Assert.NotEqual(comparer.ComputeHash(raw), comparer.ComputeHash(raw with { Ports = [port with { PciInterrupts = [] }] }));
        Assert.NotEqual(comparer.ComputeHash(raw), comparer.ComputeHash(raw with { Ports = [port with { PciDmaChannels = null }] }));
        var current = DeviceTopologyInformationProjection.Create(raw, DateTimeOffset.UtcNow);
        var irq = DeviceInformationTestData.Field(current, Identity, InterruptField); var dma = DeviceInformationTestData.Field(current, Identity, DmaField);
        Assert.Equal("allocated-interrupts", irq.Definition.ValueKind); Assert.Equal("record[]", dma.Definition.Unit);
        Assert.Equal("9007199254740993", irq.Value!.Value[0].GetProperty("processorAffinityMaskReported").GetString());
        Assert.Equal(0U, dma.Value!.Value[0].GetProperty("channelReported").GetUInt32());
        foreach (var name in new[] { InterruptField, DmaField })
            Assert.Null(DeviceInformationTestData.Field(DeviceTopologyInformationProjection.Create(restored, null), Identity, name).Value);
    }

    private static void AssertCategories(DeviceTopologyPciResourceInformation value, uint failedType)
    {
        if (failedType == 1) Assert.Null(value.Ranges); else Assert.Equal(4, value.Ranges!.Count);
        if (failedType == 4) Assert.Null(value.Interrupts); else Assert.Equal(2, value.Interrupts!.Count);
        if (failedType == 3) Assert.Null(value.DmaChannels); else Assert.Equal(2, value.DmaChannels!.Count);
    }
}
