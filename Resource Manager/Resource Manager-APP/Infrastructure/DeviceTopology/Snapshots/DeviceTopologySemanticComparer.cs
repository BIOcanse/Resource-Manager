using System.Security.Cryptography;
using System.Text.Json;
using ResourceManager.App.Domain.DeviceTopology;

namespace ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;

public sealed class DeviceTopologySemanticComparer
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string ComputeHash(DeviceTopologySnapshot snapshot)
    {
        var normalized = snapshot with
        {
            CapturedAt = DateTimeOffset.UnixEpoch,
            Ports = snapshot.Ports
                .Select(NormalizePort)
                .OrderBy(static port => port.Id, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            Notes = NormalizeStrings(snapshot.Notes)
        };
        var payload = JsonSerializer.SerializeToUtf8Bytes(normalized, JsonOptions);
        return Convert.ToHexString(SHA256.HashData(payload));
    }

    internal static DeviceTopologyPort NormalizePort(DeviceTopologyPort port)
    {
        return port with
        {
            LocationPaths = NormalizeStrings(port.LocationPaths),
            HardwareIds = NormalizeStrings(port.HardwareIds),
            CompatibleIds = NormalizeStrings(port.CompatibleIds),
            Usb = port.Usb is null
                ? null
                : port.Usb with
                {
                    CompanionPorts = port.Usb.CompanionPorts
                        .OrderBy(static companion => companion.CompanionIndex)
                        .ThenBy(static companion => companion.PortNumber)
                        .ThenBy(static companion => companion.HubSymbolicLinkName, StringComparer.OrdinalIgnoreCase)
                        .ToArray(),
                    InterfaceProtocols = NormalizeStrings(port.Usb.InterfaceProtocols),
                    Endpoints = port.Usb.Endpoints?
                        .OrderBy(static endpoint => endpoint.InterfaceNumber)
                        .ThenBy(static endpoint => endpoint.AlternateSetting)
                        .ThenBy(static endpoint => endpoint.EndpointAddress)
                        .ToArray()
                },
            Camera = port.Camera is null
                ? null
                : port.Camera with
                {
                    NativeModes = port.Camera.NativeModes
                        .OrderByDescending(static mode => mode.Width)
                        .ThenByDescending(static mode => mode.Height)
                        .ThenByDescending(static mode => mode.MaximumFrameRate)
                        .ThenBy(static mode => mode.PixelFormat, StringComparer.OrdinalIgnoreCase)
                        .ToArray()
                },
            SmartDevice = port.SmartDevice is null
                ? null
                : port.SmartDevice with
                {
                    Storages = port.SmartDevice.Storages
                        .OrderBy(static storage => storage.Name, StringComparer.OrdinalIgnoreCase)
                        .ToArray()
                },
            Storage = port.Storage is null
                ? null
                : port.Storage with
                {
                    Partitions = port.Storage.Partitions
                        .Select(static partition => partition with
                        {
                            Volumes = partition.Volumes
                                .OrderBy(static volume => volume.DriveLetter, StringComparer.OrdinalIgnoreCase)
                                .ThenBy(static volume => volume.Label, StringComparer.OrdinalIgnoreCase)
                                .ToArray()
                        })
                        .OrderBy(static partition => partition.StartingOffsetBytes ?? ulong.MaxValue)
                        .ThenBy(static partition => partition.PartitionNumber ?? uint.MaxValue)
                        .ThenBy(static partition => partition.DeviceId, StringComparer.OrdinalIgnoreCase)
                        .ToArray()
                }
        };
    }

    internal static string ComputeCanonicalPortPayload(DeviceTopologyPort port)
    {
        return JsonSerializer.Serialize(NormalizePort(port), JsonOptions);
    }

    private static IReadOnlyList<string> NormalizeStrings(IEnumerable<string> values)
    {
        return values
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
