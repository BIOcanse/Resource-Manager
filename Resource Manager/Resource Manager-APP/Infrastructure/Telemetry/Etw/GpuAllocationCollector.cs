using System.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace ResourceManager.App.Infrastructure.Telemetry.Etw;

public sealed class GpuAllocationCollector(ILogger<GpuAllocationCollector> logger) : IDisposable
{
    private static readonly Guid Provider = new("802ec45a-1e99-4b83-9920-87c98277ba9d");
    private readonly object lifecycle = new();
    private readonly object gate = new();
    private GpuAllocationLedger ledger = new();
    private TraceEventSession? session;
    private Task? processor;
    private int leases;
    private bool ready;
    private bool failed;
    private bool disposed;

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
                    return ledger.Read(currentProcesses);
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
        lock (gate) { ledger = new(); ready = false; failed = false; }
        try
        {
            var name = $"ResourceManagerGpuAllocations-{Environment.ProcessId}-{Guid.NewGuid():N}";
            var current = new TraceEventSession(name) { StopOnDispose = true, BufferSizeMB = 16, EnableProviderTimeoutMSec = 2000 };
            session = current;
            current.Source.Dynamic.All += data =>
            {
                lock (gate)
                {
                    if (failed) return;
                    try
                    {
                        if (data.ProviderGuid == EventSource.GetGuid(typeof(GpuAllocationBoundary)))
                        {
                            if ((int)data.ID == 1 && data.PayloadByName("session")?.ToString() == name) ready = true;
                            return;
                        }
                        if (data.ProviderGuid != Provider) return;
                        Apply(data);
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
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            current.EnableProvider(EventSource.GetGuid(typeof(GpuAllocationBoundary)));
            current.EnableProvider(Provider, TraceEventLevel.Verbose, 0x40,
                new TraceEventProviderOptions { EventIDsToEnable = [33, 34, 35, 36, 37, 38, 39, 110] });
            current.CaptureState(Provider, 0x41);
            current.EnableProvider(Provider, TraceEventLevel.Verbose, 0x40,
                new TraceEventProviderOptions { EventIDsToEnable = [33, 34, 35, 36, 37, 38, 39, 110] });
            GpuAllocationBoundary.Instance.Complete(name);
            current.Flush();
        }
        catch (Exception error)
        {
            lock (gate) failed = true;
            logger.LogWarning(error, "GPU allocation subscription could not start.");
            Stop();
        }
    }

    private void Apply(TraceEvent data)
    {
        static ulong Unsigned(object value) => value switch
        {
            ulong number => number,
            long number => unchecked((ulong)number),
            uint number => number,
            int number => unchecked((uint)number),
            _ => Convert.ToUInt64(value, System.Globalization.CultureInfo.InvariantCulture)
        };
        ulong Field(string field) => Unsigned(data.PayloadByName(field));
        switch ((int)data.ID)
        {
            case 110:
                ledger.SetAdapter(Field("pDxgAdapter"), Field("AdapterLuid"));
                break;
            case 33: case 35:
                ledger.SetAllocation(Field("pDxgAdapter"), Field("hVidMmGlobalAlloc"), Field("allocSize"),
                    Convert.ToBoolean(data.PayloadByName("PageTableOrDirectory"), System.Globalization.CultureInfo.InvariantCulture));
                break;
            case 36: case 38:
                ledger.Open(Field("pDxgAdapter"), Field("hVidMmGlobalAlloc"), Field("hVidMmAlloc"),
                    checked((int)Field("hProcessId")), data.TimeStamp.ToUniversalTime().ToFileTimeUtc());
                break;
            case 37: case 39:
                ledger.Close(Field("hVidMmAlloc"));
                break;
            case 34:
                ledger.Delete(Field("pDxgAdapter"), Field("hVidMmGlobalAlloc"));
                break;
        }
    }

    private void Release()
    {
        lock (lifecycle) { if (leases > 0 && --leases == 0) Stop(); }
    }

    private void Stop()
    {
        var current = session;
        session = null;
        current?.Dispose();
        if (processor is not null && !processor.Wait(TimeSpan.FromSeconds(3)))
            throw new TimeoutException("GPU allocation event processing did not stop.");
        processor = null;
        lock (gate) { ready = false; ledger = new(); }
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
}
