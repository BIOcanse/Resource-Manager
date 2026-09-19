namespace ResourceManager.App.Application.Control;

/// <summary>
/// 控制页的首次须知看过没有。
///
/// 第一次进控制页要把两件事说清楚：改固件设定和保修的关系，以及即使在默认的
/// 安全档，调整硬件配置也有它的风险。说过一次就记住，之后不再拦。
///
/// **这和 <see cref="IControlOverclockConsent"/> 不是一回事。** 那一个是
/// Intel IGCL 的硬性要求（用户不接受就拒绝一切调用），所以它必须可以随时收回；
/// 这一个只是"我看过了"，看过就是看过，不存在收回。
/// </summary>
public interface IControlNoticeAcknowledgement
{
    Task<bool> IsAcknowledgedAsync(CancellationToken cancellationToken);

    /// <summary>用户把两屏须知都看完并确认之后调。</summary>
    Task AcknowledgeAsync(CancellationToken cancellationToken);
}
