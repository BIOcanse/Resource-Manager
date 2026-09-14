using ResourceManager.App.Domain.DeviceTopology;

namespace ResourceManager.App.Infrastructure.DeviceTopology;

internal static class DeviceTopologyOemDisplayConnectorCatalog
{
    private static readonly IReadOnlyList<DeviceTopologyOemDisplayConnectorProfile> MechrevoJiaolongX6Dr55xxB2 =
    [
        new(
            "mechrevo-jiaolong-x6dr55xx-b2-mini-displayport",
            "Mini DisplayPort 2.1 接口",
            DeviceConnectorKinds.MiniDisplayPort,
            "Mini DisplayPort 物理连接器",
            "Mini DisplayPort 2.1",
            "UHBR20 / 80 Gbps",
            OutputTechnology: 10,
            PreferredAdapterHardwareIdToken: "VEN_10DE",
            AllowAnyActiveAdapter: false),
        new(
            "mechrevo-jiaolong-x6dr55xx-b2-hdmi",
            "HDMI 2.1 接口",
            DeviceConnectorKinds.Hdmi,
            "HDMI 物理连接器",
            "HDMI 2.1",
            "48 Gbps",
            OutputTechnology: 5,
            PreferredAdapterHardwareIdToken: "VEN_1002",
            AllowAnyActiveAdapter: true)
    ];

    public static IReadOnlyList<DeviceTopologyOemDisplayConnectorProfile> Resolve(
        DeviceTopologySystemIdentity system)
    {
        if (system.Manufacturer.Equals("MECHREVO", StringComparison.OrdinalIgnoreCase)
            && system.BaseBoardProduct?.StartsWith(
                "JIAOLONG Series-X6DR55xx-B2",
                StringComparison.OrdinalIgnoreCase) == true)
        {
            return MechrevoJiaolongX6Dr55xxB2;
        }

        return [];
    }
}

internal sealed record DeviceTopologyOemDisplayConnectorProfile(
    string Id,
    string DisplayName,
    string ConnectorKind,
    string HardwareKind,
    string Protocol,
    string PhysicalMaximumSpeed,
    int OutputTechnology,
    string? PreferredAdapterHardwareIdToken,
    bool AllowAnyActiveAdapter);
