using ResourceManager.App.Application.Control;
using ResourceManager.App.Domain.Control;

namespace ResourceManager.App.Infrastructure.Control.Writers;

/// <summary>OEM GPU offsets. Readback is a register observation, not measured power or a firmware limit.</summary>
public sealed class UniwillGpuPowerWriter : IControlWriter
{
    private readonly ILogger<UniwillGpuPowerWriter>? logger;
    private readonly IUniwillGpuPowerHardware bridge;

    public UniwillGpuPowerWriter(ILogger<UniwillGpuPowerWriter>? logger = null)
        : this(new UniwillGpuPowerHardware(), logger) { }

    internal UniwillGpuPowerWriter(IUniwillGpuPowerHardware hardware, ILogger<UniwillGpuPowerWriter>? logger = null)
    {
        bridge = hardware ?? throw new ArgumentNullException(nameof(hardware));
        this.logger = logger;
    }
    internal const string CtgpOffsetCapabilityId = "gpu.ctgp-offset";
    internal const string DynamicBoostEnabledCapabilityId = "gpu.dynamic-boost-enabled";
    internal const string DynamicBoostOffsetCapabilityId = "gpu.dynamic-boost-offset";

    private const string PlatformMissing = "没有 Uniwill 平台接口。";
    private const string BudgetUnknown = "读不到功率预算。";
    private const string WriteRefused = "写入后无法确认寄存器值。";
    private const string EnableRefused = "使能位未确认，未继续写入额度。";
    private const string FirmwareManaged = "功率预算现在由固件自管，EC 里这几个值不是当前生效值。";

    private const byte TakeoverBits =
        UniwillEcRegisters.CtgpDbEnableGeneralBit | UniwillEcRegisters.CtgpDbEnableCtgpBit;

    public string? ChannelOf(ControlObject target, string capabilityId)
        => IsMine(target, capabilityId) ? ControlChannels.OemEmbeddedController : null;

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
        if (string.Equals(capability.Id, DynamicBoostEnabledCapabilityId, StringComparison.Ordinal))
        {
            return bridge.Read(UniwillEcRegisters.CtgpDbEnable) is not null
                ? ControlWriteAvailability.Yes()
                : ControlWriteAvailability.No(EnableRefused);
        }
        if (ReadBudget(target) is not { } budget)
        {
            return ControlWriteAvailability.No(BudgetUnknown);
        }

