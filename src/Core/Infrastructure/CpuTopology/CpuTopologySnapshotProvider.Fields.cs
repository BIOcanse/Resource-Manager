using ResourceManager.ReadWrite.Windows.Cpu.Snapshots;
using ResourceManager.ReadWrite.Information;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace ResourceManager.App.Infrastructure.CpuTopology;

public sealed partial class CpuTopologySnapshotProvider
{
    private void ResetUsageBaselineBeforeNextCapture() => captureOwner.ResetUsageBaselineBeforeNextCapture();

    internal IDisposable AcquireInformationSubscription(string subscriptionId, TimeSpan interval, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionId);
        cancellationToken.ThrowIfCancellationRequested();
        var sourceKey = $"lease:{Interlocked.Increment(ref nextLeaseId)}:{subscriptionId.Trim()}";
        lock (publicationGate)
        {
            if (stopped) throw new InvalidOperationException("The CPU topology provider has stopped.");
            subscriptions.TrackPersistent(new TopologyRequest(sourceKey, cancellationToken), DateTimeOffset.UtcNow, interval);
        }
        SignalDemand();
        return new InformationLease(this, sourceKey);
    }

    internal async ValueTask<DeviceInformationSnapshot> CaptureInformationAsync(CancellationToken cancellationToken)
    {
        lock (publicationGate)
            if (stopped) throw new InvalidOperationException("The CPU topology provider has stopped.");
        using var lease = samplingOwner.Acquire(SamplingRoleId);
        await CaptureAndPublishAsync(lease.OwnerToken, requireDemand: false, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return GetInformationSnapshot();
    }

    private async Task<SubscriptionSampleCompletion> CaptureAndPublishAsync(
        SamplingOwnerToken ownerToken, bool requireDemand, CancellationToken cancellationToken,
        Action<bool>? captureCommitted = null)
    {
        Action<CpuTopologyPublishedState, bool>? captureCommittedAction = captureCommitted is null
            ? null
            : (_, sourceQuerySucceeded) => captureCommitted(sourceQuerySucceeded);
        var permission = new CpuTopologyCapturePermission(
            () =>
            {
                lock (publicationGate)
                    if (stopped) return CpuTopologyCaptureAdmission.Skip;
                return requireDemand && !subscriptions.HasPersistentDemand(static request => !request.CancellationToken.IsCancellationRequested)
                    ? CpuTopologyCaptureAdmission.SkipAndResetBaseline : CpuTopologyCaptureAdmission.Capture;
            },
            publish =>
            {
                lock (publicationGate)
                    return !stopped && !cancellationToken.IsCancellationRequested && ownerToken.TryPublish(publish);
            }, captureCommittedAction);
        var result = await captureOwner.CaptureAsync(cancellationToken, permission);
        return result switch
        {
            CpuTopologyCaptureCompletion.Sampled => SubscriptionSampleCompletion.Sampled,
            CpuTopologyCaptureCompletion.Failed => SubscriptionSampleCompletion.Failed,
            _ => SubscriptionSampleCompletion.Skipped
        };
    }
    private sealed class InformationLease(CpuTopologySnapshotProvider owner, string key) : IDisposable
    {
        private CpuTopologySnapshotProvider? current = owner;
        public void Dispose()
        {
            var source = Interlocked.Exchange(ref current, null);
            if (source is null) return;
            lock (source.publicationGate)
                if (!source.stopped) { source.subscriptions.Remove(key, DateTimeOffset.UtcNow); source.SignalDemand(); }
        }
    }
}

