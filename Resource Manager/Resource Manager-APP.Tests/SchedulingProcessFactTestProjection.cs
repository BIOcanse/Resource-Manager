using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.ResourceBreakdown;

namespace Resource_Manager_APP.Tests;

internal static class SchedulingProcessFactTestProjection
{
    internal static SchedulingProcessFactSnapshot? Project(
        SchedulingProcessFactSnapshot source,
        SchedulingProcessFactRequest request)
    {
        var currentMask = source.CurrentMetricMask & request.RequestedMetricMask;
        if (currentMask.HasFlag(SchedulingProcessMetricMask.MemoryUsage)
            && (!source.DatasetObservations.TryGetValue(
                    SchedulingProcessMetricMask.MemoryUsage,
                    out var memoryObservation)
                || request.ExpectedMemoryUsageDependency is null
                || memoryObservation.MemoryUsageDependency !=
                    request.ExpectedMemoryUsageDependency))
        {
            currentMask &= ~SchedulingProcessMetricMask.MemoryUsage;
        }
        if (currentMask == SchedulingProcessMetricMask.None)
        {
            return null;
        }

        var processes = source.Processes
            .Select(process => ProjectProcess(process, currentMask))
            .Where(static process => process.ValidMetricMask !=
                SchedulingProcessMetricMask.None)
            .ToArray();
        var removed = checked(source.Processes.Count - processes.Length);
        return source with
        {
            EmittedCount = checked((uint)processes.Length),
            SkippedCount = checked(source.SkippedCount + (uint)removed),
            RequestedMetricMask = request.RequestedMetricMask,
            CurrentMetricMask = currentMask,
            Processes = processes,
            DatasetObservations = source.DatasetObservations
                .Where(pair => request.RequestedMetricMask.HasFlag(pair.Key))
                .ToDictionary(static pair => pair.Key, static pair => pair.Value)
        };
    }

    private static SchedulingProcessFact ProjectProcess(
        SchedulingProcessFact process,
        SchedulingProcessMetricMask currentMask)
    {
        var validMask = process.ValidMetricMask & currentMask;
        var gpuMask = validMask & (
            SchedulingProcessMetricMask.GpuUsage
            | SchedulingProcessMetricMask.GpuDedicatedMemory);
        return process with
        {
            ValidMetricMask = validMask,
            CpuUsagePercent = validMask.HasFlag(
                SchedulingProcessMetricMask.CpuUsage)
                    ? process.CpuUsagePercent
                    : 0,
            MemoryUsagePercent = validMask.HasFlag(
                SchedulingProcessMetricMask.MemoryUsage)
                    ? process.MemoryUsagePercent
                    : 0,
            RuntimeState = validMask.HasFlag(
                SchedulingProcessMetricMask.RuntimeState)
                    ? process.RuntimeState
                    : HostManagerRuntimeStates.BackgroundProcess,
            ForegroundFocused = validMask.HasFlag(
                SchedulingProcessMetricMask.RuntimeState)
                && process.ForegroundFocused,
            HasVisibleWindow = validMask.HasFlag(
                SchedulingProcessMetricMask.RuntimeState)
                && process.HasVisibleWindow,
            HasBackgroundWindow = validMask.HasFlag(
                SchedulingProcessMetricMask.RuntimeState)
                && process.HasBackgroundWindow,
            HasHiddenWindow = validMask.HasFlag(
                SchedulingProcessMetricMask.RuntimeState)
                && process.HasHiddenWindow,
            Gpus = process.Gpus
                .Select(gpu => gpu with
                {
                    ValidMetricMask = gpu.ValidMetricMask & gpuMask,
                    UsagePercent = gpuMask.HasFlag(
                        SchedulingProcessMetricMask.GpuUsage)
                            ? gpu.UsagePercent
                            : 0,
                    DedicatedMemoryUsedPercent = gpuMask.HasFlag(
                        SchedulingProcessMetricMask.GpuDedicatedMemory)
                            ? gpu.DedicatedMemoryUsedPercent
                            : 0
                })
                .Where(static gpu => gpu.ValidMetricMask !=
                    SchedulingProcessMetricMask.None)
                .ToArray()
        };
    }
}
