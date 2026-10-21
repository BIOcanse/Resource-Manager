using System.Text.Json;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.ReadWrite.Windows.Devices.Display;
using ResourceManager.TestFixtures;

namespace Resource_Manager_APP.Tests;

internal static class DisplayEdidEmbeddedHdmiTestData
{
    internal static byte[] Bytes(byte version, bool unsupported = false)
    {
        var bytes = DisplayIdCtaHdmiFixture.Bytes(version);
        if (!unsupported) return bytes;
        bytes[169] = 5; // Unsupported complete HDMI VIC follows three valid HDMI declarations.
        bytes[170] = 0; var sum = 0;
        for (var index = 129; index < 170; index++) sum += bytes[index]; bytes[170] = unchecked((byte)-sum);
        bytes[255] = 0; sum = 0;
        for (var index = 128; index < 255; index++) sum += bytes[index]; bytes[255] = unchecked((byte)-sum);
        return bytes;
    }

    internal static DeviceTopologySnapshot Snapshot(byte version, bool unsupported = false)
        => DisplayEdidModeTestData.Snapshot(DisplayEdidModeTestData.Path(
            WindowsMonitorEdidReader.CreateReadResult(Bytes(version, unsupported))) with { Active = false, TargetAvailable = false });

    internal static void AssertValue(JsonElement value, byte version) => DisplayIdCtaHdmiExpectation.Verify(value, version);
}
