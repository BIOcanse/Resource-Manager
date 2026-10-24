using System.Globalization;
using System.Text.Json;
using ResourceManager.ReadWrite.Windows.Devices.Models;
using F = ResourceManager.ReadWrite.Information.DeviceInformationFieldRegistry;

namespace ResourceManager.ReadWrite.Information;

/// <summary>Original canonical conversion shared by native Windows collectors and the product publication.</summary>
public static partial class WindowsInformationFieldProjection
{
    private static readonly JsonSerializerOptions JsonOptions = InformationJsonContext.Default.Options;

    public static DeviceInformationSnapshot PageFiles(IReadOnlyList<DeviceTopologyPageFileInformation>? files, DateTimeOffset? observedAt)
        => Frame((files ?? []).Where(static row => !string.IsNullOrWhiteSpace(row.DeviceIdentity))
            .Select(static row => new DeviceInformationObject(row.DeviceIdentity, "page_file", PageFileFields(row))), observedAt);

    public static DeviceInformationSnapshot Volumes(IReadOnlyList<DeviceTopologyVolumeSpaceInformation>? volumes, DateTimeOffset? observedAt)
        => Frame((volumes ?? []).Where(static row => !string.IsNullOrWhiteSpace(row.DeviceIdentity))
            .Select(static row => new DeviceInformationObject(row.DeviceIdentity, "volume", VolumeSpaceFields(row))), observedAt);

    public static IReadOnlyList<DeviceInformationField> NetworkEndpointFields(DeviceTopologyNetworkEndpointInformation endpoints)
        => [Value(F.NetworkAllTcpIpv4EndpointReported, endpoints.TcpIpv4), Value(F.NetworkAllTcpIpv6EndpointReported, endpoints.TcpIpv6),
            Value(F.NetworkAllUdpIpv4EndpointReported, endpoints.UdpIpv4), Value(F.NetworkAllUdpIpv6EndpointReported, endpoints.UdpIpv6)];

    public static DeviceInformationSnapshot NetworkEndpoints(DeviceTopologyNetworkEndpointInformation endpoints, DateTimeOffset? observedAt)
        => Frame([new("machine", "machine", NetworkEndpointFields(endpoints))], observedAt);

    public static DeviceInformationSnapshot Storage(IReadOnlyList<DeviceTopologyStorageInformation>? storage, DateTimeOffset? observedAt)
        => Frame((storage ?? []).Where(static row => !string.IsNullOrWhiteSpace(row.DeviceIdentity))
            .Select(static row => new DeviceInformationObject(row.DeviceIdentity, "physical_disk", StorageFields(row).ToArray())), observedAt);

    public static DeviceInformationSnapshot Disks(IReadOnlyList<DeviceTopologyDiskLayoutInformation>? disks,
        IReadOnlyList<DeviceTopologyVolumeSpaceInformation>? volumes, IReadOnlyList<DeviceTopologyStorageInformation>? storage,
        DateTimeOffset? observedAt)
    {
        var unique = (disks ?? []).Where(static row => !string.IsNullOrWhiteSpace(row.DeviceIdentity))
            .GroupBy(static row => row.DeviceIdentity, StringComparer.Ordinal).Where(static group => group.Count() == 1)
            .Select(static group => group.Single()).ToArray();
        var physical = PhysicalDiskRelationCandidates(storage);
        return Frame(unique.Select(row => new DeviceInformationObject(row.DeviceIdentity, "os_disk",
            DiskLayoutFields(row, unique, volumes, physical))), observedAt);
    }

    private static DeviceInformationSnapshot Frame(IEnumerable<DeviceInformationObject> devices, DateTimeOffset? observedAt)
        => new(observedAt, devices.Select(device => observedAt is null
            ? device with { Fields = device.Fields.Select(static field => field with { Value = null }).ToArray() } : device).ToArray());

    private static DeviceInformationField Text(DeviceInformationDefinition definition, string? value)
        => Value(definition, string.IsNullOrWhiteSpace(value) ? null : value.Trim());
    private static DeviceInformationField Integer(DeviceInformationDefinition definition, ulong? value)
        => Value(definition, value?.ToString(CultureInfo.InvariantCulture));
    private static DeviceInformationField Boolean(DeviceInformationDefinition definition, bool? value) => Value(definition, value);
    private static DeviceInformationField Number(DeviceInformationDefinition definition, double? value)
        => Value<double?>(definition, value is { } number && double.IsFinite(number) ? number : null);
    private static DeviceInformationField Value<T>(DeviceInformationDefinition definition, T value)
        => new(definition, value is null ? null : JsonSerializer.SerializeToElement(value, JsonOptions));
}
