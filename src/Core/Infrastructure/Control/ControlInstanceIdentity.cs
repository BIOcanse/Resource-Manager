using System.Text;
using ResourceManager.App.Domain.Control;
using ResourceManager.App.Domain.Metrics;

namespace ResourceManager.App.Infrastructure.Control;

/// <summary>
/// 一台设备的身份：配置挂在它上面，所以它决定"换了硬件之后设定还在不在"。
///
/// **规则只有两条，按顺序试：**
///
/// <list type="number">
/// <item>有出厂唯一标识就用它（显卡的 GPU UUID 就是这种）。
///   这样把卡换个槽位、重装驱动、换台机器装回去，都还是同一个实例，设定跟着走。</item>
/// <item>没有就按型号。同型号用同一套设定 —— 这本来就是想要的：
///   换一块一模一样的卡上去，原来那套参数照样合适。</item>
/// </list>
///
/// 先前用的是"位置 + 型号"（设备实例路径里那串是槽位信息，不是序列号），
/// 结果换个槽位就成了另一个实例、设定全丢。那比同型号共用设定糟得多。
///
/// 只有一种情况需要额外区分：**同一台机器上装了两块认不出差别的同型号卡**。
/// 那时给第二块往后加一个出现序号 —— 不是因为它们该用不同设定，
/// 而是两个对象必须有两个不同的标识，否则目录里会撞车。
/// </summary>
internal static class ControlInstanceIdentity
{
    /// <summary>
    /// 一块显卡的身份键。
    ///
    /// <paramref name="uniqueId"/> 是出厂唯一标识（读得到才有），
    /// 读不到就退到型号。<paramref name="occurrence"/> 是同型号里的第几块，
    /// 只有在退到型号且不止一块时才会体现在结果里。
    /// </summary>
    internal static string ForGpu(GpuMetrics gpu, string? uniqueId, int occurrence)
    {
        ArgumentNullException.ThrowIfNull(gpu);
        if (!string.IsNullOrWhiteSpace(uniqueId))
        {
            return $"uid:{Normalize(uniqueId)}";
        }

        var model = Normalize(gpu.Name);
        return occurrence <= 1 ? $"model:{model}" : $"model:{model}#{occurrence}";
    }

    /// <summary>
    /// 非显卡对象（处理器、风扇）的身份键。
    ///
    /// 这些东西一台机器上就一个，而且没有出厂唯一标识可读，所以直接按型号/名字。
    /// </summary>
    internal static string ForNamed(string name) => $"model:{Normalize(name)}";

    /// <summary>
    /// 归一化：大小写、空白、分隔符的差异不该让同一块卡变成两个实例。
    /// 驱动更新之后名字里多个空格是常有的事。
    /// </summary>
    private static string Normalize(string value)
    {
        var builder = new StringBuilder(value.Length);
        var previousWasSeparator = false;
        foreach (var character in value.Trim())
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
                previousWasSeparator = false;
            }
            else if (!previousWasSeparator && builder.Length > 0)
            {
                builder.Append('-');
                previousWasSeparator = true;
            }
        }
        // 结尾可能留下一个分隔符，去掉。
        if (builder.Length > 0 && builder[^1] == '-')
        {
            builder.Length--;
        }
        return builder.Length == 0 ? "unknown" : builder.ToString();
    }
}
