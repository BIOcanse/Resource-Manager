using ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ResourceManager.App.Application.PublicResources;
using ResourceManager.App.Hosting;
using ResourceManager.App.Hosting.StartupCapabilities;
using ResourceManager.App.Infrastructure.Adaptation;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.SoftwareDiscovery;
using ResourceManager.App.Infrastructure.Security;

namespace Resource_Manager_APP.Tests;

[Collection(LocalResourceCapabilityHandlerProcessStateCollection.Name)]
public sealed class HostManagerStartupOrderTests
{
    [Fact]
    public void RuntimePlanPublicationPrecedesHostedConsumers()
    {
        using var host = Host.CreateDefaultBuilder()
            .ConfigureServices(static services =>
            {
                LoopbackApiPipelineConfigurator.CompileForCurrentProcess().ConfigureServices(services);
                services.AddResourceManagerApp(
                    ["--no-native-ui"],
                    StartupCapabilitySet.Full);
            })
            .Build();

        var hostedServices = host.Services.GetServices<IHostedService>().Where(static service => service is not BusinessBlockDependencyLifetime).ToArray();

        Assert.IsType<NonOwningHostedService<NativeEtwLifetimeService>>(
            hostedServices[0]);
        Assert.IsType<NonOwningHostedService<TrustedAccessRequestOwner>>(
            hostedServices[1]);
        Assert.IsType<NonOwningHostedService<TrustedAccessInvocationHostedService>>(
            hostedServices[2]);
        Assert.IsType<NonOwningHostedService<RuntimeSpecializationCoordinator>>(
            hostedServices[3]);
        var runtimeCoordinatorIndex = Array.FindIndex(
            hostedServices,
            static service => service
                is NonOwningHostedService<RuntimeSpecializationCoordinator>);
        Assert.True(
            Array.FindIndex(
                hostedServices,
                static service => service
                    is NonOwningHostedService<SqlitePortableSoftwareRegistry>) > runtimeCoordinatorIndex);
        var subscriptionOwnerIndex = Array.FindIndex(
            hostedServices,
            static service => service
                is NonOwningHostedService<HostManagerSamplingSubscriptionOwner>);
        var firstSamplingConsumerIndex = Array.FindIndex(
            hostedServices,
            static service => service
                is NonOwningHostedService<WindowsHardwareMetricSampler>);
        Assert.InRange(subscriptionOwnerIndex, runtimeCoordinatorIndex + 1, firstSamplingConsumerIndex - 1);

        var selfLocalManager = host.Services.GetRequiredService<
            ResourceManagerSelfLocalResourceManager>();
        Assert.False(selfLocalManager.IsBackgroundWorkerRunning);
        Assert.NotNull(host.Services.GetService<IHostPublicResourceSelfManager>());
    }
}
