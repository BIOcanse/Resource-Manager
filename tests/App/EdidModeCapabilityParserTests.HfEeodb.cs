using ResourceManager.ReadWrite.Windows.Devices.Display;

namespace Resource_Manager_APP.Tests;

public sealed partial class EdidModeCapabilityParserTests
{
    [Fact]
    public void HfEeodbThreeCompleteBlocksDeliverTailModesAndGlobal420ReferencesBeyondBaseCount()
    {
        var bytes = HfChain(2);
        SetCta(bytes, 2, [0xE2, 0x0F, 0x02, 0x41, 16]);
        var modes = Capability(bytes).Modes;
        Assert.Equal(1, bytes[126]); Assert.Equal(384, bytes.Length); Assert.Equal(2, modes.Length);
        Assert.Equal(new uint?[] { 4, 16 }, modes.Select(static mode => mode.Code));
        Assert.Equal(new uint[] { 1, 2 }, modes.Select(static mode => mode.BlockIndex));
        Assert.Equal(new uint[] { 8, 8 }, modes.Select(static mode => mode.ByteOffset));
        Assert.Equal((1280u, 720u), (modes[0].Width!.Value, modes[0].Height!.Value));
        Assert.Equal((1920u, 1080u), (modes[1].Width!.Value, modes[1].Height!.Value));
        Assert.False(modes[0].YCbCr420Supported); Assert.True(modes[1].YCbCr420Supported);
        Assert.Equal(2u, modes[0].YCbCr420CapabilityMap!.BlockIndex);
        Assert.Equal(4u, modes[0].YCbCr420CapabilityMap!.ByteOffset);
        Assert.Equal(new byte[] { 4 }, modes[0].RawDescriptor.ToArray());
        Assert.Equal(new byte[] { 16 }, modes[1].RawDescriptor.ToArray());
        Assert.All(modes, static mode => { Assert.Equal(60u, mode.NominalVerticalRateHz); Assert.Null(mode.Timing); });
        Assert.Equal(bytes, Convert.FromBase64String(WindowsMonitorEdidReader.CreateReadResult(bytes).RawEdidDeclaredBase64!));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(255)]
    public void HfEeodbUsesEntireByteCountWithoutSoftwareTruncation(byte extensions)
    {
        var bytes = HfChain(extensions);
        var modes = Capability(bytes).Modes;
        Assert.Equal((extensions + 1) * 128, bytes.Length);
        Assert.Equal((int)extensions, modes.Length);
        Assert.Equal((uint)extensions, modes[^1].BlockIndex);
        Assert.Equal(16u, modes[^1].Code);
    }

    [Fact]
    public void HfEeodbValidEmptyWholeChainIsNotUnknown()
    {
        var bytes = Base(2); bytes[126] = 1;
        SetCta(bytes, 1, [0xE2, 0x78, 2]); SetCta(bytes, 2, []);
        var capability = Capability(bytes);
        Assert.Empty(capability.Modes); Assert.Empty(capability.Ranges);
    }

