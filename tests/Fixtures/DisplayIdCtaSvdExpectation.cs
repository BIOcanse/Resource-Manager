using System.Text.Json;

namespace ResourceManager.TestFixtures;

/// <summary>Independent expected declaration facts shared by the two package-only consumers.</summary>
internal static class DisplayIdCtaSvdExpectation
{
    internal static void Verify(JsonElement value, byte version)
    {
        var modes = value.GetProperty("modes");
        Require(modes.GetArrayLength() == 4 && value.GetProperty("ranges").GetArrayLength() == 0
            && value.GetProperty("selectionDeclarations").GetArrayLength() == 0, "exact four declarations, no synthetic range or selection");
        Require(value.GetProperty("ctaFlags").ValueKind == JsonValueKind.Null, "no invented CTA extension header");
        var first = modes[0];
        Require(first.GetProperty("encoding").GetString() == "displayid-x" && first.GetProperty("byteOffset").GetUInt32() == 8
            && first.GetProperty("width").GetUInt32() == 1920 && first.GetProperty("height").GetUInt32() == 1080
            && first.GetProperty("nominalVerticalRateHz").GetUInt32() == 60, "prior TypeX declaration retained");
        Require(first.GetProperty("rawDescriptor").EnumerateArray().Select(item => item.GetByte()).SequenceEqual(new byte[] { 0x12, 0x7F, 7, 0x37, 4, 0x3B }), "prior TypeX original bytes");
        uint[] codes = [16, 65, 193], widths = [1920, 1280, 5120], heights = [1080, 720, 2160], rates = [60, 24, 120];
        byte[] raw = [0x90, 0x41, 0xC1];
        for (var i = 0; i < 3; i++)
        {
            var mode = modes[i + 1];
            Require(mode.GetProperty("encoding").GetString() == "displayid-cta-svd" && mode.GetProperty("blockIndex").GetUInt32() == 1
                && mode.GetProperty("byteOffset").GetUInt32() == 18 + i && mode.GetProperty("revision").GetByte() == 0
                && mode.GetProperty("containerFlags").GetByte() == 0 && mode.GetProperty("containerRevision").GetByte() == version,
                "real embedded wrapper, revision, version and physical location");
            Require(mode.GetProperty("code").GetUInt32() == codes[i] && mode.GetProperty("width").GetUInt32() == widths[i]
                && mode.GetProperty("height").GetUInt32() == heights[i] && mode.GetProperty("nominalVerticalRateHz").GetUInt32() == rates[i]
                && mode.GetProperty("native").GetBoolean() == (i == 0) && !mode.GetProperty("interlaced").GetBoolean()
                && !mode.GetProperty("yCbCr420Only").GetBoolean(), "full VIC including193, native bit and independent nominal facts");
            Require(mode.GetProperty("rawDescriptor").EnumerateArray().Select(item => item.GetByte()).SequenceEqual(new byte[] { raw[i] }), "exact original SVD byte");
            foreach (var name in new[] { "timing", "preferred", "cvtParameters", "dscPassThrough", "yCbCr420Supported", "yCbCr420CapabilityMap" })
                Require(mode.GetProperty(name).ValueKind == JsonValueKind.Null, "unreported field remains null: " + name);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("[EDID-EMBEDDED-SVD] " + message);
    }
}
