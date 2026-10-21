using System.Text.Json;

namespace ResourceManager.TestFixtures;

/// <summary>Independent expected colour facts consumed by App and package-only tests.</summary>
internal static class DisplayIdCtaColourExpectation
{
    internal static void Verify(JsonElement value, byte version)
    {
        var modes = value.GetProperty("modes");
        Require(modes.GetArrayLength() == 5 && value.GetProperty("ranges").GetArrayLength() == 0
            && value.GetProperty("selectionDeclarations").GetArrayLength() == 0, "five declarations with no synthetic ranges");
        Require(value.GetProperty("ctaFlags").ValueKind == JsonValueKind.Null, "real DisplayID without invented CTA flags");
        Require(modes[0].GetProperty("encoding").GetString() == "displayid-x"
            && modes[0].GetProperty("byteOffset").GetUInt32() == 8, "prior TypeX retained");
        uint[] codes = [16, 65, 193, 97], widths = [1920, 1280, 5120, 3840], heights = [1080, 720, 2160, 2160], rates = [60, 24, 120, 60];
        byte[] raw = [0x90, 65, 193, 97]; uint[] offsets = [18, 19, 20, 23]; bool[] supported = [true, false, true, true];
        for (var index = 0; index < 4; index++)
        {
            var mode = modes[index + 1]; var only = index == 3;
            Require(mode.GetProperty("encoding").GetString() == (only ? "displayid-cta-y420-vic" : "displayid-cta-svd")
                && mode.GetProperty("blockIndex").GetUInt32() == 1 && mode.GetProperty("byteOffset").GetUInt32() == offsets[index]
                && mode.GetProperty("revision").GetByte() == 0 && mode.GetProperty("containerFlags").GetByte() == 0
                && mode.GetProperty("containerRevision").GetByte() == version, "physical location and actual wrapper metadata");
            Require(mode.GetProperty("code").GetUInt32() == codes[index] && mode.GetProperty("width").GetUInt32() == widths[index]
                && mode.GetProperty("height").GetUInt32() == heights[index] && mode.GetProperty("nominalVerticalRateHz").GetUInt32() == rates[index]
                && mode.GetProperty("yCbCr420Only").GetBoolean() == only && mode.GetProperty("yCbCr420Supported").GetBoolean() == supported[index]
                && !mode.GetProperty("interlaced").GetBoolean(), "explicit code, nominal size and colour including false");
            Require(mode.GetProperty("rawDescriptor").EnumerateArray().Select(item => item.GetByte()).SequenceEqual(new[] { raw[index] }), "original VIC byte");
            var native = mode.GetProperty("native"); var map = mode.GetProperty("yCbCr420CapabilityMap");
            if (only)
                Require(native.ValueKind == JsonValueKind.Null && map.ValueKind == JsonValueKind.Null, "only420 has no native flag or map ordinal");
            else
                Require(native.GetBoolean() == (index == 0) && map.GetProperty("blockIndex").GetUInt32() == 1
                    && map.GetProperty("byteOffset").GetUInt32() == 24 && map.GetProperty("bitIndex").GetInt32() == 16 + index
                    && map.GetProperty("rawDescriptor").EnumerateArray().Select(item => item.GetByte()).SequenceEqual(new byte[] { 0xE2, 0x0F, 5 }), "real shared bitmap source and regular ordinal");
            foreach (var name in new[] { "timing", "preferred", "cvtParameters", "dscPassThrough" })
                Require(mode.GetProperty(name).ValueKind == JsonValueKind.Null, "unreported member stays null: " + name);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("[EDID-EMBEDDED-COLOUR] " + message);
    }
}
