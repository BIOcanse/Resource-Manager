using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Hosting;
using ResourceManager.App.Hosting.StartupCapabilities;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Infrastructure.Telemetry.Etw;
using ResourceManager.App.StateItems;

namespace Resource_Manager_APP.Tests;

public sealed class BackendHostedAliasOrderTests
{
    [Theory]
    [InlineData(1u)]
    [InlineData(2u)]
    [InlineData(3u)]
    public void ProductionHostedAliasesStartParentsBeforeChildren(uint profile)
    {
        var services = ProductionServices(profile);
        var registrations = Declarations(services);
        Assert.DoesNotContain(registrations, row => BackendExpectedProductionTree.Wrappers.Contains(row.Id));
        var aliases = HostedAliases(services, registrations);
        Assert.Equal(registrations.Where(row => row.HostedServiceType is not null).Select(row => row.Id).Order(),
            aliases.Select(row => row.Id).Order());
        var positions = aliases.Select((alias, index) => (alias.Id, index)).ToDictionary(row => row.Id, row => row.index);
        var names = registrations.Where(row => row.HostedServiceType is not null)
            .ToDictionary(row => row.Id, row => row.HostedServiceType!.Name);
        foreach (var (child, parent) in BackendExpectedProductionTree.Parents(profile))
        {
            if (positions.TryGetValue(child, out var childIndex) && positions.TryGetValue(parent, out var parentIndex))
                Assert.True(parentIndex < childIndex,
                    $"Profile {profile}: hosted parent {names[parent]} ({parent}) at {parentIndex} must precede child {names[child]} ({child}) at {childIndex}.");
        }
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(2u)]
    public async Task GenericHostStartsFanPairAndDrainsFeedBeforeCoreUsingProductionAliasOrder(uint profile)
    {
        var productionServices = ProductionServices(profile);
        var production = Declarations(productionServices);
        var fanOrder = HostedAliases(productionServices, production)
            .Where(row => row.Id is BlockUids.FanControlCoreLifetimeService or BlockUids.FanCurveTemperatureFeed).ToArray();
        Assert.Equal(2, fanOrder.Length);
        using var core = new CoreProbe();
        using var feed = new FeedProbe();
        await using var native = NativeBackendBlockTestSupport.CreateControl(production, profile);
        var authority = NativeBackendBlockTestSupport.CreateAuthority(native);
        var host = Host.CreateDefaultBuilder().ConfigureLogging(logging => logging.ClearProviders())
            .ConfigureServices(services =>
            {
                services.AddSingleton(native);
                services.AddSingleton(authority);
                services.AddSingleton(new NativeEtwSessionAvailability(authority));
                services.AddSingleton<BusinessBlockDiagnosticsRegistry>();
                services.AddSingleton<HostedBusinessBlockRegistry>();
                services.AddSingleton<HostedRunResultFacts>();
                services.AddSingleton<BackendHostPermitFacts>();
                services.AddSingleton<BusinessBlockScheduler>(provider =>
                {
                    _ = provider.GetRequiredService<BackendHostPermitFacts>();
                    return new(provider, provider.GetServices<BusinessBlockRegistration>(), native, authority);
                });
                services.AddSingleton<IHostedService, BusinessBlockDependencyLifetime>();
                services.AddSingleton(core);
                services.AddSingleton(feed);
                foreach (var registration in production)
                {
                    var replacement = registration.Id switch
                    {
                        BlockUids.FanControlCoreLifetimeService => WithProbe<CoreProbe>(registration, static _ => null),
                        BlockUids.FanCurveTemperatureFeed => WithProbe<FeedProbe>(registration, static service => service.ExecuteTask),
                        _ => registration
                    };
                    services.AddSingleton(replacement with
                    {
                        Create = provider => registration.Id switch
                        {
                            BlockUids.FanControlCoreLifetimeService => provider.GetRequiredService<HostedBusinessBlockRegistry>()
                                .GetOrAdd(registration.Id, () => core),
                            BlockUids.FanCurveTemperatureFeed => provider.GetRequiredService<HostedBusinessBlockRegistry>()
                                .GetOrAdd(registration.Id, () => feed),
                            _ => new BusinessBlockTestSupport.Probe()
                        }
                    });
                }
                // Copy the order selected by production, substituting only harmless underlying services.
                foreach (var alias in fanOrder)
                {
                    services.AddSingleton(new HostedBusinessBlockAlias(alias.Id));
                    if (alias.Id == BlockUids.FanControlCoreLifetimeService)
                        services.AddSingleton<IHostedService>(provider => new NonOwningHostedService<CoreProbe>(provider, alias.Id, true));
                    else
                        services.AddSingleton<IHostedService>(provider => new NonOwningHostedService<FeedProbe>(provider, alias.Id, true));
                }
            }).Build();
        Task? stopping = null;
        Exception? bodyFailure = null;
        var phase = "host-start";
        try
        {
            var options = host.Services.GetRequiredService<IOptions<HostOptions>>().Value;
            Assert.False(options.ServicesStartConcurrently);
            Assert.False(options.ServicesStopConcurrently);
            var scheduler = host.Services.GetRequiredService<BusinessBlockScheduler>();
            Assert.All(BackendExpectedProductionTree.Wrappers, uid => Assert.Contains(uid, scheduler.Graph.BlockIds));
            Assert.Equal(BlockUids.FanControlCoreLifetimeService,
                scheduler.Graph.Nodes.Single(row => row.Id == BlockUids.FanCurveTemperatureFeed).ParentId);
            // These fake services own the same exact pending-command fact as production.
            await authority.ClaimCell<bool>(StateItemUids.FanCommandPending).PublishAsync(true,
                new(BusinessBlockRequestKind.Configuration, "fan-pair-fixture", "fan-command-pending", 0));
            await authority.ClaimCell<bool>(StateItemUids.FanCoreAvailable).PublishAsync(true,
                BusinessBlockTestSupport.Reason());
            await authority.ClaimCell<bool>(StateItemUids.SoftwareCurveRunNeeded).PublishAsync(true,
                BusinessBlockTestSupport.Reason());
            using var startupLimit = new CancellationTokenSource(BusinessBlockTestSupport.TestTimeout);
            await host.StartAsync(startupLimit.Token).WaitAsync(BusinessBlockTestSupport.TestTimeout);
            phase = "fan-pair-started";
            await Task.WhenAll(core.Started.Task, feed.Started.Task).WaitAsync(BusinessBlockTestSupport.TestTimeout);
            Assert.Equal(BusinessBlockState.Running, scheduler.ReadState(BlockUids.FanControlCoreLifetimeService));
            Assert.Equal(BusinessBlockState.Running, scheduler.ReadState(BlockUids.FanCurveTemperatureFeed));
            stopping = host.StopAsync();
            phase = "feed-stop-notified";
            await feed.StopNotified.Task.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            Assert.False(stopping.IsCompleted);
            Assert.False(feed.Returned.Task.IsCompleted);
            Assert.False(core.StopNotified.Task.IsCompleted);
            Assert.False(core.Returned.Task.IsCompleted);
            feed.ReleaseCleanup.TrySetResult();
            phase = "host-stopped";
            await stopping.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            Assert.True(feed.Returned.Task.IsCompletedSuccessfully);
            Assert.True(core.Returned.Task.IsCompletedSuccessfully);
            var history = scheduler.Read().History;
            var feedStopped = Assert.Single(history, row => row.BlockId == BlockUids.FanCurveTemperatureFeed
                && row.To == BusinessBlockState.Stopped).Sequence;
            var coreStopping = Assert.Single(history, row => row.BlockId == BlockUids.FanControlCoreLifetimeService
                && row.To == BusinessBlockState.Stopping).Sequence;
            Assert.True(feedStopped < coreStopping, "FanCurveTemperatureFeed must return before FanControlCoreLifetimeService receives stop.");
        }
        catch (Exception error)
        {
            var feedPermit = authority.Read<bool>(new(StateItemUids.HostStartPermitted, BlockUids.FanCurveTemperatureFeed));
            var feedEnded = authority.Read<bool>(new(StateItemUids.HostedRunEnded, BlockUids.FanCurveTemperatureFeed));
            bodyFailure = new InvalidOperationException($"Fan pair failed during {phase}; core started={core.Started.Task.IsCompleted}, feed started={feed.Started.Task.IsCompleted}, feed notified={feed.StopNotified.Task.IsCompleted}, core notified={core.StopNotified.Task.IsCompleted}; feed permit={feedPermit.GetValueOrDefault(false)}, feed ended={feedEnded.GetValueOrDefault(false)}.", error);
            throw bodyFailure;
        }
        finally
        {
            feed.ReleaseCleanup.TrySetResult();
            using var shutdownLimit = new CancellationTokenSource(BusinessBlockTestSupport.TestTimeout);
            try { await (stopping ?? host.StopAsync(shutdownLimit.Token)).WaitAsync(BusinessBlockTestSupport.TestTimeout); }
            catch (Exception cleanupFailure) when (bodyFailure is not null)
            {
                throw new AggregateException("Fan pair body and host cleanup both failed.", bodyFailure, cleanupFailure);
            }
            await ((IAsyncDisposable)host).DisposeAsync();
        }
    }