    [Theory]
    [InlineData(0)] // Missing complete last block.
    [InlineData(1)] // Last checksum bad.
    [InlineData(2)] // Last extension unsupported, after valid earlier modes.
    [InlineData(3)] // Last VDB has a reserved code.
    [InlineData(4)] // Last map references beyond the global two-VDB list.
    [InlineData(5)] // Extra valid complete block is not silently dropped.
    [InlineData(6)] // Extra partial byte is not silently dropped.
    [InlineData(7)] // First extension checksum bad.
    [InlineData(8)] // Base checksum bad.
    public void HfEeodbAnyInvalidTailOrReferenceRejectsWholeValueAndRetainsFullRaw(int variant)
    {
        var bytes = HfChain(2); Dtd().CopyTo(bytes, 54); Checksums(bytes);
        switch (variant)
        {
            case 0: Array.Resize(ref bytes, 256); break;
            case 1: bytes[383] ^= 1; break;
            case 2: bytes[256] = 0x40; Checksums(bytes); break;
            case 3: Array.Clear(bytes, 256, 128); SetCta(bytes, 2, [0x41, 0]); Checksums(bytes); break;
            case 4: Array.Clear(bytes, 256, 128); SetCta(bytes, 2, [0xE2, 0x0F, 0x04, 0x41, 16]); Checksums(bytes); break;
            case 5: Array.Resize(ref bytes, 512); SetCta(bytes, 3, []); Checksums(bytes); break;
            case 6: Array.Resize(ref bytes, 385); break;
            case 7: bytes[255] ^= 1; break;
            default: bytes[127] ^= 1; break;
        }
        AssertHfUnknownAndRaw(bytes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void HfEeodbReservedCountsRejectEvenWithChecksumsAndEarlierDetailedMode(byte count)
    {
        var bytes = HfChain(2); Dtd().CopyTo(bytes, 54); bytes[134] = count; Checksums(bytes);
        AssertHfUnknownAndRaw(bytes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(255)]
    public void HfEeodbRequiresBaseExtensionFlagExactlyOne(byte flag)
    {
        var bytes = HfChain(2); Dtd().CopyTo(bytes, 54); bytes[126] = flag; Checksums(bytes);
        AssertHfUnknownAndRaw(bytes);
    }

    [Theory]
    [InlineData(0)] // After an ordinary VDB at offset 6, not offset 4.
    [InlineData(1)] // In block 2 rather than block 1.
    [InlineData(2)] // Twice in block 1.
    [InlineData(3)] // Correct first occurrence plus another in block 2.
    public void HfEeodbAbsoluteLocationAndUniquenessAreValidatedAcrossFullChain(int variant)
    {
        var bytes = HfChain(2); Dtd().CopyTo(bytes, 54);
        switch (variant)
        {
            case 0:
                bytes[126] = 2; Array.Clear(bytes, 128, 128); SetCta(bytes, 1, [0x41, 4, 0xE2, 0x78, 2]); break;
            case 1:
                bytes[126] = 2; Array.Clear(bytes, 128, 256);
                SetCta(bytes, 1, [0x41, 4]); SetCta(bytes, 2, [0xE2, 0x78, 2, 0x41, 16]); break;
            case 2:
                Array.Clear(bytes, 128, 128); SetCta(bytes, 1, [0xE2, 0x78, 2, 0xE2, 0x78, 2, 0x41, 4]); break;
            default:
                Array.Clear(bytes, 256, 128); SetCta(bytes, 2, [0xE2, 0x78, 2, 0x41, 16]); break;
        }
        Checksums(bytes); AssertHfUnknownAndRaw(bytes);
    }

    [Theory]
    [InlineData(0)] // Extended tag without count, whole base length otherwise valid.
    [InlineData(1)] // Two count/payload bytes instead of exactly one.
    [InlineData(2)] // Data collection ends before the count.
    [InlineData(3)] // No declared data collection despite nonzero override bytes.
    [InlineData(4)] // Block claims no payload for extended tag.
    public void HfEeodbMalformedPayloadAndCollectionCannotBeAcceptedAsComplete(int variant)
    {
        var bytes = Base(1); Dtd().CopyTo(bytes, 54);
        SetCta(bytes, 1, variant switch {
            0 => [0xE1, 0x78], 1 => [0xE3, 0x78, 2, 0],
            4 => [0xE0], _ => [0xE2, 0x78, 2] });
        if (variant == 2) bytes[130] = 6;
        if (variant == 3) bytes[130] = 0;
        Checksums(bytes); AssertHfUnknownAndRaw(bytes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void WithoutHfEeodbOriginalBaseCountRemainsAuthoritative(int extensions)
    {
        var bytes = Base(extensions);
        for (var block = 1; block <= extensions; block++) SetCta(bytes, block, [0x41, 16]);
        Assert.Equal(extensions, Capability(bytes).Modes.Length);
    }

    [Fact]
    public void OrdinaryVdbVic78IsNotMisidentifiedAsAnOverride()
    {
        var mode = Assert.Single(Capability(Cta([0x41, 0x78])).Modes);
        Assert.Equal(120u, mode.Code); Assert.Equal(1u, mode.BlockIndex);
    }

    private static byte[] HfChain(byte extensions)
    {
        var bytes = Base(extensions); bytes[126] = 1;
        SetCta(bytes, 1, [0xE2, 0x78, extensions, 0x41, 4]);
        for (var block = 2; block <= extensions; block++) SetCta(bytes, block, [0x41, 16]);
        return Checked(bytes);
    }

    private static void AssertHfUnknownAndRaw(byte[] bytes)
    {
        var read = WindowsMonitorEdidReader.CreateReadResult(bytes);
        Assert.True(read.Complete); Assert.NotNull(read.Capabilities);
        Assert.Null(read.Capabilities.ModeCapability); Assert.Equal(10u, read.Capabilities.BitsPerColorChannel);
        Assert.Equal(bytes, Convert.FromBase64String(read.RawEdidDeclaredBase64!));
    }
}
