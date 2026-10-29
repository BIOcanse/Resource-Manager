using System.Runtime.InteropServices;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Vendors.Amd;
using ResourceManager.ReadWrite.Vendors.Intel;
using ResourceManager.ReadWrite.Vendors.Nvidia;
using ResourceManager.ReadWrite.Windows.Gpu;

static class WindowsGpuVendorConsumer
{
    internal static void VerifyMissingIntelControlIdentity()
        => Require(new IntelGraphicsControlBridge().Probe(null) == IntelGraphicsControlProbe.TargetMissing,
            "missing write identity does not initialize hardware");

    public static void Run()
    {
        var owner = typeof(ReadWriteCore).Assembly;
        Require(new[] { typeof(WindowsGpuAdapterOrderReader), typeof(WindowsGpuIdentityBridge),
            typeof(WindowsGpuProviderIdentityResolver), typeof(NvidiaNvmlReader), typeof(NvidiaNvapiReader),
            typeof(AmdAdlxBridgeReader), typeof(IntelGclReader), typeof(NvidiaNvmlControlBridge),
            typeof(NvidiaNvapiControlBridge), typeof(IntelGraphicsControlBridge), typeof(GpuMetrics),
            typeof(HardwareSensorProviderState) }.All(type => type.Assembly == owner), "GPU implementation has no product assembly owner");
        var declarations = WindowsInformationFieldProjection.GpuDeclarations(
            [new("PCI\\RECORDED", "0000000000000001", "GPU", "10DE", "1234", "1.2")],
            [new("pci\\recorded", "vbios", "8.9")], [new("PCI\\AMD", "bios", "GDDR6", "part", "date")], DateTimeOffset.UtcNow);
        Require(declarations.Devices.Count == 2 && declarations.Devices.Single(device => device.DeviceIdentity == "windows-pnp|PCI\\RECORDED").Fields.Count == 7,
            "recorded declarations merge one physical identity and preserve vendor domains");
        Require(WindowsInformationFieldProjection.GpuDeclarations(
            [new("PCI\\RECORDED", null, null, null, null, null)], null, null, null).Devices.Single().Fields.All(field => field.Value is null),
            "failed declaration observation has no current fields");
        Require(GpuAdapterNameClassifier.IsLikelyIntegratedGpuName("AMD Radeon 780M")
            && !GpuAdapterNameClassifier.IsLikelyIntegratedGpuName("NVIDIA GeForce RTX 4090"), "original classification is independent of scores");
        _ = new NvidiaNvmlControlBridge();
        _ = new NvidiaNvapiControlBridge();
        VerifyMissingIntelControlIdentity();

        var nativePath = Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native", "ResourceManager.AdlxBridge.dll");
        var library = NativeLibrary.Load(nativePath);
        try
        {
            uint Query(string name) => Marshal.GetDelegateForFunctionPointer<UnsignedQuery>(NativeLibrary.GetExport(library, name))();
            Require(Query("ResourceManagerAdlxGetAbiVersion") == 6 && Query("ResourceManagerAdlxGetGpuMetricsSize") == 848
                && Query("ResourceManagerAdlxGetGpuSelectionSize") == 520, "packaged ADLX bridge matches the original managed ABI");
        }
        finally { NativeLibrary.Free(library); }

        var activityField = MetricFieldRegistry.Definitions.Single(field => field.Code == MetricFieldCode.AdlxNpuActivityPercent);
        var frequencyField = MetricFieldRegistry.Definitions.Single(field => field.Code == MetricFieldCode.AdlxNpuFrequencyMhz);
        Require(activityField.Name == "gpu_amd_npu_activity_percent" && activityField.Resource == MetricResource.Gpu
            && activityField.Vendor == MetricVendor.Amd && activityField.Unit == "%"
            && frequencyField.Name == "gpu_amd_npu_frequency_mhz" && frequencyField.Resource == MetricResource.Gpu
            && frequencyField.Vendor == MetricVendor.Amd && frequencyField.Unit == "MHz",
            "AMD ADLX NPU reports are GPU-vendor fields, separate from the OS NPU resource");

        var inventory = new WindowsGpuAdapterOrderReader().ReadInventory();
        var windows = WindowsGpuInformationProjection.Create(inventory);
        var nvidia = new NvidiaNvmlReader().ReadDeclarations(inventory);
        var amd = new AmdAdlxBridgeReader().ReadDeclarations(inventory);
        var intelRequests = inventory.Adapters.Where(adapter => adapter.VendorId == 0x8086).ToDictionary(adapter => adapter.Index,
            _ => (IReadOnlySet<MetricFieldCode>)new HashSet<MetricFieldCode> { MetricFieldCode.IgclSampleTimestamp, MetricFieldCode.GpuGraphicsClock });
        var intel = new IntelGclReader().Read(inventory, intelRequests, CancellationToken.None);
        var time = inventory.Status == SamplingObservationStatus.Current ? DateTimeOffset.UtcNow : (DateTimeOffset?)null;
        var actual = WindowsInformationFieldProjection.GpuDeclarations(windows, nvidia, amd, time);
        Console.WriteLine($"ReadWriteCore GPU vendor consumer passed: inventory {inventory.Status}, adapters {inventory.Adapters.Count}, Windows declaration objects {windows?.Count ?? -1}, NVIDIA declaration objects {nvidia?.Count ?? -1}, AMD declaration objects {amd?.Count ?? -1}, Intel telemetry objects {intel.Count}, canonical objects {actual.Devices.Count}; packaged ADLX ABI and recorded vendor identity/null contracts passed; no hardware writes.");
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint UnsignedQuery();
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
