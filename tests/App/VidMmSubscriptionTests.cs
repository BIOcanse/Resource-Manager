using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Infrastructure.ResourceBreakdown;
using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.Shared.BusinessBlocks.State;
using ResourceManager.Subscription;

namespace Resource_Manager_APP.Tests;

public sealed partial class WindowsResourceBreakdownSamplerLifecycleTests
{
    [Fact]
    public async Task OnlyVramBarSubscriptionsPublishTheVidMmResidualFieldInUse()
    {
        using var subscriptions = new HostManagerSamplingSubscriptionTestFixture();
        using var catalog = new VersionedProcessAttributionCatalogProvider();
        await using var harness = VidMmBusinessBlockTestHarness.Create();
        using var sampler = new WindowsResourceBreakdownSampler(new StaticMetricSampler(CreateMemorySnapshot()), catalog,
            new DxgkrnlVidMmEtwResidualBreakdownProvider(harness.Zone), null!, null!, subscriptions.Provider,
            subscriptions.Owner, new PdhProcessGpuReader(),
            new DelegateWindowsProcessInventoryReader(static () => []), gpuAdapterInventory: AllocationAdapterInventory);
        var reason = new BusinessBlockReason(BusinessBlockRequestKind.Configuration,
            nameof(WindowsResourceBreakdownSamplerLifecycleTests), "vram-field-changed", 0);
        var field = harness.Fixture.Authority.ClaimReadFunction<bool>(BackendItems.VramResidualInUse,
            () => StateRead<bool>.FromValue(subscriptions.Owner.Activity.HasInUseSubscriptions(SubscriptionFieldGroup.VramResidual)));
        var publication = field.NotifyChangedAsync(reason);
        void Changed(SubscriptionFieldGroup group)
        {
            if (group == SubscriptionFieldGroup.VramResidual)
                publication = field.NotifyChangedAsync(reason);
        }
        subscriptions.Owner.Activity.FieldChanged += Changed;

        bool FieldInUse() => Assert.IsType<bool>(Assert.Single(harness.Controller.Read().Items,
            item => item.ItemUid == BackendItems.VramResidualInUse).Value);
        using var dedicated = sampler.AcquireSubscription("process-memory", SchedulingProcessMetricMask.GpuDedicatedMemory,
            TimeSpan.FromSeconds(1));
        await publication.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(FieldInUse());
        Assert.Equal(0, harness.Sessions.Attempts);
        dedicated.Dispose();
        using var dataset = sampler.AcquireSubscription("process-vram-dataset",
            new ResourceBreakdownSampleRequest([], new Dictionary<string, string>(), ProcessSampleDetailLevel.ResourceTableBasic)
            { PublicationDatasetIds = [SamplingDatasetIds.ProcessGpuVram] }, TimeSpan.FromSeconds(1));
        await publication.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(FieldInUse());
        Assert.Equal(0, harness.Sessions.Attempts);

        var request = ResourceBreakdownSampleRequest.ForResourceTable(["gpu.0.vram"], new Dictionary<string, string>());
        var first = sampler.AcquireSubscription("vram-first", request, TimeSpan.FromSeconds(1));
        var second = sampler.AcquireSubscription("vram-second", request, TimeSpan.FromSeconds(5));
        try
        {
            await publication.WaitAsync(TimeSpan.FromSeconds(5));
            await harness.Fixture.WaitForStateAsync(BackendUids.DxgkrnlVidMmEtwTelemetryZone, BusinessBlockState.Running);
            await harness.Sessions.Current.ProcessEntered.WaitAsync(TimeSpan.FromSeconds(5));
            var running = Assert.Single(harness.Controller.Read().Blocks,
                block => block.Id == BackendUids.DxgkrnlVidMmEtwTelemetryZone);
            Assert.Equal(BusinessBlockState.Running, running.State);
            Assert.True(FieldInUse());
            Assert.Equal(1, harness.Sessions.Attempts);

            first.Dispose();
            await publication.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(FieldInUse());
            Assert.True(harness.Sessions.Current.ConsumerThread!.IsAlive);

            second.Dispose();
            await publication.WaitAsync(TimeSpan.FromSeconds(5));
            await harness.Fixture.WaitForStateAsync(BackendUids.DxgkrnlVidMmEtwTelemetryZone, BusinessBlockState.Stopped);
            await harness.Recorder.DrainAsync();
            Assert.False(FieldInUse());
            Assert.False(harness.Sessions.Current.ConsumerThread!.IsAlive);
            Assert.Equal(1, harness.Sessions.Current.DisposeCount);
        }
        finally
        {
            first.Dispose();
            second.Dispose();
            await publication.WaitAsync(TimeSpan.FromSeconds(5));
            subscriptions.Owner.Activity.FieldChanged -= Changed;
            await field.UnbindAsync(reason);
        }
    }
}
