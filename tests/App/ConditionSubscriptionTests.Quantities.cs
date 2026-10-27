using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace Resource_Manager_APP.Tests;

public sealed partial class ConditionSubscriptionTests
{
    [Fact]
    public async Task PercentOfTotalUsesThePublishedTotalAndEmitsPercentUnits()
    {
        var metrics = new MetricSource();
        await using var host = await Host.StartAsync(metrics);
        var rule = Rule(); rule["quantity"] = "percentOfTotal"; rule["comparison"] = "atOrAbove"; rule["threshold"] = 95d;
        await using var stream = await host.OpenAsync([Hook(rule)]);
        await metrics.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await DeliverAsync(host, stream, metrics, Memory(Epoch, 950), 0);
        await DeliverAsync(host, stream, metrics, Memory(Epoch, 950), 1);
        var met = await stream.ReadAsync();
        Assert.Equal("met", met.GetProperty("state").GetString());
        Assert.Equal(95, met.GetProperty("reading").GetDouble());
        Assert.Equal("%", met.GetProperty("unit").GetString());
    }

    [Fact]
    public async Task DeviceRemovalEndsItsHookAndReleasesItsSubscription()
    {
        var recorded = StorageMetricSnapshotTestData.CommittedSnapshot();
        var binding = recorded.FieldBindings.Values.First(item => item.DeviceIdentity == StorageMetricSnapshotTestData.Identity);
        var catalog = new HardwareMetricCatalogState(); catalog.PublishCatalogProbe(recorded);
        var metrics = new MetricSource();
        await using var host = await Host.StartAsync(metrics, catalog: catalog);
        var rule = Rule(); rule["binding"] = "device"; rule["deviceIdentity"] = binding.DeviceIdentity;
        rule["metricId"] = binding.RequestAddress; rule["quantity"] = "value";
        await using var stream = await host.OpenAsync([Hook(rule)]);
        await metrics.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        catalog.PublishCatalogProbe(Memory(Epoch, 950) with { FieldBindings = new Dictionary<string, ResourceManager.App.Domain.Metrics.MetricDeviceFieldBinding>() });
        await metrics.PublishAsync(Memory(Epoch, 950));
        Assert.Equal("objectEnded", (await stream.ReadAsync()).GetProperty("state").GetString());
        await UntilAsync(() => metrics.Active == 0);
    }
}
