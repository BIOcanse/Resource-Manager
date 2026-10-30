using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Hosting;
using ResourceManager.App.Hosting.StartupCapabilities;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Infrastructure.Telemetry.Etw;
using ResourceManager.App.StateItems;

namespace Resource_Manager_APP.Tests;

public sealed class LegacyHostedAliasFailureTests
{
    internal static readonly int[] WorkerBlocks =
    [
        BlockUids.RuntimeSpecializationCoordinator, BlockUids.HostManagerPublicServiceCoordinatorOwner,
        BlockUids.HostManagerOperationCoordinatorOwner, BlockUids.TrustedAccessRequestOwner,
        BlockUids.FanCurveTemperatureFeed, BlockUids.AutomaticProductUpdateService,
        BlockUids.ControlDesiredStateReassertion, BlockUids.ControlActualStateReader,
        BlockUids.GpuPlacementProcessHistoryObserver,
        BlockUids.JsonDebugDiagnosticLogWriter, BlockUids.HostManagerReportCoordinatorOwner,
        BlockUids.HostManagerSmartCoordinator, BlockUids.PortableSoftwareDiscoveryService,
        BlockUids.EtwPhysicalDiskIoAttributionReader, BlockUids.EtwNetworkTcpIpAttributionReader,
        BlockUids.TrustedAccessInvocationHostedService
    ];
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    public static IEnumerable<object[]> Workers => WorkerBlocks.Select(id => new object[] { id });

    [Fact]
    public void EveryLegacyWorkerExplicitlyDeclaresItsExecutionAndKeepsFailureInsideItsBlock()
    {
        var declarations = ProductionRegistrations();
        Assert.Equal(WorkerBlocks.Order(), HostedRunResultFacts.SupervisedBlocks.Order());
        foreach (var id in WorkerBlocks)
        {
            var registration = Assert.Single(declarations.Where(value => value.Id == id));
            Assert.NotNull(registration.HostedExecution);
            Assert.False(registration.HostedExecution.ForwardRunFailureToHost);
        }
        foreach (var id in new[] { BlockUids.NativeEtwLifetimeService,
            BlockUids.HostManagerSamplingSubscriptionOwner, BlockUids.FanControlCoreLifetimeService,
            BlockUids.ServiceAutoStartSettings, BlockUids.ServiceRecoverySettings })
        {
            // These aliases have no private workers: their explicit entry/exit
            // contracts retain lifetime ownership without fabricating a task.
            var contract = Assert.Single(declarations.Where(value => value.Id == id)).HostedExecution;
            Assert.NotNull(contract);
            Assert.False(contract.ForwardRunFailureToHost);
        }
    }

