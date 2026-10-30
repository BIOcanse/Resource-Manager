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

        var counters = WindowsSystemCounters.ReadCpuCounters();
        return new CpuCounterObservation(counters.IsAvailable, counters.IdleTicks, counters.KernelTicks, counters.UserTicks,
            counters.MonotonicTicks, counters.MonotonicTicksPerSecond, counters.ObservedAt);
    }

    [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.Fixed)]
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
    public MemoryMetrics ReadMetrics() => resource.ReadMetrics();
    public MemoryMetrics CreateNotRequestedMetrics() => resource.CreateNotRequestedMetrics();
}
public sealed class WindowsVirtualMemoryMonitoringZone : MonitoringSourceZone
{
    private readonly ResourceManager.ReadWrite.Windows.Metrics.Resources.WindowsVirtualMemoryMetricResource resource = new();
    public WindowsVirtualMemoryMonitoringZone() : base(MonitoringSourceZoneIds.WindowsVirtualMemory) { }
    public VirtualMemoryMetrics ReadMetrics() => resource.ReadMetrics();
    public static VirtualMemoryMetrics CreateNotRequestedMetrics()
        => ResourceManager.ReadWrite.Windows.Metrics.Resources.WindowsVirtualMemoryMetricResource.CreateNotRequestedMetrics();
    internal static VirtualMemoryMetrics CreateCommitMetrics(PerformanceInformation value)
        => ResourceManager.ReadWrite.Windows.Metrics.Resources.WindowsVirtualMemoryMetricResource.CreateCommitMetrics(new()
        {
            Size = value.Size, CommitTotal = value.CommitTotal, CommitLimit = value.CommitLimit, CommitPeak = value.CommitPeak,
            PhysicalTotal = value.PhysicalTotal, PhysicalAvailable = value.PhysicalAvailable, SystemCache = value.SystemCache,
            KernelTotal = value.KernelTotal, KernelPaged = value.KernelPaged, KernelNonpaged = value.KernelNonpaged,
            PageSize = value.PageSize, HandleCount = value.HandleCount, ProcessCount = value.ProcessCount, ThreadCount = value.ThreadCount
        });
}
