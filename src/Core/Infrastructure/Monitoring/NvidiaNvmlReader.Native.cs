using System.Runtime.InteropServices;
using System.Text;
namespace ResourceManager.App.Infrastructure.Monitoring;
internal sealed partial class NvidiaNvmlReader
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
    }
}
internal sealed record NvidiaNvmlReadRequest(
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
        IncludeElectricalState: true);

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

internal enum NvmlClockType
{
    Graphics = 0,
    Sm = 1,
    Memory = 2,
    Video = 3
}

internal enum NvmlTemperatureSensor
{
    Gpu = 0
}

[StructLayout(LayoutKind.Sequential)]
internal struct NvmlUtilization
{
    public uint Gpu;
    public uint Memory;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NvmlMemory
{
    public ulong Total;
    public ulong Free;
    public ulong Used;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
internal struct NvmlPciInfo
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
