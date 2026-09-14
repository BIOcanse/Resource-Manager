using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.ProcessAttribution;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Infrastructure.Windows;
namespace ResourceManager.App.Infrastructure.Optimization;
internal sealed partial class OptimizationProcessPolicyEngine
{
    private static bool MatchesActionIdentity(
        ProcessPolicyOptimizationActionPreview action,
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
        ProcessPolicyOptimizationAppliedAction action,
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
        string expectedName,
        string? actualPath,
        string actualName)
    {
        var normalizedExpectedPath = NormalizePath(expectedPath);
        var normalizedActualPath = NormalizePath(actualPath);
        if (normalizedExpectedPath is not null && normalizedActualPath is not null)
        {
            return normalizedExpectedPath.Equals(normalizedActualPath, StringComparison.OrdinalIgnoreCase);
        }

        return expectedName.Equals(actualName, StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> ResolveMetricIds(OptimizationReportItem report)
    {
        if (report.Evidence.ResourceKind.Equals(OptimizationResourceKinds.Cpu, StringComparison.OrdinalIgnoreCase))
        {
            return [ResourceBreakdownMetricIds.CpuUsage];
        }

        if (report.Evidence.ResourceKind.Equals(OptimizationResourceKinds.Memory, StringComparison.OrdinalIgnoreCase))
        {
            return [ResourceBreakdownMetricIds.MemoryUsage];
        }

        if (TryParseIndexedResourceKind(report.Evidence.ResourceKind, OptimizationResourceKinds.Gpu, out var gpuIndex))
        {
            return [$"gpu.{gpuIndex}.usage"];
        }

        if (TryParseIndexedResourceKind(report.Evidence.ResourceKind, OptimizationResourceKinds.Vram, out var vramGpuIndex))
        {
            return [$"gpu.{vramGpuIndex}.vram"];
        }

        return [ResourceBreakdownMetricIds.CpuUsage];
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

    private static bool IsProcessPriorityAction(string kind)
    {
        return kind.Equals(OptimizationLevel1ActionKinds.NormalizeProcessPriority, StringComparison.OrdinalIgnoreCase)
            || kind.Equals(OptimizationLevel2ActionKinds.LowerProcessPriority, StringComparison.OrdinalIgnoreCase)
            || kind.Equals(OptimizationLevel3ActionKinds.DeepLowerProcessPriority, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPowerThrottlingAction(string kind)
    {
        return kind.Equals(OptimizationLevel1ActionKinds.EnableExecutionSpeedThrottling, StringComparison.OrdinalIgnoreCase)
            || kind.Equals(OptimizationLevel2ActionKinds.EnableExecutionSpeedThrottling, StringComparison.OrdinalIgnoreCase)
            || kind.Equals(OptimizationLevel3ActionKinds.EnableExecutionSpeedThrottling, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsMemoryPriorityAction(string kind)
    {
        return kind.Equals(OptimizationLevel1ActionKinds.LowerMemoryPriority, StringComparison.OrdinalIgnoreCase)
            || kind.Equals(OptimizationLevel2ActionKinds.LowerMemoryPriority, StringComparison.OrdinalIgnoreCase)
            || kind.Equals(OptimizationLevel3ActionKinds.LowerMemoryPriority, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAffinityAction(string kind)
    {
        return kind.Equals(OptimizationLevel2ActionKinds.MoveProcessAffinity, StringComparison.OrdinalIgnoreCase)
            || kind.Equals(OptimizationLevel3ActionKinds.MoveProcessAffinity, StringComparison.OrdinalIgnoreCase);
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
        if (IsProcessPriorityAction(kind))
        {
            return 0;
        }

        if (IsPowerThrottlingAction(kind))
        {
            return 1;
        }

        if (IsMemoryPriorityAction(kind))
        {
            return 2;
        }

        if (IsAffinityAction(kind))
        {
            return 3;
        }

        return 99;
    }

    private static bool TryParseMask(string value, out long mask)
    {
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out mask)
            && mask > 0;
    }

    private static int? TryGetForegroundProcessId()
    {
        try
        {
            var window = NativeMethods.GetForegroundWindow();
            if (window == IntPtr.Zero)
            {
                return null;
            }

            NativeMethods.GetWindowThreadProcessId(window, out var processId);
            return processId == 0 ? null : (int)processId;
        }
        catch
        {
            return null;
        }
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

    internal static string CreateStableId(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value.ToUpperInvariant()));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }
}
