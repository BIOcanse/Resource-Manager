namespace ResourceManager.App.Domain.Optimization;

public sealed record GpuPerformanceScoreOverrideItem(
    string GpuId,
    double? PerformanceScore);

public sealed record GpuPerformanceScoreOverrideRequest(
    IReadOnlyList<GpuPerformanceScoreOverrideItem> Scores);

public sealed record GpuPerformanceScoreOverrideResult(
    IReadOnlyDictionary<string, double> ScoresByGpuId,
    DateTimeOffset UpdatedAt,
    string StoragePath);

public sealed record GpuPerformanceScoreSnapshot(
    DateTimeOffset CapturedAt,
    IReadOnlyList<GpuPerformanceScoreItem> Gpus,
    string StoragePath);

public sealed record GpuPerformanceScoreItem(
    string GpuId,
    int Index,
    string Name,
    double RasterPerformanceScore,
    double DefaultPerformanceScore,
    double PerformanceScore,
    bool HasPerformanceOverride,
    bool IsIntegrated,
    string Source,
    string? MatchedPreset,
    bool IsPresetMatch)
{
    public bool? HasDedicatedMemoryMetrics { get; init; }
}
