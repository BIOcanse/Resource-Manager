using System.Globalization;
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
        if (!NativeMethods.GetPerformanceInfo(
                out var performance,
                (uint)Marshal.SizeOf<PerformanceInformation>()))
        {
            return CreateUnavailableMetrics(settings.Detail);
        }

        return CreateCommitMetrics(performance, settings.Detail);
    }

    internal static VirtualMemoryMetrics CreateCommitMetrics(
        PerformanceInformation performance,
        string pagefileDetail)
    {
        if (performance.PageSize == 0 || performance.CommitLimit == 0
            || performance.CommitTotal > performance.CommitLimit
            || (ulong)performance.CommitLimit > ulong.MaxValue / (ulong)performance.PageSize)
        {
            return CreateUnavailableMetrics(pagefileDetail);
        }

        var usedBytes = (ulong)performance.CommitTotal * (ulong)performance.PageSize;
        var limitBytes = (ulong)performance.CommitLimit * (ulong)performance.PageSize;
        return new VirtualMemoryMetrics(
            usedBytes,
            limitBytes,
            usedBytes * 100d / limitBytes,
            $"{pagefileDetail}；系统提交量 / 当前提交上限，包含物理内存与页面文件后备；不是页面文件实际占用。",
            IsSelectable: true)
        {
            ObservationStatus = SamplingObservationStatus.Current
        };
    }

    private static VirtualMemoryMetrics CreateUnavailableMetrics(string detail)
        => new(0, 0, 0, $"{detail}；系统提交量不可用。", IsSelectable: false)
        {
            ObservationStatus = SamplingObservationStatus.Unavailable
        };

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
