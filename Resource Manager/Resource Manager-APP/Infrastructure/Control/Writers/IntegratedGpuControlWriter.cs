using ResourceManager.App.Application.Control;
using ResourceManager.App.Domain.Control;
using ResourceManager.App.Infrastructure.Monitoring.AmdSmu;

namespace ResourceManager.App.Infrastructure.Control.Writers;

/// <summary>
/// 核显的最高频率。
///
/// 核显和独显走的**不是同一条路**，所以按 <see cref="ControlObject.GpuAttachment"/> 分流：
/// 独显有自己的驱动接口（NVAPI / ADLX），核显则跟着它所在的平台走 ——
/// AMD 的归 CPU 封装里的 SMU，Intel 的归显卡驱动自带的控制库（IGCL）。
///
/// AMD 这一侧照 RyzenAdj（LGPL）：<c>set_max_gfxclk_freq</c> 是 MP1 的 <c>0x46</c>，
/// 参数就是 MHz，而且只覆盖 Raven / Picasso / Dali / Lucienne 四个代号 ——
/// Dragon Range 这类把核显做成附属小核的型号，SMU 上根本没有这条命令。
/// 认不出代号就如实说调不了，不拿一条不存在的命令去试。
/// </summary>
public sealed class IntegratedGpuControlWriter(
    IHostEnvironment environment,
    ILogger<IntegratedGpuControlWriter>? logger = null) : IControlWriter, IDisposable
{
    internal const string MaxCoreClockCapabilityId = "gpu.max-core-clock";

    /// <summary>RyzenAdj 的 set_max_gfxclk_freq。参数单位 MHz。</summary>
    private const uint SetMaxGfxClockMessage = 0x46;

    /// <summary>
    /// PawnIO 模块里这几个代号的枚举值：RavenRidge / RavenRidge2 / Picasso / Dali / Lucienne。
    /// 只有它们在 SMU 上有核显频率这条命令。
    /// </summary>
    private static readonly uint[] SupportedCodeNames = [6, 7, 2, 15, 22];

    private const string AmdNoSmuEntry = "这颗处理器的核显频率没有 SMU 调节入口，调不了。";
    private const string ProviderMissing = "读不到 SMU，装上并验证 AMD SMU / PawnIO Provider 之后才能调。";
    private const string Refused = "SMU 拒绝了这次写入。";
    private const string IntelNotVerified = "Intel 核显的调节还没接上。";

    private readonly object gate = new();
    private AmdSmuPawnIoSession? session;
    private bool sessionFailed;

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

        lock (gate)
        {
            if (ResolveSession() is not { } open)
            {
                return ControlWriteAvailability.No(ProviderMissing);
            }
            return SupportedCodeNames.Contains(open.CodeName)
                ? ControlWriteAvailability.Yes()
                : ControlWriteAvailability.No(AmdNoSmuEntry);
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

        if (setting.Number is not { } value || !double.IsFinite(value))
        {
            return Task.FromResult(Failed(target, capability, "这一项要一个数值，收到的不是。"));
        }

        lock (gate)
        {
            if (ResolveSession() is not { } open || !SupportedCodeNames.Contains(open.CodeName))
            {
                return Task.FromResult(Failed(target, capability, AmdNoSmuEntry));
            }

            var bounds = capability.Range;
            var megahertz = (ulong)Math.Round(bounds is null
                ? value
                : Math.Clamp(value, bounds.Minimum, bounds.Maximum));

            // 这几个代号的 MP1 邮箱是默认那一组，不是 Dragon Range 那一组。
            var response = open.SendMailboxCommand(
                AmdSmuMailbox.DefaultMp1,
                SetMaxGfxClockMessage,
                [megahertz]);
            if (response != AmdSmuPawnIoSession.SmuResponseOk)
            {
                logger?.LogWarning(
                    "SMU 拒绝了核显频率写入：{Megahertz} MHz，回执 {Response}。",
                    megahertz,
                    response);
                return Task.FromResult(Failed(target, capability, Refused));
            }
        }

        return Task.FromResult(new ControlApplyOutcome(
            target.Id,
            capability.Id,
            ControlApplyStatuses.Applied,
            null));
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
            logger?.LogInformation(error, "打不开 SMU 会话，核显频率不可调。");
        }
        return session;
    }

    private static bool IsMine(ControlObject target, string capabilityId)
        => string.Equals(target.Kind, ControlObjectKinds.Gpu, StringComparison.Ordinal)
            && string.Equals(
                target.GpuAttachment,
                ControlGpuAttachments.Integrated,
                StringComparison.Ordinal)
            && string.Equals(capabilityId, MaxCoreClockCapabilityId, StringComparison.Ordinal)
            && target.Platform.Vendor is ControlVendors.Amd or ControlVendors.Intel;

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
}
