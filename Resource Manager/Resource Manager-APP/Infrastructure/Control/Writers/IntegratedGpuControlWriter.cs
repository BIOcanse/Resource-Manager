using ResourceManager.App.Application.Control;
using ResourceManager.App.Domain.Control;

namespace ResourceManager.App.Infrastructure.Control.Writers;

/// <summary>
/// 核显的频率。
///
/// 核显和独显走的**不是同一条路**，所以按 <see cref="ControlObject.GpuAttachment"/> 分流：
/// 独显有自己的驱动接口（NVAPI / ADLX），核显则跟着它所在的平台走 ——
/// AMD 的归 CPU 封装里的 SMU，Intel 的归显卡驱动自带的控制库。
/// 这个写入器只认核显，并且按厂商给出各自的真实情况。
///
/// 现在两边都只回答"能不能调"，不写，原因各不相同，写在下面的常量上。
/// 照着文档抄一条没在真机上验证过的写入路径，等于把"能调"押在猜测上。
/// </summary>
public sealed class IntegratedGpuControlWriter : IControlWriter
{
    internal const string CoreClockOffsetCapabilityId = "gpu.core-clock-offset";

    /// <summary>
    /// AMD：RyzenAdj（LGPL）里的 <c>set_max_gfxclk_freq</c> 只覆盖
    /// Raven / Picasso / Dali / Lucienne 四个代号。Dragon Range 这类把核显做成
    /// 附属小核的型号，SMU 上根本没有这条命令。
    /// </summary>
    private const string AmdNoSmuEntry = "这颗处理器的核显频率没有 SMU 调节入口，调不了。";

    /// <summary>
    /// Intel：要走显卡驱动自带的控制库（IGCL，随驱动一起装，Alder Lake-P 及以后）。
    /// 手上没有 Intel 核显的机器，这条路还没验证过。
    /// </summary>
    private const string IntelNotVerified = "Intel 核显的调节还没接上。";

    public ControlWriteAvailability Probe(ControlObject target, ControlCapability capability)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(capability);

        return ReasonFor(target, capability.Id) is { } reason
            ? ControlWriteAvailability.No(reason)
            : ControlWriteAvailability.NotMine;
    }

    public Task<ControlApplyOutcome> WriteAsync(
        ControlObject target,
        ControlCapability capability,
        ControlSetting setting,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(capability);

        // 探测永远说不能写，所以正常流程走不到这里。
        return Task.FromResult(new ControlApplyOutcome(
            target.Id,
            capability.Id,
            ControlApplyStatuses.Unsupported,
            ReasonFor(target, capability.Id) ?? IntelNotVerified));
    }

    private static string? ReasonFor(ControlObject target, string capabilityId)
    {
        if (!string.Equals(target.Kind, ControlObjectKinds.Gpu, StringComparison.Ordinal)
            || !string.Equals(
                target.GpuAttachment,
                ControlGpuAttachments.Integrated,
                StringComparison.Ordinal)
            || !string.Equals(capabilityId, CoreClockOffsetCapabilityId, StringComparison.Ordinal))
        {
            return null;
        }

        return target.Platform.Vendor switch
        {
            ControlVendors.Amd => AmdNoSmuEntry,
            ControlVendors.Intel => IntelNotVerified,
            _ => null
        };
    }
}
