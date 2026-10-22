using ResourceManager.ReadWrite.Windows.Devices.Npu;
using ResourceManager.ReadWrite.Windows.Metrics.Topology;
using ResourceManager.ReadWrite.Helpers;
using ResourceManager.ReadWrite.Windows.Gpu.Control;
using ResourceManager.ReadWrite.Windows.Metrics.Writes;
using ResourceManager.App.Domain.Messages;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Native;
using ResourceManager.ReadWrite.Native.Pdh;
using ResourceManager.ReadWrite.Windows.Cpu;
using ResourceManager.ReadWrite.Vendors.Mechrevo;
using ResourceManager.ReadWrite.Windows;
using ResourceManager.ReadWrite.Windows.Gpu;
using ResourceManager.ReadWrite.Windows.Pdh;
using ResourceManager.ReadWrite.Windows.Platform;
using ResourceManager.ReadWrite.Windows.Devices;
using ResourceManager.ReadWrite.Windows.Devices.Battery;
using ResourceManager.ReadWrite.Vendors.Nvidia;
using ResourceManager.ReadWrite.Vendors.Amd;
using ResourceManager.ReadWrite.Vendors.Amd.Smu;
using ResourceManager.ReadWrite.Vendors.Intel;
using ResourceManager.ReadWrite.Vendors.Intel.Msr;

namespace ResourceManager.ReadWrite.Windows.Metrics.Resources;

