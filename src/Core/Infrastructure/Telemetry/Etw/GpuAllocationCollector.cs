using System.Diagnostics.Tracing;
using Microsoft.Extensions.Configuration;
using Microsoft.Diagnostics.Tracing;

namespace ResourceManager.App.Infrastructure.Telemetry.Etw;

public sealed class GpuAllocationCollector(ILogger<GpuAllocationCollector> logger, IConfiguration configuration) : IDisposable
{
    private static readonly Guid Provider = new("802ec45a-1e99-4b83-9920-87c98277ba9d");
    private static readonly Guid BoundaryProvider = EventSource.GetGuid(typeof(GpuAllocationBoundary));
    private readonly object lifecycle = new();
    private readonly object gate = new();
    private GpuAllocationLedger ledger = new();
    private IGpuAllocationSession? session;
    private readonly IGpuAllocationSessionFactory sessionFactory = new TraceEventGpuAllocationSessionFactory(logger);
    private readonly TimeProvider timeProvider = TimeProvider.System;
    private static readonly TimeSpan RestartInterval = TimeSpan.FromSeconds(5);
    private long lastStartAttempt;
    private bool hasStartAttempt;
    private bool runtimeReleaseBlocked;
    private Timer? rescanTimer;
    private GpuAllocationLedger? candidate;
    private bool candidateFailed;
    private long scanId;
    private long activeScanId;
    private long scanEventsLost;
    private long acceptedEventsLost;
    internal long CompletedRescans { get; private set; }
    private int leases;
    private bool ready;
    private bool failed;
    private bool disposed;
    private ulong generation;
    private static readonly int[] EventIds = [33, 34, 35, 36, 37, 38, 39, 40, 53, 54, 55,
        74, 78, 80, 110, 227, 306, 307, 313, 314, 391];

    internal GpuAllocationCollector(ILogger<GpuAllocationCollector> logger, IConfiguration configuration,
        IGpuAllocationSessionFactory sessionFactory, TimeProvider timeProvider) : this(logger, configuration)
        => (this.sessionFactory, this.timeProvider) = (sessionFactory, timeProvider);

