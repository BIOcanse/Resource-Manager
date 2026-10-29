using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;
using ResourceManager.ReadWrite.Windows.Processes.Etw;
using BlockUids = ResourceManager.App.StateItems.BlockUids;

namespace ResourceManager.App.Infrastructure.Telemetry.Etw;

internal interface IGpuAllocationSessionFactory
{
    IGpuAllocationSession Create(string sessionName);
}

internal interface IGpuAllocationSession
{
    bool IsProcessing { get; }
    int EventsLost { get; }
    void Bind(Action<Guid, int, IGpuAllocationPayload, DateTime> handler);
    void StartProcessing(Action<Exception?> completed);
    void EnableProvider(Guid provider, ulong keywords = 0, int[]? eventIds = null);
    void DisableProvider(Guid provider);
    void BeginScan(string sessionName, long scan, long eventsLost);
    void EndScan(string sessionName, long scan);
    void CaptureState(Guid provider, ulong keywords, int[]? eventIds = null);
    void Flush();
    KernelEtwSessionStopResult Stop(TimeSpan timeout);
    void JoinProcessing();
}

internal sealed class TraceEventGpuAllocationSessionFactory(
    ILogger logger,
    IEtwConsumerThreadObserver? consumerThreadObserver = null) : IGpuAllocationSessionFactory
{
    public IGpuAllocationSession Create(string sessionName)
    {
        ProcessOwnedEtwSessionOrphanReconciler.CreateDefault(logger).Reconcile(
            ResourceManagerEtwSessionNames.GpuAllocationsPrefix, ResourceManagerEtwSessionNames.CurrentOwner, sessionName);
        TraceEventSession? session = null;
        try
        {
            session = new TraceEventSession(sessionName);
            session.StopOnDispose = true;
            session.BufferSizeMB = 16;
            session.EnableProviderTimeoutMSec = 2000;
            return WrapSession(session);
        }
        catch (Exception failure)
        {
            if (session is not null)
            {
                var cleanup = new TraceEventSessionProcessingRuntime(session, blockId: BlockUids.GpuAllocationCollector).Stop(TimeSpan.FromSeconds(3));
                if (!cleanup.IsReleased) throw new KernelEtwSessionCreationCleanupException(failure,
                    new InvalidOperationException(string.Join(" | ", cleanup.Failures)));
            }
            throw;
        }
    }

    internal TraceEventGpuAllocationSession WrapSession(TraceEventSession session)
        => new(session, consumerThreadObserver);
}

internal sealed class TraceEventGpuAllocationSession(
    TraceEventSession session,
    IEtwConsumerThreadObserver? consumerThreadObserver = null) : IGpuAllocationSession
{
    private readonly TraceEventSessionProcessingRuntime processing = new(
        session,
        consumerThreadObserver: consumerThreadObserver,
        blockId: BlockUids.GpuAllocationCollector);
    private Action<TraceEvent>? eventHandler;
    private Action<TraceEvent>? unhandledHandler;
    private EtwEventDropSession? drops;
    private EtwEventDropSession Drops => drops ??= EtwEventDropDiagnostics.Start(session.SessionName, () => session.EventsLost);
    private GpuAllocationProviderControl? providerControl;
    private GpuAllocationProviderControl Providers => providerControl ??= new(Drops,
        (provider, keywords, options) => session.EnableProvider(provider, TraceEventLevel.Verbose, keywords, options),
        session.DisableProvider,
        (provider, keywords, filterType, data) => session.CaptureState(provider, keywords, filterType, data), session.SessionName);
    public bool IsProcessing => processing.IsProcessing;
    public int EventsLost => processing.EventsLost;
    public void Bind(Action<Guid, int, IGpuAllocationPayload, DateTime> handler)
    {
        var run = Drops;
        var payload = new GpuAllocationIndexedPayload();
        eventHandler = data =>
        {
            var counter = run.Kind(data.ProviderGuid, (int)data.ID, (int)data.Opcode);
            counter.Receive();
            payload.SetEvent(data, counter);
            handler(data.ProviderGuid, (int)data.ID, payload, data.TimeStamp.ToUniversalTime());
        };
        session.Source.Dynamic.All += eventHandler;
        unhandledHandler = data =>
        {
            run.RecordUnhandled(data);
            GpuAllocationObservation.RecordDelivery(session.SessionName, data.ProviderGuid, (int)data.ID,
                data.TimeStamp.ToUniversalTime(), false);
        };
        session.Source.UnhandledEvents += unhandledHandler;
    }
    public void StartProcessing(Action<Exception?> completed) => processing.StartProcessing(completed);
    public void EnableProvider(Guid provider, ulong keywords = 0, int[]? eventIds = null)
        => Providers.Enable(provider, keywords, eventIds);
    public void CaptureState(Guid provider, ulong keywords, int[]? eventIds = null)
        => Providers.CaptureState(provider, keywords, eventIds);
    public void DisableProvider(Guid provider)
        => Providers.Disable(provider);
    public void BeginScan(string sessionName, long scan, long eventsLost)
        => GpuAllocationBoundary.Instance.BeginScan(sessionName, scan, eventsLost);
    public void EndScan(string sessionName, long scan) => GpuAllocationBoundary.Instance.EndScan(sessionName, scan);
    public void Flush() => session.Flush();
    public KernelEtwSessionStopResult Stop(TimeSpan timeout)
    {
        KernelEtwSessionStopResult release;
        try
        {
            if (eventHandler is { } handler) session.Source.Dynamic.All -= handler;
            if (unhandledHandler is { } unhandled) session.Source.UnhandledEvents -= unhandled;
            eventHandler = null;
            unhandledHandler = null;
        }
        finally
        {
            drops?.Complete();
            release = processing.Stop(timeout);
            if (release.IsReleased) providerControl?.RecordSessionReleased();
        }
        return release;
    }
    public void JoinProcessing() => processing.JoinProcessing();
}
