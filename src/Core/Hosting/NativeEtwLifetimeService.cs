using ResourceManager.App.Infrastructure.Telemetry.Etw;

namespace ResourceManager.App.Hosting;

/// <summary>A plain host turn whose block owns the native ETW branch's lifetime.</summary>
internal sealed class NativeEtwLifetimeService(IServiceProvider services) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Preparation is serial work in the parent turn, before it reports Running.
        // Republishing reads each resource owner's retained value, including failure.
        if (services.GetService<PresentFrameEtwSource>() is { } present)
            await present.PublishAvailabilityAsync().ConfigureAwait(false);
        if (services.GetService<GpuAllocationCollector>() is { } allocations)
            await allocations.PublishAvailabilityAsync().ConfigureAwait(false);
        if (services.GetService<DxgkrnlVidMmEtwTelemetryZone>() is { } vidMm)
            await vidMm.PublishAvailabilityAsync().ConfigureAwait(false);
    }
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
