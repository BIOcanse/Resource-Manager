using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Hosting;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.Shared.BusinessBlocks.State;

namespace Resource_Manager_APP.Tests;

public sealed class BusinessBlockHostedExecutionContractTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public Task CustomHostedWorkerReturnsItsFailureAndRestartsWithoutStoppingHost()
    {
        var worker = new CustomWorker();
        return VerifyRestart(worker, static service => service.Execution);
    }

    [Fact]
    public Task BackgroundWorkerUsesTheExplicitBlockLocalRunFailurePolicy()
    {
        var worker = new BackgroundWorker();
        return VerifyRestart(worker, static service => service.ExecuteTask);
    }

    [Fact]
    public void AllSevenSamplingRolesExplicitlyDeclareTheirExecutionTaskAndRunFailurePolicy()
    {
        var services = new ServiceCollection();
        services.AddResourceManagerApp(["--startup-profile=full"]);
        var expected = new[] { BackendUids.WindowsHardwareMetricSampler, BackendUids.DeviceTopologySnapshotProvider,
            BackendUids.WindowsResourceBreakdownSampler, BackendUids.GpuTelemetryWorkerSupervisor,
            BackendUids.EtwSystemInterruptSnapshotSource, BackendUids.EtwCpuCoreResidencyReader,
            BackendUids.CpuTopologySnapshotProvider };
        var registrations = services.Select(value => value.ImplementationInstance).OfType<BusinessBlockRegistration>()
            .Where(value => expected.Contains(value.Id)).ToArray();
        Assert.Equal(expected.Order(), registrations.Select(value => value.Id).Order());
        Assert.All(registrations, registration =>
        {
            Assert.NotNull(registration.HostedExecution);
            Assert.False(registration.HostedExecution.ForwardRunFailureToHost);
        });
    }

    [Theory]
    [InlineData(BusinessBlockFailureStage.Start)]
    [InlineData(BusinessBlockFailureStage.Stop)]
    public async Task BlockLocalRunPolicyStillForwardsStartAndStopFailures(BusinessBlockFailureStage stage)
    {
        var failure = new IOException("host lifecycle failed");
        var service = new LifecycleFaultService(stage, failure);
        var registration = new BusinessBlockRegistration(BackendUids.CpuTopologySnapshotProvider, _ => throw new NotSupportedException())
        { HostedExecution = new(static _ => Task.CompletedTask, false) };
        var diagnostics = new BusinessBlockDiagnosticsRegistry([registration], NullLogger<BusinessBlockDiagnosticsRegistry>.Instance);
        var registry = new HostedBusinessBlockRegistry(diagnostics, [registration]);
        var block = registry.GetOrAdd(registration.Id, () => service);
        _ = block.PrepareStart(CancellationToken.None);
        await block.RunAsync(new(CancellationToken.None, static () => { })).WaitAsync(Timeout);
        var observed = await Assert.ThrowsAsync<IOException>(() => block.ExecutionFailure.WaitAsync(Timeout));
        Assert.Same(failure, observed);
        Assert.Equal(stage, diagnostics.Read(registration.Id).Result!.FailureStage);
    }

    private static async Task VerifyRestart<TService>(TService worker, Func<TService, Task?> execution)
        where TService : class, IHostedService, IWorkerProbe
    {
        var log = new RunFailureLogger(worker.Failure);
        using var host = Host.CreateDefaultBuilder().ConfigureLogging(logging => logging.ClearProviders())
            .ConfigureServices(services =>
            {
                services.AddSingleton(worker);
                services.AddSingleton<ILogger<BusinessBlockDiagnosticsRegistry>>(log);
                services.AddHostedServiceAlias(BackendUids.CpuTopologySnapshotProvider, execution, forwardRunFailureToHost: false);
                NativeBackendBlockTestSupport.AddFullTreeHostFixture(services);
            }).Build();
        var scheduler = host.Services.GetRequiredService<BusinessBlockScheduler>();
        log.ReadResult = () => host.Services.GetRequiredService<BusinessBlockDiagnosticsRegistry>()
            .Read(BackendUids.CpuTopologySnapshotProvider).Result;
        var fact = scheduler.Authority.ClaimCell<bool>(BackendItems.SamplingCpuTopologyInUse);
        await fact.PublishAsync(true, new(BusinessBlockRequestKind.User, "worker-contract-test", "subscription-active", 0));
        await host.StartAsync().WaitAsync(Timeout);
        try
        {
            var alias = Assert.Single(host.Services.GetServices<IHostedService>().OfType<NonOwningHostedService<TService>>());
            await Until(() => worker.StartCount == 1
                && scheduler.ReadState(BackendUids.CpuTopologySnapshotProvider) == BusinessBlockState.Running);
            worker.ReleaseFailure.TrySetResult();
            var result = await log.Failed.Task.WaitAsync(Timeout);
            Assert.Equal(BusinessBlockOutcome.Failed, result.Outcome);
            Assert.Equal(BusinessBlockFailureStage.Run, result.FailureStage);
            Assert.Equal(typeof(IOException).FullName, result.FailureType);
            await Until(() => worker.StartCount == 2
                && scheduler.ReadState(BackendUids.CpuTopologySnapshotProvider) == BusinessBlockState.Running);
            Assert.False(alias.ExecuteTask!.IsCompleted);
            Assert.False(host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested);
            Assert.True(scheduler.Authority.Read<bool>(BackendItems.SamplingCpuTopologyInUse).Value);
        }
        finally { await host.StopAsync().WaitAsync(Timeout); }
    }

    private static async Task Until(Func<bool> ready)
    {
        using var cancellation = new CancellationTokenSource(Timeout);
        while (!ready()) await Task.Delay(1, cancellation.Token);
    }

    private interface IWorkerProbe
    {
        int StartCount { get; }
        IOException Failure { get; }
        TaskCompletionSource ReleaseFailure { get; }
    }

    private sealed class LifecycleFaultService(BusinessBlockFailureStage stage, IOException failure) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
            => stage == BusinessBlockFailureStage.Start ? Task.FromException(failure) : Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken)
            => stage == BusinessBlockFailureStage.Stop ? Task.FromException(failure) : Task.CompletedTask;
    }

    private sealed class CustomWorker : IHostedService, IWorkerProbe
    {
        private int starts;
        private CancellationTokenSource? stop;
        internal Task? Execution { get; private set; }
        public int StartCount => Volatile.Read(ref starts);
        public IOException Failure { get; } = new("custom sampling run failed");
        public TaskCompletionSource ReleaseFailure { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task StartAsync(CancellationToken cancellationToken)
        {
            var run = Interlocked.Increment(ref starts);
            stop = new();
            Execution = RunAsync(run, stop.Token);
            return Task.CompletedTask;
        }

        private async Task RunAsync(int run, CancellationToken token)
        {
            if (run == 1) { await ReleaseFailure.Task.WaitAsync(token); throw Failure; }
            await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, token);
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            stop!.Cancel();
            try { await Execution!.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            finally { stop.Dispose(); }
        }
    }

    private sealed class BackgroundWorker : BackgroundService, IWorkerProbe
    {
        private int starts;
        public int StartCount => Volatile.Read(ref starts);
        public IOException Failure { get; } = new("background sampling run failed");
        public TaskCompletionSource ReleaseFailure { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override Task StartAsync(CancellationToken cancellationToken)
        { Interlocked.Increment(ref starts); return base.StartAsync(cancellationToken); }
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (StartCount == 1) { await ReleaseFailure.Task.WaitAsync(stoppingToken); throw Failure; }
            await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, stoppingToken);
        }
    }

    private sealed class RunFailureLogger(Exception expected) : ILogger<BusinessBlockDiagnosticsRegistry>
    {
        internal Func<BusinessBlockResult?> ReadResult = null!;
        internal readonly TaskCompletionSource<BusinessBlockResult> Failed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? error,
            Func<TState, Exception?, string> formatter)
        {
            if (ReferenceEquals(error, expected) && ReadResult() is { FailureStage: BusinessBlockFailureStage.Run } result)
                Failed.TrySetResult(result);
        }
    }
}
