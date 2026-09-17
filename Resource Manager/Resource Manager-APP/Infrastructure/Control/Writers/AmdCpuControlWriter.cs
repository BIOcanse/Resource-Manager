using System.Globalization;
using ResourceManager.App.Application.Control;
using ResourceManager.App.Domain.Control;
using ResourceManager.App.Infrastructure.Monitoring.AmdSmu;

namespace ResourceManager.App.Infrastructure.Control.Writers;

/// <summary>
/// AMD 处理器的功耗上限，走 SMU 邮箱。
///
/// **内核用的是已经装着的 PawnIO，不是 WinRing0。** 本机开着内存完整性（HVCI）
/// 并启用了易受攻击驱动阻止列表，WinRing0 在这种机器上根本加载不了 ——
/// RyzenAdj 实测 <c>init_ryzenadj()</c> 直接返回 NULL。PawnIO 是签名的沙箱化内核模块，
/// 正是为这种环境做的，而且监控侧读 SMU 用的就是它。
///
/// 命令号和邮箱地址取自 RyzenAdj（LGPL）：Dragon Range / Fire Range 上
/// 持续功耗是 MP1 的 <c>0x4F</c>、长时是 <c>0x5F</c>、瞬时是 <c>0x3E</c>，参数单位毫瓦。
///
/// 写完从 PM table 回读核对 —— SMU 回执说收到了，不等于限制真的改了。
/// </summary>
public sealed class AmdCpuControlWriter(
    IHostEnvironment environment,
    ILogger<AmdCpuControlWriter>? logger = null) : IControlWriter, IDisposable
{
    internal const string PowerLimitCapabilityId = "cpu.power-limit";

    /// <summary>PM table 里这几项的位置，对所有表版本都一样（RyzenAdj 也这么读）。</summary>
    private const int StapmLimitIndex = 0;
    private const int FastLimitIndex = 2;
    private const int SlowLimitIndex = 4;

    /// <summary>Dragon Range / Fire Range 的 MP1 命令号。</summary>
    private const uint SetStapmLimitMessage = 0x4F;
    private const uint SetFastLimitMessage = 0x3E;
    private const uint SetSlowLimitMessage = 0x5F;

    /// <summary>PawnIO 模块里 <c>CPU_Raphael</c> 和 <c>CPU_DragonRange</c> 的枚举值。</summary>
    private const uint CodeNameRaphael = 16;
    private const uint CodeNameDragonRange = 28;

    private const string ProviderMissing = "读不到 SMU，装上并验证 AMD SMU / PawnIO Provider 之后才能调。";
    private const string CodeNameUnsupported = "还没有这颗处理器的 SMU 命令号，不猜着写。";
    private const string Refused = "SMU 拒绝了这次写入。";
    private const string NotEffective = "写下去了，但回读的功耗上限没有变。";

    private readonly object gate = new();
    private AmdSmuPawnIoSession? session;
    private bool sessionFailed;
    private double? baselineStapmWatts;

    public ControlWriteAvailability Probe(ControlObject target, ControlCapability capability)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(capability);

        if (!IsMine(target, capability.Id))
        {
            return ControlWriteAvailability.NotMine;
        }

        lock (gate)
        {
            if (ResolveSession() is not { } open)
            {
                return ControlWriteAvailability.No(ProviderMissing);
            }
            if (!IsKnownCodeName(open.CodeName))
            {
                return ControlWriteAvailability.No(CodeNameUnsupported);
            }
            if (CaptureBaseline(open) is not { } baseline)
            {
                return ControlWriteAvailability.No(ProviderMissing);
            }

            /*
             * 上界就是基线，也就是**只能往下调，不能往上**。
             *
             * 这是笔记本。往上加功耗要靠散热和供电扛得住，而那是整机厂在出厂时
             * 连同散热模组一起定下来的；我们既读不到它的余量，也没资格替它加。
             * 用户要的是"限功耗"，不是"超功耗" —— 把上界开到一个我们编出来的
             * 大数字，等于把机器的安全押在猜测上。
             *
             * 上界也不能从**当前值**推：先前那样做，把上限调低之后上界跟着变低，
             * 用户想调回去就被静默夹成低值 —— 报"已应用"，值却不是他要的那个。
             * 基线是持久化的、我们动手之前的那个数，所以它既稳定又是真实的天花板。
             */
            var minimum = capability.Range is { } declared
                ? Math.Min(declared.Minimum, Math.Round(baseline))
                : 5;
            return ControlWriteAvailability.Yes(new ControlNumberRange(
                minimum,
                Math.Round(baseline),
                1,
                "W",
                Math.Round(baseline)));
        }
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

        return Task.FromResult(Write(target, capability, setting));
    }

    private ControlApplyOutcome Write(
        ControlObject target,
        ControlCapability capability,
        ControlSetting setting)
    {
        if (setting.Number is not { } value || !double.IsFinite(value))
        {
            return Failed(target, capability, "这一项要一个数值，收到的不是。");
        }

        lock (gate)
        {
            if (ResolveSession() is not { } open || !IsKnownCodeName(open.CodeName))
            {
                return Failed(target, capability, ProviderMissing);
            }

            var bounds = capability.Range;
            var watts = bounds is null
                ? value
                : Math.Clamp(value, bounds.Minimum, bounds.Maximum);
            var milliwatts = (ulong)Math.Round(watts * 1000);

            // 持续和长时一起设成这个值，瞬时不动 —— 用户说的"功耗上限"是持续那个；
            // 把瞬时也压下去会连短暂的加速也一并砍掉，那是另一回事。
            foreach (var message in new[] { SetStapmLimitMessage, SetSlowLimitMessage })
            {
                var response = open.SendMailboxCommand(
                    AmdSmuMailbox.DragonRangeMp1,
                    message,
                    [milliwatts]);
                if (response != AmdSmuPawnIoSession.SmuResponseOk)
                {
                    logger?.LogWarning(
                        "SMU 拒绝了功耗上限写入：命令 0x{Message:X}，回执 {Response}。",
                        message,
                        response);
                    return Failed(target, capability, Refused);
                }
            }

            // 回执只说"收到了"。真正算数的是 PM table 里那个上限有没有变。
            if (ReadLimits(open) is not { } after
                || Math.Abs(after.StapmWatts - watts) > 1)
            {
                return Failed(target, capability, NotEffective);
            }
        }

        return new ControlApplyOutcome(
            target.Id,
            capability.Id,
            ControlApplyStatuses.Applied,
            null);
    }

    /// <summary>
    /// 我们动手之前，这颗处理器的持续功耗上限是多少。
    ///
    /// **记在盘上，只记一次。** 只放在内存里不够：用户把上限调低之后重启，
    /// 下次读到的"当前值"就是那个低的，"恢复默认"会回到他调过的数而不是原厂的数，
    /// 而且一次比一次低。第一次读到的那个值才是这台机器本来的样子。
    /// </summary>
    private double? CaptureBaseline(AmdSmuPawnIoSession open)
    {
        if (baselineStapmWatts is { } captured)
        {
            return captured;
        }
        if (ReadPersistedBaseline() is { } persisted)
        {
            baselineStapmWatts = persisted;
            return persisted;
        }
        if (ReadLimits(open) is not { } limits)
        {
            return null;
        }
        baselineStapmWatts = limits.StapmWatts;
        WritePersistedBaseline(limits.StapmWatts);
        return baselineStapmWatts;
    }

    private double? ReadPersistedBaseline()
    {
        try
        {
            return File.Exists(BaselinePath)
                && double.TryParse(
                    File.ReadAllText(BaselinePath).Trim(),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var watts)
                && watts is > 0 and < 1000
                    ? watts
                    : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            logger?.LogWarning(error, "读不到处理器功耗基线。");
            return null;
        }
    }

    private void WritePersistedBaseline(double watts)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(BaselinePath)!);
            File.WriteAllText(
                BaselinePath,
                watts.ToString("0.###", CultureInfo.InvariantCulture));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // 记不下来不影响这次调节，只是下次重启后"恢复默认"可能不准。
            logger?.LogWarning(error, "存不下处理器功耗基线。");
        }
    }

    private string BaselinePath => Path.Combine(
        environment.ContentRootPath,
        "UserData",
        "Control",
        "cpu-power-baseline.txt");

    private CpuPowerLimits? ReadLimits(AmdSmuPawnIoSession open)
    {
        try
        {
            var table = open.UpdateAndReadPmTable();
            if (table.Length <= SlowLimitIndex)
            {
                return null;
            }
            var stapm = table[StapmLimitIndex];
            var fast = table[FastLimitIndex];
            var slow = table[SlowLimitIndex];
            return float.IsFinite(stapm) && stapm > 0 && float.IsFinite(fast) && fast > 0
                ? new CpuPowerLimits(stapm, fast, slow)
                : null;
        }
        catch (AmdSmuProviderUnavailableException error)
        {
            logger?.LogWarning(error, "读 PM table 失败。");
            return null;
        }
    }

    private AmdSmuPawnIoSession? ResolveSession()
    {
        if (session is not null || sessionFailed)
        {
            return session;
        }

        try
        {
            session = AmdSmuPawnIoSession.Open(environment.ContentRootPath);
        }
        catch (AmdSmuProviderUnavailableException error)
        {
            sessionFailed = true;
            logger?.LogInformation(error, "打不开 SMU 会话，处理器功耗上限不可调。");
        }
        return session;
    }

    private static bool IsKnownCodeName(uint codeName)
        => codeName is CodeNameRaphael or CodeNameDragonRange;

    private static bool IsMine(ControlObject target, string capabilityId)
        => string.Equals(target.Kind, ControlObjectKinds.Cpu, StringComparison.Ordinal)
            && string.Equals(target.Platform.Vendor, ControlVendors.Amd, StringComparison.Ordinal)
            && string.Equals(capabilityId, PowerLimitCapabilityId, StringComparison.Ordinal);

    private static ControlApplyOutcome Failed(
        ControlObject target,
        ControlCapability capability,
        string reason)
        => new(target.Id, capability.Id, ControlApplyStatuses.Failed, reason);

    public void Dispose()
    {
        lock (gate)
        {
            session?.Dispose();
            session = null;
        }
    }

    private readonly record struct CpuPowerLimits(
        double StapmWatts,
        double FastWatts,
        double SlowWatts);
}
