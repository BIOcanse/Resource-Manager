using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.ProcessAttribution;
using ResourceManager.App.Domain.Software;

namespace ResourceManager.App.Infrastructure.Optimization;

public sealed partial class OptimizationLevel4Service
{
    private static string? ResolveTargetDisabledReason(OptimizationReportItem report)
    {
        if (report.Target.TargetType != OptimizationReportTargetTypes.Software)
        {
            return "4 级冻结第一版只处理软件级目标。";
        }

        if (report.Type == OptimizationReportTypes.DiskPressure)
        {
            return "磁盘空间报告不属于 4 级冻结动作。";
        }

        if (report.Target.TargetKey.Equals(RuntimeAttributionIds.ResourceManagerSelf, StringComparison.OrdinalIgnoreCase))
        {
            return "Resource Manager 自身不走外部治理。";
        }

        if (report.Target.TargetKey.Contains("steam-app-", StringComparison.OrdinalIgnoreCase))
        {
            return "Steam AppID 目标需先完成游戏/高性能分类，暂不允许 4 级冻结。";
        }

        return report.Target.SoftwareKind switch
        {
            SoftwareKinds.Adapted => "适配软件不套用不适配软件的外部治理等级。",
            SoftwareKinds.Game => "游戏不作为 4 级冻结目标。",
            SoftwareKinds.HighPerformance => "高性能软件不作为 4 级冻结目标。",
            SoftwareKinds.WindowsSystem or SoftwareKinds.WindowsComponent or SoftwareKinds.WindowsService => "Windows 系统目标不允许 4 级冻结。",
            SoftwareKinds.Unattributed => "未归属进程不能直接应用软件级冻结。",
            _ => null
        };
    }

    private static string? ResolveProcessDisabledReason(
        ProcessFreezeTarget target,
        int? foregroundProcessId)
    {
        var process = target.Process;
        if (process.ProcessId <= 4)
        {
            return "Windows 核心进程不允许冻结。";
        }

        if (process.ProcessId == Environment.ProcessId)
        {
            return "Resource Manager 当前进程不允许冻结。";
        }

        if (foregroundProcessId is not null && process.ProcessId == foregroundProcessId.Value)
        {
            return "该进程当前属于前台进程，暂不执行冻结。";
        }

        if (!string.IsNullOrWhiteSpace(process.ExecutablePath)
            && IsUnderWindowsDirectory(process.ExecutablePath))
        {
            return "Windows 系统目录下的 exe 不允许冻结。";
        }

        return null;
    }

    private static bool IsFreezeRestoreComplete(
        ProcessFreezeRestoreResult result,
        int recordedThreadCount)
    {
        if (recordedThreadCount == 0)
        {
            return true;
        }

        if (result.Threads.Count == 0)
        {
            return false;
        }

        return result.Threads.All(static thread =>
            thread.Succeeded
            || thread.Message.StartsWith("线程不可打开", StringComparison.OrdinalIgnoreCase));
    }
}
