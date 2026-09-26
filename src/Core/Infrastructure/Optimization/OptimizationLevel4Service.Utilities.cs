using System.Security.Cryptography;
using System.Text;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.ProcessAttribution;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Infrastructure.Windows;
namespace ResourceManager.App.Infrastructure.Optimization;
public sealed partial class OptimizationLevel4Service
{
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

    private static bool MatchesActionIdentity(
        OptimizationLevel4ActionPreview action,
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
        OptimizationLevel4AppliedAction action,
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

    private static string CreateStableId(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value.ToUpperInvariant()));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }
}
