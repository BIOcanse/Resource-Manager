using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ResourceManager.App.Hosting;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.FreedomPoints;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerSmartCoordinatorScoreOnlyCompositionTests
{
    [Fact]
    public async Task PersistentRollbackFailureDoesNotStopGenericHostAndSameWorkerRecovers()
    {
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true,
            scoreOnlyEnabled: false,
            optimizationMode: AppOptimizationModes.CpuOnly,
            optimizationCapabilities: OptimizationModeCapabilities.None,
            automaticMemoryCleanupEnabled: false,
            editFreedomPoints: root => FreedomPointTestFactory.Point(root,
                BackendFreedomPointPaths.SchedulerSamplingInterval)["value"] = 100);
        fixture.PublicResourceManager.Available = false;
        var broken = 1;
        var failures = 0;
        var failedCycles = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var repairedRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.StateStore.OnLoad = () =>
        {
            if (Volatile.Read(ref broken) != 0)
            {
                if (Interlocked.Increment(ref failures) >= 3)
                    failedCycles.TrySetResult();
                throw new InvalidDataException("Simulated persistently invalid rollback state.");
            }
            repairedRead.TrySetResult();
        };
        using var host = new HostBuilder()
            .ConfigureLogging(logging => logging.ClearProviders())
            .ConfigureServices(services =>
            {
                services.AddSingleton(fixture.Coordinator);
                services.AddSingleton<IHostedService>(provider => new HostedScoreOnlyPlanFacts(
                    fixture, provider.GetRequiredService<ResourceManager.Shared.BusinessBlocks.State.StateAuthority>()));
                services.AddHostedServiceAlias<ResourceManager.App.Infrastructure.Optimization.HostManagerSmartCoordinator>(BackendUids.HostManagerSmartCoordinator);
                NativeBackendBlockTestSupport.AddFullTreeHostFixture(services);
            })
            .Build();
        Assert.Equal(BackgroundServiceExceptionBehavior.StopHost,
            host.Services.GetRequiredService<IOptions<HostOptions>>().Value.BackgroundServiceExceptionBehavior);
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await StartHostedScoreOnlyTestHostAsync(host, deadline.Token);
        try
        {
            var worker = fixture.Coordinator.ExecuteTask;
            await failedCycles.Task.WaitAsync(deadline.Token);
            Assert.False(worker!.IsCompleted);
            Assert.False(lifetime.ApplicationStopping.IsCancellationRequested);
            Volatile.Write(ref broken, 0);
            await repairedRead.Task.WaitAsync(deadline.Token);
            Assert.Same(worker, fixture.Coordinator.ExecuteTask);
            Assert.False(worker.IsCompleted);
            Assert.False(lifetime.ApplicationStopping.IsCancellationRequested);
            Assert.Empty(fixture.StateStore.Current.AppliedPlacements);
            Assert.Equal(0, fixture.ProcessPolicyWriter.BatchCalls);
            Assert.Equal(0U, fixture.PublicResourceManager.NewEffectAttemptCount);
            Assert.Equal(0, fixture.GraphicsPreferenceStore.TotalCalls);
        }
        finally
        {
            using var stopDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await host.StopAsync(stopDeadline.Token);
        }
        Assert.True(fixture.Coordinator.ExecuteTask!.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task ScheduledCycleRetriesAfterRollbackReadFailureWithoutStoppingWorker()
    {
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true,
            scoreOnlyEnabled: false,
            optimizationMode: AppOptimizationModes.Normal,
            automaticMemoryCleanupEnabled: false);
        var failures = 0;
        fixture.StateStore.OnLoad = () =>
        {
            if (Interlocked.Increment(ref failures) == 1)
                throw new IOException("Simulated temporary rollback read failure.");
        };

        await fixture.Coordinator.StartAsync(CancellationToken.None);
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (fixture.StateStore.LoadCalls < 2)
                await Task.Delay(10, deadline.Token);
            Assert.False(fixture.Coordinator.ExecuteTask!.IsCompleted);
            Assert.Empty(fixture.StateStore.Current.AppliedPlacements);
        }
        finally
        {
            await fixture.Coordinator.StopAsync(CancellationToken.None);
        }
    }
}
