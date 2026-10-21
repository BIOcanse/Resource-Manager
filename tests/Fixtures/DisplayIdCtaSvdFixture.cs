namespace ResourceManager.TestFixtures;

/// <summary>Independent literal containing one TypeX and three embedded ordinary SVD declarations.</summary>
internal static class DisplayIdCtaSvdFixture
{
    internal const string Sha256 = "DD066D47BEF3DA4F7BCC1AE47B65A2589E67F4E048CF92509013F389FA443C42";
    private const string Literal = "AP///////wAAAAAAAAAAAAAAAQS1AAAAAAAAAAAAAAAAAAAAAAABAQEBAQEBAQEBAQEBAQEBAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAATtwIBgCACoABhJ/BzcEO4EADEOQQcEjCQcH4wYFAf0AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAkA==";

    internal static byte[] Bytes(byte version = 0x20, bool unsupportedCta = false)
    {
        if (version is not (0x20)) throw new ArgumentOutOfRangeException(nameof(version));
        var bytes = Convert.FromBase64String(Literal);
        bytes[129] = version;
        SetUnsupportedCta(bytes, unsupportedCta);
        return bytes;
    }

    internal static void SetUnsupportedCta(byte[] bytes, bool unsupported)
    {
        if (bytes.Length != 256 || bytes[130] != 24 || bytes[142] != 0x81)
            throw new ArgumentException("Expected the independent TypeX plus embedded SVD fixture.");
        bytes[145] = unsupported ? (byte)0xC3 : (byte)0x43;
        bytes[157] = 0; var sum = 0;
        for (var i = 129; i < 157; i++) sum += bytes[i]; bytes[157] = unchecked((byte)-sum);
        bytes[255] = 0; sum = 0;
        for (var i = 128; i < 255; i++) sum += bytes[i]; bytes[255] = unchecked((byte)-sum);
    }
}
