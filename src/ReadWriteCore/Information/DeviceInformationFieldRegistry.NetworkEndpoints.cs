namespace ResourceManager.ReadWrite.Information;

public static partial class DeviceInformationFieldRegistry
{
    public static readonly DeviceInformationDefinition NetworkAllTcpIpv4EndpointReported =
        new("network_all_tcp_ipv4_endpoint_reported", "network", "all", "array", "endpoint[]", "network.os_tcp_ipv4_endpoints_reported", "windows.ip-helper", ReadVolatility.Sampled);
    public static readonly DeviceInformationDefinition NetworkAllTcpIpv6EndpointReported =
        new("network_all_tcp_ipv6_endpoint_reported", "network", "all", "array", "endpoint[]", "network.os_tcp_ipv6_endpoints_reported", "windows.ip-helper", ReadVolatility.Sampled);
    public static readonly DeviceInformationDefinition NetworkAllUdpIpv4EndpointReported =
        new("network_all_udp_ipv4_endpoint_reported", "network", "all", "array", "endpoint[]", "network.os_udp_ipv4_endpoints_reported", "windows.ip-helper", ReadVolatility.Sampled);
    public static readonly DeviceInformationDefinition NetworkAllUdpIpv6EndpointReported =
        new("network_all_udp_ipv6_endpoint_reported", "network", "all", "array", "endpoint[]", "network.os_udp_ipv6_endpoints_reported", "windows.ip-helper", ReadVolatility.Sampled);
}
