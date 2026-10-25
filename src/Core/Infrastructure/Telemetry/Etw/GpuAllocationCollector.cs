using System.Diagnostics.Tracing;
using Microsoft.Extensions.Configuration;
using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.ReadWrite.Windows.Processes.Etw;

namespace ResourceManager.App.Infrastructure.Telemetry.Etw;

public sealed partial class GpuAllocationCollector : IBusinessBlock, IBusinessBlockStartup
{
    internal static readonly Guid Provider = new("802ec45a-1e99-4b83-9920-87c98277ba9d");
    private static readonly Guid BoundaryProvider = EventSource.GetGuid(typeof(GpuAllocationBoundary));
    private static readonly int[] EventIds = [33, 34, 35, 36, 37, 38, 39, 40, 53, 54, 55,
        74, 78, 80, 110, 227, 306, 307, 313, 314, 391];
    private readonly object gate = new();
    private readonly GpuAllocationCapturePlan plan;
    private Scan? scan;
    private GpuAllocationLedger? liveLedger;
    private long liveEventsLost;
    private Exception? liveFailure;
    private Published? published;
    private long nextScanId;
    private ulong publicationRevision;

    public GpuAllocationReading? ReadCurrent(IReadOnlyDictionary<int, long> currentProcesses)
    {
        var current = Volatile.Read(ref published);
        if (current is null) return null;
        bool Matches(int pid) => currentProcesses.TryGetValue(pid, out var birth)
            && current.Processes.TryGetValue(pid, out var capturedBirth) && birth == capturedBirth;
        if (current.Value.Adapters.Values.All(adapter => adapter.Keys.All(Matches))) return current.Value;
        return current.Value with
        {
            Adapters = current.Value.Adapters.ToDictionary(pair => pair.Key,
                pair => (IReadOnlyDictionary<int, GpuAllocationAmount>)pair.Value.Where(item => Matches(item.Key))
                    .ToDictionary(item => item.Key, item => item.Value))
        };
    }

