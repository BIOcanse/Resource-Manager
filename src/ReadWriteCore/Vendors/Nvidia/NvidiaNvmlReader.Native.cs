using ResourceManager.ReadWrite.Windows.Gpu;
using ResourceManager.ReadWrite.Native;
using System.Runtime.InteropServices;
using System.Text;
using ResourceManager.App.Domain.Metrics;
namespace ResourceManager.ReadWrite.Vendors.Nvidia;
public sealed partial class NvidiaNvmlReader
{
    private static class NativeMethods
    {
        [DllImport("nvml.dll", EntryPoint = "nvmlInit_v2")]
        internal static extern int nvmlInit();

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetCount_v2")]
        internal static extern int nvmlDeviceGetCount(out uint deviceCount);

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetHandleByIndex_v2")]
        internal static extern int nvmlDeviceGetHandleByIndex(uint index, out IntPtr device);

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetName", CharSet = CharSet.Ansi)]
        internal static extern int nvmlDeviceGetName(IntPtr device, StringBuilder name, uint length);

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetVbiosVersion")]
        internal static extern int GetVbiosVersion(IntPtr device, [Out] byte[] version, uint length);

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetCudaComputeCapability")]
        internal static extern int GetCudaComputeCapability(IntPtr device, out int major, out int minor);

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetUtilizationRates")]
        internal static extern int nvmlDeviceGetUtilizationRates(IntPtr device, out NvmlUtilization utilization);

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetClockInfo")]
        internal static extern int nvmlDeviceGetClockInfo(IntPtr device, NvmlClockType type, out uint clockMhz);

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetMaxClockInfo")]
        internal static extern int nvmlDeviceGetMaxClockInfo(IntPtr device, NvmlClockType type, out uint clockMhz);

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetMemoryInfo")]
        internal static extern int nvmlDeviceGetMemoryInfo(IntPtr device, out NvmlMemory memory);

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetPciInfo_v3", CharSet = CharSet.Ansi)]
        internal static extern int nvmlDeviceGetPciInfo(IntPtr device, out NvmlPciInfo pciInfo);

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetPowerUsage")]
        internal static extern int nvmlDeviceGetPowerUsage(IntPtr device, out uint powerMilliwatts);

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetPowerManagementLimit")]
        internal static extern int nvmlDeviceGetPowerManagementLimit(IntPtr device, out uint limitMilliwatts);

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetEnforcedPowerLimit")]
        internal static extern int nvmlDeviceGetEnforcedPowerLimit(IntPtr device, out uint limitMilliwatts);

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetPowerManagementDefaultLimit")]
        internal static extern int nvmlDeviceGetPowerManagementDefaultLimit(IntPtr device, out uint limitMilliwatts);

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetTemperature")]
        internal static extern int nvmlDeviceGetTemperature(IntPtr device, NvmlTemperatureSensor sensorType, out uint temperatureCelsius);

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetFanSpeed")]
        internal static extern int nvmlDeviceGetFanSpeed(IntPtr device, out uint speedPercent);

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetNumFans")]
        internal static extern int nvmlDeviceGetNumFans(IntPtr device, out uint fanCount);

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetFanSpeed_v2")]
        internal static extern int nvmlDeviceGetFanSpeedV2(IntPtr device, uint fan, out uint speedPercent);

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetFanSpeedRPM")]
        internal static extern int GetFanSpeedRpm(IntPtr device, ref NvmlFanSpeedInfoV1 speed);

        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetFieldValues")]
        internal static extern int GetFieldValues(IntPtr device, int count, [In, Out] NvmlFieldValue[] values);
        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetMemoryInfo_v2")]
        internal static extern int GetMemoryV2(IntPtr device, ref NvmlMemoryV2 memory);
        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetPerformanceState")]
        internal static extern int GetPerformanceState(IntPtr device, out uint state);
        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetCurrentClocksEventReasons")]
        internal static extern int GetClockEvents(IntPtr device, out ulong reasons);
        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetSupportedClocksEventReasons")]
        internal static extern int GetSupportedClockEvents(IntPtr device, out ulong reasons);
        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetCurrPcieLinkGeneration")]
        internal static extern int GetCurrentPcieGeneration(IntPtr device, out uint value);
        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetCurrPcieLinkWidth")]
        internal static extern int GetCurrentPcieWidth(IntPtr device, out uint value);
        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetMaxPcieLinkGeneration")]
        internal static extern int GetMaximumPcieGeneration(IntPtr device, out uint value);
        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetMaxPcieLinkWidth")]
        internal static extern int GetMaximumPcieWidth(IntPtr device, out uint value);
        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetEncoderUtilization")]
        internal static extern int GetEncoderUtilization(IntPtr device, out uint percent, out uint periodUs);
        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetDecoderUtilization")]
        internal static extern int GetDecoderUtilization(IntPtr device, out uint percent, out uint periodUs);
    }
}
public sealed record NvidiaNvmlReadRequest(
    IReadOnlySet<int>? DisplayIndexes,
    bool IncludeUsage,
    bool IncludeGraphicsClocks,
    bool IncludeMemory,
    bool IncludeMemoryClock,
    bool IncludePowerUsage,
    bool IncludePowerLimit,
    bool IncludeTemperature,
    bool IncludeFan,
    bool IncludeElectricalState)
{
    public IReadOnlySet<MetricFieldCode> AdditionalMetrics { get; init; } = new HashSet<MetricFieldCode>();
    public static NvidiaNvmlReadRequest All { get; } = new(
        null,
        IncludeUsage: true,
        IncludeGraphicsClocks: true,
        IncludeMemory: true,
        IncludeMemoryClock: true,
        IncludePowerUsage: true,
        IncludePowerLimit: true,
        IncludeTemperature: true,
        IncludeFan: true,
        IncludeElectricalState: true)
    {
        AdditionalMetrics = MetricFieldRegistry.Definitions.Where(static item => NvidiaNvmlReader.IsAdditionalMetric(item.Code))
            .Select(static item => item.Code).ToHashSet()
    };

    public bool IncludesAnySensor =>
        IncludePowerUsage
        || IncludePowerLimit
        || IncludeTemperature
        || IncludeFan
        || IncludeElectricalState;

    public bool IncludesDisplayIndex(int displayIndex)
    {
        return DisplayIndexes is null || DisplayIndexes.Contains(displayIndex);
    }
}

