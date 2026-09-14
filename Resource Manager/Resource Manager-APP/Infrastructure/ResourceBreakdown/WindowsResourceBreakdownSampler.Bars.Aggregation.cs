using ResourceManager.App.Application.ProcessAttribution;
using ResourceManager.App.Application.Optimization.Scoring;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Domain.ProcessAttribution;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Domain.Software;

namespace ResourceManager.App.Infrastructure.ResourceBreakdown;

public sealed partial class WindowsResourceBreakdownSampler
{
    private static ResourceBreakdownBar CreateBar(
        string metricId,
        string label,
        string unit,
        string scaleMode,
        double capacityValue,
        double authoritativeTotalValue,
        IEnumerable<KeyValuePair<int, double>> valuesByProcess,
        ProcessAttributionSnapshot processAttribution,
        CompiledBaseScorePlan baseScorePlan,
        IResourceResidualBreakdownProvider residualBreakdownProvider,
        bool isBytes,
        bool scaleProcessValues = false,
        bool isBytesPerSecond = false,
        SamplingObservationStatus attributionStatus = SamplingObservationStatus.Current)
    {
        var positiveProcessValues =
            new List<KeyValuePair<int, double>>();
        var knownProcessIds = new HashSet<int>();
        var rawTotalValue = 0D;
        foreach (var item in valuesByProcess)
        {
            if (item.Value <= 0)
            {
                continue;
            }

            positiveProcessValues.Add(item);
            knownProcessIds.Add(item.Key);
            rawTotalValue += item.Value;
        }

        var totalValue = NormalizeTotalValue(
            authoritativeTotalValue,
            capacityValue);
        var valueScale =
            scaleProcessValues && rawTotalValue > 0
                ? totalValue / rawTotalValue
                : 1;
        var groupIndexBySoftwareId =
            new Dictionary<string, int>(
                StringComparer.OrdinalIgnoreCase);
        var groupAccumulators =
            new List<SoftwareGroupAccumulator>();
        foreach (var item in positiveProcessValues)
        {
            var attributed = CreateAttributedProcess(
                item.Key,
                item.Value * valueScale,
                processAttribution.ProcessById,
                processAttribution.AttributionByProcessId,
                baseScorePlan,
                capacityValue,
                isBytes,
                isBytesPerSecond);
            if (!attributed.HasValue
                || attributed.Value.Value <= 0)
            {
                continue;
            }

            var process = attributed.Value;
            if (!groupIndexBySoftwareId.TryGetValue(
                    process.Software.Id,
                    out var groupIndex))
            {
                groupIndex = groupAccumulators.Count;
                groupIndexBySoftwareId.Add(
                    process.Software.Id,
                    groupIndex);
                groupAccumulators.Add(
                    new SoftwareGroupAccumulator(
                        process.Software));
            }

            groupAccumulators[groupIndex].Add(process);
        }

        var softwareGroups =
            new List<ResourceSoftwareSegment>(
                groupAccumulators.Count + 1);
        var groupedTotalValue = 0D;
        foreach (var group in groupAccumulators)
        {
            var segment = CreateSoftwareSegment(
                group,
                capacityValue,
                isBytes,
                isBytesPerSecond);
            if (segment.Value <= 0)
            {
                continue;
            }

            softwareGroups.Add(segment);
            groupedTotalValue += segment.Value;
        }

        var residualValue = totalValue - groupedTotalValue;
        if (residualValue > Math.Max(1, totalValue * 0.001))
        {
            softwareGroups.Add(CreateResidualSoftwareSegment(metricId, label, residualValue, capacityValue, isBytes, isBytesPerSecond, knownProcessIds, residualBreakdownProvider));
        }
        else if (softwareGroups.Count == 0 && totalValue > 0)
        {
            softwareGroups.Add(CreateResidualSoftwareSegment(metricId, label, totalValue, capacityValue, isBytes, isBytesPerSecond, knownProcessIds, residualBreakdownProvider));
        }

        var orderedSoftwareGroups = softwareGroups
            .OrderByDescending(static segment => segment.Value)
            .ToArray();
        var totalSystemPercent = capacityValue > 0 ? totalValue * 100 / capacityValue : 0;
        return new ResourceBreakdownBar(
            metricId,
            label,
            unit,
            scaleMode,
            totalValue,
            capacityValue,
            SanitizePercent(totalSystemPercent),
            FormatValue(totalValue, capacityValue, isBytes, isBytesPerSecond),
            orderedSoftwareGroups,
            SamplingObservationStatus.Current,
            attributionStatus);
    }

