using System.ComponentModel;
using System.Runtime.InteropServices;
using ResourceManager.App.Domain.CpuTopology;

namespace ResourceManager.App.Infrastructure.CpuTopology;

public sealed partial class WindowsCpuTopologyReader
{
    private static CpuSpecificationModel ResolveCpuSpecification()
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "select Name, Manufacturer, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed, CurrentClockSpeed, L2CacheSize, L3CacheSize from Win32_Processor");
            foreach (var item in searcher.Get())
            {
                var name = Convert.ToString(item["Name"])?.Trim();
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var vendor = Convert.ToString(item["Manufacturer"])?.Trim();
                var preset = CpuCorePerformancePresetResolver.Classify(name);
                return new CpuSpecificationModel(
                    name,
                    string.IsNullOrWhiteSpace(vendor) ? preset.Vendor : vendor,
                    preset.Family,
                    ReadInt(item["NumberOfCores"]) ?? 0,
                    ReadInt(item["NumberOfLogicalProcessors"]) ?? 0,
                    ReadInt(item["MaxClockSpeed"]),
                    ReadInt(item["CurrentClockSpeed"]),
                    ReadInt(item["L2CacheSize"]),
                    ReadInt(item["L3CacheSize"]),
                    "Win32_Processor");
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Management.ManagementException or UnauthorizedAccessException)
        {
        }

        var fallbackName = RuntimeInformation.ProcessArchitecture.ToString();
        var fallbackPreset = CpuCorePerformancePresetResolver.Classify(fallbackName);
        return new CpuSpecificationModel(
            fallbackName,
            fallbackPreset.Vendor,
            fallbackPreset.Family,
            0,
            Environment.ProcessorCount,
            null,
            null,
            null,
            null,
            "RuntimeInformation");
    }

    private static int? ReadInt(object? value)
    {
        return value switch
        {
            null => null,
            int number => number,
            uint number => number <= int.MaxValue ? (int)number : null,
            long number => number is >= 0 and <= int.MaxValue ? (int)number : null,
            ulong number => number <= int.MaxValue ? (int)number : null,
            short number => number,
            ushort number => number,
            _ => int.TryParse(Convert.ToString(value), out var parsed) ? parsed : null
        };
    }

    private static bool IsAmdRyzen(string cpuName)
    {
        return cpuName.Contains("AMD", StringComparison.OrdinalIgnoreCase)
            && cpuName.Contains("Ryzen", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsExpectedNativeException(Exception ex)
    {
        return ex is Win32Exception
            or InvalidOperationException
            or NotSupportedException
            or ExternalException;
    }
}
