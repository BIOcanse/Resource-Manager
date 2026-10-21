namespace ResourceManager.TestFixtures;

/// <summary>Independent literal TypeX, SVD/420/map and four explicit embedded HDMI VICs.</summary>
internal static class DisplayIdCtaHdmiFixture
{
    internal const string Sha256Version20 = "75520B3662503C503393DD23E9F32316945940D2CFD873C538075E7E0901010B";
    internal const string Sha256Version21 = "6D4F80E92287FBE4F9D0C33D83BAB5F89159ABAF3AF8ABE6A3AB175F27871131";
    private const string Literal = "AP///////wAAAAAAAAAAAAAAAQS1AAAAAAAAAAAAAAAAAAAAAAABAQEBAQEBAQEBAQEBAQEBAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAATtwICUCACoABhJ/BzcEO4EAGUOQQcHiDmHiDwVuAwwAEAAAACAAgAECAwSOAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAkA==";

    internal static byte[] Bytes(byte version = 0x20)
    {
        if (version is not (0x20)) throw new ArgumentOutOfRangeException(nameof(version));
        var bytes = Convert.FromBase64String(Literal);
        bytes[129] = version;
        bytes[170] = 0; var sum = 0;
        for (var index = 129; index < 170; index++) sum += bytes[index]; bytes[170] = unchecked((byte)-sum);
        bytes[255] = 0; sum = 0;
        for (var index = 128; index < 255; index++) sum += bytes[index]; bytes[255] = unchecked((byte)-sum);
        return bytes;
    }
}
