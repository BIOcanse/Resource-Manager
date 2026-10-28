using ResourceManager.App.Infrastructure.Telemetry.Etw;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Hosting;
using ResourceManager.App.Hosting.StartupCapabilities;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.StateItems;

namespace Resource_Manager_APP.Tests;

public sealed class BackendBlockProductionTreeTests
{
    private static readonly int[] FullHostedBlocks =
    [
        BackendUids.NativeEtwLifetimeService, BackendUids.TrustedAccessRequestOwner, BackendUids.TrustedAccessInvocationHostedService,
        BackendUids.RuntimeSpecializationCoordinator, BackendUids.HostManagerSamplingSubscriptionOwner, BackendUids.HostManagerPublicServiceCoordinatorOwner, BackendUids.HostManagerOperationCoordinatorOwner,
        BackendUids.SqlitePortableSoftwareRegistry,
        BackendUids.ServiceAutoStartSettings, BackendUids.ServiceRecoverySettings, BackendUids.ControlDesiredStateReassertion, BackendUids.FanCurveTemperatureFeed,
        BackendUids.ControlActualStateReader, BackendUids.GpuTelemetryWorkerSupervisor, BackendUids.WindowsHardwareMetricSampler, BackendUids.CpuTopologySnapshotProvider,
        BackendUids.FanControlCoreLifetimeService, BlockUids.HardwareBridgeProcessOwner, BackendUids.PortableSoftwareDiscoveryService, BackendUids.EtwCpuCoreResidencyReader,
        BackendUids.EtwSystemInterruptSnapshotSource, BackendUids.EtwPhysicalDiskIoAttributionReader, BackendUids.EtwNetworkTcpIpAttributionReader,
        BackendUids.WindowsResourceBreakdownSampler, BackendUids.GpuPlacementProcessHistoryObserver,
        BackendUids.SharedResourceSubscriptionMaintenanceService, BackendUids.JsonDebugDiagnosticLogWriter,
        BackendUids.HostManagerReportCoordinatorOwner, BackendUids.HostManagerSmartCoordinator, BackendUids.DeviceTopologySnapshotProvider, BackendUids.HostMemoryProtection,
        BackendUids.KernelLeakObservation
    ];
    private static readonly int[] NativeBlocks = [BackendUids.PresentFrameEtwSource, BackendUids.GpuAllocationCollector, BackendUids.DxgkrnlVidMmEtwTelemetryZone];
    private static readonly int[] SamplingLifetimeConsumers =
    [
        BackendUids.GpuTelemetryWorkerSupervisor, BackendUids.WindowsHardwareMetricSampler,
        BackendUids.CpuTopologySnapshotProvider, BackendUids.EtwCpuCoreResidencyReader,
        BackendUids.EtwSystemInterruptSnapshotSource, BackendUids.WindowsResourceBreakdownSampler,
        BackendUids.DeviceTopologySnapshotProvider
    ];

    [Fact]
    public async Task FanCurveDrainsBeforeFanCoreCanCloseItsSession()
    {
        var releaseCleanup = BusinessBlockTestSupport.Signal();
        var curve = new GatedNativeProbe(releaseCleanup.Task);
        await using var fixture = NativeBackendBlockTestSupport.CreateFullTreeFixture(
            new Dictionary<int, IBusinessBlock> { [BackendUids.FanCurveTemperatureFeed] = curve });
        var reason = BusinessBlockTestSupport.Reason();
        try
        {
            var facts = new BackendPreciseRuleTestFacts(fixture.Authority, fixture.Native);
            await facts.InitializeAsync();
            await facts.PublishAsync(BackendItems.FanCommandPending, true);
            await facts.PublishAsync(BackendItems.SoftwareCurveRunNeeded, true);
            await curve.Started.Task.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            await fixture.Scheduler.ShutdownAsync(reason);
            await curve.StopNotified.Task.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            Assert.False(curve.CleanupCompleted);
            Assert.Equal(BusinessBlockState.Running, fixture.Scheduler.ReadState(BackendUids.FanControlCoreLifetimeService));
            Assert.Equal(BusinessBlockState.Running, fixture.Scheduler.ReadState(BackendUids.HardwareControl));
            Assert.Equal(BusinessBlockState.Running, fixture.Scheduler.ReadState(BackendUids.Backend));
            releaseCleanup.TrySetResult();
            await BusinessBlockTestSupport.Until(() => fixture.Scheduler.Read().Blocks.All(row => row.State == BusinessBlockState.Stopped));
            var history = fixture.Scheduler.Read().History;
            var curveStopped = history.Single(row => row.BlockId == BackendUids.FanCurveTemperatureFeed && row.To == BusinessBlockState.Stopped).Sequence;
            var coreStopping = history.Single(row => row.BlockId == BackendUids.FanControlCoreLifetimeService && row.To == BusinessBlockState.Stopping).Sequence;
            var coreReturned = history.Single(row => row.BlockId == BackendUids.FanControlCoreLifetimeService && row.To == BusinessBlockState.Stopped).Sequence;
            Assert.True(curveStopped < coreStopping && coreStopping < coreReturned);
        }
        finally { releaseCleanup.TrySetResult(); }
    }

