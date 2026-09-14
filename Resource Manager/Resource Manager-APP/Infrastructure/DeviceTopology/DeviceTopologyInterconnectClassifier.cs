using ResourceManager.App.Domain.DeviceTopology;

namespace ResourceManager.App.Infrastructure.DeviceTopology;

internal static class DeviceTopologyInterconnectClassifier
{
    public static DeviceTopologyAdvancedInterconnect? Classify(string? service)
    {
        var serviceName = NormalizeServiceName(service);
        return serviceName.ToUpperInvariant() switch
        {
            "UCMUCSIACPICLIENT" => Create(
                DeviceInterconnectKinds.UcsiConnectorManager,
                "USB Type-C 连接器管理器",
                "UCSI",
                "UcmUcsiAcpiClient"),
            "USB4HOSTROUTER" => Create(
                DeviceInterconnectKinds.Usb4HostRouter,
                "USB4 主机路由器",
                "USB4",
                "Usb4HostRouter"),
            "USB4DEVICEROUTER" => Create(
                DeviceInterconnectKinds.Usb4DeviceRouter,
                "USB4 / Thunderbolt 3 设备路由器",
                "USB4 / Thunderbolt 3",
                "Usb4DeviceRouter"),
            "USB4P2PNETADAPTER" => Create(
                DeviceInterconnectKinds.Usb4P2PNetwork,
                "USB4 主机互联网络适配器",
                "USB4NET",
                "Usb4P2PNetAdapter"),
            _ => null
        };
    }

    private static DeviceTopologyAdvancedInterconnect Create(
        string kind,
        string role,
        string technology,
        string service)
    {
        return new DeviceTopologyAdvancedInterconnect(
            kind,
            role,
            technology,
            $"Windows PnP 服务：{service}");
    }

    private static string NormalizeServiceName(string? service)
    {
        var value = service?.Trim() ?? string.Empty;
        return value.EndsWith(".sys", StringComparison.OrdinalIgnoreCase)
            ? value[..^4]
            : value;
    }
}
