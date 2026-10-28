using System.Diagnostics.Tracing;
using Microsoft.Extensions.Configuration;
using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.ReadWrite.Windows.Processes.Etw;

namespace ResourceManager.App.Infrastructure.Telemetry.Etw;

public sealed partial class GpuAllocationCollector : IBusinessBlock
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

    internal async Task CaptureAsync(IReadOnlyDictionary<int, long> currentProcesses, TimeSpan refreshInterval, CancellationToken cancellationToken)
    {
        if (refreshInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(refreshInterval));
        Run? run;
        CancellationToken admissionToken = default;
        lock (lifecycle)
        {
            // A due capture only uses the actual current run. The parent may
            // intentionally leave the producer stopped while sampling fields are in use.
            if (activeRun is not { } currentRun || Volatile.Read(ref currentRun.Failed)
                || currentRun.StopNotification.IsCancellationRequested || currentRun.CaptureAdmission.IsCancellationRequested)
            {
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
            PublishEmpty(cancellationToken);
            return;
        }

        try
        {
        using var captureStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, admissionToken);
        var captureToken = captureStop.Token;
        try { await run.Started.Task.WaitAsync(captureToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            logger.LogWarning(error, "GPU allocation current run did not become ready; publishing an empty due value.");
            PublishEmpty(cancellationToken);
            return;
        }
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
                if (!ReferenceEquals(activeRun, run)
                    || run.CaptureAdmission.IsCancellationRequested) return;
                keepLive = plan.UseLive(refreshInterval);
                captureAdmitted = true;
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
                        ReportSessionFailure(run, error);
                    }
                }
                var dueSessionEnded = !run.StopNotification.IsCancellationRequested
                    && run.ProcessingCompletion.Task.IsCompletedSuccessfully;
                if (captureAdmitted
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
            if (cancellationToken.IsCancellationRequested) return;
            Volatile.Write(ref published, null);
        }
    }

    private void VerifyLiveLoss(IGpuAllocationSession current, Run run)
    {
        if (SessionControl(run, () => current.EventsLost) != liveEventsLost)
            throw new InvalidDataException("GPU live allocation events were lost; the next due time will rebuild by rundown.");
    }

    private void SessionControl(Run run, Action operation)
        => SessionControl(run, () => { operation(); return true; });

    private T SessionControl<T>(Run run, Func<T> operation)
    {
        try { return operation(); }
        catch (Exception error)
        {
            ReportSessionFailure(run, error);
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
