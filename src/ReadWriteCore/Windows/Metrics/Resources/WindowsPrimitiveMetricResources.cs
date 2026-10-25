using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Windows;
using ResourceManager.ReadWrite.Windows.Devices;
namespace ResourceManager.ReadWrite.Windows.Metrics.Resources;

public sealed partial class WindowsMemoryMetricResource
{

    public WindowsMemoryMetricResource() { }


    public MemoryMetrics ReadMetrics()
    {
        if (readMemory is not null) return readMemory();
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Physical memory collection requires Windows.");
        var status = WindowsSystemCounters.ReadPhysicalMemory();
        if (!status.IsAvailable)
        {
            return new MemoryMetrics(UsedBytes: 0, TotalBytes: 0, UsagePercent: 0, IsUsageAvailable: false)
            {
                ObservationStatus = SamplingObservationStatus.Unavailable
            };
        }

        var used = status.TotalBytes - status.AvailableBytes;
        var percent = status.TotalBytes > 0 ? used * 100d / status.TotalBytes : 0;
        return new MemoryMetrics(UsedBytes: used, TotalBytes: status.TotalBytes, UsagePercent: MonitoringMetricSanitizer.ClampPercent(percent), IsUsageAvailable: true)
        {
            AvailableBytes = status.AvailableBytes,
            ObservationStatus = SamplingObservationStatus.Current
        };
    }

    public MemoryMetrics CreateNotRequestedMetrics()
    {
        return new MemoryMetrics(UsedBytes: 0, TotalBytes: 0, UsagePercent: 0, IsUsageAvailable: false)
        {
            ObservationStatus = SamplingObservationStatus.NotRequested
        };
    }

}

public sealed class WindowsVirtualMemoryMetricResource
{
    public VirtualMemoryMetrics ReadMetrics()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Commit memory collection requires Windows.");
        if (!WindowsMetricNative.GetPerformanceInfo(
                out var performance,
                (uint)Marshal.SizeOf<PerformanceInformation>()))
        {
            return CreateUnavailableMetrics();
        }

        return CreateCommitMetrics(performance);
    }

    public static VirtualMemoryMetrics CreateCommitMetrics(
        PerformanceInformation performance)
    {
        if (performance.PageSize == 0 || performance.CommitLimit == 0
            || performance.CommitTotal > performance.CommitLimit
            || (ulong)performance.CommitLimit > ulong.MaxValue / (ulong)performance.PageSize)
        {
            return CreateUnavailableMetrics();
        }

        var usedBytes = (ulong)performance.CommitTotal * (ulong)performance.PageSize;
        var limitBytes = (ulong)performance.CommitLimit * (ulong)performance.PageSize;
        return new VirtualMemoryMetrics(UsedBytes: usedBytes, TotalBytes: limitBytes, UsagePercent: usedBytes * 100d / limitBytes, IsSelectable: true)
        {
            CommitPeakBytes = PagesToBytes(performance.CommitPeak, performance.PageSize),
            SystemCacheBytes = PagesToBytes(performance.SystemCache, performance.PageSize),
            KernelPagedBytes = PagesToBytes(performance.KernelPaged, performance.PageSize),
            KernelNonpagedBytes = PagesToBytes(performance.KernelNonpaged, performance.PageSize),
            ProcessCount = performance.ProcessCount,
            ThreadCount = performance.ThreadCount,
            HandleCount = performance.HandleCount,
            ObservationStatus = SamplingObservationStatus.Current
        };
    }

    private static ulong? PagesToBytes(nuint pages, nuint pageSize)
        => pageSize != 0 && (ulong)pages <= ulong.MaxValue / (ulong)pageSize
            ? (ulong)pages * (ulong)pageSize
            : null;

    private static VirtualMemoryMetrics CreateUnavailableMetrics()
        => new(UsedBytes: 0, TotalBytes: 0, UsagePercent: 0, IsSelectable: false)
        {
            ObservationStatus = SamplingObservationStatus.Unavailable
        };

    public static VirtualMemoryMetrics CreateNotRequestedMetrics()
    {
        return new VirtualMemoryMetrics(UsedBytes: 0, TotalBytes: 0, UsagePercent: 0, IsSelectable: false)
        {
            ObservationStatus = SamplingObservationStatus.NotRequested
        };
    }

    public static WindowsPagefileConfiguration ReadPagefileConfiguration()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Pagefile settings require Windows.");
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management");
        var lines = ReadPagefileRegistryLines(key);
        if (lines.Length == 0)
        {
            return new(WindowsPagefileConfigurationKind.Missing, []);
        }

        var entries = lines
            .Select(ParsePagefileRegistryLine)
            .Where(static entry => entry is not null)
            .Select(static entry => entry!.Value)
            .ToArray();
        if (entries.Length == 0)
        {
            return new(WindowsPagefileConfigurationKind.Invalid, []);
        }

        if (entries.Any(static entry => entry.InitialMb <= 0 || entry.MaximumMb <= 0))
        {
            return new(WindowsPagefileConfigurationKind.SystemManaged, entries);
        }

        if (entries.Any(static entry => entry.InitialMb != entry.MaximumMb))
        {
            return new(WindowsPagefileConfigurationKind.Dynamic, entries);
        }

        return new(WindowsPagefileConfigurationKind.Fixed, entries);
    }

    [SupportedOSPlatform("windows")]
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

    private static WindowsPagefileEntry? ParsePagefileRegistryLine(string line)
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
        return new WindowsPagefileEntry(path, initialMb, maximumMb);
    }


}
public enum WindowsPagefileConfigurationKind { Missing, Invalid, SystemManaged, Dynamic, Fixed }
public readonly record struct WindowsPagefileEntry(string Path, int InitialMb, int MaximumMb);
public sealed record WindowsPagefileConfiguration(WindowsPagefileConfigurationKind Kind, IReadOnlyList<WindowsPagefileEntry> Entries);


