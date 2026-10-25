using System.Diagnostics;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Infrastructure.ResourceBreakdown;

namespace Resource_Manager_APP.Tests;

// These are ordinary hosted-consumer tests. All allocation events come from the
// injected fake; none of these tests can create an ETW session.
public sealed partial class WindowsResourceBreakdownSamplerLifecycleTests
{
    [Fact]
    public async Task SmartDedicatedMemoryPublicationWaitsForTheCompleteDueRundown()
    {
        using var subscriptions = new HostManagerSamplingSubscriptionTestFixture();
        using var catalog = new VersionedProcessAttributionCatalogProvider();
        var factory = new GpuAllocationTestSessionFactory
        {
            Configure = session => { session.Pid = Environment.ProcessId; session.HoldEnd = true; }
        };
        using var collector = factory.Collector();
        var now = DateTimeOffset.UtcNow.UtcTicks;
        var hardware = CreateMemorySnapshot() with
        {
            GpuInventory = new SchedulingGpuInventorySnapshot(SamplingObservationStatus.Current, 1, now,
                1, 0, 0, 1,
                [new SchedulingGpuAdapterObservation(0, 23, SchedulingGpuCapabilityMask.DedicatedMemory,
                    SchedulingGpuMetricMask.TotalDedicatedMemory | SchedulingGpuMetricMask.UsedDedicatedMemory,
                    SamplingObservationStatus.NotRequested, SamplingObservationStatus.Current, 0, 0, 8192, 1, now)])
        };
        using var sampler = new WindowsResourceBreakdownSampler(new StaticMetricSampler(hardware), catalog,
            new StaticResourceResidualBreakdownProvider(), null!, null!, subscriptions.Provider, subscriptions.Owner,
            new PdhProcessGpuReader(), new DelegateWindowsProcessInventoryReader(static () => [Process.GetCurrentProcess()]),
            gpuAllocations: collector);
        await sampler.StartAsync(default);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var request = new ResourceBreakdownSampleRequest([], new Dictionary<string, string>(),
            ProcessSampleDetailLevel.SmartSchedulingLite, SchedulingProcessMetricMask.GpuDedicatedMemory);
        await using var output = sampler.SubscribeAsync("smart.gpu", request,
            TimeSpan.FromSeconds(30), stop.Token).GetAsyncEnumerator();
        var round = output.MoveNextAsync().AsTask();
        await AwaitGpuAsync(() => factory.Sessions.Count > 0 && factory.Current.Captures == 1);
        Assert.False(round.IsCompleted);
        Assert.Null(collector.ReadCurrent(new Dictionary<int, long>()));
        factory.Current.DeliverEnd();
        Assert.True(await round.WaitAsync(stop.Token));
        Assert.Empty(factory.Current.Enabled);
        var facts = sampler.ReadLatest(new SchedulingProcessFactRequest(SchedulingProcessMetricMask.GpuDedicatedMemory, hardware.GpuInventory));
        var fact = Assert.Single(facts!.Processes, process => process.ProcessId == Environment.ProcessId);
        Assert.Equal(1024, Assert.Single(fact.Gpus).ResidentMemoryBytes);
        Assert.Equal(1, factory.Current.Captures);
        await sampler.StopAsync(default);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VramDatasetAndMetricUseHighestSubscribedFrequency(bool dataset)
    {
        using var subscriptions = new HostManagerSamplingSubscriptionTestFixture();
        using var catalog = new VersionedProcessAttributionCatalogProvider();
        var factory = new GpuAllocationTestSessionFactory { Configure = session => session.Pid = Environment.ProcessId };
        using var collector = factory.Collector();
        using var sampler = new WindowsResourceBreakdownSampler(new StaticMetricSampler(CreateAllocationHardware()), catalog,
            new StaticResourceResidualBreakdownProvider(), null!, null!, subscriptions.Provider, subscriptions.Owner,
            new PdhProcessGpuReader(), new DelegateWindowsProcessInventoryReader(static () => [Process.GetCurrentProcess()]),
            gpuAllocations: collector, gpuAdapterInventory: AllocationAdapterInventory);
        var request = dataset
            // The UI selects a metric; its compiled native plan carries ProcessGpuVram.
            ? new ResourceBreakdownSampleRequest(["gpu.0.vram"], new Dictionary<string, string>(), ProcessSampleDetailLevel.ResourceTableBasic)
                { PublicationDatasetIds = [SamplingDatasetIds.ProcessGpuVram] }
            : ResourceBreakdownSampleRequest.ForResourceTable(["gpu.0.vram"], new Dictionary<string, string>());
        using var smart = sampler.AcquireSubscription("smart", SchedulingProcessMetricMask.GpuDedicatedMemory, TimeSpan.FromSeconds(30));
        using var ui = sampler.AcquireSubscription("ui", request, TimeSpan.FromSeconds(1));
        await sampler.StartAsync(default);
        await AwaitGpuAsync(() => factory.Sessions.Count > 0 && factory.Current.Captures >= 1);
        // Multiple coalesced subscribers produce one capture for the first due time.
        await Task.Delay(200);
        Assert.Equal(1, factory.Current.Captures);
        Assert.Contains(ResourceManager.App.Infrastructure.Telemetry.Etw.GpuAllocationCollector.Provider, factory.Current.Enabled);
        var initial = sampler.ReadLatest(request with { MetricIds = ["gpu.0.vram"] });
        Assert.Contains(initial.Datasets, observation => observation.DatasetId == SamplingDatasetIds.ProcessGpuVram
            && observation.Status == ResourceBreakdownSamplingStatuses.Ready);
        var process = Assert.Single(Assert.Single(initial.Bars).Software.SelectMany(item => item.Processes),
            item => item.ProcessId == Environment.ProcessId);
        Assert.Equal(1024, process.Value);
        using var identity = Process.GetCurrentProcess();
        var identities = new Dictionary<int, long> { [identity.Id] = identity.StartTime.ToUniversalTime().ToFileTimeUtc() };
        await AwaitGpuAsync(() => collector.ReadCurrent(identities)?.Generation >= 3);
        Assert.Equal(1, factory.Current.Captures);
        Assert.Contains(ResourceManager.App.Infrastructure.Telemetry.Etw.GpuAllocationCollector.Provider, factory.Current.Enabled);
        ui.Dispose();
        var afterRelease = factory.Current.Captures;
        await Task.Delay(1200);
        Assert.Equal(afterRelease, factory.Current.Captures);
        smart.Dispose();
        Assert.Equal(1, factory.Current.Stops);
        Assert.Empty(factory.Current.Enabled);
        await sampler.StopAsync(default);
    }

    private static async Task AwaitGpuAsync(Func<bool> condition)
    {
        var deadline = Stopwatch.GetTimestamp() + 8 * Stopwatch.Frequency;
        while (!condition() && Stopwatch.GetTimestamp() < deadline) await Task.Delay(10);
        Assert.True(condition(), "GPU allocation hosted capture did not complete within the test budget.");
    }

    private static WindowsGpuAdapterInventoryRead AllocationAdapterInventory()
        => new(SamplingObservationStatus.Current, 1, DateTimeOffset.UtcNow.UtcTicks, 1, 0, 0, 1,
            [new WindowsGpuAdapter(0, "recorded GPU", 0x10de, 1, 0,
                new AdapterLuid { LowPart = 23 }, false, 8192,
                WindowsGpuAdapterKind.Dedicated)]);

    private static HardwareMetricSnapshot CreateAllocationHardware()
    {
        var memory = CreateMemorySnapshot();
        var now = memory.CapturedAt.UtcTicks;
        var metric = "gpu.0.vram";
        var dataset = SamplingDatasetIds.ForSystemMetric(metric);
        return memory with
        {
            Gpus = [new GpuMetrics(0, "recorded GPU", 0, 0, 0, 0, 0, 2048, 8192, 25, null!)],
            GpuInventory = new(SamplingObservationStatus.Current, 1, now, 1, 0, 0, 1,
                [new SchedulingGpuAdapterObservation(0, 23, SchedulingGpuCapabilityMask.DedicatedMemory,
                    SchedulingGpuMetricMask.TotalDedicatedMemory | SchedulingGpuMetricMask.UsedDedicatedMemory,
                    SamplingObservationStatus.NotRequested, SamplingObservationStatus.Current, 0, 0, 8192, 1, now)]),
            Items = new Dictionary<string, MetricReadingValue>(memory.Items)
            {
                [metric] = new(metric, 2048, "B", 25, 8192) { SourceId = "recording" }
            },
            Datasets = new Dictionary<string, HardwareMetricDatasetObservation>(memory.Datasets)
            {
                [dataset] = new(dataset, SamplingObservationStatus.Current, 1, now, 1, 1, 1, 1)
            }
        };
    }
}
