using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Domain.Messages;

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
                BackendMessageCodes.DeviceTopology.RoleUcsiConnectorManager,
                "UCSI",
                "UcmUcsiAcpiClient"),
            "USB4HOSTROUTER" => Create(
                DeviceInterconnectKinds.Usb4HostRouter,
                BackendMessageCodes.DeviceTopology.RoleUsb4HostRouter,
                "USB4",
                "Usb4HostRouter"),
            "USB4DEVICEROUTER" => Create(
                DeviceInterconnectKinds.Usb4DeviceRouter,
                BackendMessageCodes.DeviceTopology.RoleUsb4DeviceRouter,
                "USB4 / Thunderbolt 3",
                "Usb4DeviceRouter"),
            "USB4P2PNETADAPTER" => Create(
                DeviceInterconnectKinds.Usb4P2PNetwork,
                BackendMessageCodes.DeviceTopology.RoleUsb4P2PNetwork,
                "USB4NET",
                "Usb4P2PNetAdapter"),
            _ => null
        };
    }

    private static DeviceTopologyAdvancedInterconnect Create(
        string kind,
        byte roleCode,
        string technology,
        string service)
    {
        return new DeviceTopologyAdvancedInterconnect(
            kind,
            BackendMessage.Create(BackendMessageDomains.DeviceTopology, roleCode),
            technology,
            BackendMessage.Create(
                BackendMessageDomains.DeviceTopology,
                BackendMessageCodes.DeviceTopology.InterconnectEvidencePnpService,
                service));
    }

    private static string NormalizeServiceName(string? service)
    {
        var value = service?.Trim() ?? string.Empty;
        return value.EndsWith(".sys", StringComparison.OrdinalIgnoreCase)
            ? value[..^4]
            : value;
    }
}
