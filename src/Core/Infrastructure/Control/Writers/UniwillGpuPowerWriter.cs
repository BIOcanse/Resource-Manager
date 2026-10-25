using ResourceManager.App.Application.Control;
using ResourceManager.App.Domain.Control;
using ResourceManager.App.Infrastructure.Paths;

namespace ResourceManager.App.Infrastructure.Control.Writers;

/// <summary>OEM GPU offsets. Readback is a register observation, not measured power or a firmware limit.</summary>
public sealed partial class UniwillGpuPowerWriter : IControlWriter, IDisposable
{
    private readonly ILogger<UniwillGpuPowerWriter>? logger;
    private readonly IUniwillGpuPowerHardware bridge;

    public UniwillGpuPowerWriter(IHostEnvironment environment, ILogger<UniwillGpuPowerWriter>? logger = null)
        : this(new UniwillGpuPowerHardware(), Path.Combine(
            PackagePathResolver.ResolvePackageRoot(environment.ContentRootPath), "UserData", "Control", "uniwill-gpu-baselines.json"), logger) { }

    internal UniwillGpuPowerWriter(IUniwillGpuPowerHardware hardware, ILogger<UniwillGpuPowerWriter>? logger = null)
        : this(hardware, null, logger) { }

    internal UniwillGpuPowerWriter(IUniwillGpuPowerHardware hardware, string? baselinePath, ILogger<UniwillGpuPowerWriter>? logger = null)
    {
        bridge = hardware ?? throw new ArgumentNullException(nameof(hardware));
        this.baselinePath = baselinePath;
        this.logger = logger;
    }

    public void Dispose() => bridge.Dispose();

    internal const string CtgpOffsetCapabilityId = "gpu.ctgp-offset";
    internal const string DynamicBoostEnabledCapabilityId = "gpu.dynamic-boost-enabled";
    internal const string DynamicBoostOffsetCapabilityId = "gpu.dynamic-boost-offset";

    private const string PlatformMissing = "uniwill-platform-missing";
    internal const string TargetUnconfirmed = "uniwill-target-unconfirmed";
    internal const string FirmwareUnconfirmed = "uniwill-firmware-unconfirmed";
    private const string BudgetUnknown = "uniwill-budget-unreadable";
    private const string WriteRefused = "uniwill-register-unverified";
    private const string EnableRefused = "uniwill-enable-unverified";
    private const string FirmwareManaged = "uniwill-firmware-managed";
    internal const string BaselineUnavailable = "uniwill-baseline-unavailable";

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
        if (!IsBudgetTarget(target)) return ControlWriteAvailability.No(TargetUnconfirmed);
        if (!bridge.IsAvailable)
        {
            return ControlWriteAvailability.No(PlatformMissing);
        }
        if (!bridge.IsBudgetFirmwareConfirmed) return ControlWriteAvailability.No(FirmwareUnconfirmed);
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
            RequestMaximum(budget, capability.Id),
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

