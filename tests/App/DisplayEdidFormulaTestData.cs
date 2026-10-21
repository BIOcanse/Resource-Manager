using System.Text.Json;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.ReadWrite.Windows.Devices.Display;
using ResourceManager.TestFixtures;

namespace Resource_Manager_APP.Tests;

internal static class DisplayEdidFormulaTestData
{
    internal static byte[] Bytes(byte version, bool unsupported = false)
    {
        var bytes = DisplayIdFormulaFixture.Bytes(version);
        if (unsupported) { bytes[148] = 3; DisplayIdFormulaFixture.Repair(bytes); }
        return bytes;
    }

    internal static DeviceTopologySnapshot Snapshot(byte version, bool unsupported = false)
        => DisplayEdidModeTestData.Snapshot(Path(version, unsupported));

    internal static DeviceTopologyDisplayPath Path(byte version, bool unsupported = false)
        => DisplayEdidModeTestData.Path(WindowsMonitorEdidReader.CreateReadResult(Bytes(version, unsupported)))
            with { Active = false, TargetAvailable = false };

    internal static void AssertValue(JsonElement value, byte version) => DisplayIdFormulaExpectation.Verify(value, version);
}
