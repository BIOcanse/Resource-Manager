using ResourceManager.App.Application.DeviceTopology;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Domain.Metrics;

namespace ResourceManager.App.Endpoints;

public static partial class ResourceManagerEndpointRouteBuilderExtensions
{
    private static IReadOnlyList<DeviceMetricFieldSnapshot> ReadMetricFields(HardwareMetricSnapshot snapshot,
        IReadOnlyList<MetricFieldSelection> selections, IServiceProvider services)
        => selections.SelectMany(item => ReadMetricFields(snapshot, item.Device, item.Fields, services))
            .GroupBy(static item => item.DeviceIdentity, StringComparer.Ordinal)
            .Select(static group => group.First() with
            { Fields = group.SelectMany(static item => item.Fields).DistinctBy(static field => field.Name, StringComparer.Ordinal).ToArray() })
            .ToArray();

    private static IReadOnlyList<DeviceMetricFieldSnapshot> ReadMetricFields(
        HardwareMetricSnapshot snapshot, string? device, IReadOnlySet<string> fields, IServiceProvider services)
    {
        var project = CreateMetricDeviceProjection(services, snapshot.FieldBindings.Values);
        return MetricDeviceFields.Read(snapshot, device, fields)
            .Select(value => value with { Device = project(value.Device) }).ToArray();
    }

    private static Func<MetricDeviceDescriptor, MetricDeviceDescriptor> CreateMetricDeviceProjection(
        IServiceProvider services, IEnumerable<MetricDeviceFieldBinding> bindings)
        => ResourceManager.App.Application.Metrics.MetricDeviceProjection.Create(
            services.GetService<IDeviceTopologySnapshotProvider>()?.ReadState().Snapshot?.Ports, bindings);
}