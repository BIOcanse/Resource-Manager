using System.Text.Json;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.ReadWrite.Windows.Devices.Display;
using ResourceManager.TestFixtures;

namespace Resource_Manager_APP.Tests;

internal static class DisplayEdidEmbeddedColourTestData
{
    internal static byte[] Bytes(byte version, bool unsupported = false)
    {
        var bytes = DisplayIdCtaColourFixture.Bytes(version);
        if (!unsupported) return bytes;
        bytes[154] = 8; // Bit3 is outside the three regular SVD ordinals; only420 is not an ordinal.
        bytes[155] = 0; var sum = 0;
        for (var index = 129; index < 155; index++) sum += bytes[index]; bytes[155] = unchecked((byte)-sum);
        bytes[255] = 0; sum = 0;
        for (var index = 128; index < 255; index++) sum += bytes[index]; bytes[255] = unchecked((byte)-sum);
        return bytes;
    }

    internal static DeviceTopologySnapshot Snapshot(byte version, bool unsupported = false)
        => DisplayEdidModeTestData.Snapshot(DisplayEdidModeTestData.Path(
            WindowsMonitorEdidReader.CreateReadResult(Bytes(version, unsupported))) with { Active = false, TargetAvailable = false });

    internal static void AssertValue(JsonElement value, byte version) => DisplayIdCtaColourExpectation.Verify(value, version);
}
