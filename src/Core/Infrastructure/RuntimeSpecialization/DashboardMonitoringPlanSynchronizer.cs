using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Domain.Metrics;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization;

public sealed class DashboardMonitoringPlanSynchronizer : IHostedService, IDisposable
{
    private static readonly TimeSpan CatalogRetryInterval = TimeSpan.FromSeconds(5);
    private readonly SemaphoreSlim changedWake = new(0, 1);
    private readonly IMetricSampler sampler;
    private readonly DashboardMonitoringCatalogState catalogState;
    private readonly IRuntimeSpecializationCoordinator runtimeSpecialization;
    private readonly ILogger<DashboardMonitoringPlanSynchronizer> logger;
    private CancellationTokenSource? workerCancellation;
    private Task? worker;
    private bool started;
    private volatile bool initialCatalogReconciled;

    public DashboardMonitoringPlanSynchronizer(
        IMetricSampler sampler,
        DashboardMonitoringCatalogState catalogState,
        IRuntimeSpecializationCoordinator runtimeSpecialization,
        ILogger<DashboardMonitoringPlanSynchronizer> logger)
    {
        this.sampler = sampler;
        this.catalogState = catalogState;
        this.runtimeSpecialization = runtimeSpecialization;
        this.logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (started)
        {
            return;
        }

        started = true;
        catalogState.Changed += OnCatalogChanged;
        try
        {
            var snapshot = await sampler.GetSnapshotAsync(
                MetricSampleRequest.CatalogProbe,
                cancellationToken);
            catalogState.PublishCatalogProbe(snapshot);
            DrainWake();
            await runtimeSpecialization.RebuildAsync(
                "monitoring-catalog-ready",
                cancellationToken);
            initialCatalogReconciled = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            catalogState.Changed -= OnCatalogChanged;
            started = false;
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "The initial dashboard monitoring catalog could not be reconciled; GPU monitoring entries remain inactive until a catalog snapshot succeeds.");
        }

        workerCancellation = new CancellationTokenSource();
        worker = Task.Run(
            () => RunAsync(workerCancellation.Token),
            CancellationToken.None);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        catalogState.Changed -= OnCatalogChanged;
        started = false;
        var cancellation = workerCancellation;
        var running = worker;
        if (cancellation is null || running is null)
        {
            return;
        }

        cancellation.Cancel();
        TryWake();
        try
        {
            await running.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
    }

    public void Dispose()
    {
        catalogState.Changed -= OnCatalogChanged;
        workerCancellation?.Cancel();
        workerCancellation?.Dispose();
        changedWake.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (!initialCatalogReconciled)
            {
                try
                {
                    if (catalogState.Current is null)
                    {
                        var snapshot = await sampler.GetSnapshotAsync(
                            MetricSampleRequest.CatalogProbe, cancellationToken);
                        catalogState.PublishCatalogProbe(snapshot);
                    }
                    DrainWake();
                    await runtimeSpecialization.RebuildAsync(
                        "monitoring-catalog-ready", cancellationToken);
                    initialCatalogReconciled = true;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "The initial dashboard monitoring catalog will be retried.");
                    await Task.Delay(CatalogRetryInterval, cancellationToken);
                    continue;
                }
            }
            await changedWake.WaitAsync(cancellationToken);
            DrainWake();
            try
            {
                await runtimeSpecialization.RebuildAsync(
                    "monitoring-catalog-changed",
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "The dashboard monitoring plan could not be reconciled with the current metric catalog.");
            }
        }
    }

    private void OnCatalogChanged() => TryWake();

    private void TryWake()
    {
        try
        {
            changedWake.Release();
        }
        catch (SemaphoreFullException)
        {
            // One pending wake represents every catalog change not yet compiled.
        }
    }

    private void DrainWake()
    {
        while (changedWake.Wait(0))
        {
        }
    }
}
