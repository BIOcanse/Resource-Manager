using System.Text.Json;
using ResourceManager.App.Application.Control;
using ResourceManager.App.Domain.Control;

namespace ResourceManager.App.Infrastructure.Control.Writers;

/// <summary>
/// 核显的电压（Curve Optimizer）。
///
/// 核显和独显走的**不是同一条路**，所以按 <see cref="ControlObject.GpuAttachment"/> 分流：
/// 独显有自己的驱动接口（NVAPI / ADLX），核显跟着它所在的平台走 ——
/// AMD 的归 CPU 封装里的 SMU（和处理器同一条通道），
/// Intel 的归显卡驱动自带的控制库（IGCL，随驱动一起装，Alder Lake-P 及以后）。
/// </summary>
public sealed class IntegratedGpuControlWriter(
    HardwareBridgeClient bridge,
    ILogger<IntegratedGpuControlWriter>? logger = null) : IControlWriter
{
    internal const string CurveOptimizerCapabilityId = "gpu.curve-optimizer";

    private const string BridgeMissing = "需要先安装硬件写入辅助进程。";
    private const string BridgeSilent = "硬件写入辅助进程没有应答。";
    private const string NotSupported = "这颗处理器的核显上没有这一项。";
    private const string NotEffective = "写下去了，但回读的值没有变。";
    private const string IntelNotVerified = "Intel 核显的调节还没接上。";

    public ControlWriteAvailability Probe(ControlObject target, ControlCapability capability)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(capability);

        if (!IsMine(target, capability.Id))
        {
            return ControlWriteAvailability.NotMine;
        }
        if (string.Equals(target.Platform.Vendor, ControlVendors.Intel, StringComparison.Ordinal))
        {
            return ControlWriteAvailability.No(IntelNotVerified);
        }
        if (!bridge.IsInstalled)
        {
            return ControlWriteAvailability.No(BridgeMissing);
        }

        var described = Ask("describe");
        if (described is not { } description || !Flag(description, "available"))
        {
            return ControlWriteAvailability.No(Text(described, "reason") ?? BridgeSilent);
        }
        if (!Flag(description, "integratedGpuCurveOptimizer"))
        {
            return ControlWriteAvailability.No(NotSupported);
        }

        // 默认值取现在的读数：撤销要回到我们动手之前的样子。
        var current = Ask("read") is { } reading
            ? (int)(Number(reading, "integratedGpuCurveOptimizerCounts") ?? 0)
            : 0;
        return ControlWriteAvailability.Yes(
            capability.Range is { } declared
                ? declared with { DefaultValue = current }
                : new ControlNumberRange(-30, 10, 1, "档", current));
    }

    public async Task<ControlApplyOutcome> WriteAsync(
        ControlObject target,
        ControlCapability capability,
        ControlSetting setting,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(capability);
        ArgumentNullException.ThrowIfNull(setting);

        if (setting.Number is not { } value || !double.IsFinite(value))
        {
            return Failed(target, capability, "这一项要一个数值，收到的不是。");
        }
        var bounds = capability.Range;
        var counts = (int)(bounds is null
            ? value
            : Math.Clamp(value, bounds.Minimum, bounds.Maximum));

        var response = await bridge.SendAsync(
            "set-igpu-curve-optimizer",
            new Dictionary<string, object?> { ["counts"] = counts },
            cancellationToken).ConfigureAwait(false);
        if (response is not { } result || !Flag(result, "ok"))
        {
            return Failed(target, capability, BridgeSilent);
        }
        if (!Flag(result, "accepted"))
        {
            logger?.LogWarning("SMU 拒绝了核显电压写入：{Counts} 档。", counts);
            return Failed(target, capability, "SMU 拒绝了这次写入。");
        }
        if (Number(result, "integratedGpuCurveOptimizerCounts") is { } after
            && (int)after != counts)
        {
            return Failed(target, capability, NotEffective);
        }

        return new ControlApplyOutcome(
            target.Id,
            capability.Id,
            ControlApplyStatuses.Applied,
            null);
    }

    private JsonElement? Ask(string operation)
        => bridge.SendAsync(operation, null, CancellationToken.None).GetAwaiter().GetResult();

    private static bool Flag(JsonElement? element, string name)
        => element is { } value
            && value.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.True;

    private static double? Number(JsonElement? element, string name)
        => element is { } value
            && value.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.Number
                ? property.GetDouble()
                : null;

    private static string? Text(JsonElement? element, string name)
        => element is { } value
            && value.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;

    private static bool IsMine(ControlObject target, string capabilityId)
        => string.Equals(target.Kind, ControlObjectKinds.Gpu, StringComparison.Ordinal)
            && string.Equals(
                target.GpuAttachment,
                ControlGpuAttachments.Integrated,
                StringComparison.Ordinal)
            && string.Equals(capabilityId, CurveOptimizerCapabilityId, StringComparison.Ordinal)
            && target.Platform.Vendor is ControlVendors.Amd or ControlVendors.Intel;

    private static ControlApplyOutcome Failed(
        ControlObject target,
        ControlCapability capability,
        string reason)
        => new(target.Id, capability.Id, ControlApplyStatuses.Failed, reason);
}
