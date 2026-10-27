using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ResourceManager.App.Endpoints;
using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Hosting;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;

namespace Resource_Manager_APP.Tests;

public sealed class BusinessBlockHostingTests
{
    [Fact]
    public async Task HostedAliasesKeepHostOrderRegardlessOfDataInputs()
    {
        var events = new List<string>();
        using var host = Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => logging.ClearProviders())
            .ConfigureServices(services =>
            {
                services.AddBusinessBlockHostedService<ConsumerService>(155, [156],
                    _ => new ConsumerService(events));

                // A data input does not control another hosted service's lifecycle.
                services.AddBusinessBlockHostedService<DependencyService>(156, [],
                    _ => new DependencyService(events));
            })
            .Build();

        Assert.Single(host.Services.GetServices<IHostedService>().OfType<BusinessBlockDependencyLifetime>());
        var hostedServices = host.Services.GetServices<IHostedService>().Where(static service => service
            is NonOwningHostedService<ConsumerService> or NonOwningHostedService<DependencyService>).ToArray();
        Assert.Collection(
            hostedServices,
            service => Assert.IsType<NonOwningHostedService<ConsumerService>>(service),
            service => Assert.IsType<NonOwningHostedService<DependencyService>>(service));

        await host.StartAsync();
        await host.StopAsync();

        Assert.Equal(
            new[] { "consumer:start", "dependency:start", "dependency:stop", "consumer:stop" },
            events);
        // The host waits for each StopAsync, not for the block's run tail that records the final state;
        // under a synchronization context that tail continues on the thread pool.
        var controller = host.Services.GetRequiredService<BusinessBlockScheduler>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (controller.Read().Blocks.Any(block => block.State != BusinessBlockState.Stopped))
            await Task.Delay(5, timeout.Token);
        var blocks = host.Services.GetRequiredService<IBusinessBlockQuery>()
            .Read().Blocks.ToDictionary(static block => block.Id);
        Assert.Equal(BusinessBlockState.Stopped, blocks[156].State);
        Assert.Equal(BusinessBlockState.Stopped, blocks[155].State);
        Assert.Equal(
            new[] { 156 },
            blocks[155].Inputs);
    }

    [Fact]
    public async Task FailedHostedStartupDoesNotStartItsDataInputs()
    {
        var events = new List<string>();
        using var host = Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => logging.ClearProviders())
            .ConfigureServices(services =>
            {
                services.AddBusinessBlockHostedService<ThrowingStartService>(120, [126]);
                services.AddBusinessBlockHostedService<DependencyService>(126, [], _ => new DependencyService(events));
            }).Build();
        await Assert.ThrowsAsync<IOException>(() => host.StartAsync().WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Empty(events);
        // The native ETW scope starts before this test's service; close the real Host
        // even when the later service fails to start.
        await host.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var scheduler = host.Services.GetRequiredService<BusinessBlockScheduler>();
        Assert.Empty(scheduler.Read().Demands);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (scheduler.Read().Blocks.Any(block => block.State != BusinessBlockState.Stopped))
            await Task.Delay(5, timeout.Token);
    }

    private sealed class ThrowingStartService : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => throw new IOException("startup failed");
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
    [Fact]
    public void QueryDoesNotConstructOrStartHostedServices()
    {
        var constructed = 0;
        using var host = Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => logging.ClearProviders())
            .ConfigureServices(services =>
                services.AddBusinessBlockHostedService<LazyService>(157, [], _ =>
                {
                    constructed++;
                    return new LazyService();
                }))
            .Build();

        var query = host.Services.GetRequiredService<IBusinessBlockQuery>();
        var snapshot = query.Read();

        Assert.Equal(0, constructed);
        var block = Assert.Single(snapshot.Blocks, block => block.Id == 157);
        Assert.Equal(157, block.Id);
        Assert.Equal(BusinessBlockState.Stopped, block.State);
    }

    [Fact]
    public void DebugEndpointJsonContractIncludesBusinessBlockSnapshot()
    {
        var options = new Microsoft.AspNetCore.Http.Json.JsonOptions().SerializerOptions;
        ResourceManagerEndpointRouteBuilderExtensions.ConfigureJsonSerializerOptions(options);

        var metadata = options.GetTypeInfo(typeof(BusinessBlocksSnapshot));

        Assert.Equal(typeof(BusinessBlocksSnapshot), metadata.Type);
    }

    private abstract class RecordingService(List<string> events) : IHostedService
    {
        protected abstract string Label { get; }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            events.Add($"{Label}:start");
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            events.Add($"{Label}:stop");
            return Task.CompletedTask;
        }
    }

    private sealed class DependencyService(List<string> events) : RecordingService(events)
    {
        protected override string Label => "dependency";
    }

    private sealed class ConsumerService(List<string> events) : RecordingService(events)
    {
        protected override string Label => "consumer";
    }

    private sealed class LazyService : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
