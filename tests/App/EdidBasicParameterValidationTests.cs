using System.Text.Json;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Domain.Messages;
using ResourceManager.App.Infrastructure.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;
using ResourceManager.App.Infrastructure.NativeCore;

namespace Resource_Manager_APP.Tests;

public sealed class EdidBasicParameterValidationTests
{
    [Theory]
    [InlineData(1, 0)]
    [InlineData(1, 1)]
    [InlineData(1, 2)]
    [InlineData(1, 3)]
    [InlineData(1, 5)]
    [InlineData(0, 4)]
    [InlineData(2, 4)]
    [InlineData(255, 255)]
    public void OtherVersionsCannotInterpretOnePointFourDigitalBits(int version, int revision)
    {
        var bytes = Bytes(); bytes[18] = (byte)version; bytes[19] = (byte)revision; Checksum(bytes);
        var result = WindowsMonitorEdidReader.CreateReadResult(bytes);
        Assert.Null(result.Capabilities!.BitsPerColorChannel); Assert.Equal(DeviceEdidDigitalInterfaces.Undefined, result.Capabilities.DigitalInterface);
        Assert.Equal(BackendMessageCodes.DeviceTopology.DisplayTechnologyDigital, WindowsDeviceTopologyReader.DescribeEdidDisplayTechnology(result.Capabilities)!.Code);
        Assert.Equal(bytes, Convert.FromBase64String(result.RawEdidDeclaredBase64!));
        Assert.Equal(600U, result.Capabilities.WidthMillimeters); Assert.Equal(340U, result.Capabilities.HeightMillimeters);
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(1, 6U)]
    [InlineData(2, 8U)]
    [InlineData(3, 10U)]
    [InlineData(4, 12U)]
    [InlineData(5, 14U)]
    [InlineData(6, 16U)]
    [InlineData(7, null)]
    public void OnePointFourBitDepthCodesKeepUndefinedAndReservedEmpty(int code, uint? depth)
    {
        var bytes = Bytes(); bytes[20] = (byte)(0x85 | (code << 4)); Checksum(bytes);
        var result = EdidParser.Parse(bytes)!; Assert.Equal(depth, result.BitsPerColorChannel);
        Assert.Equal(DeviceEdidDigitalInterfaces.DisplayPort, result.DigitalInterface);
    }

    [Theory]
    [InlineData(0, DeviceEdidDigitalInterfaces.Undefined)]
    [InlineData(1, DeviceEdidDigitalInterfaces.Dvi)]
    [InlineData(2, DeviceEdidDigitalInterfaces.HdmiTypeA)]
    [InlineData(3, DeviceEdidDigitalInterfaces.HdmiTypeB)]
    [InlineData(4, DeviceEdidDigitalInterfaces.Mddi)]
    [InlineData(5, DeviceEdidDigitalInterfaces.DisplayPort)]
    [InlineData(6, DeviceEdidDigitalInterfaces.Reserved)]
    [InlineData(7, DeviceEdidDigitalInterfaces.Reserved)]
    [InlineData(8, DeviceEdidDigitalInterfaces.Reserved)]
    [InlineData(9, DeviceEdidDigitalInterfaces.Reserved)]
    [InlineData(10, DeviceEdidDigitalInterfaces.Reserved)]
    [InlineData(11, DeviceEdidDigitalInterfaces.Reserved)]
    [InlineData(12, DeviceEdidDigitalInterfaces.Reserved)]
    [InlineData(13, DeviceEdidDigitalInterfaces.Reserved)]
    [InlineData(14, DeviceEdidDigitalInterfaces.Reserved)]
    [InlineData(15, DeviceEdidDigitalInterfaces.Reserved)]
    public void OnePointFourInterfaceCodesKeepKnownAndReservedDeclarationsSeparate(int code, string expected)
    {
        var bytes = Bytes(); bytes[20] = (byte)(0xB0 | code); Checksum(bytes);
        var result = EdidParser.Parse(bytes)!; Assert.Equal(expected, result.DigitalInterface); Assert.Equal(10U, result.BitsPerColorChannel);
    }

    [Theory]
    [InlineData(0x05)]
    [InlineData(0x35)]
    [InlineData(0x55)]
    [InlineData(0x75)]
    public void AnalogSignalLevelsAreNeverDigitalDepthOrInterface(int input)
    {
        var bytes = Bytes(); bytes[20] = (byte)input; Checksum(bytes);
        var result = EdidParser.Parse(bytes)!; Assert.Null(result.BitsPerColorChannel);
        Assert.Equal(DeviceEdidDigitalInterfaces.Analog, result.DigitalInterface);
        Assert.Equal(BackendMessageCodes.DeviceTopology.DisplayTechnologyAnalog, WindowsDeviceTopologyReader.DescribeEdidDisplayTechnology(result)!.Code);
    }

