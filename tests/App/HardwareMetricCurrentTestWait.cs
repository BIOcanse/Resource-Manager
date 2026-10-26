using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.Monitoring;

namespace Resource_Manager_APP.Tests;

internal static class HardwareMetricCurrentTestWait
{
    // A sampling-only lease may have a long period. Inspect its first committed
    // current value without turning dataset completion into snapshot delivery.
    internal static async Task<HardwareMetricSnapshot> ReadAsync(WindowsHardwareMetricSampler sampler,
        MetricSampleRequest request, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = sampler.ReadLatest(request);
            if (current is not null && request.Ids.All(id => current.Items.TryGetValue(id, out var value)
                    && value.Status == SamplingObservationStatus.Current && value.ObservedAt is not null))
                return current;
            await Task.Delay(10, cancellationToken);
        }
    }
}