    [Fact]
    public async Task ProductionShutdownDrainsSamplingWorkspaceConsumersBeforeStoppingTheirOwner()
    {
        var services = new ServiceCollection();
        ResourceManager.App.Infrastructure.Security.LoopbackApiPipelineConfigurator.Compile(false).ConfigureServices(services);
        ResourceManagerServiceCollectionExtensions.AddResourceManagerApp(services, ["--startup-profile=normal-read-only"]);
        var production = services.Select(value => value.ImplementationInstance).OfType<BusinessBlockRegistration>().ToArray();
        var releaseCleanup = BusinessBlockTestSupport.Signal();
        var consumers = SamplingLifetimeConsumers.ToDictionary(id => id, _ => new GatedNativeProbe(releaseCleanup.Task));
        var probes = production.Select(registration => registration with
        {
            Create = _ => consumers.TryGetValue(registration.Id, out var consumer) ? consumer : new Probe()
        }).ToArray();
        await using var native = NativeBackendBlockTestSupport.CreateControl(probes, profile: 3);
        var graph = native.Graph;
        var authority = NativeBackendBlockTestSupport.CreateAuthority(native);
        var availability = new NativeEtwSessionAvailability(authority);
        using var provider = new ServiceCollection().AddSingleton(availability).BuildServiceProvider();
        var preciseFacts = new BackendPreciseRuleTestFacts(authority, native);
        await preciseFacts.InitializeAsync();
        await using var scheduler = new BusinessBlockScheduler(provider, probes, native, authority);
        var reason = BusinessBlockTestSupport.Reason();
        try
        {
            foreach (var uid in new[] { BackendItems.SamplingGpuTelemetryInUse, BackendItems.SamplingHardwareMetricsInUse,
                BackendItems.SamplingCpuTopologyInUse, BackendItems.SamplingCpuResidencyInUse, BackendItems.SamplingInterruptsInUse,
                BackendItems.SamplingResourceBreakdownInUse, BackendItems.SamplingDeviceTopologyInUse })
                await preciseFacts.PublishAsync(uid, true);
            await BusinessBlockTestSupport.Until(() => SamplingLifetimeConsumers.All(id => scheduler.ReadState(id) == BusinessBlockState.Running));
            await scheduler.ShutdownAsync(reason);
            await Task.WhenAll(consumers.Values.Select(consumer => consumer.StopNotified.Task)).WaitAsync(BusinessBlockTestSupport.TestTimeout);
            Assert.All(consumers.Values, consumer => Assert.False(consumer.CleanupCompleted));
            Assert.Equal(BusinessBlockState.Running, scheduler.ReadState(BackendUids.HostManagerSamplingSubscriptionOwner));
            Assert.All(SamplingLifetimeConsumers, id => Assert.Equal(BackendUids.HostManagerSamplingSubscriptionOwner,
                graph.Nodes.Single(node => node.Id == id).ParentId));
            releaseCleanup.TrySetResult();
            await BusinessBlockTestSupport.Until(() => scheduler.Read().Blocks.All(block => block.State == BusinessBlockState.Stopped));
            var history = scheduler.Read().History;
            var ownerStop = history.Single(row => row.BlockId == BackendUids.HostManagerSamplingSubscriptionOwner
                && row.To == BusinessBlockState.Stopping).Sequence;
            Assert.All(history.Where(row => SamplingLifetimeConsumers.Contains(row.BlockId) && row.To == BusinessBlockState.Stopped),
                row => Assert.True(row.Sequence < ownerStop));
        }
        finally
        {
            releaseCleanup.TrySetResult();
            await BusinessBlockTestSupport.StopAll(scheduler);
        }
    }

