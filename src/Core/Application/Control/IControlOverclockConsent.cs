namespace ResourceManager.App.Application.Control;

/// <summary>
/// 超频免责声明的同意状态。
///
/// **这一条不是我们发明的流程，是厂商的硬性要求。** Intel 的 IGCL 在
/// <c>ctlOverclockWaiverSet</c> 之前会拒绝所有超频接口，原文写着：
/// 设置这个 waiver 表示用户接受**部件寿命缩短**，而且要求应用
/// "至少一次弹窗告知危险并取得用户接受"，只有用户接受之后才可以调。
///
/// 所以这里不替用户默认接受：没同意就如实报不可用，把原因说清楚。
/// 同意一次之后可以记下来（厂商文档明确允许缓存这个选择），不必每次都问。
/// </summary>
public interface IControlOverclockConsent
{
    /// <summary>用户有没有同意过。</summary>
    Task<bool> IsAcceptedAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 记下用户的选择。
    /// 只有界面上真的把后果告诉过用户、用户点了同意，才该调这个。
    /// </summary>
    Task SetAcceptedAsync(bool accepted, CancellationToken cancellationToken);
}
