using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using ResourceManager.App.Application.Monitoring;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.Windows;
using ResourceManager.ReadWrite.Windows;

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
        if (!CanRead)
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

        var counters = WindowsSystemCounters.ReadCpuCounters();
        return new CpuCounterObservation(counters.IsAvailable, counters.IdleTicks, counters.KernelTicks, counters.UserTicks,
            counters.MonotonicTicks, counters.MonotonicTicksPerSecond, counters.ObservedAt);
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

public sealed partial class WindowsMemoryMonitoringZone : MonitoringSourceZone
{
    private readonly ResourceManager.ReadWrite.Windows.Metrics.Resources.WindowsMemoryMetricResource resource = new();
    public WindowsMemoryMonitoringZone() : base(MonitoringSourceZoneIds.WindowsMemory) { }
    internal string HardwareDescription => resource.HardwareDescription;
    public MemoryMetrics ReadMetrics() => CanRead ? resource.ReadMetrics() : resource.CreateNotRequestedMetrics();
    public MemoryMetrics CreateNotRequestedMetrics() => resource.CreateNotRequestedMetrics();
}
public sealed class WindowsVirtualMemoryMonitoringZone : MonitoringSourceZone
{
    private readonly ResourceManager.ReadWrite.Windows.Metrics.Resources.WindowsVirtualMemoryMetricResource resource = new(DescribePagefile, DescribeCommit);
    public WindowsVirtualMemoryMonitoringZone() : base(MonitoringSourceZoneIds.WindowsVirtualMemory) { }
    public VirtualMemoryMetrics ReadMetrics() => CanRead ? resource.ReadMetrics() : CreateNotRequestedMetrics();
    public static VirtualMemoryMetrics CreateNotRequestedMetrics()
        => ResourceManager.ReadWrite.Windows.Metrics.Resources.WindowsVirtualMemoryMetricResource.CreateNotRequestedMetrics();
    internal static VirtualMemoryMetrics CreateCommitMetrics(PerformanceInformation value, string detail)
        => ResourceManager.ReadWrite.Windows.Metrics.Resources.WindowsVirtualMemoryMetricResource.CreateCommitMetrics(new()
        {
            Size = value.Size, CommitTotal = value.CommitTotal, CommitLimit = value.CommitLimit, CommitPeak = value.CommitPeak,
            PhysicalTotal = value.PhysicalTotal, PhysicalAvailable = value.PhysicalAvailable, SystemCache = value.SystemCache,
            KernelTotal = value.KernelTotal, KernelPaged = value.KernelPaged, KernelNonpaged = value.KernelNonpaged,
            PageSize = value.PageSize, HandleCount = value.HandleCount, ProcessCount = value.ProcessCount, ThreadCount = value.ThreadCount
        }, detail, DescribeCommit);
    private static string DescribeCommit(string detail, bool available) => available
        ? $"{detail}；系统提交量 / 当前提交上限，包含物理内存与页面文件后备；不是页面文件实际占用。"
        : $"{detail}；系统提交量不可用。";
    private static string DescribePagefile(ResourceManager.ReadWrite.Windows.Metrics.Resources.WindowsPagefileConfiguration facts)
        => facts.Kind switch
        {
            ResourceManager.ReadWrite.Windows.Metrics.Resources.WindowsPagefileConfigurationKind.Missing => "未读取到页面文件配置",
            ResourceManager.ReadWrite.Windows.Metrics.Resources.WindowsPagefileConfigurationKind.Invalid => "页面文件配置无法解析",
            ResourceManager.ReadWrite.Windows.Metrics.Resources.WindowsPagefileConfigurationKind.SystemManaged => $"Windows 系统管理页面文件（{string.Join(", ", facts.Entries.Select(entry => entry.Path))}）",
            ResourceManager.ReadWrite.Windows.Metrics.Resources.WindowsPagefileConfigurationKind.Dynamic => $"动态页面文件（{string.Join(", ", facts.Entries.Select(entry => $"{entry.Path} {entry.InitialMb:N0}-{entry.MaximumMb:N0} MB"))}）",
            _ => $"固定页面文件 {facts.Entries.Sum(entry => (long)entry.MaximumMb):N0} MB（{string.Join(", ", facts.Entries.Select(entry => $"{entry.Path} {entry.MaximumMb:N0} MB"))}）"
        };
}
