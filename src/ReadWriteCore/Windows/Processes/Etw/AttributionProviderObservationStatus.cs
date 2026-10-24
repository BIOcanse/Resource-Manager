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

        return state.Trim() switch
        {
            NetworkAttributionProviderStates.Active
                or NetworkAttributionProviderStates.Running
                or NetworkAttributionProviderStates.Degraded => SamplingObservationStatus.Current,
            NetworkAttributionProviderStates.Starting
                or NetworkAttributionProviderStates.Idle
                or NetworkAttributionProviderStates.Warming => SamplingObservationStatus.Warming,
            NetworkAttributionProviderStates.NotRequested => SamplingObservationStatus.NotRequested,
            NetworkAttributionProviderStates.Unsupported => SamplingObservationStatus.Unsupported,
            _ => SamplingObservationStatus.Unavailable
        };
    }
}
