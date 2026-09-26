using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ResourceManager.App.Hosting;

namespace Resource_Manager_APP.Tests;

public sealed class NonOwningHostedServiceTests
{
    [Fact]
    public async Task AsynchronousWorkerFailureReachesTheHost()
    {
        using var host = Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => logging.ClearProviders())
            .ConfigureServices(services =>
            {
                services.AddSingleton<ProbeWorker>();
                services.AddHostedServiceAlias<ProbeWorker>();
            })
            .Build();
        var worker = host.Services.GetRequiredService<ProbeWorker>();
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = lifetime.ApplicationStopping.Register(() => stopping.TrySetResult());
        await host.StartAsync();
        try
        {
            await worker.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            worker.Fail.TrySetResult();
            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal("worker failure after startup", error.Message);
            await stopping.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task NormalStopCancelsTheWorkerAndOnlyTheContainerDisposesIt()
    {
        var host = Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => logging.ClearProviders())
            .ConfigureServices(services =>
            {
                services.AddSingleton<ProbeWorker>();
                services.AddHostedServiceAlias<ProbeWorker>();
            })
            .Build();
        var worker = host.Services.GetRequiredService<ProbeWorker>();
        try
        {
            await host.StartAsync();
            await worker.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await host.StopAsync();
            Assert.True(worker.ExecuteTask!.IsCompleted);
            Assert.Equal(0, worker.DisposeCount);
        }
        finally
        {
            host.Dispose();
        }
        Assert.Equal(1, worker.DisposeCount);
    }

    [Fact]
    public async Task PlainHostedServiceKeepsLazyStartAndSingleDisposal()
    {
        var created = 0;
        var host = Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => logging.ClearProviders())
            .ConfigureServices(services =>
            {
                services.AddSingleton(_ =>
                {
                    created++;
                    return new PlainService();
                });
                services.AddHostedServiceAlias<PlainService>();
            })
            .Build();
        PlainService? service = null;
        try
        {
            _ = host.Services.GetServices<IHostedService>().ToArray();
            Assert.Equal(0, created);
            await host.StartAsync();
            service = host.Services.GetRequiredService<PlainService>();
            Assert.Equal(1, created);
            Assert.Equal(1, service.StartCount);
            await host.StopAsync();
            Assert.Equal(1, service.StopCount);
            Assert.Equal(0, service.DisposeCount);
        }
        finally
        {
            host.Dispose();
        }
        Assert.Equal(1, service!.DisposeCount);
    }

    private sealed class PlainService : IHostedService, IDisposable
    {
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }
        public int DisposeCount { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            StartCount++;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            StopCount++;
            return Task.CompletedTask;
        }

        public void Dispose() => DisposeCount++;
    }

    private sealed class ProbeWorker : BackgroundService
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Fail { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int DisposeCount { get; private set; }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            Started.TrySetResult();
            await Fail.Task.WaitAsync(stoppingToken);
            throw new InvalidOperationException("worker failure after startup");
        }

        public override void Dispose()
        {
            DisposeCount++;
            base.Dispose();
        }
    }
}