    private static BusinessBlockRegistration WithProbe<TService>(BusinessBlockRegistration registration,
        Func<TService, Task?> execution) where TService : class, IHostedService
        => registration with
        {
            HostedServiceType = typeof(TService),
            HostedExecution = registration.HostedExecution! with
            {
                ReadTask = service => execution((TService)service)
            }
        };

    private static ServiceCollection ProductionServices(uint profile)
    {
        var services = new ServiceCollection();
        ResourceManager.App.Infrastructure.Security.LoopbackApiPipelineConfigurator.Compile(false).ConfigureServices(services);
        var readOnly = profile == 3;
        ResourceManagerServiceCollectionExtensions.AddResourceManagerApp(services,
            [readOnly ? "--startup-profile=normal-read-only" : "--startup-profile=full"],
            readOnly ? StartupCapabilitySet.NormalReadOnly : StartupCapabilitySet.Full,
            BackendHostEnvironment.Compile(profile == 2, profile == 2));
        return services;
    }

    private static BusinessBlockRegistration[] Declarations(IServiceCollection services)
        => services.Select(row => row.ImplementationInstance).OfType<BusinessBlockRegistration>().ToArray();

    private static (int Id, Type ServiceType)[] HostedAliases(IServiceCollection services, BusinessBlockRegistration[] registrations)
    {
        var byType = registrations.Where(row => row.HostedServiceType is not null).ToDictionary(row => row.HostedServiceType!);
        var aliases = new List<(int, Type)>();
        foreach (var descriptor in services.Where(row => row.ServiceType == typeof(IHostedService) && row.ImplementationFactory is not null))
        {
            Type serviceType;
            try
            {
                // Lazy aliases reveal T without resolving it. Eager aliases ask this provider for T,
                // which captures the same metadata before any real service can be constructed.
                using var alias = Assert.IsAssignableFrom<IDisposable>(descriptor.ImplementationFactory!(new ServiceTypeProbe()));
                var type = alias.GetType();
                Assert.Equal(typeof(NonOwningHostedService<>), type.GetGenericTypeDefinition());
                serviceType = type.GenericTypeArguments.Single();
            }
            catch (ServiceTypeCaptured captured) { serviceType = captured.ServiceType; }
            Assert.True(byType.TryGetValue(serviceType, out var registration), $"Hosted alias {serviceType.Name} must have a business declaration.");
            aliases.Add((registration!.Id, serviceType));
        }
        return aliases.ToArray();
    }

    private sealed class ServiceTypeProbe : IServiceProvider
    {
        public object GetService(Type serviceType) => throw new ServiceTypeCaptured(serviceType);
    }
    private sealed class ServiceTypeCaptured(Type serviceType) : Exception
    {
        internal Type ServiceType { get; } = serviceType;
    }
    private sealed class CoreProbe : HostedProbe { }
    private sealed class FeedProbe : HostedProbe
    {
        internal TaskCompletionSource ReleaseCleanup { get; } = BusinessBlockTestSupport.Signal();
        protected override Task CleanupAsync() => ReleaseCleanup.Task;
    }
    private abstract class HostedProbe : BackgroundService
    {
        internal TaskCompletionSource Started { get; } = BusinessBlockTestSupport.Signal();
        internal TaskCompletionSource StopNotified { get; } = BusinessBlockTestSupport.Signal();
        internal TaskCompletionSource Returned { get; } = BusinessBlockTestSupport.Signal();
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            Started.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            StopNotified.TrySetResult();
            await CleanupAsync();
            Returned.TrySetResult();
        }
        protected virtual Task CleanupAsync() => Task.CompletedTask;
    }
}
