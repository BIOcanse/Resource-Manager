using System.Globalization;

namespace ResourceManager.App.Application.Metrics;

/// <summary>
/// 可选监控项的分组键。
///
/// **按设备分组，不按数据来源分组。** 一项指标属于哪一组，看它量的是这台机器上
/// 哪个真实存在的东西 —— 处理器、内存、磁盘、某一块显卡、风扇。
/// 同一块硬件不该因为读它的通道不一样就落到两个组里
/// （核显先前就是这样：走 SMU 的那几项挂在处理器组，走 ADLX 的挂在显卡组）。
///
/// 这些是**稳定的键**，不是给人看的字样。界面上显示什么由界面按键去取，
/// 所以键里不带中文、不带空格、大小写固定 —— 先前 "Virtual Memory" 这种
/// 带空格又带大写的写法，只要哪一边少做一次归一化就对不上。
/// </summary>
public static class MetricGroups
{
    public const string Cpu = "cpu";
    public const string Memory = "memory";
    public const string VirtualMemory = "virtual-memory";
    public const string Disk = "disk";
    public const string Network = "network";
    public const string Motherboard = "motherboard";

    /// <summary>
    /// 风扇自成一组。
    ///
    /// 风扇不跟着它吹的那块硬件走 —— 笔记本往往整机共用一套散热，
    /// SoC 更是把处理器和显卡放在一颗封装里。控制面那边已经按这个口径把风扇
    /// 当成独立实例，监控项这边跟它一致。
    /// </summary>
    public const string Fan = "fan";

    /// <summary>
    /// 第 <paramref name="index"/> 块显卡。**每块卡各自一组** ——
    /// 一台机器上核显和独显能调的东西差很多，混在一个"显卡"组里分不清谁是谁。
    /// </summary>
    public static string Gpu(int index)
        => "gpu." + index.ToString(CultureInfo.InvariantCulture);
}
