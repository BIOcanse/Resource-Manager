namespace ResourceManager.TestFixtures;

/// <summary>Independent literal standard CVT, RB1 and RB2 declarations with explicit stereo/fractional facts.</summary>
internal static class DisplayIdFormulaFixture
{
    internal const string Sha256Version20 = "629A54F02CC4CCFF015A897427C0836DBF7702D166B738E9C2199207E48F1174";
    internal const string Sha256Version21 = "B32DD22C287D9A3E065DB4B7ED91068B7FCF57026939DA49FCD98D171715447C";
    private const string Literal = "AP///////wAAAAAAAAAAAAAAAQS1AAAAAAAAAAAAAAAAAAAAAAABAQEBAQEBAQEBAQEBAQEBAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAATtwIBUCACQAEgB/BzcEOzH/Dm8Id1L//////x4AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAkA==";

    internal static byte[] Bytes(byte version = 0x20)
    {
        if (version is not (0x20)) throw new ArgumentOutOfRangeException(nameof(version));
        var bytes = Convert.FromBase64String(Literal);
        bytes[129] = version;
        Repair(bytes);
        return bytes;
    }

    internal static void Repair(byte[] bytes)
    {
        bytes[154] = 0; var sum = 0;
        for (var index = 129; index < 154; index++) sum += bytes[index];
        bytes[154] = unchecked((byte)-sum);
        bytes[255] = 0; sum = 0;
        for (var index = 128; index < 255; index++) sum += bytes[index];
        bytes[255] = unchecked((byte)-sum);
    }
}
