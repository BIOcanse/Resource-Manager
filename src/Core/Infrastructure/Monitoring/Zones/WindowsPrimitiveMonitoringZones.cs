using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Diagnostics;
using Microsoft.Win32;
using ResourceManager.App.Application.Monitoring;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.Windows;

namespace ResourceManager.App.Infrastructure.Monitoring;

public sealed class WindowsCpuMonitoringZone : MonitoringSourceZone
{
    public WindowsCpuMonitoringZone()
        : base(MonitoringSourceZoneIds.WindowsCpu)
    {
        CpuName = ReadCpuName();
    }

    public string CpuName { get; }

    public CpuCounterObservation ReadCounters()
    {
        var observedAt = DateTimeOffset.UtcNow;
        if (!CanRead
            || !NativeMethods.GetSystemTimes(
                out var idle,
                out var kernel,
                out var user))
        {
            return new CpuCounterObservation(
                false,
                0,
                0,
                0,
                0,
                0,
                observedAt);
        }

        return new CpuCounterObservation(
            true,
            idle.ToUInt64(),
            kernel.ToUInt64(),
            user.ToUInt64(),
            checked((ulong)Stopwatch.GetTimestamp()),
            checked((ulong)Stopwatch.Frequency),
            observedAt);
    }

    private static string ReadCpuName()
    {
        try
        {
            return Registry.GetValue(
                @"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0",
                "ProcessorNameString",
                "CPU")?.ToString() ?? "CPU";
        }
        catch
        {
            return "CPU";
        }
    }

    public readonly record struct CpuCounterObservation(
        bool IsAvailable,
        ulong IdleTicks,
        ulong KernelTicks,
        ulong UserTicks,
        ulong MonotonicTicks,
        ulong MonotonicTicksPerSecond,
        DateTimeOffset ObservedAt);
}

public sealed class WindowsMemoryMonitoringZone : MonitoringSourceZone
{
    private readonly string? hardwareDescription = SmbiosMemoryReader.ReadDescription();

    public WindowsMemoryMonitoringZone()
        : base(MonitoringSourceZoneIds.WindowsMemory)
    {
    }

    internal string HardwareDescription =>
        hardwareDescription ?? "Physical RAM";

    public MemoryMetrics ReadMetrics()
    {
        if (!CanRead)
        {
            return CreateNotRequestedMetrics();
        }

        var status = MemoryStatusEx.Create();
        if (!NativeMethods.GlobalMemoryStatusEx(ref status))
        {
            return new MemoryMetrics(0, 0, 0, false, hardwareDescription ?? "Physical RAM")
            {
                ObservationStatus = SamplingObservationStatus.Unavailable
            };
        }

        var used = status.TotalPhys - status.AvailPhys;
        var percent = status.TotalPhys > 0 ? used * 100d / status.TotalPhys : 0;
        return new MemoryMetrics(
            used,
            status.TotalPhys,
            MonitoringMetricSanitizer.ClampPercent(percent),
            true,
            hardwareDescription ?? "Physical RAM")
        {
            ObservationStatus = SamplingObservationStatus.Current
        };
    }

    public MemoryMetrics CreateNotRequestedMetrics()
    {
        return new MemoryMetrics(0, 0, 0, false, hardwareDescription ?? "Physical RAM")
        {
            ObservationStatus = SamplingObservationStatus.NotRequested
        };
    }

}

public sealed class WindowsVirtualMemoryMonitoringZone : MonitoringSourceZone
{
    private static readonly TimeSpan PagefileSettingsCacheDuration = TimeSpan.FromSeconds(5);
    private readonly object pagefileSettingsGate = new();
    private PagefileSettings? cachedPagefileSettings;
    private DateTimeOffset cachedPagefileSettingsAt;

    public WindowsVirtualMemoryMonitoringZone()
        : base(MonitoringSourceZoneIds.WindowsVirtualMemory)
    {
    }

    public VirtualMemoryMetrics ReadMetrics()
    {
        if (!CanRead)
        {
            return CreateNotRequestedMetrics();
        }

        var settings = GetPagefileSettings();
        var usage = ReadPagefileUsage();
        if (usage.TotalBytes <= 0)
        {
            return new VirtualMemoryMetrics(
                0,
                0,
                0,
                $"{settings.Detail}；页面文件使用量不可用，虚拟内存监控不列入可选项。",
                IsSelectable: false)
            {
                ObservationStatus = SamplingObservationStatus.Unavailable
            };
        }

        var percent = usage.TotalBytes > 0 ? usage.UsedBytes * 100d / usage.TotalBytes : 0;
        return new VirtualMemoryMetrics(
            usage.UsedBytes,
            usage.TotalBytes,
            MonitoringMetricSanitizer.ClampPercent(percent),
            $"{settings.Detail}；页面文件实际使用量，不包含物理内存。",
            IsSelectable: true)
        {
            ObservationStatus = SamplingObservationStatus.Current
        };
    }

