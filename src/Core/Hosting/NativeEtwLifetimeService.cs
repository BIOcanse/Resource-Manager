namespace ResourceManager.App.Hosting;

/// <summary>A plain host turn whose block owns the native ETW branch's lifetime.</summary>
internal sealed class NativeEtwLifetimeService : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
