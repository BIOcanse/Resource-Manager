using System.Text.Json;

namespace ResourceManager.TestFixtures;

internal static class DisplayIdAdaptiveSyncFixture
{
    internal static byte[] Bytes(int variant) => Convert.FromBase64String(variant switch
    {
        0 => "AP///////wAAAAAAAAAAAAAAAQS1AAAAAAAAAAAAAAAAAAAAAAABAQEBAQEBAQEBAQEBAQEBAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAATtwIHkCACsABgEAMI8AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAB0kA==",
        1 => "AP///////wAAAAAAAAAAAAAAAQS1AAAAAAAAAAAAAAAAAAAAAAABAQEBAQEBAQEBAQEBAQEBAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAATtwIHkCACsABiYBAf8D/wAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAALkA==",
        _ => throw new ArgumentOutOfRangeException(nameof(variant))
    });

    internal static byte[] Payload(int variant) => variant switch
    {
        0 => [1,0,48,143,0,0],
        1 => [0x26,1,1,255,3,255],
        _ => throw new ArgumentOutOfRangeException(nameof(variant))
    };
}

internal static class DisplayIdAdaptiveSyncConsumerAssertions
{
    internal static void Verify(JsonElement value, int variant)
    {
        Require(variant is 0 or 1, "known independent input");
        Require(value.GetProperty("modes").GetArrayLength() == 0
            && value.GetProperty("ranges").GetArrayLength() == 0
            && value.GetProperty("dynamicTimingRanges").GetArrayLength() == 0, "no invented mode/range");
        var range = value.GetProperty("adaptiveSyncRanges").EnumerateArray().Single();
        Require(range.GetProperty("blockIndex").GetUInt32() == 1
            && range.GetProperty("byteOffset").GetUInt32() == 8
            && range.GetProperty("containerRevision").GetByte() == 0x20
            && range.GetProperty("containerFlags").GetByte() == 0
            && range.GetProperty("revision").GetByte() == 0, "original physical identity");
        Require(range.GetProperty("baseMinimumRefreshRateHz").GetUInt32() == (variant == 0 ? 48u : 1u)
            && range.GetProperty("baseMaximumRefreshRateHz").GetUInt32() == (variant == 0 ? 144u : 1024u), "base integers, not actual Hz");
        Require(range.GetProperty("nativePanelRange").GetBoolean() == (variant == 0)
            && range.GetProperty("adaptiveVTotalSupported").GetBoolean() == (variant == 1)
            && range.GetProperty("seamlessTransitionSupported").GetBoolean(), "independent operation flags");
        Require(range.GetProperty("rawDescriptor").EnumerateArray().Select(x => x.GetByte())
            .SequenceEqual(DisplayIdAdaptiveSyncFixture.Payload(variant)), "literal six bytes");
        Tolerance(range.GetProperty("frameDurationIncrease"), variant, 250);
        Tolerance(range.GetProperty("frameDurationDecrease"), variant, 63750);
    }

    private static void Tolerance(JsonElement value, int variant, uint microseconds)
    {
        Require(value.GetProperty("flickerRestrictedToSingleStep").GetBoolean() == (variant == 1), "restriction flag");
        var duration = value.GetProperty("maximumSingleStepMicroseconds");
        Require(variant == 0 ? duration.ValueKind == JsonValueKind.Null
            : duration.GetUInt32() == microseconds, "zero special or exact quarter-ms unit");
        Require(value.GetProperty("zeroCodeMeansNoJitterThroughoutRange").GetBoolean() == (variant == 0), "explicit zero-code no-jitter meaning");
    }

    private static void Require(bool condition, string reason)
    { if (!condition) throw new InvalidDataException("Adaptive-Sync package fixture: " + reason); }
}