    [Theory]
    [InlineData(false, 44, 42)]
    [InlineData(true, 45, 43)]
    public async Task ProductionTreeContainsConcurrentFlowsAndDrainsChildrenBeforeTheirOwners(bool windowsService, int expectedCount, int expectedDataEdges)
    {
        var services = new ServiceCollection();
        ResourceManager.App.Infrastructure.Security.LoopbackApiPipelineConfigurator.Compile(false).ConfigureServices(services);
        ResourceManagerServiceCollectionExtensions.AddResourceManagerApp(services, ["--startup-profile=full"],
            StartupCapabilitySet.Full, BackendHostEnvironment.Compile(windowsService, windowsService));
        var production = services.Select(value => value.ImplementationInstance).OfType<BusinessBlockRegistration>().ToArray();
        var probes = production.Select(registration => registration with { Create = _ => new Probe() }).ToArray();
        await using var native = NativeBackendBlockTestSupport.CreateControl(probes, windowsService ? 2u : 1u);
        var graph = native.Graph;
        var admittedHostedBlocks = FullHostedBlocks.ToList();
        StateRegistryTests.AssertBlocks("backend",
            graph.BlockIds.Prepend(BackendUids.Program), windowsService ? [] : [BackendUids.AutomaticProductUpdateService]);
        if (windowsService) admittedHostedBlocks.Add(BackendUids.AutomaticProductUpdateService);
        var hostedBlocks = admittedHostedBlocks.Where(id => id != BackendUids.NativeEtwLifetimeService)
            .Concat(BackendExpectedProductionTree.Wrappers).ToArray();
        Assert.Equal(expectedCount, graph.Nodes.Count);
        Assert.Equal(expectedDataEdges, graph.Nodes.Sum(node => node.Inputs.Count));
        Assert.DoesNotContain(production, node => node.HostedServiceType?.Name is "ResourceManagerDatabaseInitializer"
            or "SqliteLegacyDataImportHostedService" or "SharedMemoryPublicResourceBroker"
            or "TargetedRecordingService" or "GpuLaunchInterceptionReconciler");
        var serialFlows = services.Where(value => value.ServiceType == typeof(IHostedService)
            && value.ImplementationType?.IsGenericType == true
            && value.ImplementationType.GetGenericTypeDefinition() == typeof(SerialHostedService<>))
            .Select(value => value.ImplementationType!.GetGenericArguments()[0].Name).Order().ToArray();
        // KernelLeakObserver's serial turn only binds the plan publisher; its concurrent flow is registered separately.
        Assert.Equal(new[] { "GpuLaunchInterceptionReconciler", "KernelLeakObserver",
            "ResourceManagerDatabaseInitializer", "SharedMemoryPublicResourceBroker",
            "SqliteLegacyDataImportHostedService", "TargetedRecordingService" }.Order(), serialFlows);
        Assert.DoesNotContain(production, node => node.HostedServiceType?.Name == "KernelLeakObserver");
        Assert.Contains(production, node => node.Id == BackendUids.KernelLeakObservation);
        Assert.Contains(BackendUids.EtwNetworkTcpIpAttributionReader,
            graph.Nodes.Single(node => node.Id == BackendUids.WindowsResourceBreakdownSampler).Inputs);
        Assert.Equal([BackendUids.Backend], graph.Children(BackendUids.Program));
        Assert.True(NativeBlocks.ToHashSet().SetEquals(graph.Children(BackendUids.NativeEtwLifetimeService)));
        Assert.True(SamplingLifetimeConsumers.ToHashSet().SetEquals(graph.Children(BackendUids.HostManagerSamplingSubscriptionOwner)));
        var expectedParents = BackendExpectedProductionTree.Parents(windowsService ? 2u : 1u);
        Assert.All(graph.Nodes, node => Assert.Equal(expectedParents[node.Id], node.ParentId));

        // Exercise the real topology with inert entries; this never starts native sessions or product services.
        var authority = NativeBackendBlockTestSupport.CreateAuthority(native);
        var availability = new NativeEtwSessionAvailability(authority);
        using var provider = new ServiceCollection().AddSingleton(availability).BuildServiceProvider();
        var preciseFacts = new BackendPreciseRuleTestFacts(authority, native);
        await preciseFacts.InitializeAsync();
        await using var scheduler = new BusinessBlockScheduler(provider, probes, native, authority);
        var reason = new BusinessBlockReason(BusinessBlockRequestKind.Configuration, "host-profile", "full", 1);
        try
        {
            foreach (var key in preciseFacts.Keys.Where(key => key.ItemUid != BackendItems.HostStartPermitted
                && key.ItemUid != BackendItems.GraphicsApiObservationInUse && key.ItemUid != BackendItems.ProcessDedicatedGpuMemoryInUse
                && key.ItemUid != BackendItems.VramResidualInUse)) await preciseFacts[key].PublishAsync(true, reason);
            await BusinessBlockTestSupport.Until(() => hostedBlocks.All(id => scheduler.ReadState(id) == BusinessBlockState.Running));
            Assert.True(hostedBlocks.ToHashSet().SetEquals(scheduler.Read().History.Where(row => row.To == BusinessBlockState.Starting).Select(row => row.BlockId)));
            Assert.Equal(BusinessBlockState.Stopped, scheduler.ReadState(BackendUids.NativeEtwLifetimeService));
            Assert.All(NativeBlocks, id => Assert.Equal(BusinessBlockState.Stopped, scheduler.ReadState(id)));
            foreach (var uid in new[] { BackendItems.GraphicsApiObservationInUse, BackendItems.ProcessDedicatedGpuMemoryInUse, BackendItems.VramResidualInUse })
                await preciseFacts.PublishAsync(uid, true);
            // The independent publisher initializes availability before the group runs; no group republish is needed.
            await BusinessBlockTestSupport.Until(() => NativeBlocks.All(id => scheduler.ReadState(id) == BusinessBlockState.Running));
            var started = scheduler.Read().History;
            Assert.True(hostedBlocks.Append(BackendUids.NativeEtwLifetimeService).Concat(NativeBlocks).ToHashSet().SetEquals(started.Where(row => row.To == BusinessBlockState.Starting).Select(row => row.BlockId)));
            var groupRunning = started.Single(row => row.BlockId == BackendUids.NativeEtwLifetimeService && row.To == BusinessBlockState.Running).Sequence;
            Assert.All(started.Where(row => NativeBlocks.Contains(row.BlockId) && row.To == BusinessBlockState.Starting), row => Assert.True(row.Sequence > groupRunning));
            var samplingOwnerRunning = started.Single(row => row.BlockId == BackendUids.HostManagerSamplingSubscriptionOwner && row.To == BusinessBlockState.Running).Sequence;
            Assert.All(started.Where(row => SamplingLifetimeConsumers.Contains(row.BlockId) && row.To == BusinessBlockState.Starting), row => Assert.True(row.Sequence > samplingOwnerRunning));
            Assert.Equal([BackendUids.Backend], Assert.Single(scheduler.Read().CurrentDecisions,
                row => row.ParentId == BackendUids.Program).ShouldRun);
            await scheduler.ShutdownAsync(reason);
            await BusinessBlockTestSupport.Until(() => scheduler.Read().Blocks.All(block => block.State == BusinessBlockState.Stopped));
            var stopped = scheduler.Read().History;
            Assert.True(hostedBlocks.Append(BackendUids.NativeEtwLifetimeService).Concat(NativeBlocks).ToHashSet().SetEquals(stopped.Where(row => row.To == BusinessBlockState.Stopping).Select(row => row.BlockId)));
            var groupStop = stopped.Single(row => row.BlockId == BackendUids.NativeEtwLifetimeService && row.To == BusinessBlockState.Stopping).Sequence;
            Assert.All(stopped.Where(row => NativeBlocks.Contains(row.BlockId) && row.To == BusinessBlockState.Stopped), row => Assert.True(row.Sequence < groupStop));
            var samplingOwnerStop = stopped.Single(row => row.BlockId == BackendUids.HostManagerSamplingSubscriptionOwner && row.To == BusinessBlockState.Stopping).Sequence;
            Assert.All(stopped.Where(row => SamplingLifetimeConsumers.Contains(row.BlockId) && row.To == BusinessBlockState.Stopped), row => Assert.True(row.Sequence < samplingOwnerStop));
            // Every ownership edge drains bottom-up, including domain wrappers and Backend.
            foreach (var node in graph.Nodes.Where(node => node.ParentId != BackendUids.Program))
            {
                var childStopped = stopped.Single(row => row.BlockId == node.Id && row.To == BusinessBlockState.Stopped).Sequence;
                var parentStopping = stopped.Single(row => row.BlockId == node.ParentId && row.To == BusinessBlockState.Stopping).Sequence;
                Assert.True(childStopped < parentStopping, $"Child {node.Id} must drain before parent {node.ParentId} stops.");
            }
            Assert.Empty(Assert.Single(scheduler.Read().CurrentDecisions,
                row => row.ParentId == BackendUids.Program).ShouldRun);
        }
        finally { await BusinessBlockTestSupport.StopAll(scheduler); }
    }

