using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ResourceManager.App.Application.PublicResources;
using ResourceManager.App.Hosting;
using ResourceManager.App.Hosting.StartupCapabilities;
using ResourceManager.App.Infrastructure.Adaptation;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.SoftwareDiscovery;

namespace Resource_Manager_APP.Tests;

[Collection(LocalResourceCapabilityHandlerProcessStateCollection.Name)]
public sealed class HostManagerStartupOrderTests
{
    [Fact]
    public void RuntimePlanPublicationPrecedesHostedConsumers()
    {
        using var host = Host.CreateDefaultBuilder()
            .ConfigureServices(static services =>
                services.AddResourceManagerApp(
                    ["--no-native-ui"],
                    StartupCapabilitySet.Full))
            .Build();

        var hostedServices = host.Services.GetServices<IHostedService>().ToArray();

        Assert.IsType<NonOwningHostedService<RuntimeSpecializationCoordinator>>(
            hostedServices[0]);
        Assert.True(
            Array.FindIndex(
                hostedServices,
                static service => service
                    is NonOwningHostedService<SqlitePortableSoftwareRegistry>) > 0);
        var subscriptionOwnerIndex = Array.FindIndex(
            hostedServices,
            static service => service
                is NonOwningHostedService<HostManagerSamplingSubscriptionOwner>);
        var firstSamplingConsumerIndex = Array.FindIndex(
            hostedServices,
            static service => service
                is NonOwningHostedService<WindowsHardwareMetricSampler>);
        Assert.InRange(subscriptionOwnerIndex, 1, firstSamplingConsumerIndex - 1);

        var selfLocalManager = host.Services.GetRequiredService<
            ResourceManagerSelfLocalResourceManager>();
        Assert.False(selfLocalManager.IsBackgroundWorkerRunning);
        Assert.NotNull(host.Services.GetService<IHostPublicResourceSelfManager>());
    }
}
