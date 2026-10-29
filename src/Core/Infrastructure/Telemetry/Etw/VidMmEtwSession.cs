using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;
using ResourceManager.ReadWrite.Windows.Processes.Etw;
using BlockUids = ResourceManager.App.StateItems.BlockUids;

namespace ResourceManager.App.Infrastructure.Telemetry.Etw;

internal interface IVidMmEtwSession : IDisposable
{
    void Bind(Action<TraceEvent> handler);
    void BindUsage(Func<TraceEvent, bool> handler) => Bind(data => { handler(data); });
    void Process();
}

internal sealed class TraceEventVidMmSession : IVidMmEtwSession
{
    private readonly TraceEventSession session;
    private readonly TraceEventSessionProcessingRuntime cleanup;
    private Action<TraceEvent>? eventHandler;
    private readonly EtwEventDropSession drops;

    internal TraceEventVidMmSession(string name, string provider, ulong keywords)
    {
        session = new TraceEventSession(name) { StopOnDispose = true };
        cleanup = new(session, blockId: BlockUids.DxgkrnlVidMmEtwTelemetryZone);
        drops = EtwEventDropDiagnostics.Start(name, () => session.EventsLost);
        try
        {
            session.EnableProvider(provider, TraceEventLevel.Verbose, keywords);
            drops.EnableProvider(provider, keywords);
        }
        catch (Exception error)
        {
            drops.Complete();
            var release = cleanup.Stop(Timeout.InfiniteTimeSpan);
            if (!release.IsReleased) throw new KernelEtwSessionCreationCleanupException(error,
                new IOException(string.Join(" | ", release.Failures)));
            drops.ConfirmNativeRelease();
            throw;
        }
    }

    public void Bind(Action<TraceEvent> handler)
        => BindUsage(data => { handler(data); return true; });
    public void BindUsage(Func<TraceEvent, bool> handler)
    {
        eventHandler = data =>
        {
            var counter = drops.Kind(data.ProviderGuid, (int)data.ID, (int)data.Opcode);
            counter.Receive();
            if (!handler(data)) counter.Drop();
        };
        session.Source.Dynamic.All += eventHandler;
        session.Source.UnhandledEvents += drops.RecordUnhandled;
    }
    public void Process() => session.Source.Process();
    public void Dispose()
    {
        try
        {
            if (eventHandler is { } handler) session.Source.Dynamic.All -= handler;
            session.Source.UnhandledEvents -= drops.RecordUnhandled;
            eventHandler = null;
        }
        finally
        {
            drops.Complete();
            var release = cleanup.Stop(Timeout.InfiniteTimeSpan);
            if (!release.IsReleased) throw new IOException("VidMm session release was not confirmed: " + string.Join(" | ", release.Failures));
            drops.ConfirmNativeRelease();
        }
    }
}