    [Fact]
    public async Task HostAwaitsSerialInitializationAndStopsTheWorkerBeforeItsSerialResourceOwner()
    {
        var initialization = new SerialInitialization();
        var worker = new FollowingWorker();
        const int workerBlockId = BackendUids.RuntimeSpecializationCoordinator;
        var productionServices = new ServiceCollection();
        ResourceManager.App.Infrastructure.Security.LoopbackApiPipelineConfigurator.Compile(false).ConfigureServices(productionServices);
        ResourceManagerServiceCollectionExtensions.AddResourceManagerApp(productionServices, ["--startup-profile=full"]);
        var production = productionServices.Select(value => value.ImplementationInstance).OfType<BusinessBlockRegistration>().ToArray();
        await using var native = NativeBackendBlockTestSupport.CreateControl(production);
        var authority = NativeBackendBlockTestSupport.CreateAuthority(native);
        var host = Host.CreateDefaultBuilder().ConfigureLogging(logging => logging.ClearProviders())
            .ConfigureServices(services =>
            {
                services.AddBusinessBlockController();
                services.AddSingleton(native);
                services.AddSingleton(authority);
                services.AddSingleton(new NativeEtwSessionAvailability(authority));
                foreach (var registration in production.Where(value => value.Id != BackendUids.NativeEtwLifetimeService
                    && value.Id != workerBlockId))
                    services.AddSingleton(registration with { Create = _ => new Probe() });
                services.AddSingleton<SerialInitialization>(_ => initialization);
                services.AddSerialHostedServiceAlias<SerialInitialization>();
                services.AddBusinessBlockHostedService<FollowingWorker>(workerBlockId, _ => worker);
            }).Build();
        Task? start = null;
        Task? stop = null;
        try
        {
            var options = host.Services.GetRequiredService<IOptions<HostOptions>>().Value;
            Assert.False(options.ServicesStartConcurrently);
            Assert.False(options.ServicesStopConcurrently);
            var declarations = host.Services.GetServices<BusinessBlockRegistration>();
            Assert.DoesNotContain(declarations, declaration => declaration.HostedServiceType == typeof(SerialInitialization));
            start = host.StartAsync();
            await initialization.Started.Task.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            Assert.False(start.IsCompleted);
            Assert.False(worker.Started.Task.IsCompleted);
            initialization.ReleaseStart.TrySetResult();
            await start.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            await worker.Started.Task.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            stop = host.StopAsync();
            await worker.StopNotified.Task.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            Assert.False(stop.IsCompleted);
            Assert.False(initialization.Stopped);
            worker.ReleaseStop.TrySetResult();
            await stop.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            Assert.True(initialization.Stopped);
            Assert.Equal(0, initialization.DisposeCount);
        }
        finally
        {
            initialization.ReleaseStart.TrySetResult();
            worker.ReleaseStop.TrySetResult();
            if (start is not null) await start.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            await (stop ?? host.StopAsync()).WaitAsync(BusinessBlockTestSupport.TestTimeout);
            host.Dispose();
        }
        Assert.Equal(1, initialization.DisposeCount);
    }

