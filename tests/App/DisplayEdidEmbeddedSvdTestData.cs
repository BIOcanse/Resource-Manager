using System.Text.Json;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;
using ResourceManager.ReadWrite.Windows.Devices.Display;
using ResourceManager.TestFixtures;

namespace Resource_Manager_APP.Tests;

internal static class DisplayEdidEmbeddedSvdTestData
{
    internal static DeviceTopologySnapshot Snapshot(byte version, bool unsupported = false)
        => DisplayEdidModeTestData.Snapshot(DisplayEdidModeTestData.Path(
            WindowsMonitorEdidReader.CreateReadResult(DisplayIdCtaSvdFixture.Bytes(version, unsupported)))
            with { Active = false, TargetAvailable = false });

    internal static void AssertValue(JsonElement value, byte version)
    {
        var modes = value.GetProperty("modes"); Assert.Equal(4, modes.GetArrayLength());
        Assert.Equal(0, value.GetProperty("ranges").GetArrayLength());
        Assert.Equal(0, value.GetProperty("selectionDeclarations").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, value.GetProperty("ctaFlags").ValueKind);
        Assert.Equal("displayid-x", modes[0].GetProperty("encoding").GetString());
        Assert.Equal(8u, modes[0].GetProperty("byteOffset").GetUInt32());
        Assert.Equal(new byte[] { 0x12, 0x7F, 7, 0x37, 4, 0x3B },
            modes[0].GetProperty("rawDescriptor").EnumerateArray().Select(item => item.GetByte()));
        uint[] codes = [16, 65, 193], widths = [1920, 1280, 5120], heights = [1080, 720, 2160], rates = [60, 24, 120];
        byte[] raw = [0x90, 0x41, 0xC1];
        for (var i = 0; i < 3; i++)
        {
            var mode = modes[i + 1];
            Assert.Equal("displayid-cta-svd", mode.GetProperty("encoding").GetString());
            Assert.Equal(1u, mode.GetProperty("blockIndex").GetUInt32());
            Assert.Equal((uint)(18 + i), mode.GetProperty("byteOffset").GetUInt32());
            Assert.Equal(0, mode.GetProperty("revision").GetInt32());
            Assert.Equal(0, mode.GetProperty("containerFlags").GetInt32());
            Assert.Equal(version, mode.GetProperty("containerRevision").GetByte());
            Assert.Equal(codes[i], mode.GetProperty("code").GetUInt32());
            Assert.Equal(widths[i], mode.GetProperty("width").GetUInt32());
            Assert.Equal(heights[i], mode.GetProperty("height").GetUInt32());
            Assert.Equal(rates[i], mode.GetProperty("nominalVerticalRateHz").GetUInt32());
            Assert.Equal(i == 0, mode.GetProperty("native").GetBoolean());
            Assert.False(mode.GetProperty("interlaced").GetBoolean()); Assert.False(mode.GetProperty("yCbCr420Only").GetBoolean());
            Assert.Equal(new byte[] { raw[i] }, mode.GetProperty("rawDescriptor").EnumerateArray().Select(item => item.GetByte()));
            foreach (var name in new[] { "timing", "preferred", "cvtParameters", "dscPassThrough", "yCbCr420Supported", "yCbCr420CapabilityMap" })
                Assert.Equal(JsonValueKind.Null, mode.GetProperty(name).ValueKind);
        }
    }
}
