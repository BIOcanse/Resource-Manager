namespace ResourceManager.App.Domain.Optimization;

public static class OptimizationActivityContextKinds
{
    public const string Unknown = "unknown";
    public const string Normal = "normal";
    public const string Game = "game";
    public const string HighPerformance = "high_performance";
}

public sealed record OptimizationActivityContext(
    string ContextKind,
    int? ForegroundProcessId,
    string? ForegroundProcessName,
    string? ForegroundExecutablePath,
    string? ForegroundSoftwareId,
    string? ForegroundSoftwareName,
    string Confidence,
    IReadOnlyList<string> Evidence)
{
    public static OptimizationActivityContext Unknown(int? foregroundProcessId = null)
    {
        return new OptimizationActivityContext(
            OptimizationActivityContextKinds.Unknown,
            foregroundProcessId,
            null,
            null,
            null,
            null,
            OptimizationConfidence.Low,
            []);
    }
}
