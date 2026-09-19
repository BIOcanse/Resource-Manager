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

/// <summary>Normal includes standard tuning; Root explicitly enables direct voltage and base-clock controls.</summary>
public static class ControlAccessLevels
{
    public const string Normal = "normal";
    public const string Root = "root";
    public static readonly IReadOnlyList<string> All = [Normal, Root];
    public static int Rank(string? level) => level == Root ? 1 : 0;
    public static bool Allows(string? current, string? required) => Rank(current) >= Rank(required);
    public static string DisplayName(string? level) => level == Root ? "Root" : "普通";
}

/// <summary>
/// 一项设定**实际走哪条链路**写下去。
///
/// 摆出来是因为这一页上同一块卡的不同项走的是完全不同的通道：
/// 频率偏移走 NVAPI、功耗上限走 NVML、cTGP 走整机厂的 EC。
/// 它们的能力、限制、失败原因毫无共同点 —— 用户看到"这一项调不了"时，
/// 第一个该知道的就是**是谁说不行**。
///
/// 这也是我们自己排查的锚点：一条链路出问题，界面上一眼能看出哪些项会跟着受影响。
/// </summary>
public static class ControlChannels
{
    /// <summary>NVIDIA NVAPI。频率偏移、显卡风扇走这条。</summary>
    public const string Nvapi = "nvapi";

    /// <summary>NVIDIA NVML。功耗上限、时钟锁、温度墙走这条。</summary>
    public const string Nvml = "nvml";

    /// <summary>
    /// NVIDIA 驱动 Profile（DRS）。电源管理模式、帧率限制、每应用 GPU 走这条。
    ///
    /// **和 NVAPI 的调谐接口是两层东西**：那一层动的是硬件，这一层动的是
    /// 驱动策略。混成一条的话，用户分不清"我改的是显卡还是驱动的脾气"。
    /// </summary>
    public const string NvapiDriverProfile = "nvapi-drs";

    /// <summary>整机厂的 ACPI WMI 嵌入式控制器。cTGP、Dynamic Boost 走这条。</summary>
    public const string OemEmbeddedController = "oem-ec";

    /// <summary>AMD 的 SMU，经硬件写入辅助进程。处理器功耗墙、电压走这条。</summary>
    public const string AmdSmu = "amd-smu";

    /// <summary>风扇控制核心。</summary>
    public const string FanControlCore = "fan-core";

    /// <summary>Intel 显卡控制库。**只给核显** —— Intel 独显不在范围内。</summary>
    public const string Igcl = "igcl";

    /// <summary>
    /// Intel 处理器的 MSR（以及要配 MCHBAR 的那份 MMIO 镜像），经 PawnIO。
    ///
    /// 和 AMD 那条 SMU 完全不是一回事：AMD 是给 SMU 发命令，
    /// Intel 是直接读写寄存器。能调的项、失败方式、锁定情况都不一样。
    /// </summary>
    public const string IntelMsr = "intel-msr";

    /// <summary>AMD ADLX / ADL。</summary>
    public const string Adlx = "adlx";
}

/// <summary>
/// 这台机器的形态。**词条，不是名字。**
///
/// 和显卡的"核显/独显"是同一类东西：名字说的是"这是哪一个"，
/// 词条说的是"这是哪一类"。两者分开，名字才能保持中立
/// （风扇1、风扇2），分类才能各自演进。
///
/// 值来自 SMBIOS 的机箱类型，不从旁证推 —— 见 WindowsChassisKindReader。
/// </summary>
public static class ControlChassisKinds
{
    /// <summary>便携：笔记本、二合一、平板这一类。</summary>
    public const string Portable = "portable";
    /// <summary>固定式：台式机、塔式、机架这一类。</summary>
    public const string Fixed = "fixed";
    /// <summary>问不出来。**不猜** —— 猜错了整页词条跟着错。</summary>
    public const string Unknown = "unknown";
}

/// <summary>
/// 一个风扇在这台机器里干什么用的。
///
/// **能认出来才挂，认不出就不挂。** 固件多数只给序号，不说哪个吹什么；
/// 挂一个猜的角色会让用户按它去调，调的却可能是另一个风扇。
/// </summary>
public static class ControlFanRoles
{
    public const string Cpu = "cpu";
    public const string Gpu = "gpu";
    /// <summary>内吹风扇：笔记本里往机身内部吹的那个。</summary>
    public const string Intake = "intake";
    /// <summary>机箱风扇：台式机上不直接贴着某个芯片的那些。</summary>
    public const string Case = "case";

    /// <summary>
    /// 这条通道说不出这个风扇吹的是什么。
    ///
    /// **不当成词条挂出去** —— 界面上写一个"未知"，比什么都不写更糟：
    /// 用户会以为我们查过了，并且查出来的结果就叫"未知"。
    /// </summary>
    public const string Unknown = "unknown";
}

/// <summary>
/// 这条通道给得了什么样的风扇曲线。
///
/// **必须让用户看得见，因为它们是完全不同的承诺**：固件查表的曲线写进去就一直有效，
/// 本程序关了、重启了也照跑；软件闭环的曲线是我们自己在采样和调速，程序一退就没了；
/// 而"只能切自动/全速"根本不是曲线。
///
/// 先前只分"固件 / 软件 / 没有"三档，把"能设固定转速所以可以跑软件曲线"
/// 和"只能全速或自动"混成了同一个 none —— 前者是差我们一步，后者是这台机器做不到，
/// 那正是用户最需要分清的两件事。
/// </summary>
public static class ControlFanCurveExecutions
{
    /// <summary>
    /// 写进固件那张表，之后固件自己查表。
    ///
    /// 本程序关了、重启了都还在 —— **"设一次就一直有效"**。
    /// 代价是受固件那张表的形状限制（几个点、温度断点能不能改）。
    /// </summary>
    public const string Firmware = "firmware";