    [Theory]
    [MemberData(nameof(Workers))]
    public async Task TerminalWorkerFailureStopsOnlyItsBlockUntilHostAdmissionChanges(int id)
    {
        var worker = new ProbeWorker();
        var log = new FailureLogger(worker.Failure);
        using var host = Host.CreateDefaultBuilder().ConfigureLogging(logging => logging.ClearProviders())
            .ConfigureServices(services =>
            {
                services.AddSingleton(worker);
                services.AddSingleton<ILogger<BusinessBlockDiagnosticsRegistry>>(log);
                services.AddHostedServiceAlias<ProbeWorker>(id, static service => service.Execution, false);
                AddInertServiceProfile(services);
            }).Build();
        var scheduler = host.Services.GetRequiredService<BusinessBlockScheduler>();
        var native = host.Services.GetRequiredService<NativeControl>();
        var facts = new BackendPreciseRuleTestFacts(scheduler.Authority, native,
            hostPermits: false, hostedRunFailures: false);
        await facts.InitializeAsync(true);
        await host.StartAsync().WaitAsync(Timeout);
        try
        {
            var alias = Assert.Single(host.Services.GetServices<IHostedService>()
                .OfType<NonOwningHostedService<ProbeWorker>>());
            await Until(() => worker.StartCount == 1 && scheduler.ReadState(id) == BusinessBlockState.Running);
            worker.Fail.TrySetResult();
            await Until(() => scheduler.ReadState(id) == BusinessBlockState.Stopped
                && scheduler.Authority.Read<bool>(new(StateItemUids.HostedRunEnded, id)).GetValueOrDefault(false));
            var result = host.Services.GetRequiredService<BusinessBlockDiagnosticsRegistry>().Read(id).Result;
            Assert.NotNull(result);
            Assert.Equal(BusinessBlockOutcome.Failed, result.Outcome);
            Assert.Equal(BusinessBlockFailureStage.Run, result.FailureStage);
            Assert.Equal(typeof(IOException).FullName, result.FailureType);
            Assert.Same(worker.Failure, await log.Observed.Task.WaitAsync(Timeout));
            Assert.Equal(1, worker.StartCount);
            Assert.Equal(1, worker.StopCount);
            Assert.False(alias.ExecuteTask!.IsCompleted);
            Assert.False(host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested);

            // Every publication is awaited through the native readers. An unchanged
            // demand remains demanded, but cannot erase the failed run's result.
            for (var index = 0; index < 3; index++) await facts.InitializeAsync(true);
            Assert.Equal(BusinessBlockState.Stopped, scheduler.ReadState(id));
            Assert.Equal(1, worker.StartCount);
            Assert.True(scheduler.Authority.Read<bool>(new(StateItemUids.HostStartPermitted, id)).Value);
            Assert.True(scheduler.Authority.Read<bool>(new(StateItemUids.HostedRunEnded, id)).Value);

            var permits = host.Services.GetRequiredService<BackendHostPermitFacts>();
            await permits.SetAsync(id, false, BusinessBlockTestSupport.Reason());
            Assert.Equal(BusinessBlockState.Stopped, scheduler.ReadState(id));
            await permits.SetAsync(id, true, BusinessBlockTestSupport.Reason());
            await Until(() => worker.StartCount == 2 && scheduler.ReadState(id) == BusinessBlockState.Running);
            Assert.False(scheduler.Authority.Read<bool>(new(StateItemUids.HostedRunEnded, id)).Value);
            Assert.False(alias.ExecuteTask.IsCompleted);
            Assert.False(host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested);
        }
        finally { await host.StopAsync().WaitAsync(Timeout); }
        Assert.Equal(2, worker.StopCount);
        Assert.True(worker.Execution!.IsCompleted);
        Assert.False(scheduler.Authority.Read<bool>(new(StateItemUids.HostedRunEnded, id)).Value);
    }

