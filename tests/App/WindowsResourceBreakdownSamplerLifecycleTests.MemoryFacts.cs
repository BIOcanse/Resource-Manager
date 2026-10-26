using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Endpoints.Transport;
using ResourceManager.App.Infrastructure.ResourceBreakdown;
using ResourceManager.ReadWrite.Windows.Processes;

namespace Resource_Manager_APP.Tests;

public sealed partial class WindowsResourceBreakdownSamplerLifecycleTests
{
    [Theory]
    [InlineData("process_all_working_set_actual", "memory.usage", false, "current")]
    [InlineData("process_all_private_working_set_actual", "memory.usage", true, "current")]
    [InlineData("process_all_private_commit_actual", "virtualMemory.usage", false, "current")]
    [InlineData("process_all_working_set_actual", "memory.usage", false, "shared")]
    [InlineData("process_all_private_commit_actual", "virtualMemory.usage", false, "missing-total")]
    [InlineData("process_all_private_commit_actual", "virtualMemory.usage", false, "zero")]
    [InlineData("process_all_working_set_actual", "memory.usage", false, "mixed")]
    public async Task MemoryFactsUseTheSubscribedFieldAndPublishItsSystemTotal(
        string name, string totalId, bool privateResident, string scenario)
    {
        using var subscriptions = new HostManagerSamplingSubscriptionTestFixture();
        using var catalog = new VersionedProcessAttributionCatalogProvider();
        var api = new ProcessBasicFieldCollectorTests.Api
        {
            MemoryValue = new(scenario == "shared" ? 18000UL : 1000UL, scenario == "zero" ? 0UL : 700UL, 900, 300)
        };
        var snapshot = CreateMemorySnapshot();
        var time = snapshot.CapturedAt;
        snapshot = snapshot with
        {
            Memory = new(8000, 16000, 50, true),
            VirtualMemory = new(12000, 32000, 37.5, true),
            Items = new Dictionary<string, MetricReadingValue>
            {
                ["memory.usage"] = new("memory.usage", 8000, "B"),
                ["virtualMemory.usage"] = new("virtualMemory.usage", 12000, "B")
            },
            Datasets = new[] { "memory.usage", "virtualMemory.usage" }.ToDictionary(id => id,
                id => new HardwareMetricDatasetObservation(id, SamplingObservationStatus.Current, 1, time.UtcTicks, 1, 1, 1, 1))
        };
        if (scenario == "missing-total")
            snapshot = snapshot with { Items = new Dictionary<string, MetricReadingValue>() };
        var hardware = new StaticMetricSampler(snapshot);
        var residualCaptures = 0;
        var residualProvider = new WindowsResourceResidualBreakdownProvider(new StaticResourceResidualBreakdownProvider(),
            NullLogger<WindowsResourceResidualBreakdownProvider>.Instance, _ =>
            {
                Interlocked.Increment(ref residualCaptures);
                return new(new(100, 200, 30, 20), null, 0, "pool-tags:fixture-unavailable");
            });
        using var sampler = new WindowsResourceBreakdownSampler(hardware, catalog,
            residualProvider, null!, null!, subscriptions.Provider, subscriptions.Owner,
            new PdhProcessGpuReader(), new DelegateWindowsProcessInventoryReader(static () => [Process.GetCurrentProcess()]),
            new WindowsProcessBasicMetricReader(api));
        var request = ResourceBreakdownSampleRequest.ForResourceTable([name], new Dictionary<string, string>())
            with { ProcessMemoryFactIds = [name] };
        Assert.Contains(SamplingDatasetIds.ProcessInventory, WindowsResourceBreakdownSampler.GetPublicationDatasetIds(request));
        Assert.Contains(SamplingDatasetIds.ProcessAttribution, WindowsResourceBreakdownSampler.GetPublicationDatasetIds(request));
        var before = api.MemoryCalls;
        _ = await sampler.GetSnapshotAsync(request, default);
        Assert.Equal(before, api.MemoryCalls);
        Assert.Equal(0, Volatile.Read(ref residualCaptures));
        await sampler.StartAsync(default);
        try
        {
            using (var rawDemand = scenario == "mixed" ? sampler.AcquireSubscription("raw-commit",
                ResourceBreakdownSampleRequest.ForResourceTable(["process_all_private_commit_actual"], new Dictionary<string, string>()),
                TimeSpan.FromMilliseconds(100)) : null)
            using (sampler.AcquireSubscription("memory-facts", request, TimeSpan.FromMilliseconds(100)))
            {
                Assert.Contains(totalId, hardware.LastSubscriptionRequest!.Ids);
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (sampler.ReadLatest(request)?.ProcessMemoryFacts.Count != 1 && DateTime.UtcNow < deadline)
                    await Task.Delay(20);
                var current = sampler.ReadLatest(request)!;
                var foundations = sampler.ReadLatest(new SchedulingProcessFactRequest(SchedulingProcessMetricMask.None,
                    new(SamplingObservationStatus.NotRequested, 0, 0, 0, 0, 0, 0, [])));
                Assert.NotNull(foundations);
                var published = ResourceFactsSnapshot.Create(current, [name], foundations);
                var fact = Assert.Single(published.Metrics);
                Assert.Equal(totalId, fact.TotalMetricId);
                Assert.Equal(scenario == "missing-total" ? (double?)null : totalId == "memory.usage" ? 8000 : 12000, fact.TotalValue);
                Assert.Equal(totalId == "memory.usage" ? 16000 : 32000, fact.CapacityValue);
                var expected = scenario == "zero" ? 0 : scenario == "shared" ? 18000
                    : name.Contains("private_working") ? 300 : name.Contains("private_commit") ? 700 : 1000;
                Assert.Equal(expected, fact.ProcessSumValue);
                var process = Assert.Single(fact.Software.SelectMany(static software => software.Processes),
                    row => published.Processes[row.ProcessIndex].ProcessId == Environment.ProcessId);
                Assert.Equal(expected, process.Value);
                Assert.Contains(published.Attributions, edge => edge.ProcessIndex == process.ProcessIndex);
                if (scenario == "missing-total" || name == "process_all_working_set_actual")
                    Assert.DoesNotContain(fact.Software, software => software.SoftwareId.StartsWith("resource-residual:"));
                else
                {
                    var residual = Assert.Single(fact.Software, software => software.SoftwareId == "resource-residual:" + totalId);
                    Assert.Equal(fact.TotalValue - expected, residual.Value);
                }
                Assert.Equal([name], current.ProcessFields!.Fields.Select(static field => field.Name));
                if (scenario == "mixed")
                {
                    var combined = sampler.ReadLatest(request with { MetricIds = [name, "process_all_private_commit_actual"] });
                    Assert.Equal(2, combined.ProcessFields!.Fields.Count);
                    Assert.Equal(name, Assert.Single(combined.ProcessMemoryFacts).MetricId);
                    Assert.DoesNotContain("virtualMemory.usage", hardware.LastReadRequest!.Ids);
                }
                var captureCount = Volatile.Read(ref residualCaptures);
                Assert.Equal(name == "process_all_private_working_set_actual", captureCount > 0);

                if (name == "process_all_private_working_set_actual")
                    Assert.Contains(current.ProcessMemoryFacts.SelectMany(fact => fact.Residuals)
                        .SelectMany(residual => residual.Processes), row => row.AttributionKind == PhysicalMemoryResidualKinds.NonPagedPool);
                Assert.Empty(current.Bars); // resource bars by process field are outside this change
                Assert.All(api.PrivateRequests, value => Assert.Equal(privateResident, value));
            }
            await Task.Delay(150); // let an already accepted capture finish before measuring no demand
            var stopped = api.MemoryCalls;
            var stoppedResiduals = Volatile.Read(ref residualCaptures);
            await Task.Delay(300);
            Assert.Equal(stopped, api.MemoryCalls);
            _ = await sampler.GetSnapshotAsync(request, default);
            sampler.ReadLatest(request);
            Assert.Equal(stoppedResiduals, Volatile.Read(ref residualCaptures));
            Assert.Equal(0, hardware.ActiveSubscriptions);
        }
        finally { await sampler.StopAsync(default); }
    }
}