    public static VirtualMemoryMetrics CreateNotRequestedMetrics()
    {
        return new VirtualMemoryMetrics(
            0,
            0,
            0,
            "Windows Commit / Not requested",
            IsSelectable: false)
        {
            ObservationStatus = SamplingObservationStatus.NotRequested
        };
    }

    private PagefileSettings GetPagefileSettings()
    {
        var now = DateTimeOffset.UtcNow;
        lock (pagefileSettingsGate)
        {
            if (cachedPagefileSettings is { } settings
                && now - cachedPagefileSettingsAt < PagefileSettingsCacheDuration)
            {
                return settings;
            }
        }

        var refreshed = ReadPagefileSettings();
        lock (pagefileSettingsGate)
        {
            cachedPagefileSettings = refreshed;
            cachedPagefileSettingsAt = now;
        }

        return refreshed;
    }

    private static PagefileSettings ReadPagefileSettings()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management");
        var lines = ReadPagefileRegistryLines(key);
        if (lines.Length == 0)
        {
            return new PagefileSettings("未读取到页面文件配置");
        }

        var entries = lines
            .Select(ParsePagefileRegistryLine)
            .Where(static entry => entry is not null)
            .Select(static entry => entry!.Value)
            .ToArray();
        if (entries.Length == 0)
        {
            return new PagefileSettings("页面文件配置无法解析");
        }

        if (entries.Any(static entry => entry.InitialMb <= 0 || entry.MaximumMb <= 0))
        {
            var managedPaths = string.Join(", ", entries.Select(static entry => entry.Path));
            return new PagefileSettings($"Windows 系统管理页面文件（{managedPaths}）");
        }

        if (entries.Any(static entry => entry.InitialMb != entry.MaximumMb))
        {
            var dynamicPaths = string.Join(
                ", ",
                entries.Select(static entry => $"{entry.Path} {entry.InitialMb:N0}-{entry.MaximumMb:N0} MB"));
            return new PagefileSettings($"动态页面文件（{dynamicPaths}）");
        }

        var totalMb = entries.Sum(static entry => (long)entry.MaximumMb);
        var paths = string.Join(", ", entries.Select(static entry => $"{entry.Path} {entry.MaximumMb:N0} MB"));
        return new PagefileSettings($"固定页面文件 {totalMb:N0} MB（{paths}）");
    }

    private static PagefileUsage ReadPagefileUsage()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "root\\CIMV2",
                "SELECT AllocatedBaseSize, CurrentUsage FROM Win32_PageFileUsage");
            var totalBytes = 0UL;
            var usedBytes = 0UL;
            foreach (ManagementBaseObject row in searcher.Get())
            {
                totalBytes += MebibytesToBytes(ReadUInt64(row["AllocatedBaseSize"]));
                usedBytes += MebibytesToBytes(ReadUInt64(row["CurrentUsage"]));
            }

            return new PagefileUsage(usedBytes, totalBytes);
        }
        catch (ManagementException)
        {
            return default;
        }
        catch (UnauthorizedAccessException)
        {
            return default;
        }
    }

    private static ulong ReadUInt64(object? value)
    {
        return value switch
        {
            null => 0,
            ulong number => number,
            uint number => number,
            long number when number > 0 => (ulong)number,
            int number when number > 0 => (ulong)number,
            _ => ulong.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : 0
        };
    }

    private static ulong MebibytesToBytes(ulong value)
    {
        return value * 1024UL * 1024UL;
    }

    private static string[] ReadPagefileRegistryLines(RegistryKey? key)
    {
        var value = key?.GetValue("PagingFiles");
        return value switch
        {
            string[] values => values.Where(static item => !string.IsNullOrWhiteSpace(item)).ToArray(),
            string text => [text],
            _ => []
        };
    }

    private static PagefileEntry? ParsePagefileRegistryLine(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 3)
        {
            return null;
        }

        if (!int.TryParse(parts[^2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var initialMb)
            || !int.TryParse(parts[^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var maximumMb))
        {
            return null;
        }

        var path = string.Join(' ', parts[..^2]);
        return new PagefileEntry(path, initialMb, maximumMb);
    }

    private readonly record struct PagefileSettings(string Detail);

    private readonly record struct PagefileEntry(string Path, int InitialMb, int MaximumMb);

    private readonly record struct PagefileUsage(ulong UsedBytes, ulong TotalBytes);
}

internal readonly record struct CpuFrequency(int CurrentMhz, int MaxMhz, double Percent, string Source);

internal static class MonitoringMetricSanitizer
{
    public static double ClampPercent(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return 0;
        }

        return Math.Clamp(value, 0, 100);
    }

    public static double NormalizeNonNegative(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return 0;
        }

        return Math.Max(0, value);
    }
}
