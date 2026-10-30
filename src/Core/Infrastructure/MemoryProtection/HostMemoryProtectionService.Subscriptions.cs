using System.Runtime.CompilerServices;
using System.Threading.Channels;
using ResourceManager.App.Application.MemoryProtection;

namespace ResourceManager.App.Infrastructure.MemoryProtection;

public sealed partial class HostMemoryProtectionService
{
    private readonly HashSet<Channel<HostMemoryProtectionPublication>> subscribers = [];
    private HostMemoryProtectionStatus? publishedStatus;
    private NormalRunningPoint? publishedPoint;
    private IReadOnlyList<NormalRunningPoint> publishedPoints = [];
    private HostMemoryCommit? observedCommit;
    private DateTimeOffset observedAt;

    // Status and discrete hooks are two consumers of this one ordered publication.
    // Replay is atomic with registration; replay never masquerades as a new event.
    public async IAsyncEnumerable<HostMemoryProtectionPublication> SubscribeAsync(
        [EnumeratorCancellation] CancellationToken token)
    {
        PublishStatus(await source.ReadCurrentCommitAsync(token), useReading: true);
        var channel = Channel.CreateBounded<HostMemoryProtectionPublication>(new BoundedChannelOptions(64)
            { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        lock (gate)
        {
            channel.Writer.TryWrite(new(publishedStatus!, [])
                { NormalRunningPoint = publishedPoint, NormalRunningPoints = PointList(publishedPoints, publishedPoint), IsReplay = true });
            subscribers.Add(channel);
        }
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var observation = ObserveAsync(cancellation.Token);
        try
        {
            await foreach (var value in channel.Reader.ReadAllAsync(token)) yield return value;
        }
        finally
        {
            lock (gate) subscribers.Remove(channel);
            await cancellation.CancelAsync();
            try { await observation; }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        }

        async Task ObserveAsync(CancellationToken lifetime)
        {
            try
            {
                double interval;
                lock (gate) interval = settings.CheckIntervalSeconds;
                await foreach (var reading in source.SubscribeCommitAsync("host-memory-protection:status:" + Guid.NewGuid().ToString("N"),
                    TimeSpan.FromSeconds(interval), lifetime))
                    PublishStatus(reading.Commit, useReading: true, reading.At);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (Exception error) { channel.Writer.TryComplete(error); }
        }
    }

    private void PublishStatus(HostMemoryCommit? commit = null, bool useReading = false, DateTimeOffset? at = null)
    {
        lock (gate)
        {
            if (useReading && (at ?? DateTimeOffset.UtcNow) >= observedAt)
            {
                observedAt = at ?? DateTimeOffset.UtcNow;
                observedCommit = commit;
            }
            var next = BuildStatus(observedCommit);
            var previous = publishedStatus;
            var pointChanged = !ReferenceEquals(publishedPoint, point);
            var pointsChanged = pointChanged || !ReferenceEquals(publishedPoints, points);
            if (previous is not null && SameStatus(previous, next) && !pointsChanged) return;
            var events = new List<HostMemoryProtectionEvent>();
            if (previous is not null)
            {
                if (previous.Active != next.Active || previous.InactiveReason != next.InactiveReason)
                    events.Add(new(next.Active ? "active" : "inactive", next.InactiveReason));
                if (pointChanged)
                    events.Add(new("normal-running-point-changed"));
                if (pointChanged && point is not null && ReferenceEquals(point, createdPoint))
                    events.Add(new("normal-running-point-created"));
                if (previous.AtOrAboveThreshold != next.AtOrAboveThreshold && next.AtOrAboveThreshold is { } above)
                    events.Add(new(above ? "threshold-reached" : "below-threshold"));
                if (previous.CurrentActionId != next.CurrentActionId && next.CurrentActionId is not null)
                    events.Add(new("action-started"));
                // Written records are the action events, oldest first; a record a full report could not write is none.
                foreach (var action in appendedRecords)
                    events.Add(new("action", action.Reason, action));
                if (previous.Reserve.RemainingBytes != next.Reserve.RemainingBytes
                    || !previous.Reserve.Entries.Select(item => (item.Location, item.RemainingBytes))
                        .SequenceEqual(next.Reserve.Entries.Select(item => (item.Location, item.RemainingBytes))))
                    events.Add(new("reserve-remaining-changed"));
                if (next.Reserve.Reason is { } reason && (previous.Reserve.Reason != reason
                    || !previous.Reserve.Entries.SequenceEqual(next.Reserve.Entries)))
                    events.Add(new("reserve-error", reason));
            }
            var recordsChanged = recordsReplaced || previous is not null && previous.UnrecordedRecordCount != next.UnrecordedRecordCount;
            appendedRecords.Clear();
            recordsReplaced = false;
            publishedStatus = next;
            publishedPoint = point;
            publishedPoints = points;
            if (pointChanged) createdPoint = null;
            var value = new HostMemoryProtectionPublication(next, events)
                { NormalRunningPoint = point, PointChanged = pointChanged, NormalRunningPoints = PointList(points, point), PointsChanged = pointsChanged,
                    RecordsChanged = recordsChanged };
            foreach (var subscriber in subscribers)
                if (!subscriber.Writer.TryWrite(value))
                    subscriber.Writer.TryComplete(new IOException("memory-protection-subscriber-too-slow"));
        }
    }

    private static bool SameStatus(HostMemoryProtectionStatus left, HostMemoryProtectionStatus right)
        => left.Enabled == right.Enabled && left.ThresholdPercent == right.ThresholdPercent
            && left.CheckIntervalSeconds == right.CheckIntervalSeconds && left.ActionMode == right.ActionMode
            && left.MinimumProcessGrowthBytes == right.MinimumProcessGrowthBytes && left.ProtectedProcesses.SequenceEqual(right.ProtectedProcesses)
            && left.DecisionReason == right.DecisionReason
            && left.CurrentCommit == right.CurrentCommit && left.NormalRunningPoint == right.NormalRunningPoint
            && left.InactiveReason == right.InactiveReason && left.CurrentActionId == right.CurrentActionId
            && left.Reserve.Built == right.Reserve.Built && left.Reserve.Reason == right.Reserve.Reason
            && left.Reserve.ConfiguredSizeBytes == right.Reserve.ConfiguredSizeBytes
            && left.Reserve.RemainingBytes == right.Reserve.RemainingBytes
            && left.Reserve.Entries.SequenceEqual(right.Reserve.Entries)
            && left.ExcludedCandidates.SequenceEqual(right.ExcludedCandidates)
            // Records are replaced as a whole on every change, so the list itself is the identity.
            && ReferenceEquals(left.Actions, right.Actions) && left.RecordCapacity == right.RecordCapacity
            && left.UnrecordedRecordCount == right.UnrecordedRecordCount;
}
