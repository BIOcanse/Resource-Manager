using System.Security.Cryptography;
using System.Text;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.ProcessAttribution;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Domain.Software;
namespace ResourceManager.App.Infrastructure.Optimization;
public sealed partial class OptimizationA1Service
{
    private static string? ResolveTargetDisabledReason(
        OptimizationReportTarget target,
        string reportType)
    {
        if (target.TargetType != OptimizationReportTargetTypes.Software)
        {
            return "A1 增强第一版只处理软件级目标。";
        }

        if (reportType == OptimizationReportTypes.DiskPressure)
        {
            return "磁盘空间报告不属于 A1 增强动作。";
        }

        if (target.TargetKey.Equals(RuntimeAttributionIds.ResourceManagerSelf, StringComparison.OrdinalIgnoreCase))
        {
            return "Resource Manager 自身不走外部治理。";
        }

        if (IsSteamAppTarget(target))
        {
            return null;
        }

        return target.SoftwareKind switch
        {
            SoftwareKinds.Game or SoftwareKinds.HighPerformance => null,
            SoftwareKinds.Adapted => "适配软件不套用不适配软件的外部增强等级。",
            SoftwareKinds.WindowsSystem or SoftwareKinds.WindowsComponent or SoftwareKinds.WindowsService => "Windows 系统目标不允许 A1 增强。",
            SoftwareKinds.Unattributed => "未归属进程不能直接应用软件级增强。",
            _ => "A1 增强只面向游戏或高性能软件。"
        };
    }

    private static string? ResolveProcessDisabledReason(ProcessResourcePolicySnapshot process)
    {
        if (process.ProcessId <= 4)
        {
            return "Windows 核心进程不允许增强。";
        }

        if (process.ProcessId == Environment.ProcessId)
        {
            return "Resource Manager 当前进程不允许增强。";
        }

        if (!string.IsNullOrWhiteSpace(process.ExecutablePath)
            && IsUnderWindowsDirectory(process.ExecutablePath))
        {
            return "Windows 系统目录下的 exe 不允许增强。";
        }

        return null;
    }

    private static IReadOnlyList<string> ResolveMetricIds(OptimizationReportItem report)
    {
        var metricIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ResourceBreakdownMetricIds.CpuUsage,
            ResourceBreakdownMetricIds.MemoryUsage
        };
        if (TryParseIndexedResourceKind(report.Evidence.ResourceKind, OptimizationResourceKinds.Gpu, out var gpuIndex))
        {
            metricIds.Add($"gpu.{gpuIndex}.usage");
        }

        if (TryParseIndexedResourceKind(report.Evidence.ResourceKind, OptimizationResourceKinds.Vram, out var vramGpuIndex))
        {
            metricIds.Add($"gpu.{vramGpuIndex}.vram");
        }

        return metricIds.ToArray();
    }

    private static bool TryParseIndexedResourceKind(
        string resourceKind,
        string prefix,
        out int index)
    {
        index = 0;
        if (!resourceKind.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return int.TryParse(resourceKind[prefix.Length..], out index);
    }

    private static bool MatchesTarget(
        OptimizationReportTarget target,
        ResourceSoftwareSegment segment)
    {
        return segment.SoftwareId.Equals(target.TargetKey, StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrWhiteSpace(target.SoftwareId)
                && segment.SoftwareId.Equals(target.SoftwareId, StringComparison.OrdinalIgnoreCase));
    }

    private static bool MatchesProcessFilter(
        OptimizationReportTarget target,
        int processId)
    {
        return target.ProcessIds.Count == 0
            || target.ProcessIds.Contains(processId);
    }

    private static bool IsSteamAppTarget(OptimizationReportTarget target)
    {
        return target.TargetKey.Contains("steam-app-", StringComparison.OrdinalIgnoreCase);
    }

    private static int PriorityRank(string priorityClass)
    {
        return priorityClass switch
        {
            "Idle" => 0,
            "BelowNormal" => 1,
            "Normal" => 2,
            "AboveNormal" => 3,
            "High" => 4,
            "RealTime" => 5,
            _ => 2
        };
    }

    private static int ActionOrder(string kind)
    {
        return kind switch
        {
            OptimizationA1ActionKinds.RaiseProcessPriority => 0,
            OptimizationA1ActionKinds.DisableExecutionSpeedThrottling => 1,
            _ => 99
        };
    }

    private static bool MatchesActionIdentity(
        OptimizationA1ActionPreview action,
        ProcessResourcePolicySnapshot current)
    {
        if (action.ProcessStartedAt is not null
            && current.StartedAt is not null
            && Math.Abs((current.StartedAt.Value - action.ProcessStartedAt.Value).TotalSeconds) > 1)
        {
            return false;
        }

        return PathsMatchOrNamesMatch(action.ExecutablePath, action.ProcessName, current.ExecutablePath, current.ProcessName);
    }

    private static bool MatchesRecordedIdentity(
        OptimizationA1AppliedAction action,
        ProcessResourcePolicySnapshot current)
    {
        if (action.ProcessStartedAt is not null
            && current.StartedAt is not null
            && Math.Abs((current.StartedAt.Value - action.ProcessStartedAt.Value).TotalSeconds) > 1)
        {
            return false;
        }

        return PathsMatchOrNamesMatch(action.ExecutablePath, action.ProcessName, current.ExecutablePath, current.ProcessName);
    }

    private static bool PathsMatchOrNamesMatch(
        string? expectedPath,
        string? expectedName,
        string? actualPath,
        string actualName)
    {
        var normalizedExpectedPath = NormalizePath(expectedPath);
        var normalizedActualPath = NormalizePath(actualPath);
        if (normalizedExpectedPath is not null && normalizedActualPath is not null)
        {
            return normalizedExpectedPath.Equals(normalizedActualPath, StringComparison.OrdinalIgnoreCase);
        }

        return !string.IsNullOrWhiteSpace(expectedName)
            && expectedName.Equals(actualName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUnderWindowsDirectory(string path)
    {
        var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var normalizedPath = NormalizePath(path);
        var normalizedWindows = NormalizePath(windowsDirectory);
        return !string.IsNullOrWhiteSpace(normalizedPath)
            && !string.IsNullOrWhiteSpace(normalizedWindows)
            && (normalizedPath.Equals(normalizedWindows, StringComparison.OrdinalIgnoreCase)
                || normalizedPath.StartsWith(normalizedWindows + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }

    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static string CreateStableId(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value.ToUpperInvariant()));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }
}
