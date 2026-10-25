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
    public async Task HostedAliasesStartDependenciesFirstAndStopInReverseOrder()
    {
        var events = new List<string>();
        using var host = Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => logging.ClearProviders())
            .ConfigureServices(services =>
            {
                services.AddSingleton(new BusinessBlockRegistration(
                    nameof(ConsumerService),
                    [nameof(DependencyService)],
                    provider => new HostedBusinessBlock(
                        () => provider.GetRequiredService<ConsumerService>())));
                services.AddBusinessBlockHostedService<ConsumerService>(
                    _ => new ConsumerService(events));

                // Deliberately register the prerequisite after its consumer. The controller
                // must preserve dependency-first startup and reverse dependency shutdown.
                services.AddSingleton(new BusinessBlockRegistration(
                    nameof(DependencyService),
                    [],
                    provider => new HostedBusinessBlock(
                        () => provider.GetRequiredService<DependencyService>())));
                services.AddBusinessBlockHostedService<DependencyService>(
                    _ => new DependencyService(events));
            })
            .Build();

        var hostedServices = host.Services.GetServices<IHostedService>().ToArray();
        Assert.Collection(
            hostedServices,
            service => Assert.IsType<NonOwningHostedService<ConsumerService>>(service),
            service => Assert.IsType<NonOwningHostedService<DependencyService>>(service));

        await host.StartAsync();
        await host.StopAsync();

        Assert.Equal(
            new[] { "dependency:start", "consumer:start", "consumer:stop", "dependency:stop" },
            events);
        var blocks = host.Services.GetRequiredService<IBusinessBlockQuery>()
            .Read().Blocks.ToDictionary(static block => block.Id, StringComparer.Ordinal);
        Assert.Equal(BusinessBlockState.Stopped, blocks[nameof(DependencyService)].State);
        Assert.Equal(BusinessBlockState.Stopped, blocks[nameof(ConsumerService)].State);
        Assert.Equal(
            new[] { nameof(DependencyService) },
            blocks[nameof(ConsumerService)].Dependencies);
    }

    [Fact]
    public void QueryDoesNotConstructOrStartLazyHostedServices()
    {
        var constructed = 0;
        using var host = Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => logging.ClearProviders())
            .ConfigureServices(services =>
                services.AddBusinessBlockHostedService<LazyService>(_ =>
                {
                    constructed++;
                    return new LazyService();
                }))
            .Build();

        var query = host.Services.GetRequiredService<IBusinessBlockQuery>();
        var snapshot = query.Read();
        _ = host.Services.GetServices<IHostedService>().ToArray();

        Assert.Equal(0, constructed);
        var block = Assert.Single(snapshot.Blocks);
        Assert.Equal(nameof(LazyService), block.Id);
        Assert.Equal(BusinessBlockState.Stopped, block.State);
        Assert.False(block.ExecutionOutstanding);
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
