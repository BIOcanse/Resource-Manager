using System.Text.Json;

namespace ResourceManager.TestFixtures;

/// <summary>Independent expected HDMI and retained colour facts for real App/package consumers.</summary>
internal static class DisplayIdCtaHdmiExpectation
{
    internal static void Verify(JsonElement value, byte version)
    {
        var modes = value.GetProperty("modes");
        Require(modes.GetArrayLength() == 9 && value.GetProperty("ranges").GetArrayLength() == 0
            && value.GetProperty("selectionDeclarations").GetArrayLength() == 0, "nine explicit declarations");
        Require(value.GetProperty("ctaFlags").ValueKind == JsonValueKind.Null, "real DisplayID without synthetic CTA flags");
        Require(modes[0].GetProperty("encoding").GetString() == "displayid-x"
            && modes[0].GetProperty("byteOffset").GetUInt32() == 8, "prior TypeX retained");
        uint[] priorCodes = [16, 65, 193, 97], priorOffsets = [18, 19, 20, 23];
        bool[] supported = [true, false, true, true];
        for (var index = 0; index < 4; index++)
        {
            var mode = modes[index + 1]; var only = index == 3;
            Require(mode.GetProperty("code").GetUInt32() == priorCodes[index]
                && mode.GetProperty("byteOffset").GetUInt32() == priorOffsets[index]
                && mode.GetProperty("encoding").GetString() == (only ? "displayid-cta-y420-vic" : "displayid-cta-svd")
                && mode.GetProperty("yCbCr420Only").GetBoolean() == only
                && mode.GetProperty("yCbCr420Supported").GetBoolean() == supported[index], "original colour declarations including false");
            var map = mode.GetProperty("yCbCr420CapabilityMap");
            if (only) Require(map.ValueKind == JsonValueKind.Null, "only420 has no regular ordinal");
            else Require(map.GetProperty("byteOffset").GetUInt32() == 24
                && map.GetProperty("bitIndex").GetInt32() == 16 + index, "original regular map domain retained");
        }
        uint[] widths = [3840, 3840, 3840, 4096], rates = [30, 25, 24, 24];
        for (var index = 0; index < 4; index++)
        {
            var mode = modes[index + 5]; var code = (uint)index + 1;
            Require(mode.GetProperty("encoding").GetString() == "displayid-cta-hdmi-vic"
                && mode.GetProperty("blockIndex").GetUInt32() == 1
                && mode.GetProperty("byteOffset").GetUInt32() == (uint)index + 38
                && mode.GetProperty("revision").GetByte() == 0 && mode.GetProperty("containerFlags").GetByte() == 0
                && mode.GetProperty("containerRevision").GetByte() == version, "actual HDMI source and wrapper");
            Require(mode.GetProperty("code").GetUInt32() == code && mode.GetProperty("width").GetUInt32() == widths[index]
                && mode.GetProperty("height").GetUInt32() == 2160 && mode.GetProperty("nominalVerticalRateHz").GetUInt32() == rates[index]
                && !mode.GetProperty("interlaced").GetBoolean(), "complete HDMI code and nominal facts");
            Require(mode.GetProperty("rawDescriptor").EnumerateArray().Select(item => item.GetByte()).SequenceEqual(new[] { (byte)code }), "original HDMI VIC byte");
            foreach (var name in new[] { "timing", "native", "preferred", "cvtParameters", "dscPassThrough", "yCbCr420Only", "yCbCr420Supported", "yCbCr420CapabilityMap" })
                Require(mode.GetProperty(name).ValueKind == JsonValueKind.Null, "unreported HDMI member remains null: " + name);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("[EDID-EMBEDDED-HDMI] " + message);
    }
}
