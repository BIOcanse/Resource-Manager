using ResourceManager.App.Infrastructure.Monitoring.AmdSmu;

namespace ResourceManager.App.Infrastructure.Monitoring;

/// <summary>
/// 真的走一遍 AMD SMU 读数，用来判断这条路通不通。
///
/// 三件事都成立才算通：RyzenSMU 模块在位、PM table 读得出来、这个表版本做过索引映射。
/// 任何一件不成立，说清楚是哪一件 —— 三种情况的处理完全不同：
/// 缺模块要补文件，缺映射要加索引表，读不出来才是驱动或权限问题。
/// </summary>
internal static class AmdSmuBridgeVerification
{
    internal readonly record struct Result(bool Verified, string Message);

    public static Result Verify(string? contentRootPath)
    {
        var root = string.IsNullOrWhiteSpace(contentRootPath)
            ? AppContext.BaseDirectory
            : contentRootPath;
        try
        {
            using var reader = new AmdSmuCpuSensorReader(root);
            var description = reader.DescribeSession();
            if (!description.SessionOpen)
            {
                return new Result(false, description.Message ?? "AMD SMU 会话打不开。");
            }
            if (!description.LayoutMapped)
            {
                return new Result(
                    false,
                    $"PM table {description.TableVersion} 读得出来，但这个版本还没做索引映射。");
            }
            return new Result(
                true,
                $"PM table {description.TableVersion} 已读取并完成索引映射。");
        }
        // 这里刻意兜住所有异常。这是一个**探针**：它会打开内核设备、加载模块、读 PM table，
        // 任何一步都可能以意料之外的方式失败，而探针失败只该让这条 Provider 显示不可用，
        // 绝不该把整个后端带崩 —— 先前它逃出去一次，后端直接起不来。
        catch (Exception error)
        {
            return new Result(false, $"AMD SMU 读数验证失败：{error.Message}");
        }
    }
}
