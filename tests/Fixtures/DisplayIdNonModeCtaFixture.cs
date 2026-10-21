namespace ResourceManager.TestFixtures;

/// <summary>Independent literal input, not a live display or production-generated timing.</summary>
internal static class DisplayIdNonModeCtaFixture
{
    internal const string Sha256 = "1F1DA54864C86542EC8D63AD079BAD9079A0F2D50006BD50647FA3948B12D412";
    private const string Literal = "AP///////wAAAAAAAAAAAAAAAQS1AAAAAAAAAAAAAAAAAAAAAAABAQEBAQEBAQEBAQEBAQEBAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAATtwIC0CACoABhJ/BzcEOyoQBwJ/BzcEKwEqIAibfwc3BDtYAYEADCMJBwfjBgUB4wWAAOMAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAkA==";

    internal static byte[] Bytes(byte version = 0x20, bool unknownTiming = false, bool modeBearingCta = false)
    {
        if (version is not (0x20)) throw new ArgumentOutOfRangeException(nameof(version));
        var bytes = Convert.FromBase64String(Literal);
        bytes[129] = version;
        if (unknownTiming) bytes[155] = 0;
        if (modeBearingCta) bytes[166] = 0xC3; // Unsupported VFDB; ordinary embedded VDB is now supported.
        Recheck(bytes);
        return bytes;
    }

    internal static void SetModeBearingCta(byte[] bytes, bool modeBearing)
    {
        if (bytes.Length != 256 || bytes[130] != 45 || bytes[163] != 0x81)
            throw new ArgumentException("Expected the independent TypeX plus CTA metadata fixture.");
        bytes[166] = modeBearing ? (byte)0xC3 : (byte)0x23;
        Recheck(bytes);
    }

    private static void Recheck(byte[] bytes)
    {
        bytes[178] = 0; var sum = 0;
        for (var i = 129; i < 178; i++) sum += bytes[i]; bytes[178] = unchecked((byte)-sum);
        bytes[255] = 0; sum = 0;
        for (var i = 128; i < 255; i++) sum += bytes[i]; bytes[255] = unchecked((byte)-sum);
    }
}