    /// <summary>
    /// 本程序每 0.1 秒读温度、算转速、写下去。
    ///
    /// 曲线想怎么画都行，还能挑看哪一路温度；代价是**程序不在就没了**，
    /// 那时风扇交还固件。
    /// </summary>
    public const string Software = "software";
}

/// <summary>固件曲线的形态 —— 固件那张表给多大自由度。</summary>
public static class ControlFanFirmwareCurveKinds
{
    /// <summary>温度点和转速点都能写。</summary>
    public const string Xy = "xy";
    /// <summary>只有转速档可写，温度断点由固件定死（Legion 的 10 档表）。</summary>
    public const string Table = "table";
}

/// <summary>
/// 一项能力为什么用不了。**"你可以解锁"和"这台机器做不到"是两件事。**
///
/// 先前只有一句原因文字，界面上全都画成一把锁 —— 于是"切个档位就能用"和
/// "这台机器压根没这功能"长得一模一样，用户对着锁去翻设置，翻遍了也解不开。
///
/// 这里给的是**种类**，界面据此选图标：能解的挂锁，解不开的打叉。
/// 文字还是各写各的，种类只决定"这属于哪一类问题"。
/// </summary>
public static class ControlUnavailableKinds
{
    /// <summary>这台机器或这条通道做不到。换档位、装组件都没用。</summary>
    public const string Platform = "platform";

    /// <summary>档位不够。切到更高一档就能用。</summary>
    public const string AccessLevel = "access-level";

    /// <summary>缺某个组件，装上就有。</summary>
    public const string Component = "component";

    /// <summary>这个对象现在归固件管，先把它交给本程序。</summary>
    public const string FirmwareOwned = "firmware-owned";

    /// <summary>
    /// 机器上有另一个条件没满足，满足了这一项就能用。
    ///
    /// **和 <see cref="Platform"/> 的区别是它解得开**，和
    /// <see cref="AccessLevel"/> / <see cref="Component"/> 是同一类：
    /// 锁着，但告诉用户怎么解。
    ///
    /// **这个前置我们不替用户去满足。** 它往往连带别的后果 ——
    /// 联想的风扇曲线要切到自定义电源模式，而那同时会改整机功耗策略；
    /// 华硕改功耗墙要先接管风扇曲线，那是用户自己的另一项设定。
    /// 背着用户去动，是隐式副作用。
    /// </summary>
    public const string Prerequisite = "prerequisite";

    /// <summary>
    /// 硬件做得到，是我们还没接这条写入路径。
    ///
    /// **和 <see cref="Platform"/> 分开是有意的**：把自己没做的事显示成
    /// "你的机器不支持"，用户就再也不会问了，而那本来是我们欠的。
    /// </summary>
    public const string NotImplemented = "not-implemented";
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
    /// <summary>
    /// 不能用属于哪一类，见 <see cref="ControlUnavailableKinds"/>。
    /// 能用时为 null。界面靠它决定画锁还是画叉。
    /// </summary>
    string? UnavailableKind = null,
    /// <summary>缺哪个可选组件才不能用。没有就是 null。</summary>
    string? RequiredComponentId = null,
    string? RequiredComponentName = null,
    /// <summary>数值型能力的范围与单位；其余类型为 null。</summary>
    ControlNumberRange? Range = null,
    /// <summary>
    /// 这一项实际走哪条链路，见 <see cref="ControlChannels"/>。
    /// 没有写入器认领时为 null —— 那时它还没有链路可言。
    /// </summary>
    string? Channel = null,
    /// <summary>
    /// 这一项要哪一档权限，见 <see cref="ControlAccessLevels"/>。默认是最低的那一档。
    ///
    /// 够不到的时候 <see cref="Supported"/> 为 false、<see cref="UnavailableReason"/>
    /// 说明差哪一档 —— 界面据此把勾选框也一并锁上，写入层也会拒。
    /// </summary>
    string RequiredAccessLevel = ControlAccessLevels.Normal);

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
    /// 这个对象属于哪几类。**词条，不是名字。**
    ///
    /// 例如一个笔记本上的 CPU 风扇：名字是"风扇1"，词条是
    /// <c>portable</c> 加 <c>cpu</c>。名字保持中立（我们只知道它是第几个），
    /// 分类另说 —— 认得出来才挂，认不出就不挂，不拿猜的词条充数。
    ///
    /// 界面按词条显示成"笔记本电脑 · CPU风扇"这样的一串。
    /// </summary>
    IReadOnlyList<string>? Terms = null,
    /// <summary>
    /// 显卡的接法：核显还是独显。非显卡对象为 null。
    /// 核显能调的比独显少，见 <see cref="ControlGpuAttachments"/>。
    /// </summary>
    string? GpuAttachment = null,
    /// <summary>
    /// 这个对象对应的显示适配器序号，和监控侧的 <c>GpuMetrics.Index</c> 是同一个。
    ///
    /// 写入器靠它把对象接回真实的那块卡：NVAPI / ADLX 各有自己的句柄体系，
    /// 唯一共同的锚点就是系统枚举出来的适配器序号。非显卡对象为 null。
    /// </summary>
    int? AdapterIndex = null)
{
    /// <summary>这个对象现在一项都控不了。界面据此整体标灰。</summary>
    public bool IsControllable => Capabilities.Any(static capability => capability.Supported);
}

/// <summary>可控对象清单，加上它是什么时候读出来的。</summary>
public sealed record ControlObjectCatalog(
    IReadOnlyList<ControlObject> Objects,
    DateTimeOffset ReadAt);
