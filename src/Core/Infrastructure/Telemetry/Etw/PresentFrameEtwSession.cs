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
    void EnableProvider(Guid provider, TraceEventLevel level, ulong keywords, int[] eventIds, int[]? processIds = null);
    void DisableProvider(Guid provider);
    void Flush();
}

internal sealed class TraceEventPresentFrameEtwSession : IPresentFrameEtwSession
{
    private readonly string name;
    private readonly TraceEventSession session;
    private readonly TraceEventSessionProcessingRuntime cleanup;
    private ETWTraceEventSource? source;

    internal TraceEventPresentFrameEtwSession(string name)
    {
        this.name = name;
        session = new(name)
        {
            StopOnDispose = true,
            BufferSizeMB = 16,
            EnableProviderTimeoutMSec = 2000
        };
        cleanup = new(session, blockId: BlockUids.PresentFrameEtwSource);
    }
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
        var processingSource = source ??= session.Source;
        processingSource.Dynamic.All += eventHandler;
        processingSource.UnhandledEvents += run.RecordUnhandled;
    }
    public void Process() => cleanup.Process();
    public void EnableProvider(Guid provider, TraceEventLevel level, ulong keywords, int[] eventIds, int[]? processIds = null)
    {
        session.EnableProvider(provider, level, keywords,
            new TraceEventProviderOptions { EventIDsToEnable = eventIds, ProcessIDFilter = processIds });
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
            if (source is { } processingSource)
            {
                if (eventHandler is { } handler) processingSource.Dynamic.All -= handler;
                if (drops is { } run) processingSource.UnhandledEvents -= run.RecordUnhandled;
            }
            eventHandler = null;
        }
        finally
        {
            drops?.Complete();
            var release = cleanup.Stop(TimeSpan.FromSeconds(2));
            if (!release.IsReleased) throw new IOException("Present session release was not confirmed: " + string.Join(" | ", release.Failures));
            drops?.ConfirmNativeRelease();
        }
    }
}
