using ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ResourceManager.App.Hosting;
using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;

namespace Resource_Manager_APP.Tests;

public sealed class NonOwningHostedServiceTests
{
    [Fact]
    public async Task AsynchronousWorkerFailureReachesTheHost()
    {
        var worker = new ProbeWorker();
        using var host = Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => logging.ClearProviders())
            .ConfigureServices(services =>
            {
                services.AddBusinessBlockHostedService<ProbeWorker>(_ => worker);
            })
            .Build();
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
        var worker = new ProbeWorker();
        var host = Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => logging.ClearProviders())
            .ConfigureServices(services =>
            {
                services.AddBusinessBlockHostedService<ProbeWorker>(_ => worker);
            })
            .Build();
        try
        {
            await host.StartAsync();
            await worker.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await host.StopAsync();
            Assert.True(worker.ExecuteTask!.IsCompleted);
            var facts = Assert.Single(host.Services.GetRequiredService<IBusinessBlockQuery>().Read().Blocks);
            Assert.Equal(BusinessBlockState.Stopped, facts.State);
            Assert.Equal(BusinessBlockTransitionOutcome.Stopped, facts.Outcome);
            Assert.Equal(0, worker.DisposeCount);
        }
        finally
        {
            host.Dispose();
        }
        Assert.Equal(1, worker.DisposeCount);
    }

    [Fact]
    public async Task ConvertedDirectHostedServiceIsConstructedDuringEnumerationAndDisposedOnce()
    {
        var created = 0;
        PlainService? service = null;
        var host = Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => logging.ClearProviders())
            .ConfigureServices(services => services.AddBusinessBlockHostedService<PlainService>(_ =>
            {
                created++;
                return service = new PlainService();
            }))
            .Build();
        try
        {
            var hostedServices = host.Services.GetServices<IHostedService>().Where(static service => service is not BusinessBlockDependencyLifetime).ToArray();
            Assert.Equal(1, created);
            var owner = Assert.IsType<NonOwningHostedService<PlainService>>(
                Assert.Single(hostedServices));
            var constructedService = service!;
            Assert.Same(constructedService, owner.Service);
            Assert.Equal(0, constructedService.StartCount);
            await host.StartAsync();
            Assert.Equal(1, created);
            Assert.Equal(1, constructedService.StartCount);
            await host.StopAsync();
            Assert.Equal(1, constructedService.StopCount);
            Assert.Equal(0, constructedService.DisposeCount);
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
