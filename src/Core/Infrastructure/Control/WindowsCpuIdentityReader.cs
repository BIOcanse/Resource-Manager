using System.Management;

namespace ResourceManager.App.Infrastructure.Control;

/// <summary>
/// 处理器的身份。
///
/// **现代 x86 没有出厂唯一序列号。** Intel 的 PSN 从 Pentium III 之后就取消了，
/// AMD 那边也一样；本机实测 <c>Win32_Processor.SerialNumber</c> 是 "Unknown"、
/// <c>UniqueId</c> 为空。所以按规则退到型号 —— 反正处理器是整台机器里
/// 最不容易被换掉的那个。
///
/// 型号键用 <c>ProcessorId</c>（CPUID 签名加特性位）而不是那个营销名字：
/// 它精确到 family / model / stepping，而且不会因为 BIOS 或驱动更新
/// 把名字里的空格改一改就变成另一个实例。读不到才退回名字。
///
/// 读一次就够，处理器不会在运行期间换掉。
/// </summary>
internal sealed class WindowsCpuIdentityReader(ILogger? logger = null)
{
    /// <summary>WMI 在没有真值时填的那些占位词，不能当标识用。</summary>
    private static readonly string[] Placeholders =
        ["unknown", "none", "n/a", "to be filled by o.e.m.", "default string", "not specified"];

    private readonly object gate = new();
    private string? cached;

    /// <summary>
    /// 这颗处理器的身份键。有真的唯一序列号就用它，否则用 CPUID 签名，
    /// 再不行退回名字。
    /// </summary>
    internal string Resolve(string cpuName)
    {
        lock (gate)
        {
            return cached ??= ReadIdentity(cpuName);
        }
    }

    private string ReadIdentity(string cpuName)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessorId, SerialNumber, UniqueId FROM Win32_Processor");
            foreach (var row in searcher.Get().Cast<ManagementObject>())
            {
                using (row)
                {
                    // 真的唯一序列号优先。绝大多数机器上这一条读不到。
                    if (Meaningful(row["SerialNumber"]) is { } serial)
                    {
                        return $"uid:{Normalize(serial)}";
                    }
                    if (Meaningful(row["UniqueId"]) is { } unique)
                    {
                        return $"uid:{Normalize(unique)}";
                    }
                    if (Meaningful(row["ProcessorId"]) is { } signature)
                    {
                        // 型号级：同款同步进的芯片是同一个值。**不标成唯一。**
                        return $"model:{Normalize(signature)}";
                    }
                }
            }
        }
        catch (ManagementException error)
        {
            logger?.LogInformation(error, "读不到处理器标识，退回按名字。");
        }
        return ControlInstanceIdentity.ForNamed(cpuName);
    }

    private static string? Meaningful(object? value)
    {
        var text = value?.ToString()?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }
        return Placeholders.Contains(text, StringComparer.OrdinalIgnoreCase) ? null : text;
    }

    private static string Normalize(string value)
        => new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
