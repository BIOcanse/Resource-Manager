using System.Text.Json;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.ReadWrite.Windows.Devices.Display;

namespace Resource_Manager_APP.Tests;

internal static class DisplayIdVicTimingTestData
{
    internal const string Name = DisplayIdCodeTimingTestData.Name;
    internal static byte[] Bytes() => Append(false);
    internal static byte[] LegacyBytes() => Append(true);
    internal static DeviceTopologyDisplayPath Path() => DisplayEdidModeTestData.Path(WindowsMonitorEdidReader.CreateReadResult(Bytes()));
    internal static DeviceTopologyDisplayPath LegacyPath() => DisplayEdidModeTestData.Path(WindowsMonitorEdidReader.CreateReadResult(LegacyBytes()));
    internal static DeviceTopologySnapshot Snapshot() => DisplayEdidModeTestData.Snapshot(Path());
    internal static DeviceTopologySnapshot LegacySnapshot() => DisplayEdidModeTestData.Snapshot(LegacyPath());
    internal static ResourceManager.ReadWrite.Information.DeviceInformationField Field(ResourceManager.ReadWrite.Information.DeviceInformationSnapshot snapshot, string name = Name) => DisplayIdCodeTimingTestData.Field(snapshot, name);
    internal static void AssertValue(JsonElement value) { DisplayIdCodeTimingTestData.AssertValue(value, 13); AssertAdded(value, false); }
    internal static void AssertLegacyValue(JsonElement value, int modeCount = 13, byte containerRevision = 0x12) { DisplayIdCodeTimingTestData.AssertLegacyValue(value, modeCount, containerRevision); AssertAdded(value, true, containerRevision); }
    private static byte[] Append(bool legacy)
    {
        var bytes = legacy ? DisplayIdCodeTimingTestData.LegacyBytes() : DisplayIdCodeTimingTestData.Bytes();
        byte[] data = legacy ? [6, 0x41, 2, 16, 193, 6, 0x81, 2, 1, 4] : [0x23, 0x48, 4, 16, 0, 193, 0, 0x23, 0x88, 4, 1, 0, 4, 0];
        data.CopyTo(bytes, 517 + bytes[514]); bytes[514] = checked((byte)(bytes[514] + data.Length));
        RepairChecksums(bytes); return bytes;
    }
    internal static void RepairChecksums(byte[] bytes)
    {
        var inner = 517 + bytes[514]; bytes[inner] = 0; bytes[inner] = unchecked((byte)-bytes.AsSpan(513, inner - 512).ToArray().Sum(x => (int)x));
        bytes[639] = 0; bytes[639] = unchecked((byte)-bytes.AsSpan(512, 128).ToArray().Sum(x => (int)x));
    }
    private static void AssertAdded(JsonElement value, bool legacy, byte containerRevision = 0x12)
    {
        var modes = value.GetProperty("modes");
        uint[] codes = [16, 193, 1, 4]; uint[] widths = [1920, 5120, 3840, 4096]; uint[] rates = [60, 120, 30, 24];
        uint[] offsets = legacy ? [13, 14, 18, 19] : [15, 17, 22, 24];
        for (var i = 0; i < 4; i++)
        {
            var m = modes[i + 9]; Assert.Equal(codes[i], m.GetProperty("code").GetUInt32());
            Assert.Equal((legacy ? "displayid-iv-" : "displayid-") + (i < 2 ? "cta-vic" : "hdmi-vic"), m.GetProperty("encoding").GetString());
            Assert.Equal(4u, m.GetProperty("blockIndex").GetUInt32()); Assert.Equal(offsets[i], m.GetProperty("byteOffset").GetUInt32());
            Assert.Equal(widths[i], m.GetProperty("width").GetUInt32()); Assert.Equal(i == 0 ? 1080u : 2160u, m.GetProperty("height").GetUInt32()); Assert.Equal(rates[i], m.GetProperty("nominalVerticalRateHz").GetUInt32());
            Assert.False(m.GetProperty("interlaced").GetBoolean()); Assert.Equal(legacy ? 1 : 0, m.GetProperty("revision").GetInt32());
            Assert.Equal((i < 2 ? 0x40 : 0x80) | (legacy ? 1 : 8), m.GetProperty("containerFlags").GetInt32()); Assert.Equal(legacy ? containerRevision : 0x20, m.GetProperty("containerRevision").GetInt32());
            Assert.Equal(legacy ? new[] { codes[i] } : new[] { codes[i], 0u }, m.GetProperty("rawDescriptor").EnumerateArray().Select(x => x.GetUInt32()));
            foreach (var name in new[] { "native", "preferred", "timing", "reducedBlanking", "dscPassThrough", "yCbCr420Supported", "yCbCr420Only", "yCbCr420CapabilityMap", "formula", "cvtParameters" }) Assert.Equal(JsonValueKind.Null, m.GetProperty(name).ValueKind);
        }
    }
}