    [Fact]
    public async Task HostStopWaitsForNativeCleanupThroughItsOrdinaryGroupHostTurn()
    {
        var releaseCleanup = BusinessBlockTestSupport.Signal();
        var native = new GatedNativeProbe(releaseCleanup.Task);
        var productionServices = new ServiceCollection();
        ResourceManager.App.Infrastructure.Security.LoopbackApiPipelineConfigurator.Compile(false).ConfigureServices(productionServices);
        ResourceManagerServiceCollectionExtensions.AddResourceManagerApp(productionServices, ["--startup-profile=full"]);
        var production = productionServices.Select(value => value.ImplementationInstance).OfType<BusinessBlockRegistration>().ToArray();
        await using var nativeControl = NativeBackendBlockTestSupport.CreateControl(production);
        var authority = NativeBackendBlockTestSupport.CreateAuthority(nativeControl);
        using var host = Host.CreateDefaultBuilder().ConfigureLogging(logging => logging.ClearProviders())
            .ConfigureServices(services =>
            {
                services.AddBusinessBlockController();
                services.AddSingleton(nativeControl);
                services.AddSingleton(authority);
                services.AddSingleton(new NativeEtwSessionAvailability(authority));
                foreach (var registration in production.Where(value => value.Id != BackendUids.NativeEtwLifetimeService))
                    services.AddSingleton(registration with
                    {
                        Create = _ => registration.Id == BackendUids.GpuAllocationCollector ? native : new Probe()
                    });
            }).Build();
        var hosted = host.Services.GetServices<IHostedService>().ToArray();
        Assert.Collection(hosted, service => Assert.IsType<BusinessBlockDependencyLifetime>(service),
            service => Assert.IsType<NonOwningHostedService<NativeEtwLifetimeService>>(service));
        var scheduler = host.Services.GetRequiredService<BusinessBlockScheduler>();
        Task? stopping = null;
        try
        {
            var preciseFacts = new BackendPreciseRuleTestFacts(authority, nativeControl, hostPermits: false);
            await preciseFacts.InitializeAsync();
            await preciseFacts.PublishAsync(BackendItems.ProcessDedicatedGpuMemoryInUse, true);
            Assert.Equal(BusinessBlockState.Stopped, scheduler.ReadState(BackendUids.GpuAllocationCollector));
            await host.StartAsync().WaitAsync(BusinessBlockTestSupport.TestTimeout);
            await native.Started.Task.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            Assert.Equal(BusinessBlockState.Running, scheduler.ReadState(BackendUids.NativeEtwLifetimeService));
            stopping = host.StopAsync();
            await native.StopNotified.Task.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            Assert.False(stopping.IsCompleted);
            Assert.Equal(BusinessBlockState.Stopping, scheduler.ReadState(BackendUids.GpuAllocationCollector));
            Assert.Equal(BusinessBlockState.Running, scheduler.ReadState(BackendUids.NativeEtwLifetimeService));
            Assert.False(native.CleanupCompleted);
            releaseCleanup.TrySetResult();
            await stopping.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            await BusinessBlockTestSupport.Until(() => scheduler.Read().Blocks.All(block => block.State == BusinessBlockState.Stopped));
            Assert.True(native.CleanupCompleted);
        }
        finally
        {
            releaseCleanup.TrySetResult();
            await (stopping ?? host.StopAsync()).WaitAsync(BusinessBlockTestSupport.TestTimeout);
        }
    }

