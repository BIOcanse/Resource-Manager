using System.Text.Json;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.ReadWrite.Windows.Devices.Display;

namespace Resource_Manager_APP.Tests;

internal static class DisplayIdBitmapTestData
{
    internal static byte[] Bytes(byte containerRevision)
    {
        if (containerRevision is not (0x12))
            throw new ArgumentOutOfRangeException(nameof(containerRevision));
        var bytes = DisplayIdVicTimingTestData.LegacyBytes();
        bytes[513] = containerRevision;
        byte[] bitmaps = [0x07, 0, 1, 0x88, 0x08, 0, 1, 0x81];
        bitmaps.CopyTo(bytes, 517 + bytes[514]);
        bytes[514] = checked((byte)(bytes[514] + bitmaps.Length));
        DisplayIdVicTimingTestData.RepairChecksums(bytes);
        return bytes;
    }

    internal static DeviceTopologyDisplayPath Path(byte containerRevision)
        => DisplayEdidModeTestData.Path(WindowsMonitorEdidReader.CreateReadResult(Bytes(containerRevision)));
    internal static DeviceTopologySnapshot Snapshot(byte containerRevision)
        => DisplayEdidModeTestData.Snapshot(Path(containerRevision));

    internal static void AssertValue(JsonElement value, byte containerRevision)
    {
        DisplayIdVicTimingTestData.AssertLegacyValue(value, 17, containerRevision);
        var modes = value.GetProperty("modes");
        uint[] codes = [4, 8, 1, 8], widths = [640, 800, 640, 720], heights = [480, 600, 480, 240], rates = [60, 0, 60, 60];
        int[] bits = [3, 7, 0, 7];
        for (var i = 0; i < 4; i++)
        {
            var mode = modes[i + 13];
            Assert.Equal(i < 2 ? "displayid-dmt-bitmap" : "displayid-cta-bitmap", mode.GetProperty("encoding").GetString());
            Assert.Equal(4u, mode.GetProperty("blockIndex").GetUInt32());
            Assert.Equal(i < 2 ? 23u : 27u, mode.GetProperty("byteOffset").GetUInt32());
            Assert.Equal(bits[i], mode.GetProperty("bitIndex").GetInt32());
            Assert.Equal(codes[i], mode.GetProperty("code").GetUInt32());
            Assert.Equal(widths[i], mode.GetProperty("width").GetUInt32());
            Assert.Equal(heights[i], mode.GetProperty("height").GetUInt32());
            if (i == 1) Assert.Equal(JsonValueKind.Null, mode.GetProperty("nominalVerticalRateHz").ValueKind);
            else Assert.Equal(rates[i], mode.GetProperty("nominalVerticalRateHz").GetUInt32());
            Assert.False(mode.GetProperty("interlaced").GetBoolean());
            Assert.Equal(0, mode.GetProperty("revision").GetInt32());
            Assert.Equal(0, mode.GetProperty("containerFlags").GetInt32());
            Assert.Equal((int)containerRevision, mode.GetProperty("containerRevision").GetInt32());
            Assert.Equal(new[] { i < 2 ? 0x88u : 0x81u }, mode.GetProperty("rawDescriptor").EnumerateArray().Select(x => x.GetUInt32()));
            if (i < 2) Assert.False(mode.GetProperty("reducedBlanking").GetBoolean());
            else Assert.Equal(JsonValueKind.Null, mode.GetProperty("reducedBlanking").ValueKind);
            foreach (var property in new[] { "native", "preferred", "timing", "dscPassThrough", "yCbCr420Encoding",
                "yCbCr420Supported", "yCbCr420Only", "yCbCr420CapabilityMap", "formula", "cvtParameters" })
                Assert.Equal(JsonValueKind.Null, mode.GetProperty(property).ValueKind);
        }
        foreach (var selection in value.GetProperty("selectionDeclarations").EnumerateArray())
        foreach (var reference in selection.GetProperty("references").EnumerateArray())
        {
            Assert.DoesNotContain("bitmap", reference.GetProperty("targetEncoding").GetString()!);
            var target = reference.GetProperty("target");
            Assert.True(target.ValueKind == JsonValueKind.Null || target.GetProperty("blockIndex").GetUInt32() != 4);
        }
    }
}
