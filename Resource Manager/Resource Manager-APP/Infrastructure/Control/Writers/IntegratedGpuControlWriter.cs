using System.Text.Json;
using ResourceManager.App.Application.Control;
using ResourceManager.App.Domain.Control;

namespace ResourceManager.App.Infrastructure.Control.Writers;

/// <summary>
/// 核显的调节。
///
/// 核显和独显走的**不是同一条路**，所以按 <see cref="ControlObject.GpuAttachment"/> 分流；
/// 而两家核显能调的也**不是同一件事**，所以再按厂商分：
///
/// <list type="bullet">
/// <item>AMD 的核显归 CPU 封装里的 SMU 管，能调 Curve Optimizer 的**档位**，
///   和处理器同一条通道（辅助进程 → ZenStates-Core）。</item>
/// <item>Intel 的核显走显卡驱动自带的 IGCL，能调频率**偏移（MHz）**。
///   控制库随驱动一起装，Alder Lake-P 及以后才有。</item>
/// </list>
///
/// 硬凑成同一项只会让其中一边的单位和语义都是错的。
/// </summary>
public sealed class IntegratedGpuControlWriter(
    HardwareBridgeClient bridge,
    IControlOverclockConsent consent,
    ILogger<IntegratedGpuControlWriter>? logger = null) : IControlWriter
{
    internal const string CurveOptimizerCapabilityId = "gpu.curve-optimizer";
    internal const string CoreClockOffsetCapabilityId = "gpu.core-clock-offset";

    private readonly IntelGraphicsControlBridge intel = new();

    private const string BridgeMissing = "需要先安装硬件写入辅助进程。";
    private const string BridgeSilent = "硬件写入辅助进程没有应答。";
    private const string NotSupported = "这颗处理器的核显上没有这一项。";
    private const string NotEffective = "写下去了，但回读的值没有变。";
    private const string IntelDriverMissing =
        "读不到 Intel 显卡驱动自带的控制库，装上/更新显卡驱动之后才能调。";

    /// <summary>
    /// 这不是我们发明的流程，是厂商的硬性要求：IGCL 在用户接受免责声明之前
    /// 拒绝所有超频接口，原文是"用户据此接受部件寿命缩短"。
    /// </summary>
    private const string OverclockNotAccepted = "需要先同意超频免责声明。";

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
            return ProbeIntel();
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
                : new ControlNumberRange(-30, 10, 1, ControlUnits.Step, current));
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
        var wanted = bounds is null
            ? value
            : Math.Clamp(value, bounds.Minimum, bounds.Maximum);

        // Intel 那条的单位是 MHz，不是档位 —— 别把它当整数档位取整。
        if (string.Equals(target.Platform.Vendor, ControlVendors.Intel, StringComparison.Ordinal))
        {
            return await WriteIntelAsync(target, capability, wanted).ConfigureAwait(false);
        }

        var counts = (int)wanted;
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

    /// <summary>这一项现在实际是多少。</summary>
    public async Task<ControlActualValue?> ReadAsync(
        ControlObject target,
        ControlCapability capability,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(capability);

        // Intel 那条走 IGCL，和辅助进程没关系。
        if (string.Equals(target.Platform.Vendor, ControlVendors.Intel, StringComparison.Ordinal))
        {
            return intel.FirstDevice() is { } device
                && intel.ReadFrequencyOffsetMhz(device) is { } offset
                    ? new ControlActualValue(
                        target.Id,
                        capability.Id,
                        Number: offset,
                        Unit: ControlUnits.Megahertz)
                    : new ControlActualValue(
                        target.Id,
                        capability.Id,
                        UnreadableReason: IntelDriverMissing);
        }

        var reading = await bridge.SendAsync("read", null, cancellationToken)
            .ConfigureAwait(false);
        if (reading is not { } result || !Flag(result, "ok"))
        {
            return new ControlActualValue(
                target.Id,
                capability.Id,
                UnreadableReason: BridgeSilent);
        }
        return Number(result, "integratedGpuCurveOptimizerCounts") is { } value
            ? new ControlActualValue(
                target.Id,
                capability.Id,
                Number: Math.Round(value),
                Unit: ControlUnits.Step)
            : new ControlActualValue(
                target.Id,
                capability.Id,
                UnreadableReason: "这颗处理器的核显没有报出这一项的当前值。");
    }

    /// <summary>
    /// Intel 核显：先看控制库在不在，再看用户同意没同意。
    ///
    /// 没同意就如实报出来 —— 这既是厂商要求，也是用户应该看见的东西：
    /// 超频会缩短部件寿命，那不该被一个默认勾选悄悄带过。
    /// </summary>
    private ControlWriteAvailability ProbeIntel()
    {
        if (!intel.IsAvailable)
        {
            return ControlWriteAvailability.No(IntelDriverMissing);
        }
        if (!consent.IsAcceptedAsync(CancellationToken.None).GetAwaiter().GetResult())
        {
            return ControlWriteAvailability.No(OverclockNotAccepted);
        }
        if (intel.FirstDevice() is not { } device)
        {
            return ControlWriteAvailability.No(IntelDriverMissing);
        }

        // 同意过才去把 waiver 交给驱动。交不上说明这块卡不让超，如实说。
        if (!intel.TryAcceptOverclockWaiver(device))
        {
            return ControlWriteAvailability.No("这块核显不接受超频设置。");
        }
        return ControlWriteAvailability.Yes();
    }

    private async Task<ControlApplyOutcome> WriteIntelAsync(
        ControlObject target,
        ControlCapability capability,
        double offsetMhz)
    {
        if (intel.FirstDevice() is not { } device)
        {
            return Failed(target, capability, IntelDriverMissing);
        }
        if (!await consent.IsAcceptedAsync(CancellationToken.None).ConfigureAwait(false))
        {
            return Failed(target, capability, OverclockNotAccepted);
        }
        if (!intel.TryAcceptOverclockWaiver(device))
        {
            return Failed(target, capability, "这块核显不接受超频设置。");
        }

        var result = intel.WriteFrequencyOffsetMhz(device, offsetMhz);
        if (result != 0)
        {
            logger?.LogWarning(
                "Intel 核显拒绝了频率偏移写入：{Offset} MHz，结果码 {Result}。",
                offsetMhz,
                result);
            return Failed(target, capability, "驱动拒绝了这次写入。");
        }

        // 回读核对。驱动会按硬件余量夹值，夹过之后就不是用户要的那个数了。
        var readBack = intel.ReadFrequencyOffsetMhz(device);
        return readBack is { } applied && Math.Abs(applied - offsetMhz) > 1
            ? Failed(target, capability, NotEffective)
            : new ControlApplyOutcome(
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
            // 两家的核显能调的不是同一件事：AMD 是 Curve Optimizer 档位（走 SMU），
            // Intel 是频率偏移 MHz（走 IGCL）。所以各认各的那一项。
            && (string.Equals(target.Platform.Vendor, ControlVendors.Amd, StringComparison.Ordinal)
                    && string.Equals(
                        capabilityId,
                        CurveOptimizerCapabilityId,
                        StringComparison.Ordinal)
                || string.Equals(
                        target.Platform.Vendor,
                        ControlVendors.Intel,
                        StringComparison.Ordinal)
                    && string.Equals(
                        capabilityId,
                        CoreClockOffsetCapabilityId,
                        StringComparison.Ordinal));

    private static ControlApplyOutcome Failed(
        ControlObject target,
        ControlCapability capability,
        string reason)
        => new(target.Id, capability.Id, ControlApplyStatuses.Failed, reason);
}
