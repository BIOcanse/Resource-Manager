using System.Runtime.CompilerServices;
using ResourceManager.App.Application.CpuTopology;
using ResourceManager.App.Domain.CpuTopology;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace ResourceManager.App.Infrastructure.CpuTopology;

public sealed class CpuTopologySnapshotProvider(
    ICpuTopologySampler sampler,
    HostManagerSamplingSubscriptionOwner samplingOwner,
    ILogger<CpuTopologySnapshotProvider> logger) : BackgroundService, ICpuTopologyReader
{
    internal const int SamplingRoleId = 7;
    internal const string SnapshotItemId = "cpu.topology";
    private readonly object publicationGate = new();
    private readonly SemaphoreSlim demandSignal = new(0, 1);
    private readonly CurrentValuePublicationSignal publicationSignal = new();
    private readonly NativeItemSamplingSubscriptionTracker<TopologyRequest> subscriptions = new(
        samplingOwner,
        SamplingRoleId,
        static request => request.SourceKey,
        static _ => [SnapshotItemId],
        static (items, _) => items.Contains(SnapshotItemId, StringComparer.OrdinalIgnoreCase)
            ? new TopologyRequest("provider")
            : null,
        coalesceItemsIntoLatestCapture: true);
    private CpuTopologySnapshot? current;
    private long nextLeaseId;
    private bool stopped;

    public CpuTopologySnapshot? GetSnapshot() => Volatile.Read(ref current);

    public async IAsyncEnumerable<CpuTopologySnapshot?> SubscribeAsync(
        string subscriptionId,
        TimeSpan interval,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionId);
        cancellationToken.ThrowIfCancellationRequested();
        using var publication = publicationSignal.Subscribe();
        var sourceKey = $"lease:{Interlocked.Increment(ref nextLeaseId)}:{subscriptionId.Trim()}";
        lock (publicationGate)
        {
            if (stopped)
            {
                throw new InvalidOperationException("The CPU topology provider has stopped.");
            }
            subscriptions.TrackPersistent(
                new TopologyRequest(sourceKey), DateTimeOffset.UtcNow, interval);
        }
        SignalDemand();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await publication.WaitAsync(cancellationToken);
                yield return GetSnapshot();
            }
        }
        finally
        {
            lock (publicationGate)
            {
                if (!stopped)
                {
                    subscriptions.Remove(sourceKey, DateTimeOffset.UtcNow);
                    SignalDemand();
                }
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var plan = subscriptions.CreatePlan(DateTimeOffset.UtcNow);
                if (!plan.IsActive)
                {
                    await demandSignal.WaitAsync(stoppingToken);
                    continue;
                }
                if (plan.Request is null)
                {
                    await demandSignal.WaitAsync(plan.Delay, stoppingToken);
                    continue;
                }

                var settled = false;
                try
                {
                    CpuTopologySnapshot? value;
                    var failed = false;
                    try
                    {
                        value = sampler.CaptureSnapshot();
                    }
                    catch (Exception exception)
                    {
                        value = null;
                        failed = true;
                        logger.LogWarning(exception, "CPU topology sampling failed.");
                    }

                    var published = false;
                    lock (publicationGate)
                    {
                        if (!stopped && !stoppingToken.IsCancellationRequested)
                        {
                            published = plan.OwnerToken.TryPublish(() =>
                            {
                                Volatile.Write(ref current, value);
                                publicationSignal.Publish();
                            });
                        }
                    }

                    var completedAt = DateTimeOffset.UtcNow;
                    settled = true;
                    var completion = !published
                        ? subscriptions.MarkSkipped(plan.Request, completedAt)
                        : failed
                            ? subscriptions.MarkFailed(plan.Request, completedAt)
                            : subscriptions.MarkSampled(plan.Request, completedAt);
                    await demandSignal.WaitAsync(completion.Delay, stoppingToken);
                }
                finally
                {
                    if (!settled)
                    {
                        subscriptions.MarkSkipped(plan.Request, DateTimeOffset.UtcNow);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            lock (publicationGate)
            {
                stopped = true;
                subscriptions.Clear(DateTimeOffset.UtcNow);
            }
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        lock (publicationGate)
        {
            stopped = true;
            if (ExecuteTask is null)
            {
                subscriptions.Clear(DateTimeOffset.UtcNow);
            }
        }
        return base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        lock (publicationGate)
        {
            stopped = true;
            if (ExecuteTask is null)
            {
                subscriptions.Clear(DateTimeOffset.UtcNow);
            }
        }
        base.Dispose();
        if (ExecuteTask is null || ExecuteTask.IsCompleted)
        {
            demandSignal.Dispose();
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
    }

    private sealed record TopologyRequest(string SourceKey);
}
