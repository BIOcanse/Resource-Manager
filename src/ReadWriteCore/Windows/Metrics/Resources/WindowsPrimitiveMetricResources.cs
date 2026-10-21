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
    private readonly string? hardwareDescription = SmbiosMemoryReader.ReadDescription();

    public WindowsMemoryMetricResource() { }

    internal string HardwareDescription =>
        hardwareDescription ?? "Physical RAM";

    public MemoryMetrics ReadMetrics()
    {
        if (readMemory is not null) return readMemory();
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Physical memory collection requires Windows.");
        var status = WindowsSystemCounters.ReadPhysicalMemory();
        if (!status.IsAvailable)
        {
            return new MemoryMetrics(0, 0, 0, false, hardwareDescription ?? "Physical RAM")
            {
                ObservationStatus = SamplingObservationStatus.Unavailable
            };
        }

        var used = status.TotalBytes - status.AvailableBytes;
        var percent = status.TotalBytes > 0 ? used * 100d / status.TotalBytes : 0;
        return new MemoryMetrics(
            used,
            status.TotalBytes,
            MonitoringMetricSanitizer.ClampPercent(percent),
            true,
            hardwareDescription ?? "Physical RAM")
        {
            AvailableBytes = status.AvailableBytes,
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

public sealed class WindowsVirtualMemoryMetricResource
{
    private readonly Func<WindowsPagefileConfiguration, string> describePagefile;
    private readonly Func<string, bool, string> describeCommit;
    private static readonly TimeSpan PagefileSettingsCacheDuration = TimeSpan.FromSeconds(5);
    private readonly object pagefileSettingsGate = new();
    private PagefileSettings? cachedPagefileSettings;
    private DateTimeOffset cachedPagefileSettingsAt;

    public WindowsVirtualMemoryMetricResource(Func<WindowsPagefileConfiguration, string>? describePagefile = null,
        Func<string, bool, string>? describeCommit = null)
    {
        this.describePagefile = describePagefile ?? (static facts => facts.Kind.ToString());
        this.describeCommit = describeCommit ?? (static (detail, available) => detail + (available ? " / Windows commit" : " / unavailable"));
    }

    public VirtualMemoryMetrics ReadMetrics()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Commit memory collection requires Windows.");
        var settings = GetPagefileSettings();
        if (!WindowsMetricNative.GetPerformanceInfo(
                out var performance,
                (uint)Marshal.SizeOf<PerformanceInformation>()))
        {
            return CreateUnavailableMetrics(settings.Detail, describeCommit);
        }

        return CreateCommitMetrics(performance, settings.Detail, describeCommit);
    }

    public static VirtualMemoryMetrics CreateCommitMetrics(
        PerformanceInformation performance,
        string pagefileDetail, Func<string, bool, string>? describe = null)
    {
        if (performance.PageSize == 0 || performance.CommitLimit == 0
            || performance.CommitTotal > performance.CommitLimit
            || (ulong)performance.CommitLimit > ulong.MaxValue / (ulong)performance.PageSize)
        {
            return CreateUnavailableMetrics(pagefileDetail, describe);
        }

        var usedBytes = (ulong)performance.CommitTotal * (ulong)performance.PageSize;
        var limitBytes = (ulong)performance.CommitLimit * (ulong)performance.PageSize;
        return new VirtualMemoryMetrics(
            usedBytes,
            limitBytes,
            usedBytes * 100d / limitBytes,
            describe?.Invoke(pagefileDetail, true) ?? pagefileDetail,
            IsSelectable: true)
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

    private static VirtualMemoryMetrics CreateUnavailableMetrics(string detail, Func<string, bool, string>? describe = null)
        => new(0, 0, 0, describe?.Invoke(detail, false) ?? detail, IsSelectable: false)
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

    private PagefileSettings ReadPagefileSettings()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Pagefile settings require Windows.");
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management");
        var lines = ReadPagefileRegistryLines(key);
        if (lines.Length == 0)
        {
            return new(describePagefile(new(WindowsPagefileConfigurationKind.Missing, [])));
        }

        var entries = lines
            .Select(ParsePagefileRegistryLine)
            .Where(static entry => entry is not null)
            .Select(static entry => entry!.Value)
            .ToArray();
        if (entries.Length == 0)
        {
            return new(describePagefile(new(WindowsPagefileConfigurationKind.Invalid, [])));
        }

        if (entries.Any(static entry => entry.InitialMb <= 0 || entry.MaximumMb <= 0))
        {
            return new(describePagefile(new(WindowsPagefileConfigurationKind.SystemManaged, entries)));
        }

        if (entries.Any(static entry => entry.InitialMb != entry.MaximumMb))
        {
            return new(describePagefile(new(WindowsPagefileConfigurationKind.Dynamic, entries)));
        }

        return new(describePagefile(new(WindowsPagefileConfigurationKind.Fixed, entries)));
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

    private readonly record struct PagefileSettings(string Detail);

}
public enum WindowsPagefileConfigurationKind { Missing, Invalid, SystemManaged, Dynamic, Fixed }
public readonly record struct WindowsPagefileEntry(string Path, int InitialMb, int MaximumMb);
public sealed record WindowsPagefileConfiguration(WindowsPagefileConfigurationKind Kind, IReadOnlyList<WindowsPagefileEntry> Entries);


