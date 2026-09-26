using System.Management;
using System.Runtime.Versioning;
using ResourceManager.App.Application.Control;
using ResourceManager.App.Domain.Control;

namespace ResourceManager.App.Infrastructure.Control;

/// <summary>
/// 这是台笔记本还是台式机。
///
/// 问的是 <c>Win32_SystemEnclosure.ChassisTypes</c> —— 这件事 Windows 自己就有
/// 权威答案（值来自 SMBIOS 的机箱类型），**不用从"有没有电池""风扇通道叫什么名字"
/// 这类旁证去推**。旁证会在奇怪的机器上翻车：有的一体机报便携，
/// 有的准系统连电池都没有。
///
/// 读一次记住：机箱类型不会在运行期间变。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsChassisKindReader : IControlDetectionCache
{
    private readonly object gate = new();
    private string? cached;

    /// <summary>忘掉记下的机箱类型，下次重新问 SMBIOS。</summary>
    public void ResetDetection()
    {
        lock (gate)
        {
            cached = null;
        }
    }

    public string Read()
    {
        lock (gate)
        {
            cached ??= Query();
            return cached;
        }
    }

    private static string Query()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT ChassisTypes FROM Win32_SystemEnclosure");
            foreach (var row in searcher.Get().Cast<ManagementObject>())
            {
                using (row)
                {
                    if (row["ChassisTypes"] is not ushort[] types)
                    {
                        continue;
                    }
                    foreach (var type in types)
                    {
                        if (Classify(type) is { } kind)
                        {
                            return kind;
                        }
                    }
                }
            }
        }
        catch (ManagementException)
        {
            // 问不到就是不知道。不猜 —— 猜错了整页的词条都跟着错。
        }
        return ControlChassisKinds.Unknown;
    }

    /// <summary>
    /// SMBIOS 机箱类型到我们这两类的映射。
    ///
    /// 只认有把握的那些：便携/笔记本/亚笔记本/手持一类是便携，
    /// 台式/塔式/机架一类是固定式。认不出的返回 null，交给下一个值 ——
    /// 一台机器可以报多个机箱类型。
    /// </summary>
    private static string? Classify(ushort type) => type switch
    {
        // 8 Portable、9 Laptop、10 Notebook、11 Hand Held、14 Sub Notebook、
        // 30 Tablet、31 Convertible、32 Detachable
        8 or 9 or 10 or 11 or 14 or 30 or 31 or 32 => ControlChassisKinds.Portable,
        // 3 Desktop、4 Low Profile Desktop、5 Pizza Box、6 Mini Tower、7 Tower、
        // 15 Space-saving、16 Lunch Box、17 Main Server Chassis、23 Rack Mount
        3 or 4 or 5 or 6 or 7 or 15 or 16 or 17 or 23 => ControlChassisKinds.Fixed,
        _ => null
    };
}