/// <summary>Explicit optional resource paths and policies. No deployment, driver installation or OEM probing.</summary>
public sealed record WindowsMetricResourceOptions
{
    public WindowsMetricControlOptions? Controls { get; init; }
    public string? IntelModulePath { get; init; }
    public string? PawnIoLibraryPath { get; init; }
    public AmdSmuPawnIoOpenOptions? AmdSmu { get; init; }
    public string? MechrevoLibraryPath { get; init; }
    public bool EnableMechrevo { get; init; }
    public double? MaximumCpuBoostMegahertz { get; init; }
    public TimeSpan AmdSmuFailureCacheDuration { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan StorageCapabilityCacheLifetime { get; init; } = TimeSpan.FromSeconds(15);
    public uint MaximumNpuEngineNameCharacters { get; init; } = 4096;
    public TimeSpan WmiQueryTimeout { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan DiskPerformanceSampleWindow { get; init; } = TimeSpan.FromMilliseconds(250);
}

public sealed partial class WindowsHardwareMetricResources
{
    /// <summary>Owns the original readers. Construction opens no PDH, vendor, driver or EC session.</summary>
    public static WindowsHardwareMetricResources CreateDefault(NativePdhSessionOptions pdhOptions,
        WindowsMetricResourceOptions? options = null, TimeProvider? clock = null)
    {
        options ??= new();
        clock ??= TimeProvider.System;
        var memory = new WindowsMemoryMetricResource();
        var virtualMemory = new WindowsVirtualMemoryMetricResource();
        var frequency = new WindowsCpuFrequencyResource(options.MaximumCpuBoostMegahertz is { } boost ? () => boost : null);
        var inventory = new WindowsGpuInventoryOwner();
        var network = new WindowsNetworkInterfaceMetricResource(new WindowsNetworkInterfaceCounterApi());
        var battery = new WindowsBatteryReader();
        var npuMemory = new WindowsNpuMemoryReader();
        var npuEngine = new WindowsNpuEngineReader(options.MaximumNpuEngineNameCharacters);
        var pdh = new NativePdhSession(new NativePdhApi(), pdhOptions, clock);
        var gpuUsage = new PdhGpuEngineUsageReader(pdh);
        var systemIo = new PdhSystemIoReader(pdh);
        var nvidiaControl = new NvidiaGpuControlResource();
        var nvml = new NvidiaNvmlReader();
        var nvapi = new NvidiaNvapiReader();
        var adlx = new AmdAdlxBridgeReader();
        var intelGpu = new IntelGclReader();
        var intelMsr = new IntelMsrReader(() => new PawnIoIntelCpuHardware(
            options.IntelModulePath ?? throw new FileNotFoundException("Intel PawnIO module path was not supplied."),
            options.PawnIoLibraryPath ?? throw new FileNotFoundException("PawnIO library path was not supplied.")));
        AmdSmuCpuSensorReader? smu = null;
        var mechrevo = new MechrevoUwAcpiMetricResource(() => options.MechrevoLibraryPath,
            path => new MechrevoUwAcpiReader(path));
        var storage = new WindowsStorageOwner(options.StorageCapabilityCacheLifetime, clock,
            available => BackendMessage.Create(0, 0, available ? "storage-current" : "storage-unavailable"));
        var diskPerformance = new WindowsDiskPerformanceCaptureReader(
            new WindowsDiskLayoutInformationReader(new WindowsDiskLayoutInformationNativeApi()), clock, options.DiskPerformanceSampleWindow);
        var hardwareMonitor = new HardwareMonitorWmiReader();
        WindowsMetricControlResources? controls = null;
        HardwareBridgeProcessClient? helper = null;
        IntelCpuControlResource? intelControl = null;
        AmdCpuControlResource? amdControl = null;
        AmdDesktopGpuControlResource? amdDesktopControl = null;
        IntegratedGpuControlResource? integratedControl = null;
        if (options.Controls is { } controlOptions)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(controlOptions.BaselineDirectory);
            var baseline = Path.GetFullPath(controlOptions.BaselineDirectory);
            if (options.IntelModulePath is { } module && options.PawnIoLibraryPath is { } library)
                intelControl = IntelCpuControlResource.CreateDefault(new(module, library, Path.Combine(baseline, "intel-cpu.json")));
            if (controlOptions.HardwareBridgeExecutablePath is { } executable
                && controlOptions.HardwareBridgeOptions is { } helperOptions)
            {
                var helperPath = Path.GetFullPath(executable);
                helper = new HardwareBridgeProcessClient(() => File.Exists(helperPath) ? helperPath : null, helperOptions);
                amdControl = new AmdCpuControlResource(helper, AmdCpuControlOptions.InDirectory(baseline));
            }
            amdDesktopControl = new AmdDesktopGpuControlResource(new AdlxTuningHardware(),
                new(Path.Combine(baseline, "amd-desktop-gpu.json")));
            integratedControl = new IntegratedGpuControlResource(helper, new IntelGraphicsControlBridge(),
                new(Path.Combine(baseline, "amd-integrated-gpu.json"), controlOptions.IsIntelOverclockAccepted));
            controls = new()
            {
                IntelCpuControl = intelControl, AmdCpuControl = amdControl,
                AmdDesktopGpuControl = amdDesktopControl, IntegratedGpuControl = integratedControl,
                ControlIsFixedChassis = controlOptions.IsFixedChassis
            };
        }
        WindowsHardwareMetricResources? result = null;
        result = new()
        {
            NvidiaControl = nvidiaControl, Controls = controls,
            CpuName = CpuIdentityInformationReader.Read()?.Brand ?? "CPU",
            MemoryDescription = memory.HardwareDescription,
            ReadCpuCounters = () => { if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows CPU counters require Windows.");
                var value = WindowsSystemCounters.ReadCpuCounters(); return new(value.IsAvailable,
                value.IdleTicks, value.KernelTicks, value.UserTicks, value.MonotonicTicks, value.MonotonicTicksPerSecond, value.ObservedAt); },
            ReadMemory = memory.ReadMetrics, ReadVirtualMemory = virtualMemory.ReadMetrics,
            ReadNumaAvailable = memory.ReadNumaAvailable, ReadNumaCapacityMiB = memory.ReadNumaCapacityMiB,
            ReadNumaIdentities = WindowsMemoryMetricResource.ReadNumaIdentities,
            ReadGpuInventory = () => inventory.ReadInventory(true), ReadCpuFrequency = frequency.ReadFrequency,
            ReadGpuUsage = gpuUsage.ReadUsageSnapshotByAdapterIndex,
            ReadNvmlAsync = nvml.ReadAsync, ReadNvapiAsync = nvapi.ReadAsync, ReadAdlxAsync = adlx.ReadAsync,
            ReadNvapiDrs = nvapi.ReadDrs, DiscoverNvapiDrs = nvapi.DiscoverDrs,
            ReadNvmlFanIdentities = nvml.ReadFanIdentities, ReadNvmlFansAsync = nvml.ReadFansAsync,
            ReadIntelGpu = intelGpu.Read, ReadIntelMsr = intelMsr.Read,
            OpenIgclFanDirectory = (inventory, maximum, prior) => WindowsIntelFanTopology.OpenDirectory((current, limit) => intelGpu.OpenFanDirectory(current, limit), inventory, maximum, prior),
            ReadIgclFans = intelGpu.ReadFans,
            ReadNpuMemoryIdentities = maximum => OperatingSystem.IsWindows()
                ? WindowsNpuMemoryTopology.Read(maximum)
                : new(maximum == 0 ? NativeMetricSnapshotCatalogTopologyStatus.Complete
                    : NativeMetricSnapshotCatalogTopologyStatus.Unavailable, []),
            ReadNpuMemory = npuMemory.Read,
            OpenNpuEngineDirectory = (maximum, prior) => WindowsNpuEngineTopology.OpenDirectory(
                limit => npuEngine.OpenDirectory(limit), maximum, prior),
            ReadNpuEngines = npuEngine.Read,
            ReadAmdSmu = request =>
            {
                if (smu is null)
                {
                    // The same SMU reader owns its original failure cache/session; topology is an actual SDK OS read.
                    var cores = WindowsCpuOsTopologyReader.ReadPhysicalCoreInformation()?.Count;
                    if (cores is not > 0) throw new InvalidOperationException("Physical CPU core topology is unavailable.");
                    smu = new(() => AmdSmuPawnIoSession.Open(options.AmdSmu
                        ?? throw new FileNotFoundException("AMD SMU PawnIO options were not supplied.")), cores.Value,
                        options.AmdSmuFailureCacheDuration, clock);
                    result!.owned.Add(smu);
                }
                return smu.Read(request);
            },
            ReadBatteryIdentities = () => battery.ReadIdentities(),
            ReadBattery = (devices, selected) => devices.Where(device => selected.ContainsKey(device.DisplayIndex))
                .Select(device => battery.Read(device, selected[device.DisplayIndex])).ToArray(),
            ReadNetworkIdentities = network.ReadInterfaceIdentities, ReadNetworkInterfaces = network.ReadInterfaces,
            ReadSystemIo = systemIo.Read,
            ReadStorageIdentities = generation =>
            {
                var read = storage.ReadIdentities();
                return new(read.Error is null ? NativeMetricSnapshotCatalogTopologyStatus.Complete : NativeMetricSnapshotCatalogTopologyStatus.Unavailable,
                    read.Identities.Select(item => StorageCatalogIdentity(item, generation)).ToArray());
            },
            ReadDiskPerformanceAsync = (targets, token) =>
                diskPerformance.ReadAsync(targets, storage.ReadInformationDeclarations(), token),
            ReadFanIdentities = generation => WindowsNotebookFanProjection.CreateFanCatalog(
                hardwareMonitor.ReadFanIdentities(options.WmiQueryTimeout), options.EnableMechrevo, generation),
            ReadStorageSensors = () => ProjectStorageSensors(storage.ReadStorageSensors()),
            ReadNvmeSensors = request => ProjectStorageSensors(storage.ReadNvmeSensorsCore(request)),
            ReadAtaSensors = request => ProjectStorageSensors(storage.ReadAtaSensorsCore(request)),
            ReadHardwareMonitor = () => hardwareMonitor.Read(options.WmiQueryTimeout),
            ReadNotebookFans = () => options.EnableMechrevo ? WindowsNotebookFanProjection.Create(mechrevo.Read())
                : new(new("Notebook OEM Fan Provider", "UnsupportedFirmware", "OEM firmware policy was not enabled."), null, null, []),
            SynchronizeNotebookDemand = demanded => { if (!demanded) mechrevo.ReleaseSession(); }
        };
        // One actual helper is shared by CPU/APU resources and disposed after them.
        // Borrowed resource Dispose does not dispose this shared client twice.
        if (helper is not null) result.owned.Add(helper);
        if (intelControl is not null) result.owned.Add(intelControl);
        if (amdControl is not null) result.owned.Add(amdControl);
        if (amdDesktopControl is not null) result.owned.Add(amdDesktopControl);
        if (integratedControl is not null) result.owned.Add(integratedControl);
        result.owned.Add(nvidiaControl);
        result.owned.Add(nvapi);
        result.owned.Add(frequency);
        result.owned.Add(pdh);
        result.owned.Add(mechrevo);
        return result;
    }

    // The single storage catalog projection. The Core host's borrowed storage zone uses it too,
    // so every production path publishes the same storage descriptor.
    internal static NativeMetricSnapshotOrdinalCatalogIdentity StorageCatalogIdentity(StorageOwnerIdentity identity, ulong generation)
        => new(identity.DisplayIndex, identity.ExactIdentityKey, generation)
        {
            SupportsNvmeHealth = identity.SupportsNvmeHealth,
            SupportsAtaSmart = identity.SupportsAtaSmart,
            Device = new("storage", MetricDeviceAddress.StorageDevice(identity.DisplayIndex,
                identity.UniqueId, identity.SerialNumber), Clean(identity.NameDeclared), identity.DisplayIndex)
        };

    internal static StorageSensorSnapshot ProjectStorageSensors(StorageOwnerSensorSnapshot snapshot)
        => new(snapshot.ProviderState, snapshot.Disks)
        { Identities = snapshot.Identities?.Select(identity => StorageCatalogIdentity(identity, 0)).ToArray() };

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
