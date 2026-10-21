using System.Text.Json;

namespace ResourceManager.TestFixtures;

internal static class DisplayIdMixedStructureFixture
{
    private const string Literal = "AP///////wAAAAAAAAAAAAAAAQS1AAAAAAAAAAAAAAAAAAAAAAABAQEBAQEBAQEBAQEBAQEBAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAjpwEg4CAAQAC2MZAIx/JlP/AhUklQAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAkHAgeQIAJQEJX+oAvycJMGiBAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAOWQ";
    internal static byte[] Bytes() => Convert.FromBase64String(Literal);

    internal static void SetUnknown(byte[] bytes, bool unknown)
    {
        if (bytes.Length != 384 || bytes[129] != 0x12 || bytes[257] != 0x20 || bytes[258] != 121)
            throw new InvalidDataException("mixed EDID fixture identity");
        bytes[259] = unknown ? (byte)0 : (byte)2;
        bytes[382] = 0; var sum = 0;
        for (var n = 257; n < 382; n++) sum += bytes[n]; bytes[382] = unchecked((byte)-sum);
        bytes[383] = 0; sum = 0;
        for (var n = 256; n < 383; n++) sum += bytes[n]; bytes[383] = unchecked((byte)-sum);
    }

    internal static void Verify(JsonElement value)
    {
        DisplayIdCompactTimingFixture.Verify(value, false);
        var range = value.GetProperty("dynamicTimingRanges").EnumerateArray().Single();
        if (range.GetProperty("blockIndex").GetUInt32() != 2 || range.GetProperty("byteOffset").GetUInt32() != 8
            || range.GetProperty("containerRevision").GetByte() != 0x20 || range.GetProperty("revision").GetByte() != 1
            || range.GetProperty("minimumPixelClockHz").GetUInt64() != 60_000_000
            || range.GetProperty("maximumPixelClockHz").GetUInt64() != 600_000_000
            || range.GetProperty("minimumVerticalRateHz").GetUInt32() != 48
            || range.GetProperty("maximumVerticalRateHz").GetUInt32() != 360
            || !range.GetProperty("seamlessWithFixedHorizontalPixelRateAndDynamicVerticalBlanking").GetBoolean()
            || !range.GetProperty("rawDescriptor").EnumerateArray().Select(v => v.GetByte())
                .SequenceEqual(new byte[] { 0x5F,0xEA,0,0xBF,0x27,9,48,104,0x81 }))
            throw new InvalidDataException("mixed legacy12/modern20 declaration identity, values or original bytes");
    }
}
