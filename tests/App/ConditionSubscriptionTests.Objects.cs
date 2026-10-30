using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace Resource_Manager_APP.Tests;

public sealed partial class ConditionSubscriptionTests
{
    private const string PrivateWorkingSet = "process_all_private_working_set_actual";
    private const string ProcessSubscription = "/api/resource-facts/subscribe?items=" + PrivateWorkingSet + ",process.inventory,process.attribution&intervalMs=100";

    private static Dictionary<string, object> ObjectRule(string binding, int pid = 22, string key = "202")
    {
        var rule = Rule(binding == "software" ? 2 : 0);
        rule["binding"] = binding; rule["metricId"] = PrivateWorkingSet; rule["quantity"] = "value";
        rule["comparison"] = "atOrAbove"; rule["threshold"] = binding == "software" ? 700d : 200d;
        if (binding == "software") rule["softwareId"] = "software:a";
        else { rule["processId"] = pid; rule["startKey"] = key; }
        return rule;
    }

    [Fact]
    public async Task SoftwareHookUsesOnlyTheSubscriptionsDeliveredProcessesAndOwners()
    {
        var metrics = new MetricSource();
        var resources = new ResourceSource { Foundations = Foundations() };
        await using var host = await Host.StartAsync(metrics, resources);
        await using var stream = await host.OpenAsync([Hook(ObjectRule("software"))]);
        await resources.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await DeliverAsync(host, stream, resources, ProcessValues(Epoch), 0);
        await DeliverAsync(host, stream, resources, ProcessValues(Epoch.AddSeconds(1)), 1);
        var met = await stream.ReadAsync();
        Assert.Equal("met", met.GetProperty("state").GetString());
        Assert.Equal(800, met.GetProperty("reading").GetDouble());
        Assert.Equal(new[] { 22, 11 }, met.GetProperty("owners").EnumerateArray().Select(owner => owner.GetProperty("processId").GetInt32()));
        // A software binding evaluates process data, so it keeps its one resource subscription.
        Assert.Equal(1, resources.Acquired);
        Assert.Equal(1, resources.Active);
        Assert.Equal("monitoring-hook:pressure", resources.Owner);
        Assert.Equal(TimeSpan.FromMilliseconds(100), resources.Interval);
        Assert.Equal(0, metrics.Acquired);
    }

    [Fact]
    public async Task ProcessExitEndsOnlyItsHookAndPidReuseDoesNotRebindIt()
    {
        var resources = new ResourceSource { Foundations = Foundations() };
        await using var host = await Host.StartAsync(new MetricSource(), resources);
        await using var stream = await host.OpenAsync(
            [Hook(ObjectRule("process")), Hook(ObjectRule("process", 11, "101"), "other-process")]);
        await UntilAsync(() => resources.Active == 2);
        await DeliverAsync(host, stream, resources, ProcessValues(Epoch), 0);
        await DeliverAsync(host, stream, resources, ProcessValues(Epoch.AddSeconds(1)), 1);
        Assert.Equal("met", (await stream.ReadAsync()).GetProperty("state").GetString());
        Assert.Equal("met", (await stream.ReadAsync()).GetProperty("state").GetString());
        resources.Foundations = Foundations(reused: true);
        await DeliverAsync(host, stream, resources, ProcessValues(Epoch.AddSeconds(2)), 1);
        var ended = await stream.ReadFrameAsync();
        Assert.Equal("pressure", ended.GetProperty("hookId").GetString());
        Assert.Equal("objectEnded", ended.GetProperty("value").GetProperty("state").GetString());
        Assert.Equal(1, resources.Active);
        await DeliverAsync(host, stream, resources, ProcessValues(Epoch.AddSeconds(3), small: 100), 1);
        var cleared = await stream.ReadFrameAsync();
        Assert.Equal("other-process", cleared.GetProperty("hookId").GetString());
        Assert.Equal("cleared", cleared.GetProperty("value").GetProperty("state").GetString());
        Assert.Equal(2, resources.Acquired);
    }

    [Fact]
    public async Task UnavailableProcessInventoryDoesNotClaimObjectEnded()
    {
        var resources = new ResourceSource();
        await using var host = await Host.StartAsync(new MetricSource(), resources);
        await using var stream = await host.OpenAsync([Hook(ObjectRule("process"))]);
        await resources.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await DeliverAsync(host, stream, resources, ProcessValues(Epoch), 0);
        Assert.Equal("unavailable", (await stream.ReadAsync()).GetProperty("state").GetString());
        resources.Foundations = Foundations();
        await DeliverAsync(host, stream, resources, ProcessValues(Epoch.AddSeconds(2)), 2);
        await DeliverAsync(host, stream, resources, ProcessValues(Epoch.AddSeconds(3)), 1);
        Assert.Equal("met", (await stream.ReadAsync()).GetProperty("state").GetString());
    }