    public IDisposable AcquireSubscription()
    {
        lock (lifecycle)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (leases++ == 0) Start();
            return new Lease(this);
        }
    }

    public GpuAllocationReading? ReadCurrent(IReadOnlyDictionary<int, long> currentProcesses)
    {
        lock (lifecycle)
        {
            if (disposed || runtimeReleaseBlocked || leases == 0) return null;
            EnsureProcessing();
            if (session is null || !session.IsProcessing) return null;
            return ReadVerifiedCurrent(currentProcesses, session.EventsLost);
        }
    }

    internal GpuAllocationReading? ReadVerifiedCurrent(IReadOnlyDictionary<int, long> currentProcesses, long eventsLost)
    {
        lock (gate)
        {
            if (!ready || failed) return null;
            try
            {
                if (eventsLost != acceptedEventsLost) throw new InvalidDataException("GPU allocation events were lost.");
                return ledger.Read(currentProcesses) with { Generation = generation, ObservedAtUtcTicks = DateTime.UtcNow.Ticks };
            }
            catch (Exception error)
            {
                failed = true;
                logger.LogWarning(error, "GPU allocation current value is empty.");
                return null;
            }
        }
    }

    private void Start()
    {
        if (runtimeReleaseBlocked) return;
        hasStartAttempt = true;
        lastStartAttempt = timeProvider.GetTimestamp();
        lock (gate) { ledger = new(); ready = false; failed = false; generation++; acceptedEventsLost = 0; }
        try
        {
            var name = ResourceManagerEtwSessionNames.Create(ResourceManagerEtwSessionNames.GpuAllocationsPrefix,
                ResourceManagerEtwSessionNames.CurrentOwner);
            var current = sessionFactory.Create(name);
            session = current;
            current.Bind(data =>
            {
                lock (gate)
                {
                    if (!ReferenceEquals(Volatile.Read(ref session), current)) return;
                    try
                    {
                        if (data.ProviderGuid == BoundaryProvider)
                        {
                            if ((int)data.ID == 1 && data.PayloadByName("session")?.ToString() == name) ready = true;
                            if (data.PayloadByName("session")?.ToString() == name && (int)data.ID is 2 or 3)
                            {
                                var id = Convert.ToInt64(data.PayloadByName("scan"));
                                if ((int)data.ID == 2)
                                {
                                    BeginRescan(id, Convert.ToInt64(data.PayloadByName("eventsLost")));
                                }
                                else if (candidate is not null && activeScanId == id)
                                {
                                    CompleteRescan(id, current.EventsLost);
                                }
                            }
                            return;
                        }
                        if (data.ProviderGuid != Provider || !EventIds.Contains((int)data.ID)) return;
                        ObserveAllocation((int)data.ID, data.PayloadByName, data.TimeStamp.ToUniversalTime());
                    }
                    catch (Exception error)
                    {
                        failed = true;
                        logger.LogWarning(error, "GPU allocation event could not be applied.");
                    }
                }
            });
            current.StartProcessing(error =>
            {
                lock (gate)
                {
                    if (!ReferenceEquals(Volatile.Read(ref session), current)) return;
                    ready = false; failed = true;
                }
                logger.LogWarning(error, "GPU allocation event stream ended; the next requested read will retry.");
            });
            current.EnableProvider(BoundaryProvider);
            current.EnableProvider(Provider, 0x41, EventIds);
            current.CaptureState(Provider, 0x41);
            current.EnableProvider(Provider, 0x41, EventIds);
            GpuAllocationBoundary.Instance.Complete(name);
            current.Flush();
            var seconds = configuration.GetValue("Monitoring:GpuAllocationRescanSeconds", 60d);
            if (!double.IsFinite(seconds) || seconds <= 0 || seconds > uint.MaxValue / 1000d)
                throw new ArgumentOutOfRangeException("Monitoring:GpuAllocationRescanSeconds");
            var interval = TimeSpan.FromSeconds(seconds);
            rescanTimer = new Timer(_ => Rescan(current, name), null, interval, interval);
        }
        catch (Exception error)
        {
            lock (gate) failed = true;
            logger.LogWarning(error, "GPU allocation subscription could not start.");
            Stop();
            if (error is KernelEtwSessionCreationCleanupException) runtimeReleaseBlocked = true;
        }
    }

    internal void BeginRescan(long id, long eventsLost)
    {
        lock (gate)
        {
            candidate = new(); candidateFailed = false; activeScanId = id; scanEventsLost = eventsLost;
        }
    }

    internal void CompleteRescan(long id, long eventsLost)
    {
        lock (gate)
        {
            if (candidate is null || activeScanId != id) return;
            if (!candidateFailed && eventsLost == scanEventsLost)
            {
                candidate.Read(new Dictionary<int, long>());
                ledger = candidate; ready = true; failed = false; acceptedEventsLost = eventsLost;
                generation++; CompletedRescans++;
                logger.LogInformation("GPU allocation full rescan {Scan} published.", id);
            }
            else failed = true;
            candidate = null;
        }
    }

    internal void ObserveAllocation(int eventId, Func<string, object> payload, DateTime timestampUtc)
    {
        lock (gate)
        {
            if (candidate is not null && !candidateFailed)
            {
                try { GpuAllocationEventDecoder.Apply(candidate, eventId, payload, timestampUtc); }
                catch (Exception error) { candidateFailed = true; logger.LogWarning(error, "GPU allocation rescan could not be applied."); }
            }
            if (!failed) GpuAllocationEventDecoder.Apply(ledger, eventId, payload, timestampUtc);
            generation++;
        }
    }

    private void Rescan(IGpuAllocationSession current, string name)
    {
        lock (lifecycle)
        {
            if (disposed || session != current) return;
            if (!current.IsProcessing) { EnsureProcessing(); return; }
            try
            {
                var id = ++scanId;
                GpuAllocationBoundary.Instance.BeginScan(name, id, current.EventsLost);
                current.CaptureState(Provider, 0x41);
                GpuAllocationBoundary.Instance.EndScan(name, id);
                current.Flush();
            }
            catch (Exception error)
            {
                lock (gate) { candidateFailed = true; failed = true; }
                logger.LogWarning(error, "GPU allocation rescan failed.");
            }
        }
    }

    private void Release()
    {
        lock (lifecycle) { if (leases > 0 && --leases == 0) Stop(); }
    }

    private void Stop()
    {
        rescanTimer?.Dispose();
        rescanTimer = null;
        var current = session;
        if (current is null && runtimeReleaseBlocked) return;
        var released = current?.Stop(TimeSpan.FromSeconds(3));
        if (released is not null && !released.IsReleased)
        {
            runtimeReleaseBlocked = true;
            lock (gate) { ready = false; failed = true; }
            logger.LogError("GPU allocation session release was not confirmed: {Failures}", string.Join(" | ", released.Failures));
            return;
        }
        session = null;
        runtimeReleaseBlocked = false;
        lock (gate) { ready = false; ledger = new(); candidate = null; }
    }

    private void EnsureProcessing()
    {
        if (disposed || leases == 0 || runtimeReleaseBlocked || session?.IsProcessing == true) return;
        if (hasStartAttempt && timeProvider.GetElapsedTime(lastStartAttempt) < RestartInterval) return;
        Stop();
        if (!runtimeReleaseBlocked) Start();
    }

    public void Dispose()
    {
        lock (lifecycle) { if (disposed) return; disposed = true; leases = 0; Stop(); }
    }

    private sealed class Lease(GpuAllocationCollector owner) : IDisposable
    {
        private GpuAllocationCollector? owner = owner;
        public void Dispose() => Interlocked.Exchange(ref owner, null)?.Release();
    }
}

[EventSource(Name = "ResourceManager-GpuAllocationBoundary", Guid = "37BB21BA-90C3-4F21-947D-CAB8F561950F")]
internal sealed class GpuAllocationBoundary : EventSource
{
    public static readonly GpuAllocationBoundary Instance = new();
    [Event(1, Level = EventLevel.Informational)]
    public void Complete(string session) => WriteEvent(1, session);
    [Event(2, Level = EventLevel.Informational)]
    public void BeginScan(string session, long scan, long eventsLost) => WriteEvent(2, session, scan, eventsLost);
    [Event(3, Level = EventLevel.Informational)]
    public void EndScan(string session, long scan) => WriteEvent(3, session, scan);
}
