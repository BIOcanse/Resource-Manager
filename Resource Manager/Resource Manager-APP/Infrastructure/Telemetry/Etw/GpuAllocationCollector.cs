using System.Diagnostics.Tracing;
using Microsoft.Extensions.Configuration;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace ResourceManager.App.Infrastructure.Telemetry.Etw;

public sealed class GpuAllocationCollector(ILogger<GpuAllocationCollector> logger, IConfiguration configuration) : IDisposable
{
    private static readonly Guid Provider = new("802ec45a-1e99-4b83-9920-87c98277ba9d");
    private readonly object lifecycle = new();
    private readonly object gate = new();
    private GpuAllocationLedger ledger = new();
    private TraceEventSession? session;
    private Task? processor;
    private Timer? rescanTimer;
    private GpuAllocationLedger? candidate;
    private bool candidateFailed;
    private long scanId;
    private long activeScanId;
    internal long CompletedRescans { get; private set; }
    private int leases;
    private bool ready;
    private bool failed;
    private bool disposed;
    private ulong generation;
    private static readonly int[] EventIds = [33, 34, 35, 36, 37, 38, 39, 40, 53, 54, 55,
        74, 78, 80, 110, 227, 306, 307, 313, 314, 391];

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
            if (session is null) return null;
            lock (gate)
            {
                if (!ready || failed) return null;
                try
                {
                    if (session.EventsLost != 0) throw new InvalidDataException("GPU allocation events were lost.");
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
    }

    private void Start()
    {
        lock (gate) { ledger = new(); ready = false; failed = false; generation = 0; }
        try
        {
            var name = $"ResourceManagerGpuAllocations-{Environment.ProcessId}-{Guid.NewGuid():N}";
            var current = new TraceEventSession(name) { StopOnDispose = true, BufferSizeMB = 16, EnableProviderTimeoutMSec = 2000 };
            session = current;
            current.Source.Dynamic.All += data =>
            {
                lock (gate)
                {
                    try
                    {
                        if (data.ProviderGuid == EventSource.GetGuid(typeof(GpuAllocationBoundary)))
                        {
                            if ((int)data.ID == 1 && data.PayloadByName("session")?.ToString() == name) ready = true;
                            if (data.PayloadByName("session")?.ToString() == name && (int)data.ID is 2 or 3)
                            {
                                var id = Convert.ToInt64(data.PayloadByName("scan"));
                                if ((int)data.ID == 2)
                                {
                                    candidate = new();
                                    candidateFailed = false;
                                    activeScanId = id;
                                }
                                else if (candidate is not null && activeScanId == id)
                                {
                                    if (!candidateFailed && current.EventsLost == 0)
                                    {
                                        candidate.Read(new Dictionary<int, long>());
                                        ledger = candidate;
                                        ready = true;
                                        failed = false;
                                        generation++;
                                        CompletedRescans++;
                                        logger.LogInformation("GPU allocation full rescan {Scan} published.", id);
                                    }
                                    candidate = null;
                                }
                            }
                            return;
                        }
                        if (data.ProviderGuid != Provider || !EventIds.Contains((int)data.ID)) return;
                        if (candidate is not null && !candidateFailed)
                        {
                            try { Apply(candidate, data); }
                            catch (Exception error)
                            {
                                candidateFailed = true;
                                logger.LogWarning(error, "GPU allocation rescan could not be applied.");
                            }
                        }
                        if (!failed) Apply(ledger, data);
                        generation++;
                    }
                    catch (Exception error)
                    {
                        failed = true;
                        logger.LogWarning(error, "GPU allocation event could not be applied.");
                    }
                }
            };
            processor = Task.Factory.StartNew(() =>
            {
                try { current.Source.Process(); }
                catch (Exception error)
                {
                    lock (gate) failed = true;
                    logger.LogWarning(error, "GPU allocation event stream ended.");
                }
                finally { lock (gate) { ready = false; failed = true; } }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            current.EnableProvider(EventSource.GetGuid(typeof(GpuAllocationBoundary)));
            current.EnableProvider(Provider, TraceEventLevel.Verbose, 0x41,
                new TraceEventProviderOptions { EventIDsToEnable = EventIds });
            current.CaptureState(Provider, 0x41);
            current.EnableProvider(Provider, TraceEventLevel.Verbose, 0x41,
                new TraceEventProviderOptions { EventIDsToEnable = EventIds });
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
        }
    }

    private static void Apply(GpuAllocationLedger target, TraceEvent data)
        => GpuAllocationEventDecoder.Apply(target, (int)data.ID, data.PayloadByName, data.TimeStamp.ToUniversalTime());

    private void Rescan(TraceEventSession current, string name)
    {
        lock (lifecycle)
        {
            if (disposed || session != current) return;
            try
            {
                var id = ++scanId;
                GpuAllocationBoundary.Instance.BeginScan(name, id);
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
        session = null;
        current?.Dispose();
        if (processor is not null && !processor.Wait(TimeSpan.FromSeconds(3)))
            throw new TimeoutException("GPU allocation event processing did not stop.");
        processor = null;
        lock (gate) { ready = false; ledger = new(); candidate = null; }
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
    public void BeginScan(string session, long scan) => WriteEvent(2, session, scan);
    [Event(3, Level = EventLevel.Informational)]
    public void EndScan(string session, long scan) => WriteEvent(3, session, scan);
}