    [Fact]
    public async Task UnrequestedSuccessfulCompletionStaysStoppedUntilRealHostReadmission()
    {
        const int id = BlockUids.ControlActualStateReader;
        var worker = new ControlledWorker(failFirstRun: false);
        using var host = Host.CreateDefaultBuilder().ConfigureLogging(logging => logging.ClearProviders())
            .ConfigureServices(services =>
            {
                services.AddSingleton(worker);
                services.AddHostedServiceAlias<ControlledWorker>(id, static service => service.Execution, false);
                AddInertServiceProfile(services);
            }).Build();
        var scheduler = host.Services.GetRequiredService<BusinessBlockScheduler>();
        var facts = new BackendPreciseRuleTestFacts(scheduler.Authority, host.Services.GetRequiredService<NativeControl>(),
            hostPermits: false, hostedRunFailures: false);
        await facts.InitializeAsync(true);
        await host.StartAsync().WaitAsync(Timeout);
        try
        {
            await Until(() => worker.StartCount == 1 && scheduler.ReadState(id) == BusinessBlockState.Running);
            worker.ReleaseFirstRun.TrySetResult();
            await Until(() => scheduler.ReadState(id) == BusinessBlockState.Stopped
                && scheduler.Authority.Read<bool>(new(StateItemUids.HostedRunEnded, id)).GetValueOrDefault(false));
            var result = host.Services.GetRequiredService<BusinessBlockDiagnosticsRegistry>().Read(id).Result;
            Assert.NotNull(result);
            Assert.Equal(BusinessBlockOutcome.Done, result.Outcome);
            Assert.Null(result.FailureStage);
            Assert.Null(result.FailureType);
            for (var index = 0; index < 3; index++) await facts.InitializeAsync(true);
            Assert.Equal(1, worker.StartCount);
            Assert.Equal(BusinessBlockState.Stopped, scheduler.ReadState(id));
            Assert.True(scheduler.Authority.Read<bool>(new(StateItemUids.HostedRunEnded, id)).Value);
            var alias = Assert.Single(host.Services.GetServices<IHostedService>().OfType<NonOwningHostedService<ControlledWorker>>());
            Assert.False(alias.ExecuteTask!.IsCompleted);
            Assert.False(host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested);
            var permits = host.Services.GetRequiredService<BackendHostPermitFacts>();
            await permits.SetAsync(id, false, BusinessBlockTestSupport.Reason());
            Assert.Equal(BusinessBlockState.Stopped, scheduler.ReadState(id));
            await permits.SetAsync(id, true, BusinessBlockTestSupport.Reason());
            await Until(() => worker.StartCount == 2 && scheduler.ReadState(id) == BusinessBlockState.Running);
            Assert.False(scheduler.Authority.Read<bool>(new(StateItemUids.HostedRunEnded, id)).Value);
        }
        finally
        {
            worker.ReleaseFirstRun.TrySetResult();
            await host.StopAsync().WaitAsync(Timeout);
        }
        Assert.Equal(2, worker.StopCount);
        Assert.False(scheduler.Authority.Read<bool>(new(StateItemUids.HostedRunEnded, id)).Value);
        await Until(() => host.Services.GetRequiredService<BusinessBlockDiagnosticsRegistry>().Read(id).Result?.Outcome
            == BusinessBlockOutcome.Stopped);
        Assert.Equal(BusinessBlockOutcome.Stopped,
            host.Services.GetRequiredService<BusinessBlockDiagnosticsRegistry>().Read(id).Result!.Outcome);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResourceOwnedFailureDoesNotLatchWhenResourceRecoversBeforeWorkerFaultIsObserved(bool stopRethrowsWorkerFailure)
    {
        const int id = BlockUids.PortableSoftwareDiscoveryService;
        var worker = new ControlledWorker(failFirstRun: true, stopRethrowsWorkerFailure: stopRethrowsWorkerFailure);
        var log = new FailureLogger(worker.Failure);
        using var host = Host.CreateDefaultBuilder().ConfigureLogging(logging => logging.ClearProviders())
            .ConfigureServices(services =>
            {
                services.AddSingleton(worker);
                services.AddSingleton<ILogger<BusinessBlockDiagnosticsRegistry>>(log);
                services.AddHostedServiceAlias<ControlledWorker>(id, static service => service.Execution, false,
                    resourceFailureHandled: static _ => true);
                AddInertServiceProfile(services);
            }).Build();
        var scheduler = host.Services.GetRequiredService<BusinessBlockScheduler>();
        var facts = new BackendPreciseRuleTestFacts(scheduler.Authority, host.Services.GetRequiredService<NativeControl>(),
            hostPermits: false, hostedRunFailures: false);
        await facts.InitializeAsync(true);
        await host.StartAsync().WaitAsync(Timeout);
        try
        {
            await Until(() => worker.StartCount == 1 && scheduler.ReadState(id) == BusinessBlockState.Running);
            await facts.PublishAsync(StateItemUids.PortableAttributionCatalogAvailable, false);
            await worker.FirstStopEntered.Task.WaitAsync(Timeout);
            Assert.Equal(BusinessBlockState.Stopping, scheduler.ReadState(id));
            Assert.False(worker.Execution!.IsCompleted);
            await facts.PublishAsync(StateItemUids.PortableAttributionCatalogAvailable, true);
            Assert.Equal(1, worker.StartCount);
            worker.ReleaseFirstRun.TrySetResult();
            Assert.Same(worker.Failure, await log.Observed.Task.WaitAsync(Timeout));
            await Until(() => worker.StartCount == 2 && scheduler.ReadState(id) == BusinessBlockState.Running);
            Assert.Equal(1, worker.StopCount);
            Assert.True(scheduler.Authority.Read<bool>(StateItemUids.PortableAttributionCatalogAvailable).Value);
            Assert.False(scheduler.Authority.Read<bool>(new(StateItemUids.HostedRunEnded, id)).Value);
            for (var index = 0; index < 3; index++)
                await facts.PublishAsync(StateItemUids.PortableAttributionCatalogAvailable, true);
            Assert.Equal(2, worker.StartCount);
            var alias = Assert.Single(host.Services.GetServices<IHostedService>().OfType<NonOwningHostedService<ControlledWorker>>());
            Assert.False(alias.ExecuteTask!.IsCompleted);
            Assert.False(host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested);
        }
        finally
        {
            worker.ReleaseFirstRun.TrySetResult();
            await host.StopAsync().WaitAsync(Timeout);
        }
        Assert.Equal(2, worker.StopCount);
        Assert.False(scheduler.Authority.Read<bool>(new(StateItemUids.HostedRunEnded, id)).Value);
    }

    private static BusinessBlockRegistration[] ProductionRegistrations()
    {
        var services = new ServiceCollection();
        ResourceManager.App.Infrastructure.Security.LoopbackApiPipelineConfigurator.Compile(false).ConfigureServices(services);
        string[] args = ["--startup-profile=full"];
        services.AddResourceManagerApp(args, StartupProfileCompiler.Compile(args),
            BackendHostEnvironment.Compile(isWindowsService: true, isLocalSystem: true));
        return services.Select(value => value.ImplementationInstance).OfType<BusinessBlockRegistration>().ToArray();
    }

    private static void AddInertServiceProfile(IServiceCollection services)
    {
        ResourceManagerServiceCollectionExtensions.AddBusinessBlockController(services);
        var included = services.Select(value => value.ImplementationInstance)
            .OfType<BusinessBlockRegistration>().Select(value => value.Id).ToHashSet();
        foreach (var id in BackendExpectedProductionTree.Parents(2).Keys)
            if (included.Add(id))
                services.AddSingleton(new BusinessBlockRegistration(id, _ => new HoldingBlock()));
        var declarations = services.Select(value => value.ImplementationInstance)
            .OfType<BusinessBlockRegistration>().ToArray();
        services.AddSingleton<NativeControl>(_ => NativeBackendBlockTestSupport.CreateControl(declarations, profile: 2));
        services.AddSingleton<StateAuthority>(provider =>
            NativeBackendBlockTestSupport.CreateAuthority(provider.GetRequiredService<NativeControl>()));
        services.AddSingleton<NativeEtwSessionAvailability>(provider =>
            new(provider.GetRequiredService<StateAuthority>()));
    }

    private static async Task Until(Func<bool> ready)
    {
        using var cancellation = new CancellationTokenSource(Timeout);
        while (!ready()) await Task.Delay(1, cancellation.Token);
    }

    private sealed class ProbeWorker : IHostedService
    {
        private CancellationTokenSource? stop;
        private int starts;
        private int stops;
        internal int StartCount => Volatile.Read(ref starts);
        internal int StopCount => Volatile.Read(ref stops);
        internal Task? Execution { get; private set; }
        internal readonly IOException Failure = new("terminal legacy worker failure");
        internal readonly TaskCompletionSource Fail = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task StartAsync(CancellationToken cancellationToken)
        {
            stop = new();
            Execution = RunAsync(Interlocked.Increment(ref starts), stop.Token);
            return Task.CompletedTask;
        }
        private async Task RunAsync(int run, CancellationToken cancellationToken)
        {
            if (run == 1) { await Fail.Task.WaitAsync(cancellationToken); throw Failure; }
            await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, cancellationToken);
        }
        public async Task StopAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref stops);
            stop!.Cancel();
            try { await Execution!.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            finally { stop.Dispose(); }
        }
    }

    private sealed class FailureLogger(Exception expected) : ILogger<BusinessBlockDiagnosticsRegistry>
    {
        internal readonly TaskCompletionSource<Exception> Observed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? error,
            Func<TState, Exception?, string> formatter)
        {
            if (ReferenceEquals(error, expected)) Observed.TrySetResult(error);
        }
    }

    private sealed class ControlledWorker(bool failFirstRun, bool stopRethrowsWorkerFailure = false) : IHostedService
    {
        private CancellationTokenSource? stop;
        private int starts;
        private int stops;
        internal int StartCount => Volatile.Read(ref starts);
        internal int StopCount => Volatile.Read(ref stops);
        internal Task? Execution { get; private set; }
        internal readonly IOException Failure = new("resource-owned terminal worker failure");
        internal readonly TaskCompletionSource ReleaseFirstRun = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource FirstStopEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task StartAsync(CancellationToken cancellationToken)
        {
            stop = new();
            Execution = RunAsync(Interlocked.Increment(ref starts), stop.Token);
            return Task.CompletedTask;
        }
        private async Task RunAsync(int run, CancellationToken cancellationToken)
        {
            if (run == 1)
            {
                // The first entry models a resource operation finishing its own
                // cleanup after a stop request; cancellation cannot skip that cleanup.
                await ReleaseFirstRun.Task;
                if (failFirstRun) throw Failure;
                return;
            }
            await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, cancellationToken);
        }
        public async Task StopAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref stops) == 1) FirstStopEntered.TrySetResult();
            stop!.Cancel();
            try { await Execution!.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (Exception error) when (!stopRethrowsWorkerFailure && ReferenceEquals(error, Failure)) { }
            finally { stop.Dispose(); }
        }
    }

    private sealed class HoldingBlock : IBusinessBlock
    {
        public async Task RunAsync(BusinessBlockRunContext context)
        {
            context.ReportRunning();
            try { await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, context.StopNotification); }
            catch (OperationCanceledException) when (context.StopNotification.IsCancellationRequested) { }
        }
    }
}

