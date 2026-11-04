using Microsoft.Extensions.Configuration;

namespace ResourceManager.App.Infrastructure.Telemetry.Etw;

internal sealed record GpuAllocationCapturePlan(double LiveTrackingThresholdSeconds)
{
    internal const string ThresholdKey = "Monitoring:GpuAllocationLiveTrackingThresholdSeconds";
    // Candidate .16: a ~280 ms rundown costs 8–10 seconds of ~35 ms/s live tracking.
    internal const double DefaultLiveTrackingThresholdSeconds = 10;

    internal static GpuAllocationCapturePlan Compile(IConfiguration configuration)
    {
        var seconds = configuration.GetValue(ThresholdKey, DefaultLiveTrackingThresholdSeconds);
        if (!double.IsFinite(seconds) || seconds < 0)
            throw new ArgumentOutOfRangeException(ThresholdKey, "The GPU live tracking threshold must be a finite nonnegative number of seconds.");
        return new(seconds);
    }

    internal bool UseLive(TimeSpan shortestInterval) => shortestInterval.TotalSeconds < LiveTrackingThresholdSeconds;
}
