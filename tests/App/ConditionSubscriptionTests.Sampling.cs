using System.Reflection;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.Optimization;

namespace Resource_Manager_APP.Tests;

public sealed partial class ConditionSubscriptionTests
{
    [Fact]
    public async Task ConditionKeepsOriginalWorkerUpdatingAfterSmartSubscriptionsStopAndReleasesLastDemand()
    {
        long available = 500;
        using var harness = new MetricCatalogueSubscriptionTests.RecordedHostMetricFieldHarness(() =>
        {
            var free = (ulong)Interlocked.Read(ref available);
            return new(4096 - free, 4096, (4096 - free) * 100d / 4096, true) { AvailableBytes = free };
        });
        var tracker = (NativeItemSamplingSubscriptionTracker<MetricSampleRequest>)typeof(WindowsHardwareMetricSampler)
            .GetField("subscriptions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(harness.Sampler)!;
        await harness.Sampler.StartAsync(CancellationToken.None);
        try
        {
            var smart = new HostManagerSchedulingSubscriptions(null!, null!, harness.Sampler);
            await smart.UpdateAsync(false, SchedulingProcessMetricMask.None, MetricSampleRequest.ForIds(["memory.usage"]),
                TimeSpan.FromSeconds(1), CancellationToken.None);
            await smart.DisposeAsync();
            Assert.Equal((0, 0), tracker.CaptureManagedSourceState());
            var before = harness.MemoryReads;
            await using var host = await Host.StartAsync(harness.Sampler, catalog: harness.Catalog, realClock: true);
            var rule = Rule(); rule["holdSeconds"] = .3;
            var stream = await host.OpenAsync([Hook(rule)]);
            Interlocked.Exchange(ref available, 20);
            System.Text.Json.JsonElement met;
            do { met = await stream.ReadAsync(); } while (!met.TryGetProperty("state", out _));
            Assert.Equal("met", met.GetProperty("state").GetString());
            Assert.Equal(20, met.GetProperty("reading").GetDouble());
            Assert.True(harness.MemoryReads >= before + 3, "The condition must cause repeated original-worker reads while smart is stopped.");
            Assert.Equal((1, 1), tracker.CaptureManagedSourceState());
            using var unrelated = harness.Sampler.AcquireSubscription("unrelated-owner", MetricSampleRequest.ForIds(["memory.usage"]), TimeSpan.FromSeconds(1));
            Assert.Equal((2, 2), tracker.CaptureManagedSourceState());
            await stream.DisposeAsync();
            await UntilAsync(() => tracker.CaptureManagedSourceState() == (1, 1));
            unrelated.Dispose();
            await UntilAsync(() => tracker.CaptureManagedSourceState() == (0, 0));
            var reads = harness.MemoryReads;
            await Task.Delay(350);
            Assert.InRange(harness.MemoryReads - reads, 0, 1); // One already admitted capture may drain.
        }
        finally { await harness.Sampler.StopAsync(CancellationToken.None); }
    }

    internal static async Task UntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(20, timeout.Token);
    }
}
