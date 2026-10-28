using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;
using ResourceManager.ReadWrite.Windows.Processes.Etw;
using BlockUids = ResourceManager.App.StateItems.BlockUids;

namespace ResourceManager.App.Infrastructure.Telemetry.Etw;

internal interface IPresentFrameEtwSession : IDisposable
{
    int EventsLost { get; }
    void Bind(Action<TraceEvent> handler);
    void BindUsage(Func<TraceEvent, bool> handler) => Bind(data => { handler(data); });
    void Process();
    void EnableProvider(Guid provider, TraceEventLevel level, ulong keywords, int[] eventIds);
    void DisableProvider(Guid provider);
    void Flush();
}

internal sealed class TraceEventPresentFrameEtwSession(string name) : IPresentFrameEtwSession
{
    private readonly TraceEventSession session = new(name)
    {
        StopOnDispose = true,
        BufferSizeMB = 16,
        EnableProviderTimeoutMSec = 2000
    };
    private TraceEventSessionProcessingRuntime? cleanup;
    private Action<TraceEvent>? eventHandler;
    private EtwEventDropSession? drops;
    private EtwEventDropSession Drops => drops ??= EtwEventDropDiagnostics.Start(name, () => session.EventsLost);

    public int EventsLost => session.EventsLost;
    public void Bind(Action<TraceEvent> handler)
        => BindUsage(data => { handler(data); return true; });
    public void BindUsage(Func<TraceEvent, bool> handler)
    {
        var run = Drops;
        eventHandler = data =>
        {
            var counter = run.Kind(data.ProviderGuid, (int)data.ID, (int)data.Opcode);
            counter.Receive();
            if (!handler(data)) counter.Drop();
        };
        session.Source.Dynamic.All += eventHandler;
        session.Source.UnhandledEvents += run.RecordUnhandled;
    }
    public void Process() => session.Source.Process();
    public void EnableProvider(Guid provider, TraceEventLevel level, ulong keywords, int[] eventIds)
    {
        session.EnableProvider(provider, level, keywords,
            new TraceEventProviderOptions { EventIDsToEnable = eventIds });
        Drops.EnableProvider(provider.ToString("D"), keywords);
    }
    public void DisableProvider(Guid provider)
    {
        session.DisableProvider(provider);
        Drops.DisableProvider(provider.ToString("D"));
    }
    public void Flush() => session.Flush();
    public void Dispose()
    {
        try
        {
            if (eventHandler is { } handler) session.Source.Dynamic.All -= handler;
            if (drops is { } run) session.Source.UnhandledEvents -= run.RecordUnhandled;
            eventHandler = null;
        }
        finally
        {
            drops?.Complete();
            var release = (cleanup ??= new(session, blockId: BlockUids.PresentFrameEtwSource)).Stop(Timeout.InfiniteTimeSpan);
            if (!release.IsReleased) throw new IOException("Present session release was not confirmed: " + string.Join(" | ", release.Failures));
        }
    }
}
