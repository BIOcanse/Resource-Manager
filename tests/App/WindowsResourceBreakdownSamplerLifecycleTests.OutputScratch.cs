using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Application.ProcessAttribution;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Hosting;
using ResourceManager.App.Hosting.StartupCapabilities;
using ResourceManager.App.Infrastructure.Adaptation;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.ResourceBreakdown;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.Telemetry.Etw;
using ResourceManager.Subscription;

namespace Resource_Manager_APP.Tests;

public sealed partial class WindowsResourceBreakdownSamplerLifecycleTests
{
    [Fact]
    public void OriginalPublicElevenArgumentClrConstructorRemainsAvailableWithoutALedgerParameter()
    {
        var constructor = typeof(WindowsResourceBreakdownSampler).GetConstructor(
        [
            typeof(IMetricSampler), typeof(IRuntimeProcessAttributionCatalogProvider),
            typeof(IResourceResidualBreakdownProvider), typeof(IPhysicalDiskIoAttributionReader),
            typeof(INetworkAttributionReader), typeof(IRuntimePlanProvider),
            typeof(HostManagerSamplingSubscriptionOwner), typeof(PdhProcessGpuReader),
            typeof(ILogger<WindowsResourceBreakdownSampler>), typeof(GpuAllocationCollector),
            typeof(InteractiveDesktopObservationStore)
        ]);
        Assert.NotNull(constructor);
        Assert.Single(typeof(WindowsResourceBreakdownSampler).GetConstructors());
        var parameters = constructor!.GetParameters();
        Assert.Equal(11, parameters.Length);
        Assert.All(parameters.Take(8), parameter => Assert.False(parameter.IsOptional));
        Assert.All(parameters.Skip(8), parameter =>
        {
            Assert.True(parameter.IsOptional);
            Assert.Null(parameter.DefaultValue);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadOnlyMonitoringFactoryUsesTheExistingLedgerWithoutSelfOrAnOsPolicyWriter(bool namedMonitoringProfile)
    {
        // Monitoring is registered for None; it is not a new StartupCapability bit.
        var capabilities = namedMonitoringProfile
            ? new StartupCapabilitySet("recorded-monitoring-only", StartupCapability.None)
            : StartupCapabilitySet.NormalReadOnly;
        var declarations = new ServiceCollection();
        declarations.AddResourceManagerApp(["--no-native-ui"], capabilities);
        Assert.DoesNotContain(declarations, row => row.ServiceType == typeof(ResourceManagerSelfLocalResourceManager));
        Assert.DoesNotContain(declarations, row => row.ServiceType == typeof(IProcessResourcePolicyWriter));
        Assert.Single(declarations.Where(row => row.ServiceType == typeof(ResourceManagerLedgerOwner)));
        var production = Assert.Single(declarations.Where(row => row.ServiceType == typeof(WindowsResourceBreakdownSampler)));
        Assert.NotNull(production.ImplementationFactory);

        // Reuse the existing composition pattern: invoke the ACTUAL production
        // descriptor with cached source fixtures, without starting hardware owners.
        using var subscriptions = new HostManagerSamplingSubscriptionTestFixture();
        using var ledger = new ResourceManagerLedgerOwner();
        using var catalog = new VersionedProcessAttributionCatalogProvider();
        var hardware = new StaticMetricSampler(CreateMemorySnapshot());
        var disk = new RecordedPhysicalDiskReader(complete: false);
        var network = new MixedNetworkReader(completeWindow: false);
        IServiceCollection services = new ServiceCollection().AddLogging();
        services.Add(production);
        services.AddSingleton<IMetricSampler>(hardware);
        services.AddSingleton<IRuntimeProcessAttributionCatalogProvider>(catalog);
        services.AddSingleton<IResourceResidualBreakdownProvider>(new StaticResourceResidualBreakdownProvider());
        services.AddSingleton<IPhysicalDiskIoAttributionReader>(disk);
        services.AddSingleton<INetworkAttributionReader>(network);
        services.AddSingleton<IRuntimePlanProvider>(subscriptions.Provider);
        services.AddSingleton(subscriptions.Owner);
        services.AddSingleton(new PdhProcessGpuReader());
        services.AddSingleton(ledger);
        using var provider = services.BuildServiceProvider();
        var sampler = provider.GetRequiredService<WindowsResourceBreakdownSampler>();
        Assert.Same(ledger, provider.GetRequiredService<ResourceManagerLedgerOwner>());
        Assert.Null(provider.GetService<ResourceManagerSelfLocalResourceManager>());
        Assert.Null(provider.GetService<IProcessResourcePolicyWriter>());
        Assert.Equal(0UL, ledger.ReadTable().RegisteredBytes);
        await sampler.StartAsync(CancellationToken.None);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            if (sampler.CaptureSamplingWorkerTask() is { IsFaulted: true } failed) await failed;
            using var role = subscriptions.Owner.Acquire(3);
            var cap = role.Session.Capacity;
            var header = new ResourceManager.Subscription.Native.NativeSamplingSubscriptionSnapshotHeader
            {
                AbiVersion = ResourceManager.Subscription.Native.NativeSamplingSubscriptionAbi.Version,
                StructSize = (uint)System.Runtime.CompilerServices.Unsafe.SizeOf<ResourceManager.Subscription.Native.NativeSamplingSubscriptionSnapshotHeader>()
            };
            Assert.Equal(ResourceManager.Subscription.Native.NativeSamplingSubscriptionStatus.Ok,
                role.Session.Snapshot(ref header,
                    new ResourceManager.Subscription.Native.NativeSamplingSubscriptionSourceView[checked((int)cap.SourceCapacity)],
                    new ResourceManager.Subscription.Native.NativeSamplingSubscriptionItemState[checked((int)cap.ItemCapacity)]));
            if (header.LastPlanEpoch != 0) break;
            await Task.Delay(10, deadline.Token);
        }
        await sampler.StopAsync(deadline.Token);
        Assert.Equal(0UL, ledger.ReadTable().RegisteredBytes); // Empty real rows fit ordinary storage.
        Assert.Equal(0, hardware.CaptureCount);
        Assert.Equal(0, catalog.CaptureCount);
        Assert.Equal(0, disk.Reads);
        Assert.Equal(0, network.Reads);
        provider.Dispose();
        Assert.Equal(0UL, ledger.ReadTable().RegisteredBytes);
    }

    [Fact]
    public async Task DisposeDrainsTheOriginalWorkerBeforeClosingOutputsAndRetriesAFailedClose()
    {
        using var subscriptions = new HostManagerSamplingSubscriptionTestFixture();
        using var owner = new ResourceManagerLedgerOwner();
        using var catalogProvider = new VersionedProcessAttributionCatalogProvider();
        using var planEntered = new ManualResetEventSlim(false);
        using var releasePlan = new ManualResetEventSlim(false);
        var stores = new List<SamplingOutputScratchTests.RecordedScratch>();
        WindowsResourceBreakdownSampler? sampler = null;
        var factory = SamplingOutputScratchTests.Factory(owner, stores, store =>
        {
            store.FailCloseOnce = true;
            store.Completing = () =>
            {
                planEntered.Set();
                if (!releasePlan.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("release recorded output Plan");
            };
            store.Closing = () =>
            {
                Assert.Equal(0, store.LiveLeases);
                Assert.True(releasePlan.IsSet); // Plan/collection has drained before the worker returns its own output.
                if (store.CloseCalls > 1)
                {
                    Assert.Equal((0U, 0U), ReadResourceSubscriptionCounts(subscriptions.Owner));
                    Assert.False(subscriptions.Owner.Activity.HasInUseSubscriptions(3));
                }
            };
        });
        sampler = new WindowsResourceBreakdownSampler(new StaticMetricSampler(CreateMemorySnapshot()),
            catalogProvider, new StaticResourceResidualBreakdownProvider(), null!, null!,
            subscriptions.Provider, subscriptions.Owner, new PdhProcessGpuReader(),
            new DelegateWindowsProcessInventoryReader(static () => []), createOutputScratch: factory);
        try
        {
            using var lease = sampler.AcquireSubscription("close-outstanding",
                ResourceManager.App.Domain.ResourceBreakdown.ResourceBreakdownSampleRequest.ForResourceTable(
                    [ResourceManager.App.Domain.ResourceBreakdown.ResourceBreakdownMetricIds.MemoryUsage],
                    new Dictionary<string, string>()), TimeSpan.FromSeconds(1));
            await sampler.StartAsync(CancellationToken.None);
            Assert.True(planEntered.Wait(TimeSpan.FromSeconds(5)));
            using var disposalStarted = new ManualResetEventSlim(false);
            var disposal = Task.Run(() =>
            {
                disposalStarted.Set();
                Assert.Throws<IOException>(() => sampler.Dispose());
            });
            Assert.True(disposalStarted.Wait(TimeSpan.FromSeconds(5)));
            Assert.False(Assert.Single(stores).Closed);
            releasePlan.Set();
            await disposal.WaitAsync(TimeSpan.FromSeconds(5));
            var store = Assert.Single(stores);
            Assert.False(store.Closed);
            Assert.Equal(1, store.CloseCalls);
            // This first failure is the original run's output return. Final
            // subscription clearing is retried after that run has fully joined.
            Assert.True(ReadResourceSubscriptionCounts(subscriptions.Owner).ActiveSourceCount > 0);
            Assert.True(subscriptions.Owner.Activity.HasInUseSubscriptions(3));
            Assert.Throws<ObjectDisposedException>(() => { _ = sampler.StartAsync(CancellationToken.None); });
            sampler.Dispose(); // Admission remains closed, but the same real close obligation is retried.
            Assert.True(store.Closed);
            Assert.Equal(2, store.CloseCalls);
            Assert.Equal((0U, 0U), ReadResourceSubscriptionCounts(subscriptions.Owner));
            Assert.False(subscriptions.Owner.Activity.HasInUseSubscriptions(3));
            Assert.Null(Record.Exception(lease.Dispose));
            sampler.Dispose();
            Assert.Equal(2, store.CloseCalls); // Already released native/resource state is not closed twice.
        }
        finally
        {
            releasePlan.Set();
            sampler.Dispose();
        }
    }
}
