namespace ResourceManager.ReadWrite.Information;

public static partial class DeviceInformationFieldRegistry
{
    public static readonly DeviceInformationDefinition NetworkAllInterfaceGuidReported =
        new("network_all_interface_guid_reported", "network", "all", "text", "guid", "network.os_interface_guid_reported", "windows.ip-helper", ReadVolatility.Fixed);
    public static readonly DeviceInformationDefinition NetworkAllInterfaceLuidReported =
        new("network_all_interface_luid_reported", "network", "all", "text", "hex64", "network.os_interface_luid_reported", "windows.ip-helper", ReadVolatility.EventDriven("Windows IP Helper interface binding changed"));
    public static readonly DeviceInformationDefinition NetworkAllInterfaceIndexReported =
        new("network_all_interface_index_reported", "network", "all", "unsigned-integer", "id32", "network.os_interface_index_reported", "windows.ip-helper", ReadVolatility.EventDriven("Windows IP Helper interface binding changed"));
    public static readonly DeviceInformationDefinition NetworkAllUnicastAddressReported =
        new("network_all_unicast_address_reported", "network", "all", "array", "address[]", "network.os_unicast_address_records_reported", "windows.ip-helper", ReadVolatility.EventDriven("Windows IP Helper unicast address changed"));
    public static readonly DeviceInformationDefinition NetworkAllRouteReported =
        new("network_all_route_reported", "network", "all", "array", "route[]", "network.os_route_records_reported", "windows.ip-helper", ReadVolatility.EventDriven("Windows IP Helper route changed"));
}
