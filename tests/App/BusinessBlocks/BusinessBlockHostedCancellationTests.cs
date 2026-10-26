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
            .ConfigureServices(services => services.AddBusinessBlockHostedService<GatedCleanup>(
                "cleanup", [], [], _ => service)).Build();
        var scheduler = host.Services.GetRequiredService<BusinessBlockScheduler>();
        var block = host.Services.GetRequiredService<HostedBusinessBlockRegistry>().GetOrAdd("cleanup", () => service);
        using var deadline = new CancellationTokenSource();
        await host.StartAsync().WaitAsync(BusinessBlockTestSupport.TestTimeout);
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
            Assert.Equal(BusinessBlockState.Stopping, scheduler.ReadState("cleanup"));
            Assert.Equal(1, service.StopCount);
            Assert.False(service.CleanupCompleted);
            service.Release.TrySetResult();
            await BusinessBlockTestSupport.State(scheduler, "cleanup", BusinessBlockState.Stopped);
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
    public async Task LateObserverOfNormalMaintenanceCancellationDoesNotFaultAfterBlockHasStopped()
    {
        var maintenance = new Maintenance();
        var service = new SharedResourceSubscriptionMaintenanceService(maintenance,
            NullLogger<SharedResourceSubscriptionMaintenanceService>.Instance);
        using var host = Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => logging.ClearProviders())
            .ConfigureServices(services => services.AddBusinessBlockHostedService<SharedResourceSubscriptionMaintenanceService>(
                "maintenance", [], [], _ => service)).Build();
        var alias = host.Services.GetServices<IHostedService>()
            .OfType<NonOwningHostedService<SharedResourceSubscriptionMaintenanceService>>().Single();
        var scheduler = host.Services.GetRequiredService<BusinessBlockScheduler>();
        try
        {
            await alias.StartAsync(CancellationToken.None).WaitAsync(BusinessBlockTestSupport.TestTimeout);
            await maintenance.Entered.Task.WaitAsync(BusinessBlockTestSupport.TestTimeout);
            await scheduler.SetEnabledAsync("maintenance", false, BusinessBlockTestSupport.Reason());
            await BusinessBlockTestSupport.State(scheduler, "maintenance", BusinessBlockState.Stopped);
            Assert.True(service.ExecuteTask!.IsCanceled);

            // Invoke the same observer after the wrapper's completion. This fixes the late
            // continuation interleaving without depending on thread-pool continuation order.
            var observer = typeof(NonOwningHostedService<SharedResourceSubscriptionMaintenanceService>)
                .GetMethod("ExecuteAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var observed = (Task)observer.Invoke(alias, [CancellationToken.None])!;
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
