using ResourceManager.ReadWrite.Vendors.Amd;
using ResourceManager.ReadWrite.Windows.Metrics.Writes;
using ResourceManager.ReadWrite.Vendors.Nvidia;
using ResourceManager.ReadWrite.Windows.Gpu;
using ResourceManager.ReadWrite.Windows.Pdh;
using ResourceManager.ReadWrite.Windows.Metrics.Resources;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.ReadWrite.Vendors.Intel.Msr;
using ResourceManager.ReadWrite.Windows.Devices.Battery;
namespace ResourceManager.App.Infrastructure.Monitoring;
public sealed partial class WindowsHardwareMetricSampler
{
    internal static NativeMetricSnapshotSourceCompletion CreateNvmlCompletion(
        NativeMetricSnapshotCollectionPlan plan,
        NativeMetricSnapshotSourcePlanOutput sourcePlan,
        NativeMetricSnapshotSourceObservationIdentity identity,
        DateTimeOffset capturedAt,
        IReadOnlyList<GpuMetrics> values)
        => ResourceManager.ReadWrite.Windows.Metrics.Capture.WindowsHardwareMetricCapture.CreateNvmlCompletion(plan, sourcePlan, identity, capturedAt, values);

    internal static NativeMetricSnapshotSourceCompletion CreateAdlxCompletion(NativeMetricSnapshotCollectionPlan plan,
        NativeMetricSnapshotSourcePlanOutput sourcePlan, NativeMetricSnapshotSourceObservationIdentity identity,
        DateTimeOffset capturedAt, IReadOnlyList<GpuMetrics> values)
        => ResourceManager.ReadWrite.Windows.Metrics.Capture.WindowsHardwareMetricCapture.CreateAdlxCompletion(plan, sourcePlan, identity, capturedAt, values);

    internal static AmdAdlxReadRequest CreateAdlxReadRequest(IReadOnlyList<NativeMetricSnapshotRuleCatalogBinding> selected)
        => ResourceManager.ReadWrite.Windows.Metrics.Capture.WindowsHardwareMetricCapture.CreateAdlxReadRequest(selected);

    internal static AtaSmartSensorReadRequest CreateAtaSmartReadRequest(NativeMetricSnapshotCollectionPlan plan, ulong sourceHandle)
        => ResourceManager.ReadWrite.Windows.Metrics.Capture.WindowsHardwareMetricCapture.CreateAtaSmartReadRequest(plan, sourceHandle);

    internal static NativeMetricSnapshotSourceCompletion CreateStorageCompletion(
        NativeMetricSnapshotCollectionPlan plan, NativeMetricSnapshotSourcePlanOutput sourcePlan,
        NativeMetricSnapshotSourceObservationIdentity identity, DateTimeOffset capturedAt, StorageSensorSnapshot sensors)
        => ResourceManager.ReadWrite.Windows.Metrics.Capture.WindowsHardwareMetricCapture.CreateStorageCompletion(plan, sourcePlan, identity, capturedAt, sensors);

    internal static bool MatchesStorageInventory(NativeMetricSnapshotCatalogProjection catalog, StorageSensorSnapshot sensors)
        => ResourceManager.ReadWrite.Windows.Metrics.Capture.WindowsHardwareMetricCapture.MatchesStorageInventory(catalog, sensors);

    internal static NativeMetricSnapshotSourceCompletion CreateNotebookFanCompletion(
        NativeMetricSnapshotCollectionPlan plan,
        NativeMetricSnapshotSourcePlanOutput sourcePlan,
        NativeMetricSnapshotSourceObservationIdentity identity,
        DateTimeOffset capturedAt,
        NotebookOemFanSensorSnapshot sensors)
        => ResourceManager.ReadWrite.Windows.Metrics.Capture.WindowsHardwareMetricCapture.CreateNotebookFanCompletion(plan, sourcePlan, identity, capturedAt, sensors);

    internal static NativeMetricSnapshotSourceStatus
        ResolveNotebookOemProviderStatus(
            HardwareSensorProviderState providerState)
        => ResourceManager.ReadWrite.Windows.Metrics.Capture.WindowsHardwareMetricCapture.ResolveNotebookOemProviderStatus(providerState);

    internal static double? ResolveNotebookOemFanValue(
        NativeMetricSnapshotRuleCatalogBinding binding,
        NotebookOemFanSensorSnapshot sensors,
        int? exactGpuDisplayIndex,
        PlatformGpuSensor? gpuFan)
        => ResourceManager.ReadWrite.Windows.Metrics.Capture.WindowsHardwareMetricCapture.ResolveNotebookOemFanValue(binding, sensors, exactGpuDisplayIndex, gpuFan);

