namespace ResourceManager.App.Domain.Overlay;

public static class PerformanceOverlayMetricIds
{
    public const string Fps = "target.fps";
    public const string FrameTime = "target.frameTime";
    public const string OnePercentLow = "target.onePercentLow";
    public const string PointOnePercentLow = "target.pointOnePercentLow";
    public const string Cpu = "target.cpu";
    public const string Gpu = "target.gpu";
    public const string Memory = "target.memory";
    public const string Vram = "target.vram";

    public static readonly IReadOnlyList<string> Defaults =
        [Fps, OnePercentLow, FrameTime, Cpu, Gpu];

    public static readonly IReadOnlyList<string> Known =
        [Fps, FrameTime, OnePercentLow, PointOnePercentLow, Cpu, Gpu, Memory, Vram];
}

public sealed record PerformanceOverlaySettings
{
    public string SoftwareId { get; init; } = string.Empty;
    public bool Enabled { get; init; }
    public string Mode { get; init; } = "external";
    public IReadOnlyList<string> Metrics { get; init; } = PerformanceOverlayMetricIds.Defaults;

    public static PerformanceOverlaySettings Default(string softwareId) =>
        new() { SoftwareId = softwareId };

    public static PerformanceOverlaySettings Normalize(
        PerformanceOverlaySettings? settings,
        string softwareId)
    {
        var id = softwareId?.Trim() ?? string.Empty;
        if (id.Length == 0)
        {
            throw new ArgumentException("Software ID is required.", nameof(softwareId));
        }

        settings ??= Default(id);
        var metrics = (settings.Metrics ?? PerformanceOverlayMetricIds.Defaults)
            .Where(static metric => !string.IsNullOrWhiteSpace(metric))
            .Select(static metric => metric.Trim())
            .Where(static metric => metric.Length <= 128)
            .Select(static metric => PerformanceOverlayMetricIds.Known.FirstOrDefault(
                known => known.Equals(metric, StringComparison.OrdinalIgnoreCase)) ?? metric)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(64)
            .ToArray();
        return settings with
        {
            SoftwareId = id,
            Mode = settings.Mode?.Equals("injected", StringComparison.OrdinalIgnoreCase) == true
                ? "injected" : "external",
            Metrics = metrics
        };
    }

}

public sealed record PerformanceOverlaySettingsDocument(
    int Version,
    IReadOnlyList<PerformanceOverlaySettings> Software)
{
    public static PerformanceOverlaySettingsDocument Empty { get; } = new(2, []);
}