        if (!IsMine(target, capability.Id) || !IsBudgetTarget(target) || !bridge.IsAvailable
            || !bridge.IsBudgetFirmwareConfirmed)
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
            return Task.FromResult(Failed(target, capability, "control-capability-mismatch"));
        }
        if (!IsBudgetTarget(target)) return Task.FromResult(Failed(target, capability, TargetUnconfirmed));
        if (!bridge.IsAvailable)
        {
            return Task.FromResult(Failed(target, capability, PlatformMissing));
        }
        if (!bridge.IsBudgetFirmwareConfirmed) return Task.FromResult(Failed(target, capability, FirmwareUnconfirmed));

        lock (baselineGate)
        {
            try
            {
                return Task.FromResult(string.Equals(capability.Id, DynamicBoostEnabledCapabilityId, StringComparison.Ordinal)
                    ? WriteDynamicBoostEnabled(target, capability, setting)
                    : WriteOffset(target, capability, setting));
            }
            catch (Exception error) when (IsBaselineError(error))
            {
                logger?.LogWarning(error, "无法保存 Uniwill GPU 恢复基线。");
                return Task.FromResult(Failed(target, capability, BaselineUnavailable));
            }
        }
    }

    public Task<bool> RestoreAsync(
        ControlObject target,
        ControlCapability capability,
        IReadOnlySet<string> stillConfigured,
        CancellationToken cancellationToken, ControlSetting? previousSetting = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsMine(target, capability.Id) || !IsBudgetTarget(target) || !bridge.IsAvailable
            || !bridge.IsBudgetFirmwareConfirmed)
        {
            return Task.FromResult(false);
        }
        lock (baselineGate)
        {
            try
            {
                if (bridge.Read(UniwillEcRegisters.CtgpDbEnable) is not { } current) return Task.FromResult(false);
                var retained = stillConfigured.Any(id => id is CtgpOffsetCapabilityId
                    or DynamicBoostEnabledCapabilityId or DynamicBoostOffsetCapabilityId);
                if (!retained)
                {
                    var automatic = (byte)(current & ~TakeoverBits);
                    var restored = current == automatic
                        || bridge.WriteReadingBack(UniwillEcRegisters.CtgpDbEnable, automatic) == automatic;
                    if (restored) RemoveBaseline(target);
                    return Task.FromResult(restored);
                }
                LoadBaselines();
                if (!baselines!.TryGetValue(target.Id, out var baseline)) return Task.FromResult(false);
                if (capability.Id == DynamicBoostEnabledCapabilityId)
                {
                    var restored = (byte)((current & ~UniwillEcRegisters.CtgpDbEnableDynamicBoostBit)
                        | (baseline.DynamicBoostEnabled ? UniwillEcRegisters.CtgpDbEnableDynamicBoostBit : 0));
                    return Task.FromResult(current == restored
                        || bridge.WriteReadingBack(UniwillEcRegisters.CtgpDbEnable, restored) == restored);
                }
                var original = capability.Id == CtgpOffsetCapabilityId ? baseline.Ctgp : baseline.DynamicBoost;
                return Task.FromResult(bridge.Read(AddressOf(capability.Id)) == original
                    || bridge.WriteReadingBack(AddressOf(capability.Id), original) == original);
            }
            catch (Exception error) when (IsBaselineError(error))
            {
                logger?.LogWarning(error, "无法读取或释放 Uniwill GPU 恢复基线。");
                return Task.FromResult(false);
            }
        }
    }
    private ControlApplyOutcome WriteDynamicBoostEnabled(
        ControlObject target,
        ControlCapability capability,
        ControlSetting setting)
    {
        if (setting.Toggle is not { } wanted || setting.Number is not null)
        {
            return Failed(target, capability, "control-toggle-required");
        }
        if (bridge.Read(UniwillEcRegisters.CtgpDbEnable) is not { } current)
        {
            return Failed(target, capability, BudgetUnknown);
        }

        if (!CaptureBaseline(target, current)) return Failed(target, capability, BaselineUnavailable);

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
            return Failed(target, capability, "control-nonnegative-integer-required");
        }
        if (ReadBudget(target) is not { } budget)
        {
            return Failed(target, capability, BudgetUnknown);
        }
        if (value > RequestMaximum(budget, capability.Id))
        {
            return Failed(target, capability, new("control-out-of-range",
                new(Minimum: 0, Maximum: RequestMaximum(budget, capability.Id), Unit: ControlUnits.Watt, Requested: value)));
        }
        if (bridge.Read(UniwillEcRegisters.CtgpDbEnable) is not { } enable || !CaptureBaseline(target, enable))
            return Failed(target, capability, BaselineUnavailable);
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
        return Failed(target, capability, new("control-readback-mismatch",
            new(Unit: ControlUnits.Watt, Requested: wanted, Observed: effective)));
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

    private bool IsBudgetTarget(ControlObject target)
        => target.AdapterIndex is { } index && target.AdapterKey is { } key && bridge.IsBudgetTarget(index, key);

    private static double RequestMaximum(GpuPowerBudget budget, string capabilityId)
    {
        // Existing NVML request envelope, not a measured per-register firmware ceiling.
        var sibling = capabilityId == CtgpOffsetCapabilityId ? budget.DynamicBoostOffsetWatts : budget.CtgpOffsetWatts;
        return Math.Clamp(Math.Floor(budget.HeadroomWatts - sibling), 0, byte.MaxValue);
    }

    private static ushort AddressOf(string capabilityId)
        => string.Equals(capabilityId, CtgpOffsetCapabilityId, StringComparison.Ordinal)
            ? UniwillEcRegisters.CtgpOffset
            : UniwillEcRegisters.DynamicBoostOffset;

    private static bool IsMine(ControlObject target, string capabilityId)
        => string.Equals(target.Kind, ControlObjectKinds.Gpu, StringComparison.Ordinal)
            && target.Platform.Vendor == ControlVendors.Nvidia
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
        ControlApplyReason reason)
        => new(target.Id, capability.Id, ControlApplyStatuses.Failed, reason);

    private readonly record struct GpuPowerBudget(
        double BaseWatts,
        double HeadroomWatts,
        double CtgpOffsetWatts,
        double DynamicBoostOffsetWatts);
}
