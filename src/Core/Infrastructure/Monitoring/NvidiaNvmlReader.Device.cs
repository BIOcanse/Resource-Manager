using System.Text;
using ResourceManager.App.Domain.Metrics;

namespace ResourceManager.App.Infrastructure.Monitoring;

internal sealed partial class NvidiaNvmlReader
{
    private static GpuMetrics? ReadDevice(
        IntPtr device,
        WindowsGpuAdapterInventoryRead windowsInventory,
        NvidiaNvmlReadRequest request)
    {
        var name = ReadName(device);
        var pciInfo = ReadPciInfo(device);
        if (pciInfo is null
            || !WindowsGpuProviderIdentityResolver.TryParseNvmlPciEvidence(
                pciInfo.Value,
                out var evidence))
        {
            return null;
        }

        var match = WindowsGpuProviderIdentityResolver.ResolvePci(
            windowsInventory,
            evidence);
        if (!match.IsCurrent)
        {
            return null;
        }

        var adapter = match.Binding.Adapter;
        if (!request.IncludesDisplayIndex(adapter.Index))
        {
            return null;
        }

        var usage = request.IncludeUsage ? ReadUtilization(device) : default;
        var memory = request.IncludeMemory ? ReadMemory(device) : default;
        var graphicsClock = request.IncludeGraphicsClocks ? ReadClock(device, NvmlClockType.Graphics) : 0;
        var memoryClock = request.IncludeMemoryClock ? ReadClock(device, NvmlClockType.Memory) : 0;
        var maxGraphicsClock = request.IncludeGraphicsClocks ? ReadMaxClock(device, NvmlClockType.Graphics) : 0;
        var sensors = ReadSensors(device, request);
        var graphicsPercent = maxGraphicsClock > 0 ? graphicsClock * 100d / maxGraphicsClock : 0;
        var memoryPercent = memory.Total > 0 ? memory.Used * 100d / memory.Total : 0;

        return new GpuMetrics(
            adapter.Index,
            string.IsNullOrWhiteSpace(name) ? adapter.Name : name,
            usage.Utilization.Gpu,
            graphicsClock,
            maxGraphicsClock,
            graphicsPercent,
            memoryClock,
            memory.Used,
            memory.Total,
            memoryPercent,
            sensors,
            UsageProvider: "NVIDIA NVML",
            IsUsageAvailable: usage.IsAvailable);
    }

    private static string ReadName(IntPtr device)
    {
        var name = new StringBuilder(96);
        return NativeMethods.nvmlDeviceGetName(device, name, (uint)name.Capacity) == NvmlSuccess
            ? name.ToString()
            : "NVIDIA GPU";
    }

    private static GpuUtilizationReading ReadUtilization(IntPtr device)
    {
        return NativeMethods.nvmlDeviceGetUtilizationRates(device, out var utilization) == NvmlSuccess
            ? new GpuUtilizationReading(utilization, IsAvailable: true)
            : default;
    }

    private static NvmlMemory ReadMemory(IntPtr device)
    {
        return NativeMethods.nvmlDeviceGetMemoryInfo(device, out var memory) == NvmlSuccess
            ? memory
            : default;
    }

    private static NvmlPciInfo? ReadPciInfo(IntPtr device)
    {
        try
        {
            return NativeMethods.nvmlDeviceGetPciInfo(device, out var pciInfo) == NvmlSuccess
                ? pciInfo
                : null;
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
    }

    private static int ReadClock(IntPtr device, NvmlClockType type)
    {
        return NativeMethods.nvmlDeviceGetClockInfo(device, type, out var clock) == NvmlSuccess
            ? checked((int)clock)
            : 0;
    }

    private static int ReadMaxClock(IntPtr device, NvmlClockType type)
    {
        return NativeMethods.nvmlDeviceGetMaxClockInfo(device, type, out var clock) == NvmlSuccess
            ? checked((int)clock)
            : 0;
    }

    private readonly record struct GpuUtilizationReading(NvmlUtilization Utilization, bool IsAvailable);
}
