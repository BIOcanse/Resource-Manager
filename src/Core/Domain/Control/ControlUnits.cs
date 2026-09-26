namespace ResourceManager.App.Domain.Control;

/// <summary>
/// 数值型设定的单位。
///
/// **单位属于值本身，不属于能力描述。**
/// 先前值是一个裸数字，单位挂在能力描述上 —— 于是光看状态机里那条记录，
/// 分不清 60 是瓦还是档；换一台机器、能力描述变了，同一条记录的含义就跟着变。
/// 配置要能保存、切换、导出，那条记录就必须自解释。
///
/// 这些是稳定标识，会进持久化文件和前端，不要随便改字面。
/// </summary>
public static class ControlUnits
{
    /// <summary>瓦。功耗上限之类。</summary>
    public const string Watt = "W";

    /// <summary>兆赫。频率与频率偏移。</summary>
    public const string Megahertz = "MHz";

    /// <summary>
    /// 档。Curve Optimizer 的偏移量。
    ///
    /// **不是伏特。** 一档大概几毫伏，具体多少随体质变，厂商也不给换算，
    /// 编一个伏特数出来只会骗人。
    /// </summary>
    public const string Step = "档";

    /// <summary>百分比。转速、功率比例之类。</summary>
    public const string Percent = "%";

    /// <summary>摄氏度。</summary>
    public const string Celsius = "°C";

    /// <summary>
    /// 安培。电流墙（TDC / EDC）。
    ///
    /// **和功耗墙不是一回事。** 功耗墙管散热扛不扛得住，电流墙管供电扛不扛得住；
    /// 同样的瓦数在不同电压下是不同的电流，所以两者各有各的上限。
    /// </summary>
    public const string Ampere = "A";

    /// <summary>倍。PBO 标量这类没有物理量纲、但确实是"几倍"的数。</summary>
    public const string Multiplier = "×";

    /// <summary>
    /// 毫伏。显卡的电压偏移。
    ///
    /// **和 <see cref="Step"/> 不是一回事。** NVIDIA 这边给的就是真的电压偏移量，
    /// 有确定的量纲；AMD CPU 那边给的是没有换算的档位。两者不能混用同一个单位，
    /// 否则界面上两个"-20"会看着一样、实际差着数量级。
    /// </summary>
    public const string Millivolt = "mV";

    /// <summary>帧每秒。驱动那边的帧率限制。</summary>
    public const string FramesPerSecond = "FPS";

    /// <summary>
    /// 秒。功耗墙的时间窗。
    ///
    /// **它和瓦数是两件事**：同样的短时功耗上限，窗口给 8 秒还是 56 秒，
    /// 机器的手感完全不同。
    /// </summary>
    public const string Second = "s";

    /// <summary>没有量纲的纯数。</summary>
    public const string None = "";

    public static readonly IReadOnlyList<string> All =
        [Watt, Megahertz, Step, Percent, Celsius, Ampere, Multiplier,
            Millivolt, FramesPerSecond, Second, None];

    /// <summary>这个单位认不认得。认不得就不该往下发。</summary>
    public static bool IsKnown(string? unit)
        => unit is not null && All.Contains(unit, StringComparer.Ordinal);
}
