using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

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
    void CaptureState(Guid provider, ulong keywords);
    void Flush();
    KernelEtwSessionStopResult Stop(TimeSpan timeout);
}

internal sealed class TraceEventGpuAllocationSessionFactory(ILogger logger) : IGpuAllocationSessionFactory
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
            return new TraceEventGpuAllocationSession(session);
        }
        catch (Exception failure)
        {
            if (session is not null)
            {
                var cleanup = new TraceEventSessionProcessingRuntime(session).Stop(TimeSpan.FromSeconds(3));
                if (!cleanup.IsReleased) throw new KernelEtwSessionCreationCleanupException(failure,
                    new InvalidOperationException(string.Join(" | ", cleanup.Failures)));
            }
            throw;
        }
    }
}

internal sealed class TraceEventGpuAllocationSession(TraceEventSession session) : IGpuAllocationSession
{
    private readonly TraceEventSessionProcessingRuntime processing = new(session);
    public bool IsProcessing => processing.IsProcessing;
    public int EventsLost => processing.EventsLost;
    public void Bind(Action<Guid, int, IGpuAllocationPayload, DateTime> handler)
    {
        var payload = new GpuAllocationIndexedPayload();
        session.Source.Dynamic.All += data =>
        {
            payload.SetEvent(data);
            handler(data.ProviderGuid, (int)data.ID, payload, data.TimeStamp.ToUniversalTime());
        };
    }
    public void StartProcessing(Action<Exception?> completed) => processing.StartProcessing(completed);
    public void EnableProvider(Guid provider, ulong keywords = 0, int[]? eventIds = null)
    {
        if (eventIds is null) session.EnableProvider(provider, TraceEventLevel.Verbose, keywords);
        else session.EnableProvider(provider, TraceEventLevel.Verbose, keywords,
            new TraceEventProviderOptions { EventIDsToEnable = eventIds });
    }
    public void CaptureState(Guid provider, ulong keywords) => session.CaptureState(provider, keywords);
    public void DisableProvider(Guid provider) => session.DisableProvider(provider);
    public void BeginScan(string sessionName, long scan, long eventsLost)
        => GpuAllocationBoundary.Instance.BeginScan(sessionName, scan, eventsLost);
    public void EndScan(string sessionName, long scan) => GpuAllocationBoundary.Instance.EndScan(sessionName, scan);
    public void Flush() => session.Flush();
    public KernelEtwSessionStopResult Stop(TimeSpan timeout) => processing.Stop(timeout);
}
