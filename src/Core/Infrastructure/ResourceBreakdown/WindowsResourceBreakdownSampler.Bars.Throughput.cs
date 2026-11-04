using ResourceManager.App.Application.ProcessAttribution;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Domain.RuntimeSpecialization;

namespace ResourceManager.App.Infrastructure.ResourceBreakdown;

public sealed partial class WindowsResourceBreakdownSampler
{
    private static ResourceBreakdownBar CreateDiskThroughputBar(
        string metricId,
        PhysicalDiskIoAttributionSnapshot diskAttribution,
        ProcessAttributionSnapshot processAttribution,
        CompiledBaseScorePlan baseScorePlan,
        IResourceResidualBreakdownProvider residualBreakdownProvider)
    {
        if (diskAttribution.ProviderState.ObservationStatus != ResourceManager.App.Domain.Metrics.SamplingObservationStatus.Current)
            return CreateUnavailableBar(metricId, "B/s", ResourceBreakdownScaleModes.Active,
                diskAttribution.ProviderState.ObservationStatus, diskAttribution.ProviderState.ObservationStatus) with
            { UnavailableReason = diskAttribution.ProviderState.State == PhysicalDiskIoProviderStates.Warming
                ? "disk-io-window-incomplete" : "disk-io-source-unavailable" };
        var valuesByProcess = CreateDiskProcessValues(metricId, diskAttribution, processAttribution);
        var total = valuesByProcess
            .Where(static item => item.Value > 0)
            .Sum(static item => item.Value);
        return CreateBar(
            metricId,
            "B/s",
            ResourceBreakdownScaleModes.Active,
            Math.Max(1, total),
            total,
            valuesByProcess,
            processAttribution,
            baseScorePlan,
            residualBreakdownProvider,
            isBytes: true,
            scaleProcessValues: false,
            isBytesPerSecond: true,
            attributionStatus: diskAttribution.ProviderState.ObservationStatus);
    }

    private static ResourceBreakdownBar CreateNetworkThroughputBar(
        string metricId,
        NetworkAttributionSnapshot networkAttribution,
        ProcessAttributionSnapshot processAttribution,
        CompiledBaseScorePlan baseScorePlan,
        IResourceResidualBreakdownProvider residualBreakdownProvider)
    {
        if (networkAttribution.ProviderState.ObservationStatus != ResourceManager.App.Domain.Metrics.SamplingObservationStatus.Current)
            return CreateUnavailableBar(metricId, "B/s", ResourceBreakdownScaleModes.Active,
                networkAttribution.ProviderState.ObservationStatus, networkAttribution.ProviderState.ObservationStatus);
        var valuesByProcess = CreateNetworkProcessValues(metricId, networkAttribution, processAttribution);
        var total = valuesByProcess
            .Where(static item => item.Value > 0)
            .Sum(static item => item.Value);
        return CreateBar(
            metricId,
            "B/s",
            ResourceBreakdownScaleModes.Active,
            Math.Max(1, total),
            total,
            valuesByProcess,
            processAttribution,
            baseScorePlan,
            residualBreakdownProvider,
            isBytes: true,
            scaleProcessValues: false,
            isBytesPerSecond: true,
            attributionStatus: networkAttribution.ProviderState.ObservationStatus);
    }

    private static IReadOnlyDictionary<int, double> CreateDiskProcessValues(
        string metricId,
        PhysicalDiskIoAttributionSnapshot diskAttribution,
        ProcessAttributionSnapshot processAttribution)
    {
        return diskAttribution.Processes.Where(item => processAttribution.ProcessById.TryGetValue(item.Key, out var process)
                && process.StartKey is long start && start > 0 && item.Value.ProcessStartKey == (ulong)start)
            .ToDictionary(
            static item => item.Key,
            item => DiskProcessValue(metricId, item.Value));
    }

    private static double DiskProcessValue(string metricId, PhysicalDiskProcessAttribution process)
    {
        if (metricId.Equals(ResourceBreakdownMetricIds.DiskRead, StringComparison.OrdinalIgnoreCase))
        {
            return process.ReadBytesPerSecond;
        }

        if (metricId.Equals(ResourceBreakdownMetricIds.DiskWrite, StringComparison.OrdinalIgnoreCase))
        {
            return process.WriteBytesPerSecond;
        }

        return process.TotalBytesPerSecond;
    }

    private static IReadOnlyDictionary<int, double> CreateNetworkProcessValues(
        string metricId,
        NetworkAttributionSnapshot networkAttribution,
        ProcessAttributionSnapshot processAttribution)
    {
        var processes = IsRawNetworkMetric(metricId)
            ? networkAttribution.RawProcessValues
            : networkAttribution.EffectiveProcesses;
        return processes.Where(item => item.Value.ProcessStartKey is null || (processAttribution.ProcessById.TryGetValue(item.Key, out var process)
                && process.StartKey is long start && start > 0 && item.Value.ProcessStartKey == (ulong)start))
            .ToDictionary(
            static item => item.Key,
            item => NetworkProcessValue(metricId, item.Value));
    }

    private static double NetworkProcessValue(string metricId, NetworkProcessAttribution process)
    {
        if (metricId.Equals(ResourceBreakdownMetricIds.NetworkReceive, StringComparison.OrdinalIgnoreCase)
            || metricId.Equals(ResourceBreakdownMetricIds.NetworkRawReceive, StringComparison.OrdinalIgnoreCase))
        {
            return process.ReceiveBytesPerSecond;
        }

        if (metricId.Equals(ResourceBreakdownMetricIds.NetworkSend, StringComparison.OrdinalIgnoreCase)
            || metricId.Equals(ResourceBreakdownMetricIds.NetworkRawSend, StringComparison.OrdinalIgnoreCase))
        {
            return process.SendBytesPerSecond;
        }

        return process.TotalBytesPerSecond;
    }
}
