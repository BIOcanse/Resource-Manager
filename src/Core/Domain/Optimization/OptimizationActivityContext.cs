namespace ResourceManager.App.Domain.Optimization;

public static class OptimizationActivityContextKinds
{
    public const string Unknown = "Unknown";
    public const string Normal = "Normal";
    public const string Game = "Game";
    public const string HighPerformance = "HighPerformance";
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
            "Low",
            []);
    }
}
