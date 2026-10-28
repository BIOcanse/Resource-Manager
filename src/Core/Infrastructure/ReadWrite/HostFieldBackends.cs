using System.Collections.Immutable;
using ResourceManager.App.Application.DiskUsage;
using ResourceManager.App.Infrastructure.DiskUsage;
using ResourceManager.App.Infrastructure.CpuTopology;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Windows.SystemInformation;
using ResourceManager.Subscription;

namespace ResourceManager.App.Infrastructure.ReadWrite;

/// <summary>Connects public fields to their existing collection and publication owners.</summary>
public sealed class HostFieldBackends
{
    public CompositeReadWriteBackend Reads { get; }
    public CompositeFieldSubscriptionBackend Subscriptions { get; }
    private readonly HostPresentFrameFieldQueries? presentFrameQueries;

    /// <summary>
    /// The explicit query factory registered by the real Host composition. Query-scoped
    /// frame fields remain outside the fixed global Reads and Subscriptions catalogs.
    /// </summary>
    public HostPresentFrameFieldQueries PresentFrameQueries => presentFrameQueries
        ?? throw new InvalidOperationException("The Host Present-frame query factory is not registered for this manually composed backend.");

    public HostFieldBackends(HostReadWriteBackend metrics, HostFieldSubscriptionBackend metricSubscriptions,
        DeviceTopologySnapshotProvider devices, CpuTopologySnapshotProvider cpu, HostProcessFieldBackend processes,
        IDiskUsageTreeStore? diskUsage = null, EtwCpuCoreResidencyReader? cpuResidency = null,
        HostPresentFrameFieldQueries? presentFrameQueries = null)
    {
        this.presentFrameQueries = presentFrameQueries;
        var information = new InformationReadWriteBackend(() => devices.ReadState().InformationFields,
            devices.CaptureInformationAsync, ReadSupport, null,
            static definition => definition != DeviceInformationFieldRegistry.GpuNvidiaDriverProfileSettingValue
                && definition != DeviceInformationFieldRegistry.VolumeAllFreeBytesActual);
        var volumeFreeSpace = new InformationReadWriteBackend(devices.ReadVolumeFreeSpaceInformation,
            devices.CaptureAllVolumeFreeSpaceInformationAsync, ReadSupport,
            (selection, token) => devices.CaptureVolumeFreeSpaceInformationAsync(selection.Select(static field => field.Field).ToImmutableArray(), token));
        var networkEndpoints = new InformationReadWriteBackend(devices.ReadNetworkEndpointInformation,
            devices.CaptureNetworkEndpointInformationAsync, ReadSupport);
        var acpi = new InformationReadWriteBackend(() => devices.ReadState().AcpiInformationFields,
            devices.CaptureAcpiInformationAsync, ReadSupport);
        var topology = new InformationReadWriteBackend(cpu.GetInformationSnapshot, cpu.CaptureInformationAsync, ReadSupport);
        var backends = new List<IReadWriteBackend> { metrics, information, volumeFreeSpace, networkEndpoints, acpi, topology, processes };
        var subscriptions = new Dictionary<IReadWriteBackend, IFieldSubscriptionBackend>
        {
            [metrics] = metricSubscriptions,
            [information] = new DeviceFields(information, devices, DeviceTopologyFieldScope.Snapshot),
            [volumeFreeSpace] = new DeviceFields(volumeFreeSpace, devices, DeviceTopologyFieldScope.VolumeFreeSpace),
            [networkEndpoints] = new DeviceFields(networkEndpoints, devices, DeviceTopologyFieldScope.NetworkEndpoints),
            [acpi] = new DeviceFields(acpi, devices, DeviceTopologyFieldScope.Acpi),
            [topology] = new CpuFields(topology, cpu),
            [processes] = processes
        };
        if (cpuResidency is not null)
        {
            var residency = new HostCpuResidencyFieldBackend(cpuResidency);
            backends.Add(residency); subscriptions.Add(residency, residency);
        }
        if (devices.SoftwareInventory is { } inventory)
        {
            var software = new InformationReadWriteBackend(inventory.ReadInformation,
                devices.CaptureSoftwareInformationAsync, ReadSupport);
            backends.Add(software);
            subscriptions.Add(software, new DeviceFields(software, devices, DeviceTopologyFieldScope.Software));
        }
        var services = new InformationReadWriteBackend(devices.ServicesInventory.ReadInformation,
            token => devices.CaptureServiceInformationAsync(token), ReadSupport,
            (selection, token) => devices.CaptureServiceInformationAsync(token,
                selection.Any(static field => field.Definition.Name == "service_all_start_type_configured")));
        backends.Add(services);
        subscriptions.Add(services, new DeviceFields(services, devices, DeviceTopologyFieldScope.Services));
        var operatingSystem = new InformationReadWriteBackend(devices.OperatingSystemInventory.ReadInformation,
            token => devices.CaptureOperatingSystemInformationAsync(token), ReadSupport,
            (selection, token) => devices.CaptureOperatingSystemInformationAsync(token, WindowsOperatingSystemInformationOwner.SelectFields(selection)));
        backends.Add(operatingSystem);
        subscriptions.Add(operatingSystem, new DeviceFields(operatingSystem, devices, DeviceTopologyFieldScope.OperatingSystem));
        if (diskUsage is DiskUsageTreeStore committedTree)
        {
            var mft = committedTree.Backend;
            backends.Add(mft);
            subscriptions.Add(mft, new DeviceFields(mft, devices, DeviceTopologyFieldScope.PassiveCurrent));
        }
        Reads = new(backends);
        Subscriptions = new(Reads, subscriptions);
    }