    internal async Task CaptureAsync(IReadOnlyDictionary<int, long> currentProcesses, CancellationToken cancellationToken)
    {
        Lease[] subscriptions;
        lock (lifecycle)
        {
            subscriptions = ActiveSubscriptionsLocked();
            if (subscriptions.Length == 0) return;
        }

        try { await Task.WhenAll(subscriptions.Select(static lease => lease.Ready)).WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            // A dependency's Ready task records only its first queued admission. If
            // that startup failed, an explicit controller retry can start a new run
            // while the same dependency remains held; inspect the current run rather
            // than treating the old Ready fault as permanent.
            Run? currentRun;
            lock (lifecycle)
                currentRun = ActiveSubscriptionsLocked().Length == 0 ? null : activeRun;
            if (currentRun is null)
            {
                logger.LogWarning(error, "GPU allocation dependency did not become ready; publishing an empty due value.");
                PublishEmpty(cancellationToken);
                return;
            }

            try { await currentRun.Started.Task.WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception runError)
            {
                logger.LogWarning(runError, "The current GPU allocation run did not become ready; publishing an empty due value.");
                PublishEmpty(cancellationToken);
                return;
            }
        }

        Run? run;
        CancellationToken admissionToken = default;
        var publishEmptyForCompletedRun = false;
        lock (lifecycle)
        {
            if (ActiveSubscriptionsLocked().Length == 0) return;
            if (activeRun is not { } currentRun)
            {
                publishEmptyForCompletedRun = lastRunOutcome is BusinessBlockOutcome.Done or BusinessBlockOutcome.Failed;
                run = null;
            }
            else if (currentRun.CaptureAdmission.IsCancellationRequested)
            {
                publishEmptyForCompletedRun = !currentRun.StopNotification.IsCancellationRequested
                    && currentRun.ProcessingCompletion.Task.IsCompletedSuccessfully;
                run = null;
            }
            else
            {
                run = currentRun;
                admissionToken = currentRun.CaptureAdmission.Token;
                currentRun.RegisterCapture();
            }
        }
        if (run is null)
        {
            if (publishEmptyForCompletedRun) PublishEmpty(cancellationToken);
            return;
        }

        try
        {
        using var captureStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, admissionToken);
        var captureToken = captureStop.Token;
        var enteredCaptureGate = false;
        IGpuAllocationSession? current = null;
        Scan? pending = null;
        GpuAllocationReading? value = null;
        var keepLive = false;
        var captureAdmitted = false;
        try
        {
            await captureGate.WaitAsync(captureToken).ConfigureAwait(false);
            enteredCaptureGate = true;
            lock (lifecycle)
            {
                var activeSubscriptions = ActiveSubscriptionsLocked();
                if (activeSubscriptions.Length == 0 || !ReferenceEquals(activeRun, run)
                    || run.CaptureAdmission.IsCancellationRequested) return;
                keepLive = plan.UseLive(activeSubscriptions.Min(static lease => lease.Interval));
                captureAdmitted = true;
                if (runtimeReleaseBlocked)
                {
                    var error = new InvalidOperationException("The owned GPU allocation runtime was not released.");
                    run.ProcessingCompletion.TrySetResult(error);
                    throw error;
                }
                if (!SessionControl(run, () => session?.IsProcessing == true))
                {
                    run.ProcessingCompletion.TrySetResult(null);
                    return;
                }
                current = session!;
                lock (gate)
                {
                    if (liveLedger is not null)
                    {
                        if (liveFailure is not null) throw new InvalidDataException("GPU live allocation decoding failed.", liveFailure);
                        VerifyLiveLoss(current, run);
                    }
                    if (keepLive && liveLedger is not null)
                    {
                        // This due publishes empty on loss/failure. Its NEXT due,
                        // after invalidation in finally, is the sole rebuild path.
                        value = liveLedger.Read(currentProcesses);
                        VerifyLiveLoss(current, run);
                    }
                    else
                    {
                        liveLedger = null;
                        liveFailure = null;
                        scan = pending = new Scan(++nextScanId, keepLive);
                    }
                }
                if (pending is not null)
                {
                    SessionControl(run, () => current.EnableProvider(BoundaryProvider));
                    SessionControl(run, () => current.EnableProvider(Provider, 0x41, EventIds));
                    var eventsLost = SessionControl(run, () => current.EventsLost);
                    SessionControl(run, () => current.BeginScan(sessionName!, pending.Id, eventsLost));
                    SessionControl(run, () => current.CaptureState(Provider, 0x41));
                    SessionControl(run, () => current.EndScan(sessionName!, pending.Id));
                    SessionControl(run, current.Flush);
                }
            }
            if (pending is not null)
            {
                var candidate = await pending.Completion.Task.WaitAsync(deliveryBudget, timeProvider, captureToken).ConfigureAwait(false);
                lock (gate)
                {
                    if (keepLive)
                    {
                        if (liveFailure is not null) throw new InvalidDataException("GPU live allocation decoding failed after rundown.", liveFailure);
                        VerifyLiveLoss(current!, run);
                    }
                    value = candidate.Read(currentProcesses);
                    if (keepLive) VerifyLiveLoss(current!, run);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || run.StopNotification.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            if (!run.StopNotification.IsCancellationRequested && run.ProcessingCompletion.Task.IsCompletedSuccessfully)
                captureAdmitted = true;
            value = null;
            logger.LogWarning(error, "GPU allocation due capture published an empty value.");
        }
        finally
        {
            lock (lifecycle)
            {
                lock (gate)
                {
                    if (scan == pending) scan = null;
                    if (!keepLive || value is null || captureToken.IsCancellationRequested)
                    {
                        liveLedger = null;
                        liveFailure = null;
                    }
                }
                if (current is not null && ReferenceEquals(current, session)
                    && (!keepLive || value is null || captureToken.IsCancellationRequested))
                {
                    try { current.DisableProvider(Provider); current.DisableProvider(BoundaryProvider); }
                    catch (Exception error)
                    {
                        value = null;
                        logger.LogWarning(error, "GPU allocation provider could not be paused; reporting the owned session failure and publishing empty.");
                        run.ProcessingCompletion.TrySetResult(error);
                    }
                }
                var dueSessionEnded = !run.StopNotification.IsCancellationRequested
                    && run.ProcessingCompletion.Task.IsCompletedSuccessfully;
                if (captureAdmitted && ActiveSubscriptionsLocked().Length > 0
                    && (!captureToken.IsCancellationRequested || dueSessionEnded))
                {
                    if (!ReferenceEquals(current, session)) value = null;
                    if (value is not null) value = value with
                    {
                        Generation = ++publicationRevision, ObservedAtUtcTicks = timeProvider.GetUtcNow().UtcTicks
                    };
                    Volatile.Write(ref published, value is null ? null : new Published(value, new Dictionary<int, long>(currentProcesses)));
                }
            }
            if (enteredCaptureGate) captureGate.Release();
        }
        }
        finally
        {
            lock (lifecycle) run.ReleaseCapture();
        }
    }

    private void PublishEmpty(CancellationToken cancellationToken)
    {
        lock (lifecycle)
        {
            if (ActiveSubscriptionsLocked().Length == 0 || cancellationToken.IsCancellationRequested) return;
            Volatile.Write(ref published, null);
        }
    }

    private void VerifyLiveLoss(IGpuAllocationSession current, Run run)
    {
        if (SessionControl(run, () => current.EventsLost) != liveEventsLost)
            throw new InvalidDataException("GPU live allocation events were lost; the next due time will rebuild by rundown.");
    }

    private static void SessionControl(Run run, Action operation)
        => SessionControl(run, () => { operation(); return true; });

    private static T SessionControl<T>(Run run, Func<T> operation)
    {
        try { return operation(); }
        catch (Exception error)
        {
            run.ProcessingCompletion.TrySetResult(error);
            throw;
        }
    }

    private sealed record Published(GpuAllocationReading Value, IReadOnlyDictionary<int, long> Processes);
    private sealed class Scan(long id, bool keepLive)
    {
        internal long Id { get; } = id;
        internal bool KeepLive { get; } = keepLive;
        internal GpuAllocationLedger? Candidate;
        internal long EventsLost;
        internal TaskCompletionSource<GpuAllocationLedger> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

[EventSource(Name = "ResourceManager-GpuAllocationBoundary", Guid = "37BB21BA-90C3-4F21-947D-CAB8F561950F")]
internal sealed class GpuAllocationBoundary : EventSource
{
    public static readonly GpuAllocationBoundary Instance = new();
    [Event(2, Level = EventLevel.Informational)]
    public void BeginScan(string session, long scan, long eventsLost) => WriteEvent(2, session, scan, eventsLost);
    [Event(3, Level = EventLevel.Informational)]
    public void EndScan(string session, long scan) => WriteEvent(3, session, scan);
}
