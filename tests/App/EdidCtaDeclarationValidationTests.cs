using ResourceManager.App.Infrastructure.DeviceTopology;

namespace Resource_Manager_APP.Tests;

public sealed class EdidCtaDeclarationValidationTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(128)]
    [InlineData(255)]
    public void EmptyOrInvalidDirectoryNeverInfersHdrFromReservedOrDetailedTimingBytes(int offset)
    {
        var bytes = DisplayHdrLuminanceInformationCollectorTests.Bytes(); bytes[130] = (byte)offset; Checksum(bytes.AsSpan(128, 128));
        var parsed = EdidParser.Parse(bytes)!; Assert.Null(parsed.HdrFormats);
        if (offset is 0 or 4) Assert.Empty(parsed.HdrLuminanceDeclared!); else Assert.Null(parsed.HdrLuminanceDeclared);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void OldCtaRevisionDoesNotTurnTimingDataIntoHdrCapabilities(int revision)
    {
        var bytes = DisplayHdrLuminanceInformationCollectorTests.Bytes(); bytes[129] = (byte)revision; Checksum(bytes.AsSpan(128, 128));
        Assert.Null(EdidParser.Parse(bytes)!.HdrFormats);
    }

    [Theory]
    [InlineData(127)]
    [InlineData(255)]
    public void InvalidBaseOrCtaChecksumClearsHdrCapabilitiesWithoutErasingRaw(int offset)
    {
        var bytes = DisplayHdrLuminanceInformationCollectorTests.Bytes(); bytes[offset] ^= 1;
        var result = WindowsMonitorEdidReader.CreateReadResult(bytes);
        Assert.Null(result.Capabilities!.HdrFormats); Assert.Null(result.Capabilities.HdrLuminanceDeclared);
        Assert.Equal(bytes, Convert.FromBase64String(result.RawEdidDeclaredBase64!));
        if (offset == 127) Assert.Null(result.Capabilities.ChromaticityDeclared);
        else Assert.NotNull(result.Capabilities.ChromaticityDeclared);
    }

    [Fact]
    public void IncompleteDeclaredTailDoesNotPublishPartialHdrCapabilities()
    {
        var bytes = DisplayHdrLuminanceInformationCollectorTests.Bytes(); bytes[126] = 2; Checksum(bytes.AsSpan(0, 128));
        Assert.Null(EdidParser.Parse(bytes)!.HdrFormats);
    }

    [Fact]
    public void MalformedHdrPrefixOrLaterBlockClearsBothSharedDeclarations()
    {
        var bytes = DisplayHdrLuminanceInformationCollectorTests.Bytes(); bytes[132] = 0xE2; bytes[130] = 7; Checksum(bytes.AsSpan(128, 128));
        Assert.Null(EdidParser.Parse(bytes)!.HdrFormats); Assert.Null(EdidParser.Parse(bytes)!.HdrLuminanceDeclared);
        bytes = DisplayHdrLuminanceInformationCollectorTests.Bytes(); bytes[139] = 0xE3; bytes[130] = 12; Checksum(bytes.AsSpan(128, 128));
        Assert.Null(EdidParser.Parse(bytes)!.HdrFormats); Assert.Null(EdidParser.Parse(bytes)!.HdrLuminanceDeclared);
    }

    [Fact]
    public void RepeatedHdrCapabilitiesAreASetWhileEachLuminanceDeclarationRemainsIndependent()
    {
        var bytes = DisplayHdrLuminanceInformationCollectorTests.Bytes();
        bytes[134] = 0x06;
        new byte[] { 0xE4, 6, 0x0C, 1, 32 }.CopyTo(bytes, 139); bytes[130] = 16; Checksum(bytes.AsSpan(128, 128));
        var result = EdidParser.Parse(bytes)!;
        Assert.Equal("Traditional HDR / HDR10 / PQ / HLG", result.HdrFormats);
        Assert.Equal(new double?[] { 800, 100 }, result.HdrLuminanceDeclared!.Select(static value => value.DesiredContentMaxLuminanceNits));
    }

    [Fact]
    public void ValidEmptyDirectoryKeepsOtherCapabilitiesAndRawAvailable()
    {
        var bytes = DisplayHdrLuminanceInformationCollectorTests.Bytes(); bytes[130] = 0; Checksum(bytes.AsSpan(128, 128));
        var result = WindowsMonitorEdidReader.CreateReadResult(bytes);
        Assert.Null(result.Capabilities!.HdrFormats); Assert.Empty(result.Capabilities.HdrLuminanceDeclared!);
        Assert.NotNull(result.Capabilities.ChromaticityDeclared); Assert.Equal(10u, result.Capabilities.BitsPerColorChannel);
        Assert.Equal(bytes, Convert.FromBase64String(result.RawEdidDeclaredBase64!));
    }

    private static void Checksum(Span<byte> block)
    {
        block[127] = 0; var sum = 0; foreach (var value in block) sum += value; block[127] = unchecked((byte)-sum);
    }
}