    [Fact]
    public async Task ReadOnlyDiscoveryHasDiagnosticsWithoutAControlNode()
    {
        var services = new ServiceCollection();
        ResourceManager.App.Infrastructure.Security.LoopbackApiPipelineConfigurator.Compile(false).ConfigureServices(services);
        ResourceManagerServiceCollectionExtensions.AddResourceManagerApp(services, ["--startup-profile=normal-read-only"]);
        var declarations = services.Select(value => value.ImplementationInstance).OfType<BusinessBlockRegistration>().ToArray();
        await using var native = NativeBackendBlockTestSupport.CreateControl(declarations, profile: 3);
        var graph = native.Graph;
        var readOnlyIds = BackendNativeControlFactory.WithWrappers(declarations, graph.BlockIds)
            .Select(value => value.Id).ToHashSet();
        Assert.Equal(BackendUids.Program, graph.Program);
        Assert.True(readOnlyIds.SetEquals(graph.BlockIds));
        Assert.DoesNotContain(graph.BlockIds, id => id == graph.Program);
        var readOnlyNodes = graph.Nodes;
        Assert.Equal(28, readOnlyNodes.Count);
        Assert.Equal(20, readOnlyNodes.Sum(node => node.Inputs.Count));
        Assert.Equal([BackendUids.Backend], graph.Children(BackendUids.Program));
        Assert.True(SamplingLifetimeConsumers.ToHashSet().SetEquals(readOnlyNodes
            .Where(node => node.ParentId == BackendUids.HostManagerSamplingSubscriptionOwner).Select(node => node.Id)));
        Assert.True(NativeBlocks.ToHashSet().SetEquals(readOnlyNodes
            .Where(node => node.ParentId == BackendUids.NativeEtwLifetimeService).Select(node => node.Id)));
        var observations = services.Select(value => value.ImplementationInstance).OfType<BusinessBlockDiagnosticRegistration>().ToArray();
        Assert.DoesNotContain(declarations, value => value.Id == BackendUids.PortableSoftwareDiscoveryService);
        var diagnostics = new BusinessBlockDiagnosticsRegistry(declarations, NullLogger<BusinessBlockDiagnosticsRegistry>.Instance, observations);
        using (diagnostics.MeasureSegment(BackendUids.PortableSoftwareDiscoveryService)) { }
        Assert.NotNull(diagnostics.Read(BackendUids.PortableSoftwareDiscoveryService));
        Assert.Throws<KeyNotFoundException>(() => diagnostics.Read(153));
    }

