using System.Diagnostics;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Infrastructure.ResourceBreakdown;
using ResourceManager.App.Endpoints.Transport;

namespace Resource_Manager_APP.Tests;

public sealed partial class WindowsResourceBreakdownSamplerLifecycleTests
{
    [Fact]
    public async Task BarsSubscriptionDeliversCurrentItemsOncePerIntervalAcrossDatasetPhases()
    {
        using var subscriptions = new HostManagerSamplingSubscriptionTestFixture();
        using var catalog = new VersionedProcessAttributionCatalogProvider();
        var hardware = CreateMemorySnapshot();
        hardware = hardware with
        {
            Memory = new(8000, 16000, 50, true),
            VirtualMemory = new(12000, 32000, 37.5, true),
            Items = new Dictionary<string, MetricReadingValue>(hardware.Items)
            {
                ["memory.usage"] = new("memory.usage", 8000, "B", Total: 16000),
                ["virtualMemory.usage"] = new("virtualMemory.usage", 12000, "B", Total: 32000)
            },
            Datasets = new Dictionary<string, HardwareMetricDatasetObservation>(hardware.Datasets)
            {
                [SamplingDatasetIds.SystemVirtualMemoryUsage] = new(SamplingDatasetIds.SystemVirtualMemoryUsage,
                    SamplingObservationStatus.Current, 1, hardware.CapturedAt.UtcTicks, 1, 1, 1, 1)
            }
        };
        var hardwareSource = new CadenceHardwareSampler(hardware);
        using var sampler = new WindowsResourceBreakdownSampler(hardwareSource, catalog,
            new StaticResourceResidualBreakdownProvider(), null!, null!, subscriptions.Provider, subscriptions.Owner,
            new PdhProcessGpuReader(), new DelegateWindowsProcessInventoryReader(static () => []));
        var memory = ResourceBreakdownSampleRequest.ForResourceTable(["memory.usage"], new Dictionary<string, string>());
        var commit = ResourceBreakdownSampleRequest.ForResourceTable(["virtualMemory.usage"], new Dictionary<string, string>());
        var both = ResourceBreakdownSampleRequest.ForResourceTable(["memory.usage", "virtualMemory.usage"], new Dictionary<string, string>());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await sampler.StartAsync(timeout.Token);
        try
        {
            using var memoryDemand = sampler.AcquireSubscription("phase-memory", memory, TimeSpan.FromSeconds(1));
            while (sampler.ReadLatest(memory).Bars.FirstOrDefault()?.TotalValue is null) await Task.Delay(10, timeout.Token);
            await Task.Delay(350, timeout.Token);
            using var commitDemand = sampler.AcquireSubscription("phase-commit", commit, TimeSpan.FromSeconds(1));
            while (sampler.ReadLatest(commit).Bars.FirstOrDefault()?.TotalValue is null) await Task.Delay(10, timeout.Token);

            await using var stream = sampler.SubscribeAsync("one-second-bars", both, TimeSpan.FromSeconds(1), timeout.Token)
                .GetAsyncEnumerator(timeout.Token);
            var clock = Stopwatch.StartNew();
            for (var frame = 1; frame <= 4; frame++)
            {
                Assert.True(await stream.MoveNextAsync());
                Assert.InRange(clock.Elapsed.TotalMilliseconds, frame * 1000 - 150, frame * 1000 + 700);
                Assert.Equal(new[] { "memory.usage", "virtualMemory.usage" }, stream.Current.Bars.Select(bar => bar.MetricId).Order());
                var facts = ResourceFactsSnapshot.Create(stream.Current, both.MetricIds);
                Assert.Equal(2, facts.Metrics.Count);
                foreach (var bar in stream.Current.Bars)
                {
                    var expected = bar.MetricId == "memory.usage" ? hardware.Memory.UsedBytes : hardware.VirtualMemory.UsedBytes;
                    Assert.Equal((double)expected, bar.TotalValue);
                    Assert.Equal(bar.TotalValue, Assert.Single(facts.Metrics, fact => fact.MetricId == bar.MetricId).TotalValue);
                }
                // Change both source values between delivery rounds. Each next frame must
                // retain the latest completed value from both independently phased datasets.
                hardware = hardware with
                {
                    Memory = hardware.Memory with { UsedBytes = hardware.Memory.UsedBytes + 1 },
                    VirtualMemory = hardware.VirtualMemory with { UsedBytes = hardware.VirtualMemory.UsedBytes + 1 },
                    Items = hardware.Items.ToDictionary(pair => pair.Key,
                        pair => pair.Value with { NumericValue = pair.Value.NumericValue + 1 })
                };
                hardwareSource.Publish(hardware);
            }
        }
        finally { await sampler.StopAsync(CancellationToken.None); }
    }

    private sealed class CadenceHardwareSampler(HardwareMetricSnapshot initial) : IMetricSampler, IMetricSnapshotObservationSource
    {
        private HardwareMetricSnapshot current = initial;
        internal void Publish(HardwareMetricSnapshot value) => Volatile.Write(ref current, value);
        public HardwareMetricSnapshot? ReadLatest(MetricSampleRequest request) => Volatile.Read(ref current);
        public IDisposable AcquireSubscription(string id, MetricSampleRequest request, TimeSpan interval)
            => new CallbackSubscription(static () => { });
        public Task<HardwareMetricSnapshot> GetSnapshotAsync(CancellationToken token)
            => Task.FromResult(Volatile.Read(ref current));
        public Task<HardwareMetricSnapshot> GetSnapshotAsync(MetricSampleRequest request, CancellationToken token)
            => GetSnapshotAsync(token);
        public Task<HardwareMetricSnapshot> CaptureSnapshotAsync(MetricSampleRequest request, CancellationToken token)
            => GetSnapshotAsync(token);
    }
}
