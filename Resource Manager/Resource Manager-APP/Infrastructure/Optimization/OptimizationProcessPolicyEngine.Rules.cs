using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.ProcessAttribution;
using ResourceManager.App.Domain.Software;
namespace ResourceManager.App.Infrastructure.Optimization;
internal sealed partial class OptimizationProcessPolicyEngine
{
    private static string? ResolveTargetDisabledReason(
        OptimizationReportItem report,
        ProcessPolicyOptimizationProfile profile)
    {
        if (report.Target.TargetType != OptimizationReportTargetTypes.Software)
        {
            return $"{profile.DisplayName} 第一版只处理软件级目标。";
        }

        if (report.Type == OptimizationReportTypes.DiskPressure)
        {
            return "磁盘空间报告不属于 CPU/内存资源优化动作。";
        }

        if (report.Target.TargetKey.Equals(RuntimeAttributionIds.ResourceManagerSelf, StringComparison.OrdinalIgnoreCase))
        {
            return "Resource Manager 自身不走外部治理。";
        }

        return report.Target.SoftwareKind switch
        {
            SoftwareKinds.Adapted => "适配软件不套用不适配软件的外部治理等级。",
            SoftwareKinds.Game => "游戏不作为后台压制目标。",
            SoftwareKinds.HighPerformance => "高性能软件不作为后台压制目标。",
            SoftwareKinds.WindowsSystem or SoftwareKinds.WindowsComponent or SoftwareKinds.WindowsService => "Windows 系统目标不允许优化压制。",
            SoftwareKinds.Unattributed => "未归属进程不能直接应用软件级优化。",
            _ => null
        };
    }

    private static string? ResolveProcessDisabledReason(
        ProcessResourcePolicySnapshot process,
        int? foregroundProcessId)
    {
        if (process.ProcessId <= 4)
        {
            return "Windows 核心进程不允许压制。";
        }

        if (process.ProcessId == Environment.ProcessId)
        {
            return "Resource Manager 当前进程不允许压制。";
        }

        if (foregroundProcessId is not null && process.ProcessId == foregroundProcessId.Value)
        {
            return "该进程当前属于前台进程，暂不执行后台压制。";
        }

        if (!string.IsNullOrWhiteSpace(process.ExecutablePath)
            && IsUnderWindowsDirectory(process.ExecutablePath))
        {
            return "Windows 系统目录下的 exe 不允许压制。";
        }

        return null;
    }
}
