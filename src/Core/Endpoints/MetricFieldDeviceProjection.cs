using ResourceManager.App.Application.DeviceTopology;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Domain.Metrics;

namespace ResourceManager.App.Endpoints;

public static partial class ResourceManagerEndpointRouteBuilderExtensions
{
    private static IReadOnlyList<DeviceMetricFieldSnapshot> ReadMetricFields(
        HardwareMetricSnapshot snapshot, string? device, IReadOnlySet<string> fields, IServiceProvider services)
    {
        var project = CreateMetricDeviceProjection(services, snapshot.FieldBindings.Values);
        return MetricDeviceFields.Read(snapshot, device, fields)
            .Select(value => value with { Device = project(value.Device) }).ToArray();
    }

    private static IReadOnlyList<MetricDeviceFieldCatalog> ReadMetricFieldCatalog(
        HardwareMetricSnapshot snapshot, IServiceProvider services)
    {
        var project = CreateMetricDeviceProjection(services, snapshot.FieldBindings.Values);
        return MetricDeviceFields.Catalog(snapshot)
            .Select(value => value with { Device = project(value.Device) }).ToArray();
    }

    private static Func<MetricDeviceDescriptor, MetricDeviceDescriptor> CreateMetricDeviceProjection(
        IServiceProvider services, IEnumerable<MetricDeviceFieldBinding> bindings)
    {
        // Read the existing topology cache once. Projection never collects or subscribes.
        var ports = services.GetService<IDeviceTopologySnapshotProvider>()?.ReadState().Snapshot?.Ports;
        if (ports is not { Count: > 0 }) return static device => device;
        var pnpPorts = ports.GroupBy(static port => port.DeviceId.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(static group => group.Count() == 1)
            .ToDictionary(static group => group.Key, static group => group.Single(), StringComparer.OrdinalIgnoreCase);
        var disks = ports.Where(static port => !string.IsNullOrWhiteSpace(port.Storage?.SerialNumber))
            .GroupBy(static port => port.Storage!.SerialNumber!.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(static group => group.Count() == 1)
            .ToDictionary(static group => group.Key, static group => group.Single(), StringComparer.OrdinalIgnoreCase);
        var uniqueDiskSerials = bindings.GroupBy(static binding => binding.DeviceIdentity, StringComparer.Ordinal)
            .Select(static group => group.First().Device)
            .Where(static device => device.Kind == "storage" && !string.IsNullOrWhiteSpace(device.Address?.SerialNumber))
            .GroupBy(static device => device.Address!.SerialNumber!.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(static group => group.Count() == 1).Select(static group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var networks = ports.Where(static port => port.Network?.InterfaceGuid is not null)
            .GroupBy(static port => port.Network!.InterfaceGuid!, StringComparer.OrdinalIgnoreCase)
            .Where(static group => group.Count() == 1)
            .ToDictionary(static group => group.Key, static group => group.Single(), StringComparer.OrdinalIgnoreCase);
        return device =>
        {
            var address = device.Address;
            if (address is null) return device;
            DeviceTopologyPort? matched = null;
            if (address.PnpInstanceId is { } pnp) pnpPorts.TryGetValue(pnp.Trim(), out matched);
            // PhysicalDisk.DeviceId and Win32_DiskDrive.Index are distinct numbering systems.
            else if (device.Kind == "storage" && !string.IsNullOrWhiteSpace(address.SerialNumber)
                && uniqueDiskSerials.Contains(address.SerialNumber.Trim())
                && disks.TryGetValue(address.SerialNumber.Trim(), out var disk))
                matched = disk;
            else if (device.Kind == "network_interface" && address.InterfaceGuid is { } guid)
                networks.TryGetValue(guid, out matched);

            var pnpMembers = address.PnpInstanceIds?.Select(member =>
                pnpPorts.TryGetValue(member.Trim(), out var port) ? port.DeviceId : member).ToArray();
            var name = device.DisplayName ?? (device.Kind switch
            {
                "storage" => matched?.Storage?.Model,
                "network_interface" => matched?.Network?.InterfaceDescription ?? matched?.Network?.InterfaceName,
                _ => null
            });
            return device with
            {
                Address = address with { PnpInstanceId = matched?.DeviceId ?? address.PnpInstanceId,
                    PnpInstanceIds = pnpMembers },
                DisplayName = string.IsNullOrWhiteSpace(name) ? null : name
            };
        };
    }
}
