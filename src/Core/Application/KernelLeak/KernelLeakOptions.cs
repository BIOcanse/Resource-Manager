using System.Text.Json.Serialization;

namespace ResourceManager.App.Application.KernelLeak;

// Immutable compiled backend freedom point. Bounds reject oversized owner allocations.
public sealed record KernelLeakOptions(
    [property: JsonPropertyName("interval_milliseconds")] int IntervalMilliseconds,
    [property: JsonPropertyName("series_capacity")] int SeriesCapacity,
    [property: JsonPropertyName("maximum_holders")] int MaximumHolders,
    [property: JsonPropertyName("minimum_samples")] int MinimumSamples,
    [property: JsonPropertyName("minimum_observation_seconds")] int MinimumObservationSeconds,
    [property: JsonPropertyName("handle_floor")] int HandleFloor,
    [property: JsonPropertyName("minimum_growth")] int MinimumGrowth,
    [property: JsonPropertyName("edge_percentage")] int EdgePercentage,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("reported_item_capacity")] int ReportedItemCapacity)
{
    public const int MaximumReportedItemCapacity = 16384;

    public KernelLeakOptions Validate()
    {
        if (IntervalMilliseconds < 1000 || SeriesCapacity is < 2 or > 1024 || MaximumHolders is < 1 or > 16384
            || MinimumSamples < 2 || MinimumSamples > SeriesCapacity || MinimumObservationSeconds < 1
            || HandleFloor < 0 || MinimumGrowth < 1 || EdgePercentage is < 1 or > 50
            || ReportedItemCapacity is < 1 or > MaximumReportedItemCapacity
            || (long)SeriesCapacity * MaximumHolders > 524288
            || (long)(SeriesCapacity - 1) * IntervalMilliseconds < (long)MinimumObservationSeconds * 1000)
            throw new InvalidDataException("Invalid kernel leak observation limits.");
        return this;
    }
}
