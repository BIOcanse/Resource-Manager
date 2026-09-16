namespace ResourceManager.App.Domain.Control;

/// <summary>可控对象的种类。稳定标识，前端按它分组。</summary>
public static class ControlObjectKinds
{
    public const string Gpu = "gpu";
    public const string Cpu = "cpu";
    public const string Fan = "fan";

    public static readonly IReadOnlyList<string> All = [Gpu, Cpu, Fan];
}

/// <summary>
/// 这台机器上这个对象所处的"情况"。
///
/// 按用户的要求，这是**每个对象自己的**，不是全局的：
/// 一台机器上 A 卡和 N 卡各是一种情况，各自需要各自的驱动，
/// 所以"这块卡能不能控"是对象级的事实，不是整个程序的开关。
/// </summary>
public readonly record struct ControlObjectPlatform(
    /// <summary>操作系统。当前只有 windows，Linux 以后再加。</summary>
    string OperatingSystem,
    /// <summary>厂商：nvidia / amd / intel / oem / unknown。</summary>
    string Vendor);

public static class ControlVendors
{
    public const string Nvidia = "nvidia";
    public const string Amd = "amd";
    public const string Intel = "intel";
    public const string Oem = "oem";
    public const string Unknown = "unknown";
}

public static class ControlOperatingSystems
{
    public const string Windows = "windows";
}

/// <summary>
/// 显卡是怎么接上来的。
///
/// 要分这个是因为**可调的自由度差很多**：核显的频率和功耗通常由 CPU 封装的 SMU 管，
/// 走的是处理器那条路，不是独显那条；能调的项更少，范围也更窄。
/// 不分的话界面上会对着核显显示一堆它根本做不到的项。
/// </summary>
public static class ControlGpuAttachments
{
    /// <summary>核显，和 CPU 同一封装。</summary>
    public const string Integrated = "integrated";
    /// <summary>独显。</summary>
    public const string Discrete = "discrete";
    /// <summary>认不出来时用它，不猜。</summary>
    public const string Unknown = "unknown";
}

/// <summary>一个能力能取什么值。前端据此决定画滑块、曲线还是开关。</summary>
public static class ControlValueKinds
{
    /// <summary>开关。</summary>
    public const string Toggle = "toggle";
    /// <summary>一个数，带上下界和步长。</summary>
    public const string Number = "number";
    /// <summary>温度到转速的曲线。</summary>
    public const string Curve = "curve";
}

/// <summary>
/// 一个对象上的一项能力，比如"核心频率偏移"或"锁最高转速"。
///
/// **读不到/控不了的能力也要列出来，并说明原因。**
/// 这和监控项的口径是同一条：把算好的原因扔掉，用户看到的就是"根本没这功能"，
/// 分不清是这台机器不支持还是软件没做。
/// </summary>
public sealed record ControlCapability(
    /// <summary>稳定标识，例如 <c>fan.lock-maximum</c>。</summary>
    string Id,
    string Label,
    string ValueKind,
    /// <summary>现在能不能用。不能用时 <see cref="UnavailableReason"/> 必须有值。</summary>
    bool Supported,
    string? UnavailableReason = null,
    /// <summary>缺哪个可选组件才不能用。没有就是 null。</summary>
    string? RequiredComponentId = null,
    string? RequiredComponentName = null,
    /// <summary>数值型能力的范围与单位；其余类型为 null。</summary>
    ControlNumberRange? Range = null);

public sealed record ControlNumberRange(
    double Minimum,
    double Maximum,
    double Step,
    string Unit,
    /// <summary>厂商/系统默认值，"恢复默认"回到这里。读不到就是 null。</summary>
    double? DefaultValue = null);

/// <summary>
/// 一个可控对象：一块显卡、一个风扇、一颗 CPU。
///
/// 身份沿用监控侧已有的标识（GPU 用 IdentityKey），不另造一套编号 ——
/// 同一块卡在监控页和控制页必须是同一个东西。
/// </summary>
public sealed record ControlObject(
    /// <summary>形如 <c>gpu:{identityKey}</c>，全局唯一且跨重启稳定。</summary>
    string Id,
    string Kind,
    string DisplayName,
    ControlObjectPlatform Platform,
    IReadOnlyList<ControlCapability> Capabilities,
    /// <summary>补充说明，例如这个风扇是哪个采集器报上来的。</summary>
    string? Detail = null,
    /// <summary>
    /// 显卡的接法：核显还是独显。非显卡对象为 null。
    /// 核显能调的比独显少，见 <see cref="ControlGpuAttachments"/>。
    /// </summary>
    string? GpuAttachment = null)
{
    /// <summary>这个对象现在一项都控不了。界面据此整体标灰。</summary>
    public bool IsControllable => Capabilities.Any(static capability => capability.Supported);
}

/// <summary>可控对象清单，加上它是什么时候读出来的。</summary>
public sealed record ControlObjectCatalog(
    IReadOnlyList<ControlObject> Objects,
    DateTimeOffset ReadAt);
