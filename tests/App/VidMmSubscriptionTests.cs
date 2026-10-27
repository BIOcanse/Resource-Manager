using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Infrastructure.ResourceBreakdown;
using ResourceManager.App.Infrastructure.Telemetry.Etw;
using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;

namespace Resource_Manager_APP.Tests;

public sealed partial class WindowsResourceBreakdownSamplerLifecycleTests
{
    [Fact]
    public async Task OnlyVramBarSubscriptionsHoldTheVidMmResidualProducer()
    {
        using var subscriptions = new HostManagerSamplingSubscriptionTestFixture();
        using var catalog = new VersionedProcessAttributionCatalogProvider();
        await using var harness = VidMmBusinessBlockTestHarness.Create();
        using var sampler = new WindowsResourceBreakdownSampler(new StaticMetricSampler(CreateMemorySnapshot()), catalog,
            new DxgkrnlVidMmEtwResidualBreakdownProvider(harness.Zone), null!, null!, subscriptions.Provider,
            subscriptions.Owner, new PdhProcessGpuReader(),
            new DelegateWindowsProcessInventoryReader(static () => []), gpuAdapterInventory: AllocationAdapterInventory);
        using var dedicated = sampler.AcquireSubscription("process-memory", SchedulingProcessMetricMask.GpuDedicatedMemory,
            TimeSpan.FromSeconds(1));
        await harness.Zone.ApplyModeAsync(ResourceManagerComputeZoneMode.Normal);
        Assert.Equal(0, harness.Sessions.Attempts);
        dedicated.Dispose();
        using var dataset = sampler.AcquireSubscription("process-vram-dataset",
            new ResourceBreakdownSampleRequest([], new Dictionary<string, string>(), ProcessSampleDetailLevel.ResourceTableBasic)
            { PublicationDatasetIds = [SamplingDatasetIds.ProcessGpuVram] }, TimeSpan.FromSeconds(1));
        // Apply the existing mode unchanged to await preceding dependency changes.
        await harness.Zone.ApplyModeAsync(ResourceManagerComputeZoneMode.Normal);
        Assert.Equal(0, harness.Sessions.Attempts);

        var request = ResourceBreakdownSampleRequest.ForResourceTable(["gpu.0.vram"], new Dictionary<string, string>());
        var first = sampler.AcquireSubscription("vram-first", request, TimeSpan.FromSeconds(1));
        var second = sampler.AcquireSubscription("vram-second", request, TimeSpan.FromSeconds(5));
        try
        {
            await harness.Zone.ApplyModeAsync(ResourceManagerComputeZoneMode.Normal);
            await EtwTestRunRecorder.UntilAsync(() => harness.Sessions.Attempts == 1);
            await harness.Sessions.Current.ProcessEntered.WaitAsync(TimeSpan.FromSeconds(5));
            var running = Assert.Single(harness.Controller.Read().Blocks,
                block => block.Id == BackendUids.DxgkrnlVidMmEtwTelemetryZone);
            Assert.Equal(BusinessBlockState.Running, running.State);
            Assert.Equal(2, harness.Controller.Read().Demands.Count(demand => demand.BlockId == BackendUids.DxgkrnlVidMmEtwTelemetryZone));
            Assert.Equal(1, harness.Sessions.Attempts);

            first.Dispose();
            await harness.Zone.ApplyModeAsync(ResourceManagerComputeZoneMode.Normal);
            Assert.Single(harness.Controller.Read().Demands, demand => demand.BlockId == BackendUids.DxgkrnlVidMmEtwTelemetryZone);
            Assert.True(harness.Sessions.Current.ConsumerThread!.IsAlive);

            second.Dispose();
            await harness.Zone.ApplyModeAsync(ResourceManagerComputeZoneMode.Normal);
            await EtwTestRunRecorder.UntilAsync(() => harness.Controller.ReadState(BackendUids.DxgkrnlVidMmEtwTelemetryZone) == BusinessBlockState.Stopped);
            await harness.Recorder.DrainAsync();
            Assert.False(harness.Sessions.Current.ConsumerThread!.IsAlive);
            Assert.Equal(1, harness.Sessions.Current.DisposeCount);
        }
        finally { first.Dispose(); second.Dispose(); }
    }
}
