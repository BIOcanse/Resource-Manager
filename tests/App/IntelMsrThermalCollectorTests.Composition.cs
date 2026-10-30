using Microsoft.Extensions.DependencyInjection;
using ResourceManager.App.Application.Monitoring;
using ResourceManager.App.Hosting;
using ResourceManager.App.Hosting.StartupCapabilities;
using ResourceManager.App.Infrastructure.Monitoring;

namespace Resource_Manager_APP.Tests;

public sealed partial class IntelMsrThermalCollectorTests
{
    [Fact]
    public void RealProductionRegistrationKeepsEverySourceZoneInstance()
    {
        var services = new ServiceCollection().AddLogging()
            .AddResourceManagerApp(["--no-native-ui"], StartupCapabilitySet.NormalReadOnly);
        var zones = MonitoringSourceZoneCatalog.CreateDefaultZones(Path.Combine(Path.GetTempPath(), $"intel-cold-zones-{Guid.NewGuid():N}"));
        // Supply cold concrete zone instances; resolve the actual production registry factory without starting hosted services.
        foreach (var zone in zones) services.AddSingleton(zone.GetType(), zone);
        try
        {
            using var provider = services.BuildServiceProvider();
            var registry = provider.GetRequiredService<MonitoringSourceZoneRegistry>();
            Assert.Same(registry, provider.GetRequiredService<IMonitoringSourceZoneRegistry>());
            foreach (var zone in zones) Assert.Same(zone, registry.GetZone(zone.DisplayName));
            var intel = provider.GetRequiredService<IntelMsrMonitoringZone>();
            Assert.Same(intel, registry.GetZone(MonitoringSourceZoneIds.VendorIntelMsr));
            foreach (var source in HostManagerTestPlanFactory.CreatePlan().MetricSnapshot.HotPublish.SourcePolicies)
                Assert.NotNull(registry.GetZone(NativeMetricSnapshotSourceCatalog.ResolveZoneId(source.SourceId)));
        }
        finally
        {
            foreach (var disposable in zones.OfType<IDisposable>()) disposable.Dispose();
        }
    }
}