    internal static bool MatchesExactGpuInventory(
        NativeMetricSnapshotCatalogProjection catalog,
        IReadOnlyList<WindowsGpuAdapter> adapters)
        => ResourceManager.ReadWrite.Windows.Metrics.Capture.WindowsHardwareMetricCapture.MatchesExactGpuInventory(catalog, adapters);

    private WindowsHardwareMetricResources CreateBorrowedResources(WindowsMetricControlResources? borrowedControls) => new()
    {
        Controls = borrowedControls ?? new WindowsMetricControlResources(),
        CpuName = cpuZone?.CpuName ?? "CPU",
        MemoryDescription = memoryZone?.HardwareDescription ?? "Physical RAM",
        // Production supplies these zones; metadata-only fixtures may omit them without invoking a read.
        ReadCpuCounters = () => { var value = cpuZone!.ReadCounters(); return new(value.IsAvailable, value.IdleTicks,
            value.KernelTicks, value.UserTicks, value.MonotonicTicks, value.MonotonicTicksPerSecond, value.ObservedAt); },
        ReadMemory = () => memoryZone!.ReadMetrics(), ReadVirtualMemory = () => virtualMemoryZone.ReadMetrics(),
        ReadNumaAvailable = (nodes, devices) => memoryZone!.ReadNumaAvailable(nodes, devices), ReadNumaIdentities = WindowsMemoryMonitoringZone.ReadNumaIdentities,
        ReadGpuInventory = () => gpuAdapterOrderZone.ReadInventory(requested: true), ReadCpuFrequency = () => cpuFrequencyZone.ReadFrequency(),
        ReadGpuUsage = adapters => gpuEngineZone.ReadUsageSnapshotByAdapterIndex(adapters),
        ReadNvmlAsync = (request, inventory, token) => nvidiaZone.ReadAsync(request, inventory, token),
        ReadNvmlFanIdentities = inventory => nvidiaZone.ReadFanIdentities(inventory),
        ReadNvmlFansAsync = (targets, inventory, token) => nvidiaZone.ReadFansAsync(targets, inventory, token),
        ReadNvapiAsync = (request, inventory, token) => nvidiaNvapiZone.ReadAsync(request, inventory, token),
        ReadNvapiDrs = (profiles, limits, token) => nvidiaNvapiZone.ReadDrs(profiles, limits, token),
        DiscoverNvapiDrs = (limits, token) => nvidiaNvapiZone.DiscoverDrs(limits, token),
        ReadAdlxAsync = (request, inventory, token) => amdAdlxZone.ReadAsync(request, inventory, token), ReadAmdSmu = request => amdSmuZone.Read(request),
        ReadIntelGpu = (inventory, request, token) => intelGclZone.Read(inventory, request, token),
        OpenIgclFanDirectory = intelGclZone.OpenFanTopology, ReadIgclFans = intelGclZone.ReadFans,
        ReadIntelMsr = (devices, request) => intelMsrZone.Read(devices, request),
        ReadNpuMemoryIdentities = npuMemoryZone.ReadIdentities, ReadNpuMemory = npuMemoryZone.Read,
        OpenNpuEngineDirectory = npuEngineZone.OpenDirectory, ReadNpuEngines = npuEngineZone.Read,
        ReadBatteryIdentities = () => batteryZone.ReadIdentities(), ReadBattery = (devices, request) => batteryZone.Read(devices, request),
        ReadNetworkIdentities = () => networkZone.ReadInterfaceIdentities(), ReadNetworkInterfaces = (devices, request) => networkZone.ReadInterfaces(devices, request),
        ReadSystemIo = request => systemIoZone.Read(request), ReadStorageIdentities = generation => WindowsStorageSensorsMonitoringZone.ReadIdentities(generation),
        ReadDiskPerformanceAsync = (targets, token) => platformSensorReader.ReadDiskPerformanceAsync(targets, token),
        ReadFanIdentities = generation => HardwareMonitorWmiMonitoringZone.ReadFanIdentities(generation),
        ReadStorageSensors = () => platformSensorReader.ReadStorageSensors(), ReadNvmeSensors = request => platformSensorReader.ReadNvmeSensors(request),
        ReadAtaSensors = request => platformSensorReader.ReadAtaSensors(request), ReadHardwareMonitor = () => platformSensorReader.ReadHardwareMonitorSensors(),
        ReadNotebookFans = () => platformSensorReader.ReadNotebookOemFanSensors(),
        SynchronizeNotebookDemand = demanded => platformSensorReader?.SynchronizeNotebookOemFanDemand(demanded)
    };}






