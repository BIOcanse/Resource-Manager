namespace ResourceManager.App.Domain.GpuPlacement;

public static class GpuStartupPlacementDecisionKinds
{
    public const string PassThrough = "pass-through";
    public const string Inject = "inject";
}

public sealed record GpuStartupPlacementRequest(string ExecutablePath);

public sealed record GpuStartupPlacementDecision(
    string Decision,
    string Status,
    string Message,
    string ExecutablePath,
    string? SoftwareId,
    string? ProcessKey,
    string? StartupTargetGpu,
    string? AssignedPositionId,
    string? PolicyPath,
    string? TargetAdapterName,
    string? TargetLuid,
    IReadOnlyList<string> StartupProviders)
{
    public bool ShouldInject => Decision.Equals(
        GpuStartupPlacementDecisionKinds.Inject,
        StringComparison.OrdinalIgnoreCase);
}
