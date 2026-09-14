namespace ResourceManager.App.Domain.RuntimeSpecialization;

public sealed record CompiledDiagnosticsPlan(
    bool DebugModeEnabled,
    bool DebugLogEnabled,
    bool HostManagerSmartCoordinatorScoreOnlyEnabled,
    bool HostManagerSmartCoordinatorPerformanceLogEnabled)
{
    public static CompiledDiagnosticsPlan Default { get; } = new(
        false,
        false,
        false,
        false);
}
