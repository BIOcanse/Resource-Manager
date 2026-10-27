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

namespace Resource_Manager_APP.Tests;

public sealed class BackendBlockProductionTreeTests
{
    private static readonly int[] FullHostedBlocks =
    [
        BackendUids.NativeEtwLifetimeService, BackendUids.TrustedAccessRequestOwner, BackendUids.TrustedAccessInvocationHostedService,
        BackendUids.RuntimeSpecializationCoordinator, BackendUids.HostManagerSamplingSubscriptionOwner, BackendUids.HostManagerMetricSnapshotOwner,
        BackendUids.HostManagerSoftwareIdentityOwner, BackendUids.HostManagerFileQueryOwner, BackendUids.HostManagerDisplayCoordinatorOwner,
        BackendUids.HostManagerPublicServiceCoordinatorOwner, BackendUids.HostManagerOperationCoordinatorOwner,
        BackendUids.SqlitePortableSoftwareRegistry,
        BackendUids.ServiceAutoStartSettings, BackendUids.ServiceRecoverySettings, BackendUids.ControlDesiredStateReassertion, BackendUids.FanCurveTemperatureFeed,
        BackendUids.ControlActualStateReader, BackendUids.GpuTelemetryWorkerSupervisor, BackendUids.WindowsHardwareMetricSampler, BackendUids.CpuTopologySnapshotProvider,
        BackendUids.FanControlCoreLifetimeService, BackendUids.KernelEtwSessionBroker, BackendUids.PortableSoftwareDiscoveryService, BackendUids.EtwCpuCoreResidencyReader,
        BackendUids.EtwSystemInterruptSnapshotSource, BackendUids.EtwPhysicalDiskIoAttributionReader, BackendUids.EtwNetworkTcpIpAttributionReader,
        BackendUids.WindowsResourceBreakdownSampler, BackendUids.GpuPlacementProcessHistoryObserver,
        BackendUids.SharedResourceSubscriptionMaintenanceService, BackendUids.JsonDebugDiagnosticLogWriter,
        BackendUids.HostManagerReportCoordinatorOwner, BackendUids.HostManagerSmartCoordinator, BackendUids.DeviceTopologySnapshotProvider, BackendUids.HostMemoryProtection
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
    public async Task ProductionShutdownDrainsSamplingWorkspaceConsumersBeforeStoppingTheirOwner()
    {
        var services = new ServiceCollection();
        ResourceManagerServiceCollectionExtensions.AddResourceManagerApp(services, ["--startup-profile=normal-read-only"]);
        var production = services.Select(value => value.ImplementationInstance).OfType<BusinessBlockRegistration>().ToArray();
        var graph = new BackendBlockGraph(production);
        var releaseCleanup = BusinessBlockTestSupport.Signal();
        var consumers = SamplingLifetimeConsumers.ToDictionary(id => id, _ => new GatedNativeProbe(releaseCleanup.Task));
        var probes = production.Select(registration => registration with
        {
            Create = _ => consumers.TryGetValue(registration.Id, out var consumer) ? consumer : new Probe()
        }).ToArray();
        using var provider = new ServiceCollection().BuildServiceProvider();
        var control = new BusinessBlockController(NullLogger<BusinessBlockController>.Instance, 256, _ => null);
        await using var scheduler = new BusinessBlockScheduler(provider, probes, Options.Create(new BusinessBlockOptions()),
            NullLogger<BusinessBlockScheduler>.Instance, BusinessBlockTestSupport.CreateAuthority(), control,
            [new(BackendUids.Program, [TestStateUids.Profile], input => input.Read<bool>(TestStateUids.Profile).GetValueOrDefault(false) ? graph.Children(BackendUids.Program) : [])]);
        var profileWriter = scheduler.Authority.ClaimWrite<bool>(TestStateUids.Profile);
        var reason = BusinessBlockTestSupport.Reason();
        try
        {
            foreach (var id in SamplingLifetimeConsumers) await scheduler.SetDemandAsync("sampling:" + id, id, reason);
            await profileWriter.PublishAsync(true, reason);
            await BusinessBlockTestSupport.Until(() => SamplingLifetimeConsumers.All(id => scheduler.ReadState(id) == BusinessBlockState.Running));
            await scheduler.ShutdownAsync(reason);
            await Task.WhenAll(consumers.Values.Select(consumer => consumer.StopNotified.Task)).WaitAsync(BusinessBlockTestSupport.TestTimeout);
            Assert.All(consumers.Values, consumer => Assert.False(consumer.CleanupCompleted));
            Assert.Equal(BusinessBlockState.Running, scheduler.ReadState(BackendUids.HostManagerSamplingSubscriptionOwner));
            Assert.All(SamplingLifetimeConsumers, id => Assert.Equal(BackendUids.HostManagerSamplingSubscriptionOwner,
                graph.Nodes.Single(node => node.Id == id).ParentId));
            releaseCleanup.TrySetResult();
            await BusinessBlockTestSupport.Until(() => scheduler.Read().Blocks.All(block => block.State == BusinessBlockState.Stopped));
            var history = control.Read().History;
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
    [InlineData(false, 38, 51, 28)]
    [InlineData(true, 39, 52, 29)]
    public async Task ProductionTreeContainsConcurrentFlowsAndDrainsChildrenBeforeTheirOwners(bool windowsService, int expectedCount, int expectedDataEdges, int expectedRoots)
    {
        var services = new ServiceCollection();
        ResourceManager.App.Infrastructure.Security.LoopbackApiPipelineConfigurator.Compile(false).ConfigureServices(services);
        ResourceManagerServiceCollectionExtensions.AddResourceManagerApp(services, ["--startup-profile=full"],
            StartupCapabilitySet.Full, BackendHostEnvironment.Compile(windowsService, windowsService));
        var production = services.Select(value => value.ImplementationInstance).OfType<BusinessBlockRegistration>().ToArray();
        var graph = new BackendBlockGraph(production);
        var rootBlocks = FullHostedBlocks.Except(SamplingLifetimeConsumers).ToList();
        StateRegistryTests.AssertBlocks("backend",
            graph.BlockIds.Prepend(BackendUids.Program), windowsService ? [] : [BackendUids.AutomaticProductUpdateService]);
        if (windowsService) rootBlocks.Add(BackendUids.AutomaticProductUpdateService);
        var hostedBlocks = rootBlocks.Concat(SamplingLifetimeConsumers).ToArray();
        Assert.Equal(expectedCount, graph.Nodes.Count);
        Assert.Equal(expectedDataEdges, graph.Nodes.Sum(node => node.Inputs.Count));
        Assert.DoesNotContain(production, node => node.HostedServiceType?.Name is "ResourceManagerDatabaseInitializer"
            or "SqliteLegacyDataImportHostedService" or "SharedMemoryPublicResourceBroker"
            or "TargetedRecordingService" or "GpuLaunchInterceptionReconciler");
        var serialFlows = services.Where(value => value.ServiceType == typeof(IHostedService)
            && value.ImplementationType?.IsGenericType == true
            && value.ImplementationType.GetGenericTypeDefinition() == typeof(SerialHostedService<>))
            .Select(value => value.ImplementationType!.GetGenericArguments()[0].Name).Order().ToArray();
        // KernelLeakObserver is the known pending Reports/KernelLeakObservation block (state authority 5c).
        // Keep the exception explicit so another unregistered concurrent flow cannot hide behind a count.
        Assert.Equal(new[] { "GpuLaunchInterceptionReconciler", "KernelLeakObserver",
            "ResourceManagerDatabaseInitializer", "SharedMemoryPublicResourceBroker",
            "SqliteLegacyDataImportHostedService", "TargetedRecordingService" }.Order(), serialFlows);
        Assert.DoesNotContain(production, node => node.HostedServiceType?.Name == "KernelLeakObserver");
        Assert.Contains(BackendUids.EtwNetworkTcpIpAttributionReader,
            graph.Nodes.Single(node => node.Id == BackendUids.WindowsResourceBreakdownSampler).Inputs);
        Assert.True(rootBlocks.ToHashSet().SetEquals(graph.Children(BackendUids.Program)));
        Assert.Equal(expectedRoots, graph.Children(BackendUids.Program).Count);
        Assert.True(NativeBlocks.ToHashSet().SetEquals(graph.Children(BackendUids.NativeEtwLifetimeService)));
        Assert.True(SamplingLifetimeConsumers.ToHashSet().SetEquals(graph.Children(BackendUids.HostManagerSamplingSubscriptionOwner)));
        Assert.All(graph.Nodes, node => Assert.Equal(NativeBlocks.Contains(node.Id)
            ? BackendUids.NativeEtwLifetimeService : SamplingLifetimeConsumers.Contains(node.Id)
                ? BackendUids.HostManagerSamplingSubscriptionOwner : BackendUids.Program, node.ParentId));

        // Exercise the real topology with inert entries; this never starts native sessions or product services.
        var probes = production.Select(registration => registration with { Create = _ => new Probe() }).ToArray();
        var authority = BusinessBlockTestSupport.CreateAuthority(graph.BlockIds);
        services.AddSingleton(authority);
        using var provider = services.BuildServiceProvider();
        var control = new BusinessBlockController(NullLogger<BusinessBlockController>.Instance, 256, _ => null);
        var nativeRule = provider.GetServices<BusinessBlockDecisionRegistration>()
            .Single(rule => rule.ParentId == BackendUids.NativeEtwLifetimeService);
        await using var scheduler = new BusinessBlockScheduler(provider, probes, Options.Create(new BusinessBlockOptions()),
            NullLogger<BusinessBlockScheduler>.Instance, authority, control,
            [new(BackendUids.Program, [TestStateUids.Profile], input => input.Read<bool>(TestStateUids.Profile).GetValueOrDefault(false) ? rootBlocks : []), nativeRule]);
        var profileWriter = scheduler.Authority.ClaimWrite<bool>(TestStateUids.Profile);
        var reason = new BusinessBlockReason(BusinessBlockRequestKind.Configuration, "host-profile", "full", 1);
        try
        {
            foreach (var id in SamplingLifetimeConsumers) await scheduler.SetDemandAsync("sampling:" + id, id, reason);
            await profileWriter.PublishAsync(true, reason);
            await BusinessBlockTestSupport.Until(() => hostedBlocks.All(id => scheduler.ReadState(id) == BusinessBlockState.Running));
            Assert.True(hostedBlocks.ToHashSet().SetEquals(control.Read().History.Where(row => row.To == BusinessBlockState.Starting).Select(row => row.BlockId)));
            Assert.Equal(BusinessBlockState.Running, scheduler.ReadState(BackendUids.NativeEtwLifetimeService));
            Assert.All(NativeBlocks, id => Assert.Equal(BusinessBlockState.Stopped, scheduler.ReadState(id)));
            foreach (var id in NativeBlocks) await scheduler.SetDemandAsync("native:" + id, id, reason);
            // The independent publisher initializes availability before the group runs; no group republish is needed.
            await BusinessBlockTestSupport.Until(() => NativeBlocks.All(id => scheduler.ReadState(id) == BusinessBlockState.Running));
            var started = control.Read().History;
            Assert.True(hostedBlocks.Concat(NativeBlocks).ToHashSet().SetEquals(started.Where(row => row.To == BusinessBlockState.Starting).Select(row => row.BlockId)));
            var groupRunning = started.Single(row => row.BlockId == BackendUids.NativeEtwLifetimeService && row.To == BusinessBlockState.Running).Sequence;
            Assert.All(started.Where(row => NativeBlocks.Contains(row.BlockId) && row.To == BusinessBlockState.Starting), row => Assert.True(row.Sequence > groupRunning));
            var samplingOwnerRunning = started.Single(row => row.BlockId == BackendUids.HostManagerSamplingSubscriptionOwner && row.To == BusinessBlockState.Running).Sequence;
            Assert.All(started.Where(row => SamplingLifetimeConsumers.Contains(row.BlockId) && row.To == BusinessBlockState.Starting), row => Assert.True(row.Sequence > samplingOwnerRunning));
            await scheduler.ShutdownAsync(reason);
            await BusinessBlockTestSupport.Until(() => scheduler.Read().Blocks.All(block => block.State == BusinessBlockState.Stopped));
            var stopped = control.Read().History;
            Assert.True(hostedBlocks.Concat(NativeBlocks).ToHashSet().SetEquals(stopped.Where(row => row.To == BusinessBlockState.Stopping).Select(row => row.BlockId)));
            var groupStop = stopped.Single(row => row.BlockId == BackendUids.NativeEtwLifetimeService && row.To == BusinessBlockState.Stopping).Sequence;
            Assert.All(stopped.Where(row => NativeBlocks.Contains(row.BlockId) && row.To == BusinessBlockState.Stopped), row => Assert.True(row.Sequence < groupStop));
            var samplingOwnerStop = stopped.Single(row => row.BlockId == BackendUids.HostManagerSamplingSubscriptionOwner && row.To == BusinessBlockState.Stopping).Sequence;
            Assert.All(stopped.Where(row => SamplingLifetimeConsumers.Contains(row.BlockId) && row.To == BusinessBlockState.Stopped), row => Assert.True(row.Sequence < samplingOwnerStop));
        }
        finally { await BusinessBlockTestSupport.StopAll(scheduler); }
    }

    [Fact]
    public async Task HostAwaitsSerialInitializationAndStopsTheWorkerBeforeItsSerialResourceOwner()
    {
        var initialization = new SerialInitialization();
        var worker = new FollowingWorker();
        var host = Host.CreateDefaultBuilder().ConfigureLogging(logging => logging.ClearProviders())
            .ConfigureServices(services =>
            {
                services.AddBusinessBlockController();
                services.AddSingleton<SerialInitialization>(_ => initialization);
                services.AddSerialHostedServiceAlias<SerialInitialization>();
                services.AddBusinessBlockHostedService<FollowingWorker>(125, [], _ => worker);
            }).ConfigureServices(BusinessBlockTestSupport.AddStateFixtures).Build();
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
        using var host = Host.CreateDefaultBuilder().ConfigureLogging(logging => logging.ClearProviders())
            .ConfigureServices(services =>
            {
                services.AddBusinessBlockController();
                services.AddSingleton(new BusinessBlockRegistration(134, [], _ => native)
                    { ParentId = BackendUids.NativeEtwLifetimeService });
            }).ConfigureServices(BusinessBlockTestSupport.AddStateFixtures).Build();
        var hosted = host.Services.GetServices<IHostedService>().ToArray();
        Assert.Collection(hosted, service => Assert.IsType<BusinessBlockDependencyLifetime>(service),
            service => Assert.IsType<NonOwningHostedService<NativeEtwLifetimeService>>(service));
        var scheduler = host.Services.GetRequiredService<BusinessBlockScheduler>();
        Task? stopping = null;
        try
        {
            await scheduler.SetDemandAsync("native-consumer", 134, BusinessBlockTestSupport.Reason());
            Assert.Equal(BusinessBlockState.Stopped, scheduler.ReadState(134));
            await host.StartAsync().WaitAsync(BusinessBlockTestSupport.TestTimeout);
            await native.Started.Task.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            Assert.Equal(BusinessBlockState.Running, scheduler.ReadState(BackendUids.NativeEtwLifetimeService));
            stopping = host.StopAsync();
            await native.StopNotified.Task.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            Assert.False(stopping.IsCompleted);
            Assert.Equal(BusinessBlockState.Stopping, scheduler.ReadState(134));
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
    public void ReadOnlyDiscoveryHasDiagnosticsWithoutAControlNode()
    {
        var services = new ServiceCollection();
        ResourceManager.App.Infrastructure.Security.LoopbackApiPipelineConfigurator.Compile(false).ConfigureServices(services);
        ResourceManagerServiceCollectionExtensions.AddResourceManagerApp(services, ["--startup-profile=normal-read-only"]);
        var declarations = services.Select(value => value.ImplementationInstance).OfType<BusinessBlockRegistration>().ToArray();
        var graph = new BackendBlockGraph(declarations);
        Assert.Equal(25, graph.Nodes.Count);
        Assert.Equal(28, graph.Nodes.Sum(node => node.Inputs.Count));
        Assert.Equal(15, graph.Children(BackendUids.Program).Count);
        Assert.True(SamplingLifetimeConsumers.ToHashSet().SetEquals(graph.Children(BackendUids.HostManagerSamplingSubscriptionOwner)));
        Assert.True(NativeBlocks.ToHashSet().SetEquals(graph.Children(BackendUids.NativeEtwLifetimeService)));
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
