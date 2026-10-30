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
    public async Task HostedAliasesPreserveRegistrationAndStopOrderWithoutOrderingSiblingStartup()
    {
        var events = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var host = Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => logging.ClearProviders())
            .ConfigureServices(services =>
            {
                services.AddBusinessBlockLifetime<ConsumerService>(BackendUids.TrustedAccessRequestOwner,
                    _ => new ConsumerService(events), constructService: true);

                // A data input does not control another hosted service's lifecycle.
                services.AddBusinessBlockLifetime<DependencyService>(BackendUids.RuntimeSpecializationCoordinator,
                    _ => new DependencyService(events), constructService: true);
                NativeBackendBlockTestSupport.AddFullTreeHostFixture(services);
            })
            .Build();

        Assert.Single(host.Services.GetServices<IHostedService>().OfType<BusinessBlockDependencyLifetime>());
        var hostedServices = host.Services.GetServices<IHostedService>().Where(static service => service
            is NonOwningHostedService<ConsumerService> or NonOwningHostedService<DependencyService>).ToArray();
        Assert.Collection(
            hostedServices,
            service => Assert.IsType<NonOwningHostedService<ConsumerService>>(service),
            service => Assert.IsType<NonOwningHostedService<DependencyService>>(service));

        var scheduler = host.Services.GetRequiredService<BusinessBlockScheduler>();
        var pendingRequests = scheduler.Authority.ClaimCell<bool>(BackendItems.TrustedRequestsPending);
        await pendingRequests.PublishAsync(true, BusinessBlockTestSupport.Reason());
        await host.StartAsync();
        await BusinessBlockTestSupport.State(scheduler, BackendUids.TrustedAccessRequestOwner, BusinessBlockState.Running);
        await BusinessBlockTestSupport.State(scheduler, BackendUids.RuntimeSpecializationCoordinator, BusinessBlockState.Running);
        await host.StopAsync();

        Assert.Equal(new[] { "consumer:start", "dependency:start" }.Order(), events.Take(2).Order());
        Assert.Equal(new[] { "dependency:stop", "consumer:stop" }, events.Skip(2));
        // The host waits for each StopAsync, not for the block's run tail that records the final state;
        // under a synchronization context that tail continues on the thread pool.
        var controller = host.Services.GetRequiredService<BusinessBlockScheduler>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (controller.Read().Blocks.Any(block => block.State != BusinessBlockState.Stopped))
            await Task.Delay(5, timeout.Token);
        var blocks = host.Services.GetRequiredService<IBusinessBlockQuery>()
            .Read().Blocks.ToDictionary(static block => block.Id);
        Assert.Equal(BusinessBlockState.Stopped, blocks[BackendUids.RuntimeSpecializationCoordinator].State);
        Assert.Equal(BusinessBlockState.Stopped, blocks[BackendUids.TrustedAccessRequestOwner].State);
        Assert.Equal(
            new[] { BackendUids.RuntimeSpecializationCoordinator },
            blocks[BackendUids.TrustedAccessRequestOwner].Inputs);
    }

    [Fact]
    public async Task HostAdmissionDoesNotWaitForServicePreparationAndFailureDoesNotStartItsDataInputs()
    {
        var events = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var failed = new ThrowingStartService();
        using var host = Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => logging.ClearProviders())
            .ConfigureServices(services =>
            {
                services.AddBusinessBlockLifetime<ThrowingStartService>(BackendUids.TrustedAccessRequestOwner, _ => failed, constructService: true);
                services.AddBusinessBlockLifetime<DependencyService>(BackendUids.RuntimeSpecializationCoordinator,
                    _ => new DependencyService(events), constructService: true);
                NativeBackendBlockTestSupport.AddFullTreeHostFixture(services);
            }).Build();
        var scheduler = host.Services.GetRequiredService<BusinessBlockScheduler>();
        var pendingRequests = scheduler.Authority.ClaimCell<bool>(BackendItems.TrustedRequestsPending);
        await pendingRequests.PublishAsync(true, BusinessBlockTestSupport.Reason());
        var block = host.Services.GetRequiredService<HostedBusinessBlockRegistry>()
            .GetOrAdd(BackendUids.TrustedAccessRequestOwner, () => failed);
        var session = block.PrepareStart(CancellationToken.None);
        var alias = host.Services.GetServices<IHostedService>().OfType<NonOwningHostedService<ThrowingStartService>>().Single();
        try
        {
            await alias.StartAsync(CancellationToken.None).WaitAsync(BusinessBlockTestSupport.TestTimeout);
            await failed.Entered.Task.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            Assert.False(session.Readiness.Task.IsCompleted);
            Assert.Equal(BusinessBlockState.Starting, scheduler.ReadState(BackendUids.TrustedAccessRequestOwner));
            Assert.Equal(BusinessBlockState.Stopped, scheduler.ReadState(BackendUids.RuntimeSpecializationCoordinator));
            Assert.Empty(events);
            // Withdraw permission before releasing preparation so the failed entry cannot restart.
            await host.Services.GetRequiredService<BackendHostPermitFacts>().SetAsync(BackendUids.TrustedAccessRequestOwner,
                false, BusinessBlockTestSupport.Reason());
            failed.Release.TrySetResult();
            var error = await Assert.ThrowsAsync<IOException>(() => session.Readiness.Task.WaitAsync(BusinessBlockTestSupport.TestTimeout));
            Assert.Same(failed.Failure, error);
            await BusinessBlockTestSupport.State(scheduler, BackendUids.TrustedAccessRequestOwner, BusinessBlockState.Stopped);
            Assert.Empty(events);
            var result = host.Services.GetRequiredService<BusinessBlockDiagnosticsRegistry>().Read(BackendUids.TrustedAccessRequestOwner).Result!;
            Assert.Equal(BusinessBlockFailureStage.Start, result.FailureStage);
        }
        finally
        {
            failed.Release.TrySetResult();
            try { await alias.StopAsync(CancellationToken.None).WaitAsync(BusinessBlockTestSupport.TestTimeout); }
            catch (IOException error) when (ReferenceEquals(error, failed.Failure)) { }
            await BusinessBlockTestSupport.StopAll(scheduler);
        }
    }

    private sealed class ThrowingStartService : IHostedService
    {
        internal TaskCompletionSource Entered { get; } = BusinessBlockTestSupport.Signal();
        internal TaskCompletionSource Release { get; } = BusinessBlockTestSupport.Signal();
        internal IOException Failure { get; } = new("startup failed");
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Release.Task;
            throw Failure;
        }
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
    [Fact]
    public void QueryDoesNotConstructOrStartHostedServices()
    {
        var constructed = 0;
        using var host = Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => logging.ClearProviders())
            .ConfigureServices(services =>
                services.AddBusinessBlockLifetime<LazyService>(BackendUids.TrustedAccessRequestOwner, _ =>
                {
                    constructed++;
                    return new LazyService();
                }, constructService: true))
            .ConfigureServices(NativeBackendBlockTestSupport.AddFullTreeHostFixture).Build();

        var query = host.Services.GetRequiredService<IBusinessBlockQuery>();
        var snapshot = query.Read();

        Assert.Equal(0, constructed);
        var block = Assert.Single(snapshot.Blocks, block => block.Id == BackendUids.TrustedAccessRequestOwner);
        Assert.Equal(BackendUids.TrustedAccessRequestOwner, block.Id);
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

    private abstract class RecordingService(System.Collections.Concurrent.ConcurrentQueue<string> events) : IHostedService
    {
        protected abstract string Label { get; }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            events.Enqueue($"{Label}:start");
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            events.Enqueue($"{Label}:stop");
            return Task.CompletedTask;
        }
    }

    private sealed class DependencyService(System.Collections.Concurrent.ConcurrentQueue<string> events) : RecordingService(events)
    {
        protected override string Label => "dependency";
    }

    private sealed class ConsumerService(System.Collections.Concurrent.ConcurrentQueue<string> events) : RecordingService(events)
    {
        protected override string Label => "consumer";
    }

    private sealed class LazyService : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
