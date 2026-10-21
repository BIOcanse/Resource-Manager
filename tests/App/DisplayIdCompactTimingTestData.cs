using ResourceManager.ReadWrite.Windows.Devices.Display;

namespace Resource_Manager_APP.Tests;

internal static class DisplayIdCompactTimingTestData
{
    // Independent discriminating vector: 65MHz, 1024/160/48/32, 768/22/3/5.
    // Byte9 and byte10 intentionally differ; the polarity is only in byte3.
    internal static byte[] Descriptor() => [0x63, 0x19, 0, 0x8C, 0x7F, 0x26, 0x53, 0xFF, 2, 0x15, 0x24];

    internal static byte[] Bytes(byte[]? payload = null, byte header = 0, byte version = 0x12, byte tag = 4)
    {
        payload ??= Descriptor();
        var bytes = new byte[256];
        new byte[] { 0, 255, 255, 255, 255, 255, 255, 0 }.CopyTo(bytes, 0);
        bytes[18] = 1; bytes[19] = 4; bytes[20] = 0xB5; bytes[126] = 1;
        for (var i = 38; i < 54; i += 2) { bytes[i] = 1; bytes[i + 1] = 1; }
        bytes[128] = 0x70; bytes[129] = version; bytes[130] = checked((byte)(3 + payload.Length)); bytes[131] = 2;
        bytes[133] = tag; bytes[134] = header; bytes[135] = checked((byte)payload.Length);
        payload.CopyTo(bytes, 136); Recheck(bytes);
        return bytes;
    }

    internal static void Recheck(byte[] bytes)
    {
        var end = 133 + bytes[130]; bytes[end] = 0;
        var sum = 0; for (var i = 129; i < end; i++) sum += bytes[i];
        bytes[end] = unchecked((byte)-sum); OuterChecksums(bytes);
    }

    internal static void OuterChecksums(byte[] bytes)
    {
        for (var block = 0; block < bytes.Length; block += 128)
        {
            bytes[block + 127] = 0; var sum = 0;
            for (var i = block; i < block + 127; i++) sum += bytes[i];
            bytes[block + 127] = unchecked((byte)-sum);
        }
    }

    internal static EdidModeDeclaration Mode(byte[] bytes)
        => Assert.Single(Assert.IsType<EdidModeCapability>(EdidParser.Parse(bytes)!.ModeCapability).Modes);

    internal static void AssertUnknown(byte[] bytes)
    {
        var read = WindowsMonitorEdidReader.CreateReadResult(bytes);
        Assert.Null(read.Capabilities?.ModeCapability);
        Assert.Equal(bytes, Convert.FromBase64String(read.RawEdidDeclaredBase64!));
    }
}
