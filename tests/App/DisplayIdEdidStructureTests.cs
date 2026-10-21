using ResourceManager.ReadWrite.Windows.Devices.Display;

namespace Resource_Manager_APP.Tests;

public sealed class DisplayIdEdidStructureTests
{
    private static byte[] Compact() => [4, 0, 11, 0x63, 0x19, 0, 0x8C, 0x7F, 0x26, 0x53, 0xFF, 2, 0x15, 0x24];
    private static byte[] Range() => [0x25, 1, 9, 0x5F, 0xEA, 0, 0xBF, 0x27, 9, 48, 104, 0x81];

    [Fact]
    public void LegacyBaseAndModernBaseHaveIndependentCountsAndRealPositions()
    {
        var bytes = Input(2);
        Section(bytes, 1, 0x12, 2, 0, Compact());
        Section(bytes, 2, 0x20, 2, 0, Range());
        var value = Capability(bytes);
        Assert.Equal((1u, (byte?)0x12), (Assert.Single(value.Modes).BlockIndex, value.Modes[0].ContainerRevision));
        var range = Assert.Single(value.DynamicTimingRanges);
        Assert.Equal((2u, 8u, (byte)0x20), (range.BlockIndex, range.ByteOffset, range.ContainerRevision));
        Assert.Equal(360u, range.MaximumVerticalRateHz);
        Assert.Equal(new byte[] { 0x70, 0x20, 121, 2, 0 }, bytes[256..261]);
        Assert.Equal((byte)0x90, bytes[383]);
    }

    [Fact]
    public void LegacyContinuationCountExcludesTheFollowingModernStructure()
    {
        var bytes = Input(3);
        Section(bytes, 1, 0x12, 2, 1, Compact());
        Section(bytes, 2, 0x12, 0, 0, Compact());
        Section(bytes, 3, 0x20, 2, 0, Range());
        var value = Capability(bytes);
        Assert.Equal(new uint[] { 1, 2 }, value.Modes.Select(m => m.BlockIndex));
        Assert.Equal(3u, Assert.Single(value.DynamicTimingRanges).BlockIndex);
        bytes[132] = 2; Checksums(bytes, 1);
        Unknown(bytes);
    }

    [Fact]
    public void ModernEdidSectionsUseZeroExtensionCountAndOnePrimaryUseCase()
    {
        var bytes = ModernPair();
        var value = Capability(bytes);
        Assert.Empty(value.Modes);
        Assert.Single(value.DynamicTimingRanges);
        Assert.Equal(new byte[] { 0x70, 0x20, 121, 0, 0 }, bytes[256..261]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void NativeCountsMissingBaseAndRepeatedUseCaseAreNotValidModernEdidHeaders(int variant)
    {
        var bytes = ModernPair();
        if (variant == 0) bytes[132] = 1;
        if (variant == 1) bytes[131] = 0;
        if (variant == 2) bytes[259] = 2;
        if (variant == 3) bytes[260] = 1;
        Checksums(bytes, variant < 2 ? 1 : 2);
        Unknown(bytes);
    }

    [Fact]
    public void BadModernTailCannotPublishTheAlreadyParsedLegacyPrefix()
    {
        var bytes = Input(2);
        Section(bytes, 1, 0x12, 2, 0, Compact());
        Section(bytes, 2, 0x20, 2, 0, Range());
        var inner = bytes.ToArray(); inner[264] ^= 1; Outer(inner, 2); Unknown(inner);
        var outer = bytes.ToArray(); outer[383] ^= 1; Unknown(outer);
        var tail = bytes.ToArray(); tail[273] = 0x7E; tail[275] = 1; Checksums(tail, 2); Unknown(tail);
        var missing = bytes[..256]; Unknown(missing);
    }

    private static byte[] ModernPair()
    {
        var bytes = Input(2);
        Section(bytes, 1, 0x20, 2, 0, Range());
        Section(bytes, 2, 0x20, 0, 0, []);
        return bytes;
    }

    private static byte[] Input(byte extensions)
    {
        var bytes = new byte[(extensions + 1) * 128];
        new byte[] { 0, 255, 255, 255, 255, 255, 255, 0 }.CopyTo(bytes, 0);
        bytes[18] = 1; bytes[19] = 4; bytes[20] = 0xB5; bytes[126] = extensions;
        for (var n = 38; n < 54; n += 2) { bytes[n] = 1; bytes[n + 1] = 1; }
        Outer(bytes, 0);
        return bytes;
    }

    private static void Section(byte[] bytes, int index, byte version, byte primaryUseCase, byte count, byte[] data)
    {
        var start = index * 128;
        bytes[start] = 0x70; bytes[start + 1] = version;
        bytes[start + 2] = version == 0x20 ? (byte)121 : checked((byte)data.Length);
        bytes[start + 3] = primaryUseCase; bytes[start + 4] = count;
        data.CopyTo(bytes, start + 5); Checksums(bytes, index);
    }

    private static void Checksums(byte[] bytes, int index)
    {
        var start = index * 128; var end = start + 5 + bytes[start + 2]; bytes[end] = 0;
        bytes[end] = unchecked((byte)-bytes[(start + 1)..end].Sum(b => b));
        Outer(bytes, index);
    }

    private static void Outer(byte[] bytes, int index)
    {
        var start = index * 128; bytes[start + 127] = 0;
        bytes[start + 127] = unchecked((byte)-bytes[start..(start + 127)].Sum(b => b));
    }

    private static EdidModeCapability Capability(byte[] bytes)
        => Assert.IsType<EdidModeCapability>(EdidParser.Parse(bytes)!.ModeCapability);

    private static void Unknown(byte[] bytes)
    {
        var result = WindowsMonitorEdidReader.CreateReadResult(bytes);
        Assert.Null(result.Capabilities?.ModeCapability);
        Assert.Equal(bytes, Convert.FromBase64String(result.RawEdidDeclaredBase64!));
    }
}