        return ControlWriteAvailability.Yes(new ControlNumberRange(
            0,
            RequestMaximum(budget.HeadroomWatts),
            1,
            ControlUnits.Watt,
            0));
    }

    public Task<ControlActualValue?> ReadAsync(
        ControlObject target,
        ControlCapability capability,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(capability);
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsMine(target, capability.Id) || !bridge.IsAvailable)
        {
            return Task.FromResult<ControlActualValue?>(null);
        }
        if (bridge.Read(UniwillEcRegisters.CtgpDbEnable) is not { } enable)
        {
            return Task.FromResult<ControlActualValue?>(null);
        }
        if ((enable & TakeoverBits) != TakeoverBits)
        {
            return Task.FromResult<ControlActualValue?>(new ControlActualValue(
                target.Id,
                capability.Id,
                UnreadableReason: FirmwareManaged));
        }

        if (string.Equals(capability.Id, DynamicBoostEnabledCapabilityId, StringComparison.Ordinal))
        {
            return Task.FromResult<ControlActualValue?>(new ControlActualValue(
                target.Id,
                capability.Id,
                Toggle: (enable & UniwillEcRegisters.CtgpDbEnableDynamicBoostBit) != 0));
        }

        return Task.FromResult(bridge.Read(AddressOf(capability.Id)) is { } watts
            ? new ControlActualValue(target.Id, capability.Id, watts, Unit: ControlUnits.Watt)
            : null);
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

        if (!IsMine(target, capability.Id) || setting.CapabilityId != capability.Id)
        {
            return Task.FromResult(Failed(target, capability, "控制项不匹配。"));
        }
        if (!bridge.IsAvailable)
        {
            return Task.FromResult(Failed(target, capability, PlatformMissing));
        }

        return Task.FromResult(
            string.Equals(capability.Id, DynamicBoostEnabledCapabilityId, StringComparison.Ordinal)
                ? WriteDynamicBoostEnabled(target, capability, setting)
                : WriteOffset(target, capability, setting));
    }

    public Task<bool> RestoreAsync(
        ControlObject target,
        ControlCapability capability,
        IReadOnlySet<string> stillConfigured,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsMine(target, capability.Id) || !bridge.IsAvailable
            || bridge.Read(UniwillEcRegisters.CtgpDbEnable) is not { } current)
        {
            return Task.FromResult(false);
        }
        var retained = stillConfigured.Any(id => id is CtgpOffsetCapabilityId
            or DynamicBoostEnabledCapabilityId or DynamicBoostOffsetCapabilityId);
        if (!retained)
        {
            var automatic = (byte)(current & ~TakeoverBits);
            return Task.FromResult(current == automatic
                || bridge.WriteReadingBack(UniwillEcRegisters.CtgpDbEnable, automatic) == automatic);
        }
        if (capability.Id == DynamicBoostEnabledCapabilityId)
        {
            var disabled = (byte)(current & ~UniwillEcRegisters.CtgpDbEnableDynamicBoostBit);
            return Task.FromResult(current == disabled
                || bridge.WriteReadingBack(UniwillEcRegisters.CtgpDbEnable, disabled) == disabled);
        }
        return Task.FromResult(bridge.WriteReadingBack(AddressOf(capability.Id), 0) == 0);
    }

    private ControlApplyOutcome WriteDynamicBoostEnabled(
        ControlObject target,
        ControlCapability capability,
        ControlSetting setting)
    {
        if (setting.Toggle is not { } wanted || setting.Number is not null)
        {
            return Failed(target, capability, "这一项要一个开关值，收到的不是。");
        }
        if (bridge.Read(UniwillEcRegisters.CtgpDbEnable) is not { } current)
        {
            return Failed(target, capability, BudgetUnknown);
        }

        var next = (byte)(wanted
            ? current | TakeoverBits | UniwillEcRegisters.CtgpDbEnableDynamicBoostBit
            : (current | TakeoverBits) & ~UniwillEcRegisters.CtgpDbEnableDynamicBoostBit);

        return current == next || bridge.WriteReadingBack(UniwillEcRegisters.CtgpDbEnable, next) == next
            ? Applied(target, capability)
            : Failed(target, capability, EnableRefused);
    }

    private ControlApplyOutcome WriteOffset(
        ControlObject target,
        ControlCapability capability,
        ControlSetting setting)
    {
        if (setting.Number is not { } value || !double.IsFinite(value)
            || value < 0 || value > byte.MaxValue || value != Math.Truncate(value) || setting.Toggle is not null)
        {
            return Failed(target, capability, "功率偏移必须是范围内的非负整数。");
        }
        if (ReadBudget(target) is not { } budget)
        {
            return Failed(target, capability, BudgetUnknown);
        }
        if (value > RequestMaximum(budget.HeadroomWatts))
        {
            return Failed(target, capability, "请求超出当前功率偏移范围。");
        }
        if (!EnsureTakeover())
        {
            return Failed(target, capability, EnableRefused);
        }

        // 上界和 Probe 报出去的量程取同一个数，免得界面能拉到的值这里又夹一次。
        var wanted = checked((byte)value);
        if (bridge.WriteReadingBack(AddressOf(capability.Id), wanted) is not { } effective)
        {
            logger?.LogWarning(
                "写 Uniwill 功率额度失败：{Capability} = {Watts} W。",
                capability.Id,
                wanted);
            return Failed(target, capability, WriteRefused);
        }
        if (effective == wanted)
        {
            return Applied(target, capability);
        }

        logger?.LogWarning(
            "{Capability} 请求 {Wanted} W，回读 {Effective} W，未确认应用。",
            capability.Id,
            wanted,
            effective);
        // The OEM also writes these registers. A mismatch alone cannot identify clamping.
        return Failed(target, capability,
            $"请求 {wanted} W，回读 {effective} W，未确认应用。");
    }

    private bool EnsureTakeover()
    {
        if (bridge.Read(UniwillEcRegisters.CtgpDbEnable) is not { } current)
        {
            return false;
        }
        var wanted = (byte)(current | TakeoverBits);
        return current == wanted || bridge.WriteReadingBack(UniwillEcRegisters.CtgpDbEnable, wanted) == wanted;
    }

    private GpuPowerBudget? ReadBudget(ControlObject target)
    {
        if (target.AdapterIndex is not { } adapterIndex
            || bridge.ReadPowerLimit(adapterIndex) is not { DefaultWatts: { } baseWatts } limits
            || !double.IsFinite(baseWatts) || baseWatts < 0 || !double.IsFinite(limits.MaximumWatts)
            || bridge.Read(UniwillEcRegisters.CtgpOffset) is not { } ctgp
            || bridge.Read(UniwillEcRegisters.DynamicBoostOffset) is not { } dynamicBoost)
        {
            return null;
        }

        var headroom = limits.MaximumWatts - baseWatts;
        return !double.IsFinite(headroom) || headroom <= 0
            ? null
            : new GpuPowerBudget(baseWatts, headroom, ctgp, dynamicBoost);
    }

    private static double RequestMaximum(double headroomWatts)
    {
        // Existing NVML request envelope, not a measured per-register firmware ceiling.
        return Math.Min(byte.MaxValue, Math.Floor(headroomWatts));
    }

    private static ushort AddressOf(string capabilityId)
        => string.Equals(capabilityId, CtgpOffsetCapabilityId, StringComparison.Ordinal)
            ? UniwillEcRegisters.CtgpOffset
            : UniwillEcRegisters.DynamicBoostOffset;

    private static bool IsMine(ControlObject target, string capabilityId)
        => string.Equals(target.Kind, ControlObjectKinds.Gpu, StringComparison.Ordinal)
            && string.Equals(
                target.GpuAttachment,
                ControlGpuAttachments.Discrete,
                StringComparison.Ordinal)
            && capabilityId is CtgpOffsetCapabilityId
                or DynamicBoostEnabledCapabilityId
                or DynamicBoostOffsetCapabilityId;

    private static ControlApplyOutcome Applied(ControlObject target, ControlCapability capability)
        => new(target.Id, capability.Id, ControlApplyStatuses.Applied, null);

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
