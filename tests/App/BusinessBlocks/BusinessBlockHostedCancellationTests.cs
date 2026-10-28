using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Application.PublicResources;
using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Hosting;
using ResourceManager.App.Infrastructure.PublicResources;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;

namespace Resource_Manager_APP.Tests;

public sealed class BusinessBlockHostedCancellationTests
{
    [Fact]
    public async Task HostDeadlineEndsOnlyHostWaitWhileServiceCleanupKeepsBlockStopping()
    {
        var service = new GatedCleanup();
        using var host = Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => logging.ClearProviders())
            .ConfigureServices(services =>
            {
                services.AddBusinessBlockHostedService<GatedCleanup>(BackendUids.RuntimeSpecializationCoordinator, _ => service);
                NativeBackendBlockTestSupport.AddFullTreeHostFixture(services);
            }).Build();
        var scheduler = host.Services.GetRequiredService<BusinessBlockScheduler>();
        var block = host.Services.GetRequiredService<HostedBusinessBlockRegistry>().GetOrAdd(BackendUids.RuntimeSpecializationCoordinator, () => service);
        using var deadline = new CancellationTokenSource();
        await host.StartAsync().WaitAsync(BusinessBlockTestSupport.TestTimeout);
        await BusinessBlockTestSupport.State(scheduler, BackendUids.RuntimeSpecializationCoordinator, BusinessBlockState.Running);
        Task? stopping = null;
        try
        {
            stopping = host.StopAsync(deadline.Token);
            await service.Entered.Task.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            deadline.Cancel();
            try { await stopping.WaitAsync(BusinessBlockTestSupport.TestTimeout); }
            catch (OperationCanceledException) { }
            catch (AggregateException errors) when (errors.Flatten().InnerExceptions.All(error => error is OperationCanceledException)) { }
            Assert.False(block.CurrentStopCompletion().IsCompleted);
            Assert.Equal(BusinessBlockState.Stopping, scheduler.ReadState(BackendUids.RuntimeSpecializationCoordinator));
            Assert.Equal(1, service.StopCount);
            Assert.False(service.CleanupCompleted);
            service.Release.TrySetResult();
            await BusinessBlockTestSupport.State(scheduler, BackendUids.RuntimeSpecializationCoordinator, BusinessBlockState.Stopped);
            Assert.True(service.CleanupCompleted);
        }
        finally
        {
            service.Release.TrySetResult();
            if (stopping is not null)
            {
                try { await stopping.WaitAsync(BusinessBlockTestSupport.TestTimeout); }
                catch (OperationCanceledException) { }
                catch (AggregateException errors) when (errors.Flatten().InnerExceptions.All(error => error is OperationCanceledException)) { }
            }
            await BusinessBlockTestSupport.StopAll(scheduler);
        }
    }

    [Fact]
    public async Task LateFailureObserverIgnoresNormalMaintenanceCancellationAndEndsWhenHostStops()
    {
        var maintenance = new Maintenance();
        var service = new SharedResourceSubscriptionMaintenanceService(maintenance,
            NullLogger<SharedResourceSubscriptionMaintenanceService>.Instance);
        using var host = Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => logging.ClearProviders())
            .ConfigureServices(services =>
            {
                services.AddBusinessBlockHostedService<SharedResourceSubscriptionMaintenanceService>(
                    BackendUids.SharedResourceSubscriptionMaintenanceService, _ => service);
                NativeBackendBlockTestSupport.AddFullTreeHostFixture(services);
            }).Build();
        var alias = host.Services.GetServices<IHostedService>()
            .OfType<NonOwningHostedService<SharedResourceSubscriptionMaintenanceService>>().Single();
        var scheduler = host.Services.GetRequiredService<BusinessBlockScheduler>();
        var activity = scheduler.Authority.ClaimCell<bool>(BackendItems.SharedResourceActivityExists);
        await activity.PublishAsync(true, BusinessBlockTestSupport.Reason());
        try
        {
            await alias.StartAsync(CancellationToken.None).WaitAsync(BusinessBlockTestSupport.TestTimeout);
            await maintenance.Entered.Task.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            await host.Services.GetRequiredService<BackendHostPermitFacts>().SetAsync(
                BackendUids.SharedResourceSubscriptionMaintenanceService, false, BusinessBlockTestSupport.Reason());
            await BusinessBlockTestSupport.State(scheduler, BackendUids.SharedResourceSubscriptionMaintenanceService, BusinessBlockState.Stopped);
            Assert.True(service.ExecuteTask!.IsCanceled);

            // A normal stopped run is not a host failure. A late failure observer stays
            // active across runs and finishes only when its own host token is canceled.
            var observer = typeof(NonOwningHostedService<SharedResourceSubscriptionMaintenanceService>)
                .GetMethod("ExecuteAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            using var observationStop = new CancellationTokenSource();
            var observed = (Task)observer.Invoke(alias, [observationStop.Token])!;
            Assert.False(observed.IsCompleted);
            observationStop.Cancel();
            await observed.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            Assert.False(host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested);
        }
        finally
        {
            await alias.StopAsync(CancellationToken.None).WaitAsync(BusinessBlockTestSupport.TestTimeout);
            await BusinessBlockTestSupport.StopAll(scheduler);
        }
    }

    private sealed class Maintenance : ISharedResourceSubscriptionMaintenance
    {
        internal TaskCompletionSource Entered { get; } = BusinessBlockTestSupport.Signal();
        public int MaintenanceIntervalMilliseconds
        {
            get { Entered.TrySetResult(); return Timeout.Infinite; }
        }
        public SharedResourceMaintenanceResult Refresh(ulong timestamp) => default;
    }

    private sealed class GatedCleanup : IHostedService
    {
        internal TaskCompletionSource Entered { get; } = BusinessBlockTestSupport.Signal();
        internal TaskCompletionSource Release { get; } = BusinessBlockTestSupport.Signal();
        internal int StopCount { get; private set; }
        internal bool CleanupCompleted { get; private set; }
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public async Task StopAsync(CancellationToken cancellationToken)
        {
            StopCount++;
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            CleanupCompleted = true;
        }
    }
}
