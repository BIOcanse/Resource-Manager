using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;

namespace Resource_Manager_APP.Tests;

internal static class DisplayEdidModeTestData
{
    internal static DeviceTopologyDisplayPath Path(DeviceTopologyEdidReadResult read) => new DeviceTopologyDisplayPath(
        1, 2, 3, 4, 5, 0, "Panel", @"\\?\DISPLAY#TEST#1", 3840, 2160, 60000, 1001, true, true,
        @"PCI\GPU", ClockwiseRotationDegrees: 90) with {
        Edid = read.Capabilities, EdidObservationComplete = read.Complete, EdidRawDeclaredBase64 = read.RawEdidDeclaredBase64 };

    internal static DeviceTopologySnapshot Snapshot(DeviceTopologyDisplayPath path) => new(
        DateTimeOffset.UtcNow, new("Machine Vendor", "Machine Model", "Brand", "Logo", "Bios Version", "Board Vendor", "Board Model"),
        [WindowsDeviceTopologyReader.CreateDisplayPathCandidate(new(path, null))], []);
}
