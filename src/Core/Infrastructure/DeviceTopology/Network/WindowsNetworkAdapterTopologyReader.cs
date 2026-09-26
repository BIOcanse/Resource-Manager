namespace ResourceManager.App.Infrastructure.DeviceTopology;

internal static class WindowsNetworkAdapterTopologyReader
{
    public static DeviceTopologyNetworkAdapterSnapshot ReadSnapshot()
    {
        var result = DeviceTopologyWmiUtilities.QueryObjects(
            @"root\StandardCimv2",
            "SELECT PnPDeviceID, InterfaceName, MediaConnectState, TransmitLinkSpeed, ReceiveLinkSpeed, PermanentAddress, ActiveMaximumTransmissionUnit, HardwareInterface, ConnectorPresent FROM MSFT_NetAdapter");
        var adapters = result.Rows
            .Select(CreateAdapter)
            .Where(static adapter => adapter is not null)
            .Select(static adapter => adapter!)
            .GroupBy(static adapter => adapter.DeviceId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.OrdinalIgnoreCase);
        return new DeviceTopologyNetworkAdapterSnapshot(adapters, result.Error);
    }

    private static DeviceTopologyNetworkAdapter? CreateAdapter(IReadOnlyDictionary<string, object?> row)
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
            DeviceTopologyWmiUtilities.ReadUInt64(row, "TransmitLinkSpeed"),
            DeviceTopologyWmiUtilities.ReadUInt64(row, "ReceiveLinkSpeed"),
            Clean(DeviceTopologyWmiUtilities.ReadString(row, "PermanentAddress")),
            DeviceTopologyWmiUtilities.ReadUInt64(row, "ActiveMaximumTransmissionUnit"),
            DeviceTopologyWmiUtilities.ReadBoolean(row, "HardwareInterface"),
            DeviceTopologyWmiUtilities.ReadBoolean(row, "ConnectorPresent"));
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

internal sealed record DeviceTopologyNetworkAdapterSnapshot(
    IReadOnlyDictionary<string, DeviceTopologyNetworkAdapter> Adapters,
    string? Error);

internal sealed record DeviceTopologyNetworkAdapter(
    string DeviceId,
    string? InterfaceName,
    uint? MediaConnectState,
    ulong? TransmitLinkSpeedBitsPerSecond,
    ulong? ReceiveLinkSpeedBitsPerSecond,
    string? PermanentAddress,
    ulong? ActiveMtuBytes,
    bool? HardwareInterface,
    bool? ConnectorPresent);
