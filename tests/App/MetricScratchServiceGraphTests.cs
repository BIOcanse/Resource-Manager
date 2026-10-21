using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Hosting;
using ResourceManager.App.Hosting.StartupCapabilities;
using ResourceManager.App.Infrastructure.Adaptation;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.NativeCore;

namespace Resource_Manager_APP.Tests;

public sealed class MetricScratchServiceGraphTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void MetricUsesTheOneLedgerInReadOnlyFullAndEffectsOnlyGraphs(int profile)
    {
        var capabilities = profile switch
        {
            0 => StartupCapabilitySet.NormalReadOnly,
            1 => StartupCapabilitySet.Full,
            _ => new StartupCapabilitySet("effects-only", StartupCapability.RuntimeEffectOwners)
        };
        using var host = Host.CreateDefaultBuilder()
            .ConfigureServices(services => services.AddResourceManagerApp(["--no-native-ui"], capabilities))
            .Build();
        var services = host.Services;
        var metric = services.GetRequiredService<HostManagerMetricSnapshotOwner>();
        Assert.Same(metric, services.GetRequiredService<HostManagerMetricSnapshotOwner>());
        var ledger = Assert.Single(services.GetServices<ResourceManagerLedgerOwner>());
        Assert.Same(ledger, services.GetRequiredService<ResourceManagerLedgerOwner>());
        var pdh = services.GetRequiredService<NativePdhCollector>();
        Assert.Same(pdh, services.GetRequiredService<NativePdhCollector>());
        Assert.Contains(typeof(NativePdhCollector).GetConstructors(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic),
            candidate => candidate.GetParameters().Any(parameter => parameter.ParameterType == typeof(ResourceManagerLedgerOwner)));
        var constructor = Assert.Single(typeof(HostManagerMetricSnapshotOwner).GetConstructors());
        Assert.Contains(constructor.GetParameters(), parameter => parameter.ParameterType == typeof(ResourceManagerLedgerOwner));
        Assert.DoesNotContain(constructor.GetParameters(), parameter => parameter.ParameterType == typeof(ResourceManagerSelfLocalResourceManager));
        if (profile == 0)
        {
            Assert.Null(services.GetService<ResourceManagerSelfLocalResourceManager>());
            Assert.Null(services.GetService<IProcessResourcePolicyWriter>());
        }
        else
        {
            Assert.NotNull(services.GetService<ResourceManagerSelfLocalResourceManager>());
            Assert.NotNull(services.GetService<IProcessResourcePolicyWriter>());
        }
        // Construction resolves the real production DI graph; no hosted start or device sampling.
    }
}
