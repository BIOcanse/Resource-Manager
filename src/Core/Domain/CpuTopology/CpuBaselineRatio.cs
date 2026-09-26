namespace ResourceManager.App.Domain.CpuTopology;

public sealed record CpuPerformanceOverrides(
    IReadOnlyDictionary<int, double> ScoresByCoreIndex,
    double? BaselineRatio);

public sealed record CpuBaselineRatioRequest(double Ratio);

public sealed record CpuBaselineRatioUpdateResult(
    CpuBaselineRatioSettings? Settings,
    double? SavedOverrideRatio,
    string RuntimeApplicationDisposition);

public sealed record CpuBaselineRatioSettings(
    string CpuName,
    double DefaultRatio,
    double? OverrideRatio,
    string DefaultSource,
    string? MatchedModel,
    string DatasetVersion)
{
    public double Ratio => OverrideRatio ?? DefaultRatio;

    public double Multiplier => 1 / Ratio;

    public double DefaultMultiplier => 1 / DefaultRatio;
}

public static class CpuBaselineRatio
{
    public static bool IsValid(double ratio) => double.IsFinite(ratio) && ratio > 0 && ratio <= 1
        && double.IsFinite(1 / ratio);

    public static void Validate(double ratio)
    {
        if (!IsValid(ratio))
        {
            throw new InvalidOperationException("CPU baseline ratio must be greater than zero and at most one, with a finite reciprocal.");
        }
    }
}