public enum NvmlClockType
{
    Graphics = 0,
    Sm = 1,
    Memory = 2,
    Video = 3
}

public enum NvmlTemperatureSensor
{
    Gpu = 0
}

[StructLayout(LayoutKind.Sequential)]
public struct NvmlUtilization
{
    public uint Gpu;
    public uint Memory;
}

[StructLayout(LayoutKind.Sequential)]
public struct NvmlMemory
{
    public ulong Total;
    public ulong Free;
    public ulong Used;
}

// NVIDIA/go-nvml nvml.h, Windows LLP64 ABI. NVML_STRUCT_VERSION includes sizeof.
[StructLayout(LayoutKind.Sequential)]
public struct NvmlMemoryV2
{
    public uint Version;
    public ulong Total;
    public ulong Reserved;
    public ulong Free;
    public ulong Used;
}

[StructLayout(LayoutKind.Explicit, Size = 8)]
public struct NvmlValue
{
    [FieldOffset(0)] public double Double;
    [FieldOffset(0)] public uint UInt32;
    [FieldOffset(0)] public ulong UInt64;
    [FieldOffset(0)] public long Int64;
    [FieldOffset(0)] public int Int32;
    [FieldOffset(0)] public ushort UInt16;
}

[StructLayout(LayoutKind.Sequential)]
public struct NvmlFieldValue
{
    public uint FieldId;
    public uint ScopeId;
    public long TimestampUs;
    public long LatencyUs;
    public int ValueType;
    public int ReturnCode;
    public NvmlValue Value;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
public struct NvmlPciInfo
{
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 16)]
    public string BusIdLegacy;

    public uint Domain;
    public uint Bus;
    public uint Device;
    public uint PciDeviceId;
    public uint PciSubSystemId;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
    public string BusId;
}

// NVML nvmlFanSpeedInfo_v1_t: NVML versions shift by 24, unlike NVAPI's versions.
[StructLayout(LayoutKind.Sequential)]
public struct NvmlFanSpeedInfoV1
{
    public const uint StructVersion = 12U | (1U << 24);
    public uint Version;
    public uint Fan;
    public uint Speed;
}