    private sealed class Probe : IBusinessBlock
    {
        public async Task RunAsync(BusinessBlockRunContext context)
        {
            context.ReportRunning();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, context.StopNotification); }
            catch (OperationCanceledException) when (context.StopNotification.IsCancellationRequested) { }
        }
    }
    private sealed class SerialInitialization : IHostedService, IDisposable
    {
        public TaskCompletionSource Started { get; } = BusinessBlockTestSupport.Signal();
        public TaskCompletionSource ReleaseStart { get; } = BusinessBlockTestSupport.Signal();
        public bool Stopped { get; private set; }
        public int DisposeCount { get; private set; }
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await ReleaseStart.Task.WaitAsync(cancellationToken);
        }
        public Task StopAsync(CancellationToken cancellationToken) { Stopped = true; return Task.CompletedTask; }
        public void Dispose() => DisposeCount++;
    }
    private sealed class FollowingWorker : BackgroundService
    {
        public TaskCompletionSource Started { get; } = BusinessBlockTestSupport.Signal();
        public TaskCompletionSource StopNotified { get; } = BusinessBlockTestSupport.Signal();
        public TaskCompletionSource ReleaseStop { get; } = BusinessBlockTestSupport.Signal();
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            Started.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            StopNotified.TrySetResult();
            await ReleaseStop.Task;
        }
    }
    private sealed class GatedNativeProbe(Task releaseCleanup) : IBusinessBlock
    {
        public TaskCompletionSource Started { get; } = BusinessBlockTestSupport.Signal();
        public TaskCompletionSource StopNotified { get; } = BusinessBlockTestSupport.Signal();
        public bool CleanupCompleted { get; private set; }
        public async Task RunAsync(BusinessBlockRunContext context)
        {
            context.ReportRunning();
            Started.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, context.StopNotification); }
            catch (OperationCanceledException) when (context.StopNotification.IsCancellationRequested) { }
            StopNotified.TrySetResult();
            await releaseCleanup;
            CleanupCompleted = true;
        }
    }
}
