using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.ResourceBreakdown;

namespace ResourceManager.App.Infrastructure.Optimization;

public sealed partial class OptimizationProtectionPlacementService
{
    private static string? ResolveProcessDisabledReason(ProcessResourcePolicySnapshot process)
    {
        if (process.ProcessId <= 4)
        {
            return "Windows 核心进程不允许迁移 affinity。";
        }

        if (process.ProcessId == Environment.ProcessId)
        {
            return "Resource Manager 当前进程不参与保护避让写入。";
        }

        return null;
    }

    private static bool MatchesTarget(
        ProtectedOptimizationTarget target,
        ResourceSoftwareSegment segment)
    {
        return segment.SoftwareId.Equals(target.TargetKey, StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrWhiteSpace(target.SoftwareId)
                && segment.SoftwareId.Equals(target.SoftwareId, StringComparison.OrdinalIgnoreCase));
    }
}
