using ResourceManager.ReadWrite.Windows.Cpu.Snapshots;
using System.Runtime.CompilerServices;
using ResourceManager.App.Application.CpuTopology;
using ResourceManager.App.Domain.CpuTopology;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.Adaptation;

namespace ResourceManager.App.Infrastructure.CpuTopology;

public sealed partial class CpuTopologySnapshotProvider(
    ICpuTopologySampler sampler,
    HostManagerSamplingSubscriptionOwner samplingOwner,
    ILogger<CpuTopologySnapshotProvider> logger) : BackgroundService, ICpuTopologyReader
{
    internal const int SamplingRoleId = 7;
    internal const string SnapshotItemId = "cpu.topology";
    private readonly object publicationGate = new();
    private readonly object resourceDisposeGate = new();
    private readonly SemaphoreSlim demandSignal = new(0, 1);
    private readonly CpuTopologyCaptureOwner captureOwner = new(
        () =>
        {
            var snapshot = sampler.CaptureSnapshot();
            return new(snapshot, snapshot.OsTopologyInformation, snapshot.CapturedAt);
        }, sampler.ResetUsageBaseline, error => logger.LogWarning(error, "CPU topology sampling failed."));
    private readonly NativeItemSamplingSubscriptionTracker<TopologyRequest> subscriptions = CreateSubscriptions(samplingOwner, null);
    private long nextLeaseId;
    private bool stopped;
    private int ownedResourcesDisposed;

#if RM_STATE_NAMES
    internal (int SourceRegistrations, bool PendingSessionLease, ulong OutputScratchBytes,
        ulong PendingOutputCloseBytes, bool OutputUseActive, int ManagedOutputRows) ReadSamplingResourceDiagnostics()
        => subscriptions.CaptureResourceDiagnostics();
#endif

    private static NativeItemSamplingSubscriptionTracker<TopologyRequest> CreateSubscriptions(
        HostManagerSamplingSubscriptionOwner owner,
        Func<ulong, uint, ResourceManager.Subscription.INativeSamplingOutputScratch>? createOutputScratch)
        => new(owner, SamplingRoleId, static request => request.SourceKey, static _ => [SnapshotItemId],
            static (items, _) => items.Contains(SnapshotItemId, StringComparer.OrdinalIgnoreCase)
                ? new TopologyRequest("provider") : null,
            coalesceItemsIntoLatestCapture: true, createOutputScratch);

    internal CpuTopologySnapshotProvider(
        ICpuTopologySampler sampler,
        HostManagerSamplingSubscriptionOwner samplingOwner,
        ILogger<CpuTopologySnapshotProvider> logger,
        ResourceManagerLedgerOwner ledgerOwner)
        : this(sampler, samplingOwner, logger)
    {
        ArgumentNullException.ThrowIfNull(ledgerOwner);
        subscriptions = CreateSubscriptions(samplingOwner,
            (length, alignment) => SamplingOutputScratch.Create(ledgerOwner.CreateScratch, length, alignment));
    }

    public CpuTopologySnapshot? GetSnapshot() => captureOwner.ReadState().Snapshot;

    public DeviceInformationSnapshot GetInformationSnapshot() => captureOwner.ReadInformation();

    public async IAsyncEnumerable<CpuTopologySnapshot?> SubscribeAsync(
        string subscriptionId,
        TimeSpan interval,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var state in SubscribePublishedAsync(subscriptionId, interval, cancellationToken))
            yield return state.Snapshot;
    }

    public async IAsyncEnumerable<DeviceInformationSnapshot> SubscribeInformationAsync(
        string subscriptionId,
        TimeSpan interval,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var state in SubscribePublishedAsync(subscriptionId, interval, cancellationToken))
            yield return state.InformationFields;
    }

    private async IAsyncEnumerable<CpuTopologyPublishedState> SubscribePublishedAsync(
        string subscriptionId,
        TimeSpan interval,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionId);
        cancellationToken.ThrowIfCancellationRequested();
        using var publication = captureOwner.SubscribePublications();
        using var lease = AcquireInformationSubscription(subscriptionId, interval, cancellationToken);
        while (!cancellationToken.IsCancellationRequested)
        {
            await publication.WaitAsync(cancellationToken);
            yield return captureOwner.ReadState();
        }
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        lock (publicationGate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref ownedResourcesDisposed) != 0, this);
            stopped = false;
        }
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var worker = new NativeSamplingSubscriptionWorker<TopologyRequest, SamplingOwnerToken>(
            subscriptions, demandSignal,
            _ =>
            {
                if (!subscriptions.HasPersistentDemand(static request => !request.CancellationToken.IsCancellationRequested))
                    ResetUsageBaselineBeforeNextCapture();
            },
            static failure => System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure.Error).Throw());
        try
        {
            await worker.RunAsync(async (plan, token) =>
            {
                return await CaptureAndPublishAsync(plan.OwnerToken, requireDemand: true,
                    cancellationToken: token, captureCommitted: sourceQuerySucceeded =>
                    {
                        IReadOnlySet<string> queried = sourceQuerySucceeded
                            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) { SnapshotItemId }
                            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        pendingFieldCollection = fieldDeliveries.CaptureCompleted<TopologyRequest>(
                            plan.Triggers, queried, committed: true);
                    });
            }, stoppingToken, planCreated: plan =>
            {
                pendingFieldCollection = null;
                if (plan.Request is null)
                    fieldDeliveries.Deliver<TopologyRequest>(plan.Triggers, null, freshOnly: false);
            }, collectionCompleted: (plan, completion) =>
            {
                var result = pendingFieldCollection;
                pendingFieldCollection = null;
                if (completion == SubscriptionSampleCompletion.Canceled) return;
                fieldDeliveries.Deliver<TopologyRequest>(plan.Triggers, result,
                    freshOnly: plan.Request is null ? true : null);
            });
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            lock (publicationGate)
            {
                stopped = true;
            }
            pendingFieldCollection = null;
            fieldDeliveries.DiscardRunRounds();
            await captureOwner.DrainAndResetUsageBaselineAsync();
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        lock (publicationGate)
        {
            stopped = true;
        }
        return base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        lock (publicationGate)
        {
            stopped = true;
        }
        fieldDeliveries.RetireAll();
        try
        {
            base.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (ExecuteTask?.IsCompleted == true)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "CPU topology sampling worker failed while draining for disposal.");
        }
        lock (publicationGate)
        {
            if (ExecuteTask is null || ExecuteTask.IsCompleted)
                subscriptions.Clear(DateTimeOffset.UtcNow);
        }
        lock (resourceDisposeGate)
        {
            if (Volatile.Read(ref ownedResourcesDisposed) != 0) return;
            subscriptions.CloseOutputScratch();
            demandSignal.Dispose();
            captureOwner.Dispose();
            base.Dispose();
            Volatile.Write(ref ownedResourcesDisposed, 1);
        }
    }

    private void SignalDemand()
    {
        try
        {
            demandSignal.Release();
        }
        catch (SemaphoreFullException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private sealed record TopologyRequest(string SourceKey, CancellationToken CancellationToken = default);

}


