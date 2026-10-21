using System.Text.Json;

namespace ResourceManager.TestFixtures;

internal static class DisplayIdCompactTimingFixture
{
    private const string TypeII = "AP///////wAAAAAAAAAAAAAAAQS1AAAAAAAAAAAAAAAAAAAAAAABAQEBAQEBAQEBAQEBAQEBAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAATtwEg4CAAQAC2MZAIx/JlP/AhUklQAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAkA==";
    private const string TypeI = "AP///////wAAAAAAAAAAAAAAAQS1AAAAAAAAAAAAAAAAAAAAAAABAQEBAQEBAQEBAQEBAQEBAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAATtwEhcCAAMBFAAdAJR/BxcBV4ArADcELAADgAkAeQAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAkA==";
    internal static byte[] Bytes(bool typeI) => Convert.FromBase64String(typeI ? TypeI : TypeII);

    internal static void SetUnknown(byte[] bytes, bool typeI, bool unknown)
    {
        if (bytes.Length != 256 || bytes[129] != 0x12) throw new InvalidDataException("compact fixture identity");
        if (typeI) bytes[150] = unknown ? (byte)43 : (byte)44; // Even versus odd declared total.
        else bytes[139] = unknown ? (byte)0x8D : (byte)0x8C; // Reserved descriptor bit.
        var end = 133 + bytes[130]; bytes[end] = 0; var sum = 0;
        for (var n = 129; n < end; n++) sum += bytes[n]; bytes[end] = unchecked((byte)-sum);
        bytes[255] = 0; sum = 0;
        for (var n = 128; n < 255; n++) sum += bytes[n]; bytes[255] = unchecked((byte)-sum);
    }

    internal static void Verify(JsonElement value, bool typeI)
    {
        var mode = value.GetProperty("modes").EnumerateArray().Single(); var t = mode.GetProperty("timing");
        Require(mode.GetProperty("encoding").GetString() == (typeI ? "displayid-i" : "displayid-ii"), "source encoding");
        Require(mode.GetProperty("blockIndex").GetUInt32() == 1 && mode.GetProperty("byteOffset").GetUInt32() == 8
            && mode.GetProperty("containerRevision").GetUInt32() == 0x12, "physical declaration identity");
        Require(mode.GetProperty("interlaced").GetBoolean() == typeI && mode.GetProperty("preferred").GetBoolean(), "scan/preferred");
        Require(t.GetProperty("pixelClockHz").GetUInt64() == (typeI ? 74_250_000UL : 65_000_000UL), "clock");
        Require(t.GetProperty("horizontalActive").GetUInt32() == (typeI ? 1920u : 1024u)
            && t.GetProperty("horizontalBlanking").GetUInt32() == (typeI ? 280u : 160u)
            && t.GetProperty("verticalActive").GetUInt32() == (typeI ? 1080u : 768u)
            && t.GetProperty("verticalBlanking").GetUInt32() == (typeI ? 45u : 22u), "declared frame geometry");
        Require(t.GetProperty("verticalSyncOffset").GetUInt32() == (typeI ? 4u : 3u)
            && t.GetProperty("verticalSyncWidth").GetUInt32() == (typeI ? 10u : 5u)
            && t.GetProperty("positiveVerticalSync").GetBoolean(), "normative sync bytes");
        var rate = t.GetProperty("frameRate");
        Require(rate.GetProperty("numerator").GetUInt64() == (typeI ? 30UL : 203125UL)
            && rate.GetProperty("denominator").GetUInt64() == (typeI ? 1UL : 2923UL), "exact declared frame rate");
        var field = t.GetProperty("fieldRate");
        Require(typeI ? field.GetProperty("numerator").GetUInt64() == 60 && field.GetProperty("denominator").GetUInt64() == 1
            : field.ValueKind == JsonValueKind.Null, "field rate");
        Require(mode.GetProperty("rawDescriptor").EnumerateArray().Select(v => v.GetByte())
            .SequenceEqual(Bytes(typeI).Skip(136).Take(typeI ? 20 : 11)), "owned original descriptor bytes");
    }

    private static void Require(bool condition, string reason)
    { if (!condition) throw new InvalidDataException("DisplayID1.3 package fixture: " + reason); }
}
