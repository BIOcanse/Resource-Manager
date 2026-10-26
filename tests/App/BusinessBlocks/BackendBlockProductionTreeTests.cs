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
    private static readonly string[] FullHostedBlocks =
    [
        "NativeEtwLifetimeService", "TrustedAccessRequestOwner", "TrustedAccessInvocationHostedService",
        "RuntimeSpecializationCoordinator", "HostManagerSamplingSubscriptionOwner", "HostManagerMetricSnapshotOwner",
        "HostManagerSoftwareIdentityOwner", "HostManagerFileQueryOwner", "HostManagerDisplayCoordinatorOwner",
        "HostManagerPublicServiceCoordinatorOwner", "HostManagerOperationCoordinatorOwner",
        "SqlitePortableSoftwareRegistry",
        "ServiceAutoStartSettings", "ServiceRecoverySettings", "ControlDesiredStateReassertion", "FanCurveTemperatureFeed",
        "ControlActualStateReader", "GpuTelemetryWorkerSupervisor", "WindowsHardwareMetricSampler", "CpuTopologySnapshotProvider",
        "FanControlCoreLifetimeService", "KernelEtwSessionBroker", "PortableSoftwareDiscoveryService", "EtwCpuCoreResidencyReader",
        "EtwSystemInterruptSnapshotSource", "EtwPhysicalDiskIoAttributionReader", "EtwNetworkTcpIpAttributionReader",
        "WindowsResourceBreakdownSampler", "GpuPlacementProcessHistoryObserver",
        "SharedResourceSubscriptionMaintenanceService", "JsonDebugDiagnosticLogWriter",
        "HostManagerReportCoordinatorOwner", "HostManagerSmartCoordinator", "DeviceTopologySnapshotProvider"
    ];
    private static readonly string[] NativeBlocks = ["present-etw", "gpu-allocation-etw", "DxgkrnlVidMmEtwTelemetryZone"];
    private static readonly string[] SamplingLifetimeConsumers =
    [
        BackendBlockIds.GpuTelemetryWorkerSupervisor, BackendBlockIds.WindowsHardwareMetricSampler,
        BackendBlockIds.CpuTopologySnapshotProvider, BackendBlockIds.EtwCpuCoreResidencyReader,
        BackendBlockIds.EtwSystemInterruptSnapshotSource, BackendBlockIds.WindowsResourceBreakdownSampler,
        BackendBlockIds.DeviceTopologySnapshotProvider
    ];

    [Fact]
    public async Task ProductionShutdownDrainsSamplingWorkspaceConsumersBeforeStoppingTheirOwner()
    {
        var services = new ServiceCollection();
        ResourceManagerServiceCollectionExtensions.AddResourceManagerApp(services, ["--startup-profile=normal-read-only"]);
        var production = services.Select(value => value.ImplementationInstance).OfType<BusinessBlockRegistration>().ToArray();
        var graph = new BackendBlockGraph(production);
        var releaseCleanup = BusinessBlockTestSupport.Signal();
        var consumers = SamplingLifetimeConsumers.ToDictionary(id => id, _ => new GatedNativeProbe(releaseCleanup.Task), StringComparer.Ordinal);
        var probes = production.Select(registration => registration with
        {
            Create = _ => consumers.TryGetValue(registration.Id, out var consumer) ? consumer : new Probe()
        }).ToArray();
        using var provider = new ServiceCollection().BuildServiceProvider();
        var control = new BusinessBlockController(NullLogger<BusinessBlockController>.Instance, 256);
        await using var scheduler = new BusinessBlockScheduler(provider, probes, Options.Create(new BusinessBlockOptions()),
            NullLogger<BusinessBlockScheduler>.Instance, control,
            [new(BackendBlockGraph.Program, ["profile"], input => input.Facts["profile"].Value ? graph.Children(BackendBlockGraph.Program) : [])]);
        var reason = BusinessBlockTestSupport.Reason();
        try
        {
            foreach (var id in SamplingLifetimeConsumers) await scheduler.SetDemandAsync("sampling:" + id, id, reason);
            await scheduler.SetFactAsync("profile", true, reason);
            await BusinessBlockTestSupport.Until(() => SamplingLifetimeConsumers.All(id => scheduler.ReadState(id) == BusinessBlockState.Running));
            await scheduler.ShutdownAsync(reason);
            await Task.WhenAll(consumers.Values.Select(consumer => consumer.StopNotified.Task)).WaitAsync(BusinessBlockTestSupport.TestTimeout);
            Assert.All(consumers.Values, consumer => Assert.False(consumer.CleanupCompleted));
            Assert.Equal(BusinessBlockState.Running, scheduler.ReadState(BackendBlockIds.HostManagerSamplingSubscriptionOwner));
            Assert.All(SamplingLifetimeConsumers, id => Assert.Equal(BackendBlockIds.HostManagerSamplingSubscriptionOwner,
                graph.Nodes.Single(node => node.Id == id).ParentId));
            releaseCleanup.TrySetResult();
            await BusinessBlockTestSupport.Until(() => scheduler.Read().Blocks.All(block => block.State == BusinessBlockState.Stopped));
            var history = control.Read().History;
            var ownerStop = history.Single(row => row.BlockId == BackendBlockIds.HostManagerSamplingSubscriptionOwner
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
    [InlineData(false, 37, 49, 27)]
    [InlineData(true, 38, 50, 28)]
    public async Task ProductionTreeContainsConcurrentFlowsAndDrainsChildrenBeforeTheirOwners(bool windowsService, int expectedCount, int expectedDataEdges, int expectedRoots)
    {
        var services = new ServiceCollection();
        ResourceManager.App.Infrastructure.Security.LoopbackApiPipelineConfigurator.Compile(false).ConfigureServices(services);
        ResourceManagerServiceCollectionExtensions.AddResourceManagerApp(services, ["--startup-profile=full"],
            StartupCapabilitySet.Full, BackendHostEnvironment.Compile(windowsService, windowsService));
        var production = services.Select(value => value.ImplementationInstance).OfType<BusinessBlockRegistration>().ToArray();
        var graph = new BackendBlockGraph(production);
        var rootBlocks = FullHostedBlocks.Except(SamplingLifetimeConsumers, StringComparer.Ordinal).ToList();
        if (windowsService) rootBlocks.Add("AutomaticProductUpdateService");
        var hostedBlocks = rootBlocks.Concat(SamplingLifetimeConsumers).ToArray();
        Assert.Equal(expectedCount, graph.Nodes.Count);
        Assert.Equal(expectedDataEdges, graph.Nodes.Sum(node => node.Inputs.Count));
        Assert.DoesNotContain(graph.Nodes, node => node.Id is "ResourceManagerDatabaseInitializer"
            or "SqliteLegacyDataImportHostedService" or "SharedMemoryPublicResourceBroker"
            or "TargetedRecordingService" or "GpuLaunchInterceptionReconciler");
        Assert.Equal(5, services.Count(value => value.ServiceType == typeof(IHostedService)
            && value.ImplementationType?.IsGenericType == true
            && value.ImplementationType.GetGenericTypeDefinition() == typeof(SerialHostedService<>)));
        Assert.Contains(BackendBlockIds.EtwNetworkTcpIpAttributionReader,
            graph.Nodes.Single(node => node.Id == BackendBlockIds.WindowsResourceBreakdownSampler).Inputs);
        Assert.True(rootBlocks.ToHashSet(StringComparer.Ordinal).SetEquals(graph.Children(BackendBlockGraph.Program)));
        Assert.Equal(expectedRoots, graph.Children(BackendBlockGraph.Program).Count);
        Assert.True(NativeBlocks.ToHashSet(StringComparer.Ordinal).SetEquals(graph.Children(BackendBlockIds.NativeEtwLifetimeService)));
        Assert.True(SamplingLifetimeConsumers.ToHashSet(StringComparer.Ordinal).SetEquals(graph.Children(BackendBlockIds.HostManagerSamplingSubscriptionOwner)));
        Assert.All(graph.Nodes, node => Assert.Equal(NativeBlocks.Contains(node.Id)
            ? BackendBlockIds.NativeEtwLifetimeService : SamplingLifetimeConsumers.Contains(node.Id)
                ? BackendBlockIds.HostManagerSamplingSubscriptionOwner : BackendBlockGraph.Program, node.ParentId));
        Assert.Empty(production.Single(node => node.Id == BackendBlockIds.NativeEtwLifetimeService).Outputs);
        Assert.All(production.Where(node => node.Id != BackendBlockIds.NativeEtwLifetimeService), node => Assert.NotEmpty(node.Outputs));
        Assert.Equal(["frame-timing", "graphics-api"], production.Single(node => node.Id == "present-etw").Outputs);
        Assert.Equal(["gpu-allocation"], production.Single(node => node.Id == "gpu-allocation-etw").Outputs);
        Assert.Equal(["gpu-vram-residual"], production.Single(node => node.Id == "DxgkrnlVidMmEtwTelemetryZone").Outputs);

        // Exercise the real topology with inert entries; this never starts native sessions or product services.
        var probes = production.Select(registration => registration with { Create = _ => new Probe() }).ToArray();
        using var provider = new ServiceCollection().BuildServiceProvider();
        var control = new BusinessBlockController(NullLogger<BusinessBlockController>.Instance, 256);
        var nativeRule = services.Select(value => value.ImplementationInstance).OfType<BusinessBlockDecisionRegistration>()
            .Single(rule => rule.ParentId == BackendBlockIds.NativeEtwLifetimeService);
        await using var scheduler = new BusinessBlockScheduler(provider, probes, Options.Create(new BusinessBlockOptions()),
            NullLogger<BusinessBlockScheduler>.Instance, control,
            [new(BackendBlockGraph.Program, ["profile"], input => input.Facts["profile"].Value ? rootBlocks : []), nativeRule]);
        var reason = new BusinessBlockReason(BusinessBlockRequestKind.Configuration, "host-profile", "full", 1);
        try
        {
            foreach (var id in SamplingLifetimeConsumers) await scheduler.SetDemandAsync("sampling:" + id, id, reason);
            await scheduler.SetFactAsync("profile", true, reason);
            await BusinessBlockTestSupport.Until(() => hostedBlocks.All(id => scheduler.ReadState(id) == BusinessBlockState.Running));
            Assert.True(hostedBlocks.ToHashSet(StringComparer.Ordinal).SetEquals(control.Read().History.Where(row => row.To == BusinessBlockState.Starting).Select(row => row.BlockId)));
            Assert.Equal(BusinessBlockState.Running, scheduler.ReadState(BackendBlockIds.NativeEtwLifetimeService));
            Assert.All(NativeBlocks, id => Assert.Equal(BusinessBlockState.Stopped, scheduler.ReadState(id)));
            foreach (var id in NativeBlocks) await scheduler.SetDemandAsync("native:" + id, id, reason);
            Assert.All(NativeBlocks, id => Assert.Equal(BusinessBlockState.Stopped, scheduler.ReadState(id)));
            foreach (var fact in nativeRule.FactKeys.Where(key => !key.StartsWith("demand:", StringComparison.Ordinal)))
                await scheduler.SetFactAsync(fact, true, reason);
            await BusinessBlockTestSupport.Until(() => NativeBlocks.All(id => scheduler.ReadState(id) == BusinessBlockState.Running));
            var started = control.Read().History;
            Assert.True(hostedBlocks.Concat(NativeBlocks).ToHashSet(StringComparer.Ordinal).SetEquals(started.Where(row => row.To == BusinessBlockState.Starting).Select(row => row.BlockId)));
            var groupRunning = started.Single(row => row.BlockId == BackendBlockIds.NativeEtwLifetimeService && row.To == BusinessBlockState.Running).Sequence;
            Assert.All(started.Where(row => NativeBlocks.Contains(row.BlockId) && row.To == BusinessBlockState.Starting), row => Assert.True(row.Sequence > groupRunning));
            var samplingOwnerRunning = started.Single(row => row.BlockId == BackendBlockIds.HostManagerSamplingSubscriptionOwner && row.To == BusinessBlockState.Running).Sequence;
            Assert.All(started.Where(row => SamplingLifetimeConsumers.Contains(row.BlockId) && row.To == BusinessBlockState.Starting), row => Assert.True(row.Sequence > samplingOwnerRunning));
            await scheduler.ShutdownAsync(reason);
            await BusinessBlockTestSupport.Until(() => scheduler.Read().Blocks.All(block => block.State == BusinessBlockState.Stopped));
            var stopped = control.Read().History;
            Assert.True(hostedBlocks.Concat(NativeBlocks).ToHashSet(StringComparer.Ordinal).SetEquals(stopped.Where(row => row.To == BusinessBlockState.Stopping).Select(row => row.BlockId)));
            var groupStop = stopped.Single(row => row.BlockId == BackendBlockIds.NativeEtwLifetimeService && row.To == BusinessBlockState.Stopping).Sequence;
            Assert.All(stopped.Where(row => NativeBlocks.Contains(row.BlockId) && row.To == BusinessBlockState.Stopped), row => Assert.True(row.Sequence < groupStop));
            var samplingOwnerStop = stopped.Single(row => row.BlockId == BackendBlockIds.HostManagerSamplingSubscriptionOwner && row.To == BusinessBlockState.Stopping).Sequence;
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
                services.AddBusinessBlockHostedService<FollowingWorker>("following-worker", [], ["probe"], _ => worker);
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
        using var host = Host.CreateDefaultBuilder().ConfigureLogging(logging => logging.ClearProviders())
            .ConfigureServices(services =>
            {
                services.AddBusinessBlockController();
                services.AddSingleton(new BusinessBlockRegistration("native-probe", [], _ => native)
                    { ParentId = BackendBlockIds.NativeEtwLifetimeService });
            }).Build();
        var hosted = host.Services.GetServices<IHostedService>().ToArray();
        Assert.Collection(hosted, service => Assert.IsType<BusinessBlockDependencyLifetime>(service),
            service => Assert.IsType<NonOwningHostedService<NativeEtwLifetimeService>>(service));
        var scheduler = host.Services.GetRequiredService<BusinessBlockScheduler>();
        Task? stopping = null;
        try
        {
            await scheduler.SetDemandAsync("native-consumer", "native-probe", BusinessBlockTestSupport.Reason());
            Assert.Equal(BusinessBlockState.Stopped, scheduler.ReadState("native-probe"));
            await host.StartAsync().WaitAsync(BusinessBlockTestSupport.TestTimeout);
            await native.Started.Task.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            Assert.Equal(BusinessBlockState.Running, scheduler.ReadState(BackendBlockIds.NativeEtwLifetimeService));
            stopping = host.StopAsync();
            await native.StopNotified.Task.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            Assert.False(stopping.IsCompleted);
            Assert.Equal(BusinessBlockState.Stopping, scheduler.ReadState("native-probe"));
            Assert.Equal(BusinessBlockState.Running, scheduler.ReadState(BackendBlockIds.NativeEtwLifetimeService));
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
        Assert.Equal(15, graph.Children(BackendBlockGraph.Program).Count);
        Assert.True(SamplingLifetimeConsumers.ToHashSet(StringComparer.Ordinal).SetEquals(graph.Children(BackendBlockIds.HostManagerSamplingSubscriptionOwner)));
        Assert.True(NativeBlocks.ToHashSet(StringComparer.Ordinal).SetEquals(graph.Children(BackendBlockIds.NativeEtwLifetimeService)));
        var observations = services.Select(value => value.ImplementationInstance).OfType<BusinessBlockDiagnosticRegistration>().ToArray();
        Assert.DoesNotContain(declarations, value => value.Id == "PortableSoftwareDiscoveryService");
        var diagnostics = new BusinessBlockDiagnosticsRegistry(declarations, NullLogger<BusinessBlockDiagnosticsRegistry>.Instance, observations);
        using (diagnostics.MeasureSegment("PortableSoftwareDiscoveryService")) { }
        Assert.NotNull(diagnostics.Read("PortableSoftwareDiscoveryService"));
        Assert.Throws<KeyNotFoundException>(() => diagnostics.Read("unknown"));
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