    [Theory]
    [InlineData(79, 0, null, null)]
    [InlineData(0, 79, null, null)]
    [InlineData(0, 0, null, null)]
    [InlineData(60, 34, 600U, 340U)]
    [InlineData(1, 255, 10U, 2550U)]
    [InlineData(255, 1, 2550U, 10U)]
    public void OnePointFourAspectRatioBytesAreNotPhysicalMillimeters(int x, int y, uint? width, uint? height)
    {
        var bytes = Bytes(); bytes[21] = (byte)x; bytes[22] = (byte)y; Checksum(bytes);
        var result = EdidParser.Parse(bytes)!; Assert.Equal(width, result.WidthMillimeters); Assert.Equal(height, result.HeightMillimeters);
        var connection = Assert.Single(Snapshot(bytes).Ports).Display!;
        Assert.Equal(width.HasValue ? $"{width} x {height} mm" : null, connection.PhysicalSize);
        Assert.Equal(bytes, Convert.FromBase64String(connection.EdidRawDeclaredBase64!));
    }

    [Fact]
    public void NormativeSampleUsesEightBitHdmiAndAspectRatioWithoutInventedDimensions()
    {
        // VESA A2 Annex B sample: A2 input and 4F/00 screen bytes (16:9 landscape).
        var bytes = Bytes(); bytes[20] = 0xA2; bytes[21] = 0x4F; bytes[22] = 0; Checksum(bytes);
        var result = EdidParser.Parse(bytes)!; Assert.Equal(8U, result.BitsPerColorChannel);
        Assert.Equal(DeviceEdidDigitalInterfaces.HdmiTypeA, result.DigitalInterface); Assert.Null(result.WidthMillimeters); Assert.Null(result.HeightMillimeters);
    }

    [Fact]
    public void CurrentPathModeRawCtaAndChromaticityStayIndependentOfBasicSize()
    {
        var bytes = DisplayHdrLuminanceInformationCollectorTests.Bytes(); bytes[21] = 79; bytes[22] = 0; Checksum(bytes);
        var result = WindowsMonitorEdidReader.CreateReadResult(bytes); Assert.NotNull(result.Capabilities!.HdrLuminanceDeclared);
        Assert.NotNull(result.Capabilities.ChromaticityDeclared); Assert.NotNull(result.Capabilities.HdrFormats);
        var fields = DeviceTopologyInformationProjection.Create(Snapshot(bytes), DateTimeOffset.UtcNow);
        Assert.Equal("90", DisplayHdrInformationCollectorTests.Field(fields, DisplayRotationInformationCollectorTests.Name).Value!.Value.GetString());
        Assert.Equal(bytes, Convert.FromBase64String(DisplayHdrInformationCollectorTests.Field(fields, DisplayEdidRawInformationCollectorTests.Name).Value!.Value.GetString()!));
        Assert.Equal("HDR10 / PQ", result.Capabilities.HdrFormats);
    }

    [Fact]
    public void CorrectedParametersUseOriginalTypedPayloadAndSemanticHash()
    {
        var bytes = Bytes(); var first = DisplayChromaticityInformationCollectorTests.Path(bytes);
        bytes[19] = 3; Checksum(bytes); var old = DisplayChromaticityInformationCollectorTests.Path(bytes);
        var catalog = new NativeDisplayCoordinatorPayloadCatalog(); Assert.NotEqual(catalog.AddDisplayPath(first), catalog.AddDisplayPath(old));
        var batches = NativeDisplayCoordinatorObservationProjector.Create([old], [], catalog, DateTimeOffset.UtcNow);
        var fact = Assert.Single(Assert.Single(batches, b => b.Source == NativeDisplaySource.DisplayConfig).Facts);
        var restored = new NativeDisplayCoordinatorPayloadCatalog(); restored.Import(catalog.Export());
        var resolved = Assert.Single(NativeDisplayCoordinatorReadProjection.CreateDisplayPaths(
            [new NativeDisplayNodeOutput { Kind = (uint)NativeDisplayNodeKind.Connector, PrimaryPayloadHandle = fact.PayloadHandle }], restored));
        Assert.Equal(JsonSerializer.Serialize(old), JsonSerializer.Serialize(resolved.Path)); Assert.Null(resolved.Path.Edid!.BitsPerColorChannel);
        Assert.NotEqual(new DeviceTopologySemanticComparer().ComputeHash(DisplayHdrInformationCollectorTests.Snapshot(first)),
            new DeviceTopologySemanticComparer().ComputeHash(DisplayHdrInformationCollectorTests.Snapshot(old)));
    }

    internal static byte[] Bytes()
    {
        var bytes = DisplayChromaticityInformationCollectorTests.Bytes(); bytes[21] = 60; bytes[22] = 34; Checksum(bytes); return bytes;
    }
    internal static void Checksum(byte[] bytes)
    {
        bytes[127] = 0; var sum = bytes.Take(128).Sum(static value => (int)value); bytes[127] = unchecked((byte)-sum);
    }
    internal static DeviceTopologySnapshot Snapshot(byte[] bytes) => DisplayChromaticityInformationCollectorTests.Snapshot(bytes);
}