    [Fact]
    public async Task DeviceHookKeepsExactIdentityWhenTheOrdinalIsReused()
    {
        HardwareMetricSnapshot Gpu(string device) => Memory(Epoch, 950) with
        {
            Items = new Dictionary<string, MetricReadingValue> { ["gpu.0.vram"] = new("gpu.0.vram", 950, "B", Total: 1000)
                { DeviceIdentity = device, FieldCode = MetricFieldCode.GpuMemoryUsed, Status = SamplingObservationStatus.Current } },
            Datasets = new Dictionary<string, HardwareMetricDatasetObservation>
            { [SamplingDatasetIds.ForSystemMetric("gpu.0.vram")] = new(SamplingDatasetIds.ForSystemMetric("gpu.0.vram"), SamplingObservationStatus.Current, 1, Epoch.UtcTicks, 1, 1, 1, 1) }
        };
        var catalog = new HardwareMetricCatalogState(); catalog.PublishCatalogProbe(Gpu("gpu-first"));
        var metrics = new MetricSource();
        await using var host = await Host.StartAsync(metrics, catalog: catalog);
        var rule = Rule(); rule["binding"] = "device"; rule["metricId"] = "gpu.0.vram"; rule["deviceIdentity"] = "gpu-first";
        await using var stream = await host.OpenAsync([Hook(rule)]);
        await metrics.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await DeliverAsync(host, stream, metrics, Gpu("gpu-first"), 0);
        await DeliverAsync(host, stream, metrics, Gpu("gpu-first"), 1);
        Assert.Equal("met", (await stream.ReadAsync()).GetProperty("state").GetString());
        await DeliverAsync(host, stream, metrics, Gpu("gpu-new"), 1);
        Assert.Equal("objectEnded", (await stream.ReadAsync()).GetProperty("state").GetString());
        await DeliverAsync(host, stream, metrics, Gpu("gpu-new"), 2);
    }

    private static ResourceBreakdownSnapshot ProcessValues(DateTimeOffset at, double small = 200)
        => new(at, [])
        {
            ProcessFields = new(at, null, [ProcessMetricFieldRegistry.Find(PrivateWorkingSet)!],
                [new(11, "101", [new(PrivateWorkingSet, new MetricInteger((ulong)small), null)]),
                 new(22, "202", [new(PrivateWorkingSet, new MetricInteger(600), null)]),
                 new(33, "303", [new(PrivateWorkingSet, new MetricInteger(900), null)])])
                { Sources = [new("windows.process.query", at, null)] },
            ProcessMemoryFacts = [new(PrivateWorkingSet, "memory.usage", 1000, 2000, small + 1500,
                [new(11, 101, small), new(22, 202, 600), new(33, 303, 900)]) { CapturedAt = at }],
            Datasets = [new(SamplingDatasetIds.ForProcessMetric(PrivateWorkingSet), "ready", 1, 1, at, at, at, null, null, null)]
        };

    private static SchedulingProcessFactSnapshot Foundations(bool reused = false)
    {
        SchedulingProcessFact Row(int pid, ulong key, string name, string software) => new(pid, key, name, null,
            software, software, "app", "app", 0, SchedulingProcessMetricMask.None, 0, 0, 1, []);
        var rows = new[] { Row(11, 101, "small", "software:a"), Row(22, reused ? 999UL : 202UL, "large", "software:a"), Row(33, 303, "other", "software:b") };
        return new(SamplingObservationStatus.Current, 1, Epoch.UtcTicks, 3, 3, 0, 0,
            SchedulingProcessMetricMask.None, SchedulingProcessMetricMask.None, rows)
        {
            HasSeparateFoundationPayloads = true, InventoryProcesses = rows, AttributionProcesses = rows,
            FoundationDatasetObservations = new Dictionary<string, SchedulingProcessFoundationDatasetObservation>
            {
                [SamplingDatasetIds.ProcessInventory] = SchedulingProcessFoundationDatasetObservation.CreateCurrent(SamplingDatasetIds.ProcessInventory, 1, Epoch.UtcTicks),
                [SamplingDatasetIds.ProcessAttribution] = SchedulingProcessFoundationDatasetObservation.CreateCurrent(SamplingDatasetIds.ProcessAttribution, 1, Epoch.UtcTicks)
            }
        };
    }
}
