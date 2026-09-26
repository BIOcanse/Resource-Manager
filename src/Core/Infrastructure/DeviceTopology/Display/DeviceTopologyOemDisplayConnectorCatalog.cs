using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Domain.Messages;

namespace ResourceManager.App.Infrastructure.DeviceTopology;

internal static class DeviceTopologyOemDisplayConnectorCatalog
{
    private static readonly IReadOnlyList<DeviceTopologyOemDisplayConnectorProfile> MechrevoJiaolongX6Dr55xxB2 =
    [
        new(
            "mechrevo-jiaolong-x6dr55xx-b2-mini-displayport",
            "Mini DisplayPort 2.1",
            Name(BackendMessageCodes.DeviceTopology.NameConnectorInterface, "Mini DisplayPort 2.1"),
            DeviceConnectorKinds.MiniDisplayPort,
            Name(BackendMessageCodes.DeviceTopology.KindPhysicalConnector, "Mini DisplayPort"),
            "Mini DisplayPort 2.1",
            "UHBR20 / 80 Gbps",
            OutputTechnology: 10,
            PreferredAdapterHardwareIdToken: "VEN_10DE",
            AllowAnyActiveAdapter: false),
        new(
            "mechrevo-jiaolong-x6dr55xx-b2-hdmi",
            "HDMI 2.1",
            Name(BackendMessageCodes.DeviceTopology.NameConnectorInterface, "HDMI 2.1"),
            DeviceConnectorKinds.Hdmi,
            Name(BackendMessageCodes.DeviceTopology.KindPhysicalConnector, "HDMI"),
            "HDMI 2.1",
            "48 Gbps",
            OutputTechnology: 5,
            PreferredAdapterHardwareIdToken: "VEN_1002",
            AllowAnyActiveAdapter: true)
    ];

    private static BackendMessage Name(byte code, string connector)
        => BackendMessage.Create(BackendMessageDomains.DeviceTopology, code, connector);

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
    /// <summary>接口的技术名，只给原生事实缓存与匹配用，不直接显示。</summary>
    string CatalogName,
    BackendMessage DisplayName,
    string ConnectorKind,
    BackendMessage HardwareKind,
    string Protocol,
    string PhysicalMaximumSpeed,
    int OutputTechnology,
    string? PreferredAdapterHardwareIdToken,
    bool AllowAnyActiveAdapter);
