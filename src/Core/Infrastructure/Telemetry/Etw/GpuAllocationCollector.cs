using System.Diagnostics.Tracing;
using Microsoft.Extensions.Configuration;
using ResourceManager.ReadWrite.Windows.Processes.Etw;

namespace ResourceManager.App.Infrastructure.Telemetry.Etw;

public sealed class GpuAllocationCollector(
    ILogger<GpuAllocationCollector> logger,
    IConfiguration configuration,
    IEtwConsumerThreadObserver? consumerThreadObserver = null) : IDisposable
{
    internal static readonly Guid Provider = new("802ec45a-1e99-4b83-9920-87c98277ba9d");
    private static readonly Guid BoundaryProvider = EventSource.GetGuid(typeof(GpuAllocationBoundary));
    private static readonly int[] EventIds = [33, 34, 35, 36, 37, 38, 39, 40, 53, 54, 55,
        74, 78, 80, 110, 227, 306, 307, 313, 314, 391];
    private readonly GpuAllocationCapturePlan plan = GpuAllocationCapturePlan.Compile(configuration);
    private readonly object lifecycle = new();
    private readonly object gate = new();
    private readonly SemaphoreSlim captureGate = new(1, 1);
    private readonly IGpuAllocationSessionFactory sessionFactory = new TraceEventGpuAllocationSessionFactory(
        logger,
        consumerThreadObserver);
    private readonly TimeProvider timeProvider = TimeProvider.System;
    private readonly TimeSpan deliveryBudget = TimeSpan.FromSeconds(5);
    private readonly HashSet<Lease> leases = [];
    private IGpuAllocationSession? session;
    private string? sessionName;
    private Scan? scan;
    private GpuAllocationLedger? liveLedger;
    private long liveEventsLost;
    private Exception? liveFailure;
    private Published? published;
    private long nextScanId;
    private ulong publicationRevision;
    private bool runtimeReleaseBlocked;
    private bool disposed;

    internal GpuAllocationCollector(ILogger<GpuAllocationCollector> logger, IConfiguration configuration,
        IGpuAllocationSessionFactory sessionFactory, TimeProvider timeProvider, TimeSpan? deliveryBudget = null) : this(logger, configuration)
        => (this.sessionFactory, this.timeProvider, this.deliveryBudget) =
            (sessionFactory, timeProvider, deliveryBudget ?? TimeSpan.FromSeconds(5));

    // Native resource subscriptions still own due times. Held intervals select
    // the execution mechanism only; admission and release do not collect or switch.
    public IDisposable AcquireSubscription(TimeSpan refreshInterval)
    {
        if (refreshInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(refreshInterval));
        lock (lifecycle)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var lease = new Lease(this, refreshInterval);
            leases.Add(lease);
            return lease;
        }
    }

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
        await captureGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        IGpuAllocationSession? current = null;
        Scan? pending = null;
        GpuAllocationReading? value = null;
        var keepLive = false;
        try
        {
            lock (lifecycle)
            {
                if (disposed || leases.Count == 0) return;
                keepLive = plan.UseLive(leases.Min(static lease => lease.Interval));
                if (runtimeReleaseBlocked) throw new InvalidOperationException("The owned GPU allocation runtime was not released.");
                if (session?.IsProcessing != true)
                {
                    StopSession();
                    if (runtimeReleaseBlocked) throw new InvalidOperationException("The previous GPU allocation runtime was not released.");
                    StartSession();
                }
                current = session!;
                lock (gate)
                {
                    if (liveLedger is not null)
                    {
                        if (liveFailure is not null) throw new InvalidDataException("GPU live allocation decoding failed.", liveFailure);
                        VerifyLiveLoss(current);
                    }
                    if (keepLive && liveLedger is not null)
                    {
                        // This due publishes empty on loss/failure. Its NEXT due,
                        // after invalidation in finally, is the sole rebuild path.
                        value = liveLedger.Read(currentProcesses);
                        VerifyLiveLoss(current);
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
                    current.EnableProvider(BoundaryProvider);
                    current.EnableProvider(Provider, 0x41, EventIds);
                    current.BeginScan(sessionName!, pending.Id, current.EventsLost);
                    current.CaptureState(Provider, 0x41);
                    current.EndScan(sessionName!, pending.Id);
                    current.Flush();
                }
            }
            if (pending is not null)
            {
                var candidate = await pending.Completion.Task.WaitAsync(deliveryBudget, timeProvider, cancellationToken).ConfigureAwait(false);
                lock (gate)
                {
                    if (keepLive)
                    {
                        if (liveFailure is not null) throw new InvalidDataException("GPU live allocation decoding failed after rundown.", liveFailure);
                        VerifyLiveLoss(current!);
                    }
                    value = candidate.Read(currentProcesses);
                    if (keepLive) VerifyLiveLoss(current!);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
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
                    if (!keepLive || value is null || cancellationToken.IsCancellationRequested)
                    {
                        liveLedger = null;
                        liveFailure = null;
                    }
                }
                if (current is not null && ReferenceEquals(current, session)
                    && (!keepLive || value is null || cancellationToken.IsCancellationRequested))
                {
                    try { current.DisableProvider(Provider); current.DisableProvider(BoundaryProvider); }
                    catch (Exception error)
                    {
                        value = null;
                        logger.LogWarning(error, "GPU allocation provider could not be paused; stopping the owned session and publishing empty.");
                        StopSession();
                    }
                }
                if (!disposed && leases.Count > 0 && !cancellationToken.IsCancellationRequested)
                {
                    if (!ReferenceEquals(current, session)) value = null;
                    if (value is not null) value = value with
                    {
                        Generation = ++publicationRevision, ObservedAtUtcTicks = timeProvider.GetUtcNow().UtcTicks
                    };
                    Volatile.Write(ref published, value is null ? null : new Published(value, new Dictionary<int, long>(currentProcesses)));
                }
            }
            captureGate.Release();
        }
    }

    private void VerifyLiveLoss(IGpuAllocationSession current)
    {
        if (current.EventsLost != liveEventsLost)
            throw new InvalidDataException("GPU live allocation events were lost; the next due time will rebuild by rundown.");
    }

    private void StartSession()
    {
        var name = ResourceManagerEtwSessionNames.Create(ResourceManagerEtwSessionNames.GpuAllocationsPrefix,
            ResourceManagerEtwSessionNames.CurrentOwner);
        IGpuAllocationSession current;
        try { current = sessionFactory.Create(name); }
        catch (KernelEtwSessionCreationCleanupException) { runtimeReleaseBlocked = true; throw; }
        session = current;
        sessionName = name;
        current.Bind((provider, id, payload, timestamp) =>
        {
            lock (gate)
            {
                if (!ReferenceEquals(Volatile.Read(ref session), current)) return;
                try
                {
                    if (provider == BoundaryProvider)
                    {
                        // Only BeginScan (2) and EndScan (3) carry boundary fields. The EventSource also writes
                        // its self-describing manifest (event 65534) each time a capture enables the provider.
                        if (id is not (2 or 3)) return;
                        if (scan is not { } pending || pending.Completion.Task.IsCompleted
                            || payload.Get(GpuAllocationField.session)?.ToString() != name
                            || Convert.ToInt64(payload.Get(GpuAllocationField.scan)) != pending.Id) return;
                        if (id == 2)
                        {
                            pending.Candidate = new();
                            pending.EventsLost = Convert.ToInt64(payload.Get(GpuAllocationField.eventsLost));
                        }
                        else if (id == 3 && pending.Candidate is { } candidate)
                        {
                            if (current.EventsLost != pending.EventsLost)
                                throw new InvalidDataException("GPU allocation events were lost between rundown boundaries.");
                            if (pending.KeepLive) { liveLedger = candidate; liveEventsLost = pending.EventsLost; }
                            pending.Candidate = null;
                            pending.Completion.TrySetResult(candidate);
                        }
                        return;
                    }
                    if (provider != Provider || !EventIds.Contains(id)) return;
                    var target = scan?.Candidate ?? (liveFailure is null ? liveLedger : null);
                    if (target is not null) GpuAllocationEventDecoder.Apply(target, id, payload, timestamp);
                }
                catch (Exception error)
                {
                    if (scan is { } pending && !pending.Completion.Task.IsCompleted)
                    {
                        pending.Candidate = null;
                        pending.Completion.TrySetException(error);
                    }
                    else if (liveLedger is not null) liveFailure = error;
                }
            }
        });
        current.StartProcessing(error =>
        {
            lock (gate)
            {
                if (!ReferenceEquals(Volatile.Read(ref session), current)) return;
                scan?.Completion.TrySetException(error ?? new IOException("GPU allocation event stream ended during rundown."));
            }
            logger.LogWarning(error, "GPU allocation event stream ended; the next due time will try again.");
        });
    }

    private void Release(Lease lease)
    {
        lock (lifecycle)
        {
            if (leases.Remove(lease) && leases.Count == 0) { StopSession(); Volatile.Write(ref published, null); }
        }
    }

    private void StopSession()
    {
        lock (gate)
        {
            scan?.Completion.TrySetException(new OperationCanceledException("GPU allocation demand was released."));
            scan = null;
            liveLedger = null;
            liveFailure = null;
        }
        if (runtimeReleaseBlocked) return;
        var released = session?.Stop(TimeSpan.FromSeconds(3));
        if (released is not null && !released.IsReleased)
        {
            runtimeReleaseBlocked = true;
            logger.LogError("GPU allocation session release was not confirmed: {Failures}", string.Join(" | ", released.Failures));
            return;
        }
        session = null;
        sessionName = null;
    }

    public void Dispose()
    {
        lock (lifecycle) { if (disposed) return; disposed = true; leases.Clear(); StopSession(); Volatile.Write(ref published, null); }
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
    private sealed class Lease(GpuAllocationCollector owner, TimeSpan interval) : IDisposable
    {
        private GpuAllocationCollector? owner = owner;
        internal TimeSpan Interval { get; } = interval;
        public void Dispose() => Interlocked.Exchange(ref owner, null)?.Release(this);
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
