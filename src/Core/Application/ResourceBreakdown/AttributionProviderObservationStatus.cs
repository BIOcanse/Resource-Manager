using ResourceManager.App.Domain.Metrics;

namespace ResourceManager.App.Application.ResourceBreakdown;

public static class AttributionProviderObservationStatus
{
    public static SamplingObservationStatus Resolve(string? state)
    {
        if (string.IsNullOrWhiteSpace(state))
        {
            return SamplingObservationStatus.Unavailable;
        }

        return state.Trim().ToUpperInvariant() switch
        {
            "ACTIVE" or "RUNNING" or "DEGRADED" => SamplingObservationStatus.Current,
            "STARTING" or "IDLE" or "WARMING" => SamplingObservationStatus.Warming,
            "NOTREQUESTED" => SamplingObservationStatus.NotRequested,
            "UNSUPPORTED" => SamplingObservationStatus.Unsupported,
            _ => SamplingObservationStatus.Unavailable
        };
    }
}
