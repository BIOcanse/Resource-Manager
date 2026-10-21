namespace ResourceManager.TestFixtures;

/// <summary>Independent literal TypeX, ordinary SVD, only420 VIC97 and bitmap5 declarations.</summary>
internal static class DisplayIdCtaColourFixture
{
    internal const string Sha256Version20 = "FDC32DA72FCDAA0F93DAFA5550C519F2B4B5CCBAB414092AAACE829B0717E307";
    internal const string Sha256Version21 = "57D6AE976C78C7DE4C8EDD865131006E1274DE456F07AF4852320DA245D16E68";
    private const string Literal = "AP///////wAAAAAAAAAAAAAAAQS1AAAAAAAAAAAAAAAAAAAAAAABAQEBAQEBAQEBAQEBAQEBAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAATtwIBYCACoABhJ/BzcEO4EACkOQQcHiDmHiDwXjAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAkA==";

    internal static byte[] Bytes(byte version = 0x20)
    {
        if (version is not (0x20)) throw new ArgumentOutOfRangeException(nameof(version));
        var bytes = Convert.FromBase64String(Literal);
        bytes[129] = version;
        bytes[155] = 0; var sum = 0;
        for (var i = 129; i < 155; i++) sum += bytes[i]; bytes[155] = unchecked((byte)-sum);
        bytes[255] = 0; sum = 0;
        for (var i = 128; i < 255; i++) sum += bytes[i]; bytes[255] = unchecked((byte)-sum);
        return bytes;
    }
}
