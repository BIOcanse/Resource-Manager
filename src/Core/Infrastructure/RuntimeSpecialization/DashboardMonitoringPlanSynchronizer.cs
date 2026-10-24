using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Domain.Metrics;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization;

public sealed class DashboardMonitoringPlanSynchronizer : IHostedService, IDisposable
{
    internal static readonly TimeSpan CatalogSubscriptionInterval = TimeSpan.FromSeconds(5);
    private readonly IMetricSnapshotPushSource source;
    private readonly DashboardMonitoringCatalogState catalogState;
    private readonly IRuntimeSpecializationCoordinator runtimeSpecialization;
    private readonly ILogger<DashboardMonitoringPlanSynchronizer> logger;
    private CancellationTokenSource? workerCancellation;
    private Task? worker;
    private readonly SemaphoreSlim catalogSignal = new(0, 1);
    private bool initialCatalogReconciled;

    public DashboardMonitoringPlanSynchronizer(
        IMetricSnapshotPushSource source,
        DashboardMonitoringCatalogState catalogState,
        IRuntimeSpecializationCoordinator runtimeSpecialization,
        ILogger<DashboardMonitoringPlanSynchronizer> logger)
    {
        this.source = source;
        this.catalogState = catalogState;
        this.runtimeSpecialization = runtimeSpecialization;
        this.logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (worker is { IsCompleted: false }) return Task.CompletedTask;
        catalogState.Changed -= OnCatalogChanged;
        workerCancellation?.Dispose();
        initialCatalogReconciled = false;
        SignalCatalogChange();
        catalogState.Changed += OnCatalogChanged;
        workerCancellation = new CancellationTokenSource();
        worker = Task.Run(() => RunAsync(workerCancellation.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        catalogState.Changed -= OnCatalogChanged;
        var cancellation = workerCancellation;
        var running = worker;
        if (cancellation is null || running is null) return;
        cancellation.Cancel();
        await running.WaitAsync(cancellationToken);
        cancellation.Dispose();
        workerCancellation = null;
        worker = null;
    }

    public void Dispose()
    {
        catalogState.Changed -= OnCatalogChanged;
        workerCancellation?.Cancel();
        worker?.GetAwaiter().GetResult();
        workerCancellation?.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (catalogState.Current is not { CatalogGeneration: > 0 })
                {
                    await foreach (var snapshot in source.SubscribeAsync("dashboard-monitoring-catalog",
                        MetricSampleRequest.CatalogProbe, CatalogSubscriptionInterval, cancellationToken))
                    {
                        if (snapshot.CatalogGeneration == 0) continue;
                        break;
                    }
                    if (catalogState.Current is not { CatalogGeneration: > 0 })
                        throw new InvalidOperationException("The monitoring catalog subscription ended before publication.");
                }
                // The bootstrap demand has ended. Subsequent publications come from
                // consumer refreshes and topology changes, not from this plan owner.
                while (!cancellationToken.IsCancellationRequested)
                {
                    await catalogSignal.WaitAsync(cancellationToken);
                    try
                    {
                        await runtimeSpecialization.RebuildAsync(initialCatalogReconciled
                            ? "monitoring-catalog-changed" : "monitoring-catalog-ready", cancellationToken);
                        initialCatalogReconciled = true;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception exception)
                    {
                        logger.LogWarning(exception, "The dashboard monitoring plan rebuild failed; it will be retried without sampling demand.");
                        await Task.Delay(CatalogSubscriptionInterval, cancellationToken);
                        SignalCatalogChange();
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "The monitoring catalog subscription failed; it will be reacquired.");
            }
            try
            {
                await Task.Delay(CatalogSubscriptionInterval, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private void OnCatalogChanged() => SignalCatalogChange();

    private void SignalCatalogChange()
    {
        try { catalogSignal.Release(); }
        catch (SemaphoreFullException) { }
    }
}