    private static AttributedProcess? CreateAttributedProcess(
        int processId,
        double value,
        IReadOnlyDictionary<int, ProcessResourceSample> processById,
        IReadOnlyDictionary<int, RuntimeSoftwareAttribution> attributionByProcessId,
        CompiledBaseScorePlan baseScorePlan,
        double capacityValue,
        bool isBytes,
        bool isBytesPerSecond)
    {
        if (value <= 0 || !processById.TryGetValue(processId, out var process))
        {
            return null;
        }

        var software = attributionByProcessId.GetValueOrDefault(process.ProcessId)
            ?? RuntimeSoftwareAttribution.Unattributed;
        var processKey = OptimizationBaseScorePolicyResolver.CreateProcessKey(
            process.Name,
            process.ExecutablePath);
        var baseScore = baseScorePlan.ResolveBaseScore(
            software.Id,
            software.Kind,
            processKey);
        var systemPercent = capacityValue > 0 ? value * 100 / capacityValue : 0;
        return new AttributedProcess(
            software,
            process,
            value,
            SanitizePercent(systemPercent),
            FormatValue(value, capacityValue, isBytes, isBytesPerSecond),
            baseScore);
    }

    private static ResourceSoftwareSegment CreateSoftwareSegment(
        SoftwareGroupAccumulator group,
        double capacityValue,
        bool isBytes,
        bool isBytesPerSecond)
    {
        var totalValue = group.TotalValue;
        var systemPercent = capacityValue > 0 ? totalValue * 100 / capacityValue : 0;
        var processSegments = group.Processes
            .OrderByDescending(static process => process.Value)
            .Select(process => new ResourceProcessSegment(
                process.Process.ProcessId,
                process.Process.Name,
                process.Process.ExecutablePath,
                process.Value,
                process.SystemPercent,
                totalValue > 0 ? process.Value * 100 / totalValue : 0,
                process.DisplayValue,
                process.Process.UserName,
                process.Process.Architecture,
                ResourceProcessAttributionKinds.Process,
                process.BaseScore)
            {
                ProcessStartKey = process.Process.StartKey
            })
            .ToArray();

        return new ResourceSoftwareSegment(
            group.Software.Id,
            group.Software.Name,
            group.Software.Kind,
            group.Software.DisplayKind,
            totalValue,
            SanitizePercent(systemPercent),
            FormatValue(totalValue, capacityValue, isBytes, isBytesPerSecond),
            group.Processes.Count,
            processSegments,
            group.BaseScore);
    }

    private sealed class SoftwareGroupAccumulator(
        RuntimeSoftwareAttribution software)
    {
        internal RuntimeSoftwareAttribution Software { get; } =
            software;

        internal List<AttributedProcess> Processes { get; } = [];

        internal double TotalValue { get; private set; }

        internal double BaseScore { get; private set; }

        internal void Add(AttributedProcess process)
        {
            Processes.Add(process);
            TotalValue += process.Value;
            BaseScore = Math.Max(BaseScore, process.BaseScore);
        }
    }

    private static ResourceSoftwareSegment CreateResidualSoftwareSegment(
        string metricId,
        string label,
        double totalValue,
        double capacityValue,
        bool isBytes,
        bool isBytesPerSecond,
        IReadOnlySet<int> knownProcessIds,
        IResourceResidualBreakdownProvider residualBreakdownProvider)
    {
        var systemPercent = capacityValue > 0 ? totalValue * 100 / capacityValue : 0;
        var name = $"系统/驱动保留 · {NormalizeResidualMetricLabel(metricId, label)}";
        var displayValue = FormatValue(totalValue, capacityValue, isBytes, isBytesPerSecond);
        var categories = residualBreakdownProvider.CreateResidualSegments(new ResourceResidualBreakdownRequest(
            metricId,
            label,
            totalValue,
            capacityValue,
            SanitizePercent(systemPercent),
            displayValue,
            knownProcessIds));
        return new ResourceSoftwareSegment(
            $"resource-residual:{metricId}",
            name,
            SoftwareKinds.WindowsSystem,
            "系统/驱动保留",
            totalValue,
            SanitizePercent(systemPercent),
            displayValue,
            0,
            categories,
            0);
    }

    private static string NormalizeResidualMetricLabel(string metricId, string label)
    {
        if (!string.IsNullOrWhiteSpace(label))
        {
            return label.Trim();
        }

        return metricId;
    }
}
