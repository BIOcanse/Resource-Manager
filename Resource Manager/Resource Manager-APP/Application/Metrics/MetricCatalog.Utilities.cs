using ResourceManager.App.Domain.Metrics;

using ResourceManager.App.Domain.Messages;

namespace ResourceManager.App.Application.Metrics;

public static partial class MetricCatalog
{
    /// <summary>
    /// 往目录里加一项。
    ///
    /// **读不到的照样加，标成不可选并说明原因。** 目录列的是这台机器上这个设备
    /// **该有哪些项**，不是"此刻读得到哪些项"。
    ///
    /// 先前这里读不到就整条丢掉（函数原来就叫 AddIfAvailable），于是核显的显存、
    /// 显存频率、风扇那几项在目录里凭空消失 —— 用户看到的不是"这台机器不支持"，
    /// 而是根本没有这回事，分不清是硬件没有还是软件没做。
    /// AI SoC 上更明显：它的 GPU 和处理器共用内存，本来就没有独立显存，
    /// 那一项就该写着"不支持读取"。
    /// </summary>
    private static void AddMetric(
        List<MetricDefinition> definitions,
        IReadOnlyDictionary<string, MetricValue>? items,
        string id,
        string label,
        string group,
        string unit,
        string preferredSlot,
        string detail,
        string? requiredComponentId = null,
        string? requiredComponentName = null,
        string? scopeKind = null,
        string? scopeKey = null)
    {
        MetricValue? item = null;
        items?.TryGetValue(id, out item);

        var itemDetail = item?.Detail ?? detail;
        var dependencyId = requiredComponentId;
        var selectable = items is null || IsMetricSelectable(item);
        definitions.Add(new MetricDefinition(
            id,
            label,
            group,
            string.IsNullOrWhiteSpace(item?.Unit) ? unit : item.Unit,
            preferredSlot,
            itemDetail,
            dependencyId,
            requiredComponentName ?? ComponentName(dependencyId),
            selectable,
            selectable ? null : DisabledReason(item, requiredComponentName ?? ComponentName(dependencyId)),
            scopeKind,
            scopeKey));
    }

    private static void AddSnapshotMetric(
        List<MetricDefinition> definitions,
        IReadOnlyDictionary<string, MetricValue> items,
        string id,
        string label,
        string group,
        string unit,
        string preferredSlot,
        string? detail = null,
        string? requiredComponentId = null,
        string? requiredComponentName = null)
    {
        items.TryGetValue(id, out var item);
        var selectable = IsMetricSelectable(item);
        var componentName = requiredComponentName ?? ComponentName(requiredComponentId);
        definitions.Add(new MetricDefinition(
            id,
            label,
            group,
            unit,
            preferredSlot,
            item?.Detail ?? detail,
            requiredComponentId,
            componentName,
            selectable,
            selectable ? null : DisabledReason(item, componentName)));
    }

    private static bool IsMetricSelectable(MetricValue? item)
    {
        if (item is null)
        {
            return false;
        }

        if (item.NumericValue is not null || item.Percent is not null)
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(item.DisplayValue)
            && !item.DisplayValue.Equals("-", StringComparison.Ordinal)
            && !item.DisplayValue.Equals("N/A", StringComparison.OrdinalIgnoreCase)
            && !item.DisplayValue.Equals("--", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 这一项为什么不能选。三种情况各自确定：
    ///
    /// <list type="bullet">
    /// <item><b>需要 X</b> —— 有个组件能提供它，只是还没装。这条最要紧：用户装上就有了。</item>
    /// <item><b>读不到</b> —— 有读数这条路，只是这次没读出来。</item>
    /// <item><b>不支持读取</b> —— 没有任何东西能提供它。核显没有独立显存、
    ///   AI SoC 和处理器共用内存，那就是真没有。</item>
    /// </list>
    ///
    /// **先看有没有组件能救。** 先前这里第一个判的是"有没有读数"，
    /// 于是缺驱动的独显把"需要 NVIDIA 显卡监控支持"报成了"不支持读取" ——
    /// 一个装个组件就能解决的事，被说成硬件不支持。
    /// </summary>
    private static BackendMessage DisabledReason(MetricValue? item, string? requiredComponentName)
    {
        if (!string.IsNullOrWhiteSpace(requiredComponentName))
        {
            return BackendMessage.Create(
                BackendMessageDomains.Metric,
                BackendMessageCodes.Metric.NeedsComponent,
                requiredComponentName);
        }

        return BackendMessage.Create(
            BackendMessageDomains.Metric,
            item is null
                ? BackendMessageCodes.Metric.NotExposed
                : BackendMessageCodes.Metric.NoValidReading);
    }

    private static string? RequiredComponentWhenUnavailable(
        IReadOnlyDictionary<string, MetricValue> items,
        string metricId,
        string componentId)
    {
        return items.TryGetValue(metricId, out var item) && IsMetricSelectable(item)
            ? null
            : componentId;
    }

    private static string? RequiredFanComponentWhenUnavailable(
        IReadOnlyDictionary<string, MetricValue> items,
        string metricId)
    {
        if (items.TryGetValue(metricId, out var item) && IsMetricSelectable(item))
        {
            return null;
        }

        if (item?.Detail?.Contains("Mechrevo", StringComparison.OrdinalIgnoreCase) == true
            || item?.Detail?.Contains("UWACPI", StringComparison.OrdinalIgnoreCase) == true
            || item?.Detail?.Contains("Notebook OEM", StringComparison.OrdinalIgnoreCase) == true)
        {
            return "notebook-oem-fan-provider";
        }

        return "librehardwaremonitor-provider";
    }

    private static string? ComponentName(string? componentId)
    {
        return componentId switch
        {
            "amd-smu-pawnio-provider" => "AMD SMU / PawnIO Provider",
            "amd-ryzen-master-monitoring-sdk" => "AMD Ryzen Master Monitoring SDK",
            "intel-pcm-provider" => "Intel PCM / MSR Provider",
            "nvidia-nvml-provider" => "NVIDIA NVML Provider",
            "nvidia-nvapi-provider" => "NVIDIA NVAPI Provider",
            "amd-adlx-provider" => "AMD ADLX / ADL Provider",
            "librehardwaremonitor-provider" => "LibreHardwareMonitor Provider",
            "notebook-fancontrol-provider" => "Notebook FanControl Provider",
            "notebook-oem-fan-provider" => "Notebook OEM Fan Provider",
            _ => null
        };
    }
}
