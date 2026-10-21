using System.Security.Cryptography;
using System.Text.Json;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Domain.Messages;

namespace ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;

public sealed class DeviceTopologySemanticComparer
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string ComputeHash(DeviceTopologySnapshot snapshot)
    {
        var normalized = snapshot with
        {
            CapturedAt = DateTimeOffset.UnixEpoch,
            DriverInformation = snapshot.DriverInformation?.OrderBy(static row => row.DeviceIdentity, StringComparer.Ordinal).ToArray(),
            NpuInformation = snapshot.NpuInformation?.Select(static row => row with {
                LocationPathsDeclared = row.LocationPathsDeclared?.Order(StringComparer.Ordinal).ToArray()
            }).OrderBy(static row => row.DeviceIdentity, StringComparer.Ordinal).ToArray(),
            FirmwareDeclarations = snapshot.FirmwareDeclarations is null ? null : snapshot.FirmwareDeclarations with {
                Slots = snapshot.FirmwareDeclarations.Slots.OrderBy(static slot => slot.DeviceIdentity, StringComparer.Ordinal).ToArray(),
                ProcessorSockets = snapshot.FirmwareDeclarations.ProcessorSockets.OrderBy(static socket => socket.DeviceIdentity, StringComparer.Ordinal).ToArray()
            },
            Batteries = snapshot.Batteries?.OrderBy(static battery => battery.DeviceIdentity, StringComparer.Ordinal).ToArray(),
            StorageInformation = snapshot.StorageInformation?.OrderBy(static storage => storage.DeviceIdentity, StringComparer.Ordinal).ToArray(),
            PageFileInformation = snapshot.PageFileInformation?.OrderBy(static file => file.DeviceIdentity, StringComparer.Ordinal).ToArray(),
            DiskLayoutInformation = snapshot.DiskLayoutInformation?.Select(static disk => disk with {
                Layout = disk.Layout is null ? null : disk.Layout with { Partitions = disk.Layout.Partitions.OrderBy(static partition => partition.Number).ToArray() }
            }).OrderBy(static disk => disk.DeviceIdentity, StringComparer.Ordinal).ToArray(),
            GpuInformation = snapshot.GpuInformation?.OrderBy(static gpu => gpu.PnpInstanceId, StringComparer.Ordinal).ToArray(),
            NvidiaGpuInformation = snapshot.NvidiaGpuInformation?.OrderBy(static gpu => gpu.PnpInstanceId, StringComparer.Ordinal).ToArray(),
            AmdGpuInformation = snapshot.AmdGpuInformation?.OrderBy(static gpu => gpu.PnpInstanceId, StringComparer.Ordinal).ToArray(),
            HidInformation = snapshot.HidInformation?.OrderBy(static hid => hid.DeviceIdentity, StringComparer.Ordinal).ToArray(),
            NetworkInterfaceInformation = snapshot.NetworkInterfaceInformation?.OrderBy(static row => row.DeviceIdentity, StringComparer.Ordinal).ToArray(),
            VolumeSpaceInformation = snapshot.VolumeSpaceInformation?.Select(static volume => volume with {
                MountPaths = volume.MountPaths?.Order(StringComparer.Ordinal).ToArray(),
                DiskExtents = volume.DiskExtents?.OrderBy(static extent => extent.DiskNumberReported)
                    .ThenBy(static extent => extent.StartingOffsetBytes).ThenBy(static extent => extent.ExtentLengthBytes).ToArray()
            }).OrderBy(static volume => volume.DeviceIdentity, StringComparer.Ordinal).ToArray(),
            Ports = snapshot.Ports
                .Select(NormalizePort)
                .OrderBy(static port => port.Id, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            Notes = NormalizeNotes(snapshot.Notes)
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

    // 说明按「域/码/参数」排序去重，和字符串说明一样保证同一台机器两轮哈希一致。
    private static IReadOnlyList<BackendMessage> NormalizeNotes(IEnumerable<BackendMessage> notes)
    {
        return notes
            .DistinctBy(static note => (note.Domain, note.Code, string.Join('', note.Args)))
            .OrderBy(static note => note.Domain)
            .ThenBy(static note => note.Code)
            .ThenBy(static note => string.Join('', note.Args), StringComparer.Ordinal)
            .ToArray();
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

