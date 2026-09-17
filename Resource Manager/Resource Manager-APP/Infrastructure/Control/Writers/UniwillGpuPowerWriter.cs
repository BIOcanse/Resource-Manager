using ResourceManager.App.Application.Control;
using ResourceManager.App.Domain.Control;

namespace ResourceManager.App.Infrastructure.Control.Writers;

/// <summary>
/// 笔记本独显的功率预算：cTGP 偏移和 Dynamic Boost 偏移，走 Uniwill 的 EC 通道。
///
/// **笔记本的 TGP 不是往显卡里写的。** 固件（EC）把功率预算报给 ACPI 的 NvPCF 表，
/// 显卡驱动读到之后才决定当前 TGP —— 所以 NVML 的 SetPowerManagementLimit 在这类机器上
/// 返回"不支持"是正确行为，用户那一侧真正的入口就是这两个偏移。
///
/// 本机实测三方闭合：
/// <code>
/// 基础 TGP 50 W（NVML 报的默认值）
///   + cTGP 偏移 50 W（EC 0x0744）  = 100 W  ← 官方控制台标称值
///   + DB 偏移   15 W（EC 0x0746）  = 115 W  ← NVML 报的最大值
/// </code>
/// </summary>
public sealed class UniwillGpuPowerWriter(
    ILogger<UniwillGpuPowerWriter>? logger = null) : IControlWriter
{
    internal const string CtgpOffsetCapabilityId = "gpu.ctgp-offset";
    internal const string DynamicBoostOffsetCapabilityId = "gpu.dynamic-boost-offset";

    private const string PlatformMissing = "这台机器没有 Uniwill 平台接口，调不了显卡功率预算。";
    private const string BudgetUnknown = "读不到这块卡的功率预算，暂时调不了。";
    private const string ReadBackMismatch = "写进去了但回读对不上，固件没有接受这个值。";
    private const string NotEffective = "这台机器的固件收得下这个值，但显卡的功率预算没有跟着变，先不开放。";

    private readonly UniwillEcBridge bridge = new();
    private readonly NvidiaNvmlControlBridge powerBridge = new();

    public ControlWriteAvailability Probe(ControlObject target, ControlCapability capability)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(capability);

        if (!IsMine(target, capability.Id))
        {
            return ControlWriteAvailability.NotMine;
        }
        if (!bridge.IsAvailable)
        {
            return ControlWriteAvailability.No(PlatformMissing);
        }
        if (ReadBudget(target) is not { } budget)
        {
            return ControlWriteAvailability.No(BudgetUnknown);
        }

        /*
         * 这两项现在一律报不可用，**这是刻意的**，不是还没做完。
         *
         * 本机（机型 ID 0x1A）实测：寄存器写得进去、回读也对
         * （cTGP 50→20、DB 15→5 都确实落到了 EC 里），但显卡那边的
         * Max Power Limit 一直是 115 W 不动 —— 固件收下了值，没有把新预算
         * 交给驱动。数值语义是对的（50 基础 + 50 cTGP + 15 DB = 115，
         * 和 NVML 报的最大值严丝合缝），所以寄存器没找错；缺的是让固件
         * 重新下发预算的那一步，而 TUXEDO 那份 GPL 驱动里没有这一步
         * （它认的机型 ID 列表也不含 0x1A）。
         *
         * 写得进寄存器不等于用户能得到效果。界面上出现一个"应用成功但什么
         * 都没变"的开关，比没有这个开关更糟，所以在找到并**验证**那一步之前
         * 不开放。上面那些读取和写入的代码留着 —— 它们经过实机验证，
         * 补上激活步骤之后把这里换成真正的可用性判断即可。
         */
        _ = budget;
        return ControlWriteAvailability.No(NotEffective);
    }

    public Task<ControlApplyOutcome> WriteAsync(
        ControlObject target,
        ControlCapability capability,
        ControlSetting setting,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(capability);
        ArgumentNullException.ThrowIfNull(setting);
        cancellationToken.ThrowIfCancellationRequested();

        if (setting.Number is not { } value || !double.IsFinite(value))
        {
            return Task.FromResult(Failed(target, capability, "这一项要一个数值，收到的不是。"));
        }
        if (ReadBudget(target) is not { } budget)
        {
            return Task.FromResult(Failed(target, capability, BudgetUnknown));
        }

        var watts = (byte)Math.Clamp(Math.Round(value), 0, Math.Round(budget.HeadroomWatts));
        var address = string.Equals(capability.Id, CtgpOffsetCapabilityId, StringComparison.Ordinal)
            ? UniwillEcRegisters.CtgpOffset
            : UniwillEcRegisters.DynamicBoostOffset;

        // 写之前先把对应的使能位打开，否则固件不会把这个偏移算进预算里。
        if (!EnsureEnabled(capability.Id))
        {
            return Task.FromResult(Failed(target, capability, ReadBackMismatch));
        }
        if (!bridge.Write(address, watts))
        {
            logger?.LogWarning(
                "写 Uniwill 功率预算失败：{Capability} = {Watts} W。",
                capability.Id,
                watts);
            return Task.FromResult(Failed(target, capability, ReadBackMismatch));
        }

        return Task.FromResult(new ControlApplyOutcome(
            target.Id,
            capability.Id,
            ControlApplyStatuses.Applied,
            null));
    }

    /// <summary>
    /// 把总开关和这一项的使能位打开。
    ///
    /// 使能位和偏移量是两个寄存器：只写偏移而不开使能，固件会照旧用它自己那一套，
    /// 用户会看到"应用成功但什么都没变"。
    /// </summary>
    private bool EnsureEnabled(string capabilityId)
    {
        if (bridge.Read(UniwillEcRegisters.CtgpDbEnable) is not { } current)
        {
            return false;
        }
        var wanted = (byte)(current
            | UniwillEcRegisters.CtgpDbEnableGeneralBit
            | (string.Equals(capabilityId, CtgpOffsetCapabilityId, StringComparison.Ordinal)
                ? UniwillEcRegisters.CtgpDbEnableCtgpBit
                : UniwillEcRegisters.CtgpDbEnableDynamicBoostBit));
        return current == wanted || bridge.Write(UniwillEcRegisters.CtgpDbEnable, wanted);
    }

    /// <summary>
    /// 这块卡现在的功率预算。基础 TGP 和硬件上限由显卡报，两个偏移由 EC 报 ——
    /// 各问各的属主，不互相推算。
    /// </summary>
    private GpuPowerBudget? ReadBudget(ControlObject target)
    {
        if (target.AdapterIndex is not { } adapterIndex
            || powerBridge.FindHandleByAdapterIndex(adapterIndex) is not { } device
            || powerBridge.ReadPowerLimit(device) is not { DefaultWatts: { } baseWatts } limits
            || bridge.Read(UniwillEcRegisters.CtgpOffset) is not { } ctgp
            || bridge.Read(UniwillEcRegisters.DynamicBoostOffset) is not { } dynamicBoost)
        {
            return null;
        }

        var headroom = limits.MaximumWatts - baseWatts;
        return headroom <= 0
            ? null
            : new GpuPowerBudget(baseWatts, headroom, ctgp, dynamicBoost);
    }

    private static bool IsMine(ControlObject target, string capabilityId)
        => string.Equals(target.Kind, ControlObjectKinds.Gpu, StringComparison.Ordinal)
            && string.Equals(
                target.GpuAttachment,
                ControlGpuAttachments.Discrete,
                StringComparison.Ordinal)
            && capabilityId is CtgpOffsetCapabilityId or DynamicBoostOffsetCapabilityId;

    private static ControlApplyOutcome Failed(
        ControlObject target,
        ControlCapability capability,
        string reason)
        => new(target.Id, capability.Id, ControlApplyStatuses.Failed, reason);

    private readonly record struct GpuPowerBudget(
        double BaseWatts,
        double HeadroomWatts,
        double CtgpOffsetWatts,
        double DynamicBoostOffsetWatts);
}
