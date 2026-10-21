using System.Text.Json;

namespace ResourceManager.TestFixtures;

internal static class DisplayIdFormulaExpectation
{
    internal static void Verify(JsonElement value, byte version)
    {
        var modes = value.GetProperty("modes");
        Require(modes.GetArrayLength() == 3 && value.GetProperty("ranges").GetArrayLength() == 0
            && value.GetProperty("selectionDeclarations").GetArrayLength() == 0, "three complete declarations");
        Require(value.GetProperty("ctaFlags").ValueKind == JsonValueKind.Null, "no synthetic CTA header");
        uint[] widths = [1920, 3840, 65536], heights = [1080, 2160, 65536], rates = [60, 120, 256];
        string[] raw = ["007F0737043B", "31FF0E6F0877", "52FFFFFFFFFF"];
        for (var index = 0; index < 3; index++)
        {
            var mode = modes[index]; var parameters = mode.GetProperty("cvtParameters");
            Require(mode.GetProperty("encoding").GetString() == "displayid-ix"
                && mode.GetProperty("blockIndex").GetUInt32() == 1
                && mode.GetProperty("byteOffset").GetUInt32() == (uint)(8 + 6 * index)
                && mode.GetProperty("revision").GetByte() == 0
                && mode.GetProperty("containerFlags").GetByte() == 0
                && mode.GetProperty("containerRevision").GetByte() == version, "original source and container");
            Require(mode.GetProperty("width").GetUInt32() == widths[index]
                && mode.GetProperty("height").GetUInt32() == heights[index]
                && mode.GetProperty("nominalVerticalRateHz").GetUInt32() == rates[index]
                && !mode.GetProperty("interlaced").GetBoolean()
                && mode.GetProperty("formula").GetString() == "cvt"
                && mode.GetProperty("reducedBlanking").GetBoolean() == (index != 0), "nominal geometry and explicit formula");
            Require(parameters.GetProperty("reducedBlankingVersion").GetByte() == index
                && parameters.GetProperty("stereoCode").GetByte() == index
                && parameters.GetProperty("alternativeRefresh1000Over1001Supported").GetBoolean() == (index != 0), "independent formula/stereo/fractional expectations");
            Require(mode.GetProperty("rawDescriptor").EnumerateArray().Select(item => item.GetByte())
                .SequenceEqual(Convert.FromHexString(raw[index])), "original six-byte descriptor");
            foreach (var name in new[] { "timing", "native", "preferred", "code", "bitIndex", "dscPassThrough",
                "yCbCr420Only", "yCbCr420Encoding", "yCbCr420Supported", "yCbCr420CapabilityMap" })
                Require(mode.GetProperty(name).ValueKind == JsonValueKind.Null, "unreported member: " + name);
            foreach (var name in new[] { "horizontalBlankingBase160Pixels", "earlyVsyncRequired", "deltaHorizontalBlankingCode",
                "additionalVerticalBlankingCode", "alternativeMinimumVerticalBlanking" })
                Require(parameters.GetProperty(name).ValueKind == JsonValueKind.Null, "not a Type X parameter: " + name);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("[EDID-TYPE-IX] " + message);
    }
}
