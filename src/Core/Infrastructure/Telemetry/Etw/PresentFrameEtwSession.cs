using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;
using ResourceManager.ReadWrite.Windows.Processes.Etw;

namespace ResourceManager.App.Infrastructure.Telemetry.Etw;

internal interface IPresentFrameEtwSession : IDisposable
{
    int EventsLost { get; }
    void Bind(Action<TraceEvent> handler);
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

    public int EventsLost => session.EventsLost;
    public void Bind(Action<TraceEvent> handler)
    {
        eventHandler = handler;
        session.Source.Dynamic.All += handler;
    }
    public void Process() => session.Source.Process();
    public void EnableProvider(Guid provider, TraceEventLevel level, ulong keywords, int[] eventIds)
        => session.EnableProvider(provider, level, keywords,
            new TraceEventProviderOptions { EventIDsToEnable = eventIds });
    public void DisableProvider(Guid provider) => session.DisableProvider(provider);
    public void Flush() => session.Flush();
    public void Dispose()
    {
        try
        {
            if (eventHandler is { } handler) session.Source.Dynamic.All -= handler;
            eventHandler = null;
        }
        finally
        {
            var release = (cleanup ??= new(session)).Stop(Timeout.InfiniteTimeSpan);
            if (!release.IsReleased) throw new IOException("Present session release was not confirmed: " + string.Join(" | ", release.Failures));
        }
    }
}
