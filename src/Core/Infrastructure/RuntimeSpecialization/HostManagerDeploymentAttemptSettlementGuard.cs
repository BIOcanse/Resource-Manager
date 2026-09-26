using ResourceManager.App.Domain.RuntimeSpecialization;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization;

internal static class HostManagerDeploymentAttemptSettlementGuard
{
    public static HostManagerDeploymentAttemptSettlement RequireApplied(
        HostManagerDeploymentAttemptSettlement settlement,
        HostManagerModuleKind module)
        => Require(
            settlement,
            HostManagerDeploymentAttemptSettlement.Applied,
            module);

    public static HostManagerDeploymentAttemptSettlement RequireFailed(
        HostManagerDeploymentAttemptSettlement settlement,
        HostManagerModuleKind module)
        => Require(
            settlement,
            HostManagerDeploymentAttemptSettlement.Failed,
            module);

    private static HostManagerDeploymentAttemptSettlement Require(
        HostManagerDeploymentAttemptSettlement settlement,
        HostManagerDeploymentAttemptSettlement expected,
        HostManagerModuleKind module)
    {
        if (settlement != expected)
        {
            throw new InvalidOperationException(
                $"Host Manager deployment settlement for {module} was {settlement}; expected {expected}.");
        }

        return settlement;
    }
}
