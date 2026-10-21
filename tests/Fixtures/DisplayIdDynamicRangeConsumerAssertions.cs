using System.Text.Json;

namespace ResourceManager.TestFixtures;

internal static class DisplayIdDynamicRangeConsumerAssertions
{
    internal static void Verify(JsonElement value)
    {
        Require(value.GetProperty("modes").GetArrayLength() == 0 && value.GetProperty("ranges").GetArrayLength() == 0, "no invented modes or legacy range");
        var range = value.GetProperty("dynamicTimingRanges").EnumerateArray().Single();
        Require(range.GetProperty("blockIndex").GetUInt32() == 1 && range.GetProperty("byteOffset").GetUInt32() == 8
            && range.GetProperty("revision").GetByte() == 1 && range.GetProperty("containerFlags").GetByte() == 1
            && range.GetProperty("containerRevision").GetByte() == 0x20, "physical declaration identity");
        Require(range.GetProperty("minimumPixelClockHz").GetUInt64() == 60_000_000
            && range.GetProperty("maximumPixelClockHz").GetUInt64() == 600_000_000, "declared clocks");
        Require(range.GetProperty("minimumVerticalRateHz").GetUInt32() == 48
            && range.GetProperty("maximumVerticalRateHz").GetUInt32() == 360, "ten-bit vertical rate");
        Require(range.GetProperty("seamlessWithFixedHorizontalPixelRateAndDynamicVerticalBlanking").GetBoolean(), "specific seamless declaration");
        Require(range.GetProperty("rawDescriptor").EnumerateArray().Select(static v => v.GetByte())
            .SequenceEqual(DisplayIdDynamicRangeFixture.Payload()), "original payload");
    }

    private static void Require(bool condition, string reason)
    { if (!condition) throw new InvalidDataException("DisplayID dynamic range package fixture: " + reason); }
}
