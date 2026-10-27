namespace ResourceManager.App.Infrastructure.DeviceTopology;

public static class WindowsNetworkAdapterTopologyReader
{
    public static DeviceTopologyNetworkAdapterSnapshot ReadSnapshot()
        => ReadSnapshot(TimeSpan.FromSeconds(2));

    [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.Sampled)]
    public static DeviceTopologyNetworkAdapterSnapshot ReadSnapshot(TimeSpan timeout,
        Func<string, string, TimeSpan, DeviceTopologyWmiQueryResult>? query = null)
    {
        var result = (query ?? DeviceTopologyWmiUtilities.QueryObjects)(
            @"root\StandardCimv2",
            "SELECT PnPDeviceID, InterfaceGuid, InterfaceDescription, InterfaceName, MediaConnectState, TransmitLinkSpeed, ReceiveLinkSpeed, PermanentAddress, ActiveMaximumTransmissionUnit, HardwareInterface, ConnectorPresent FROM MSFT_NetAdapter", timeout);
        var adapters = result.Rows
            .Select(CreateAdapter)
            .Where(static adapter => adapter is not null)
            .Select(static adapter => adapter!)
            .GroupBy(static adapter => adapter.DeviceId, StringComparer.OrdinalIgnoreCase)
            .Where(static group => group.Count() == 1)
            .ToDictionary(static group => group.Key, static group => group.Single(), StringComparer.OrdinalIgnoreCase);
        return new DeviceTopologyNetworkAdapterSnapshot(adapters, result.Error);
    }

    internal static DeviceTopologyNetworkAdapter? CreateAdapter(IReadOnlyDictionary<string, object?> row)
    {
        var deviceId = NormalizeDeviceId(DeviceTopologyWmiUtilities.ReadString(row, "PnPDeviceID"));
        if (deviceId.Length == 0)
        {
            return null;
        }

        return new DeviceTopologyNetworkAdapter(
            deviceId,
            Clean(DeviceTopologyWmiUtilities.ReadString(row, "InterfaceName")),
            DeviceTopologyWmiUtilities.ReadUInt32(row, "MediaConnectState"),
            ReadLinkSpeed(row, "TransmitLinkSpeed"),
            ReadLinkSpeed(row, "ReceiveLinkSpeed"),
            Clean(DeviceTopologyWmiUtilities.ReadString(row, "PermanentAddress")),
            DeviceTopologyWmiUtilities.ReadUInt64(row, "ActiveMaximumTransmissionUnit"),
            DeviceTopologyWmiUtilities.ReadBoolean(row, "HardwareInterface"),
            DeviceTopologyWmiUtilities.ReadBoolean(row, "ConnectorPresent"))
        {
            InterfaceGuid = Guid.TryParse(DeviceTopologyWmiUtilities.ReadString(row, "InterfaceGuid"), out var guid)
                && guid != Guid.Empty ? guid.ToString("D").ToUpperInvariant() : null,
            InterfaceDescription = Clean(DeviceTopologyWmiUtilities.ReadString(row, "InterfaceDescription"))
        };
    }

    private static ulong? ReadLinkSpeed(IReadOnlyDictionary<string, object?> row, string property)
    {
        var speed = DeviceTopologyWmiUtilities.ReadUInt64(row, property);
        // NDIS_LINK_SPEED_UNKNOWN is (ULONG64)-1; it is not a measured speed.
        return speed == ulong.MaxValue ? null : speed;
    }

    private static string NormalizeDeviceId(string? deviceId)
    {
        return string.IsNullOrWhiteSpace(deviceId)
            ? string.Empty
            : deviceId.Trim().Replace(@"\\", @"\", StringComparison.Ordinal).ToUpperInvariant();
    }

    private static string? Clean(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}

public sealed record DeviceTopologyNetworkAdapterSnapshot(
    IReadOnlyDictionary<string, DeviceTopologyNetworkAdapter> Adapters,
    string? Error);

public sealed record DeviceTopologyNetworkAdapter(
    string DeviceId,
    string? InterfaceName,
    uint? MediaConnectState,
    ulong? TransmitLinkSpeedBitsPerSecond,
    ulong? ReceiveLinkSpeedBitsPerSecond,
    string? PermanentAddress,
    ulong? ActiveMtuBytes,
    bool? HardwareInterface,
    bool? ConnectorPresent)
{
    public string? InterfaceGuid { get; init; }
    public string? InterfaceDescription { get; init; }
    public ResourceManager.ReadWrite.Windows.Devices.Models.DeviceTopologyWirelessConnection? Wireless { get; init; }
}
