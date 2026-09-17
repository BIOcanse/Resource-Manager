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
        double? attributionTotalValue = null,
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
        /*
         * 分项换算到总和那把尺子上的比例。
         *
         * 分母默认是**已归属的那部分之和**（rawTotalValue）—— 那意味着把总和全部
         * 摊给认得出的进程，等于宣称归属是完整的。
         *
         * 传了 <paramref name="attributionTotalValue"/> 就用它当分母：它是归属来源
         * 自己报的**整体**（含归不到进程头上的那部分）。这时分项之和只会等于
         * "总和 × 已归属份额"，剩下的那块仍然落进残差 —— 归属不完整这件事
         * 就不会被换算悄悄抹掉。
         */
        var scaleDenominator = attributionTotalValue is { } declared && declared > 0
            ? declared
            : rawTotalValue;
        var valueScale =
            scaleProcessValues && scaleDenominator > 0
                ? totalValue / scaleDenominator
                : 1;

        /*
         * **分项不得超过总和。**
         *
         * 这是"分解"这个词的意思，不是一条可选的策略。两侧口径一致、只是采样窗口
         * 错开时，分项之和会稍稍超过总和（CPU 上实测 5.40 对 4.87），
         * 于是界面上出现"某个软件比总和还大"，而残差那一步算出负数被静默丢掉。
         *
         * 超了就按比例收到总和，不足就照旧留给残差 —— **只收缩，不放大**：
         * 放大等于把没测到的那部分硬摊给认得出的进程，那是编数字。
         *
         * 只对百分比这类列生效。字节类（分项是进程私有字节、总和是系统已用字节）
         * 本来就不是同一口径，"部分大于整体"在那里另有解释，不该一起收缩。
         */
        if (!isBytes && !isBytesPerSecond && attributionTotalValue is null)
        {
            var attributed = rawTotalValue * valueScale;
            if (attributed > totalValue && attributed > 0)
            {
                valueScale *= totalValue / attributed;
            }
        }
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
        var categories = residualBreakdownProvider.CreateResidualSegments(new ResourceResidualBreakdownRequest(
            metricId,
            label,
            totalValue,
            capacityValue,
            SanitizePercent(systemPercent),
            knownProcessIds));
        return new ResourceSoftwareSegment(
            // 名字为空：前端按这个 id 里的度量名出「系统/驱动保留 · 某度量」。
            $"resource-residual:{metricId}",
            string.Empty,
            SoftwareKinds.WindowsSystem,
            SoftwareDisplayKinds.SystemResidual,
            totalValue,
            SanitizePercent(systemPercent),
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