    private sealed class DeviceFields(InformationReadWriteBackend backend, DeviceTopologySnapshotProvider provider,
        DeviceTopologyFieldScope scope)
        : IFieldSubscriptionBackend
    {
        public bool SupportsPeriodicDelivery => true;
        public IDisposable Acquire(string id, ImmutableArray<FieldReference> fields, TimeSpan interval)
            => Acquire(id, fields, interval, FieldSubscriptionOptions.Default);
        public IDisposable Acquire(string id, ImmutableArray<FieldReference> fields, TimeSpan interval, FieldSubscriptionOptions options)
            => provider.AcquireFieldSubscription(id, fields, interval, options, backend, scope);
        public IAsyncEnumerable<ImmutableArray<FieldReading>> SubscribeAsync(string id,
            ImmutableArray<FieldReference> fields, TimeSpan interval, CancellationToken cancellationToken)
            => SubscribeAsync(id, fields, interval, FieldSubscriptionOptions.Default, cancellationToken);
        public IAsyncEnumerable<ImmutableArray<FieldReading>> SubscribeAsync(string id,
            ImmutableArray<FieldReference> fields, TimeSpan interval, FieldSubscriptionOptions options, CancellationToken cancellationToken)
            => provider.SubscribeFieldsAsync(id, fields, interval, options, backend, scope, cancellationToken);
    }
    private sealed class CpuFields(InformationReadWriteBackend backend, CpuTopologySnapshotProvider provider)
        : IFieldSubscriptionBackend
    {
        public bool SupportsPeriodicDelivery => true;
        public IDisposable Acquire(string id, ImmutableArray<FieldReference> fields, TimeSpan interval)
            => Acquire(id, fields, interval, FieldSubscriptionOptions.Default);
        public IDisposable Acquire(string id, ImmutableArray<FieldReference> fields, TimeSpan interval, FieldSubscriptionOptions options)
            => provider.AcquireFieldSubscription(id, fields, interval, options, backend);
        public IAsyncEnumerable<ImmutableArray<FieldReading>> SubscribeAsync(string id,
            ImmutableArray<FieldReference> fields, TimeSpan interval, CancellationToken cancellationToken)
            => SubscribeAsync(id, fields, interval, FieldSubscriptionOptions.Default, cancellationToken);
        public IAsyncEnumerable<ImmutableArray<FieldReading>> SubscribeAsync(string id,
            ImmutableArray<FieldReference> fields, TimeSpan interval, FieldSubscriptionOptions options, CancellationToken cancellationToken)
            => provider.SubscribeFieldsAsync(id, fields, interval, options, backend, cancellationToken);
    }
    private static OperationSupport ReadSupport(DeviceInformationDefinition definition)
        => new(true, null, definition.SourceId switch { "nvidia.nvml" or "amd.adlx" => [definition.SourceId], _ => [] });

}




