using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;
using ResourceManager.ReadWrite.Windows.Processes.Etw;

namespace ResourceManager.App.Infrastructure.Telemetry.Etw;

internal interface IVidMmEtwSession : IDisposable
{
    void Bind(Action<TraceEvent> handler);
    void Process();
}

internal sealed class TraceEventVidMmSession : IVidMmEtwSession
{
    private readonly TraceEventSession session;
    private readonly TraceEventSessionProcessingRuntime cleanup;
    private Action<TraceEvent>? eventHandler;

    internal TraceEventVidMmSession(string name, string provider, ulong keywords)
    {
        session = new TraceEventSession(name) { StopOnDispose = true };
        cleanup = new(session);
        try { session.EnableProvider(provider, TraceEventLevel.Verbose, keywords); }
        catch (Exception error)
        {
            var release = cleanup.Stop(Timeout.InfiniteTimeSpan);
            if (!release.IsReleased) throw new KernelEtwSessionCreationCleanupException(error,
                new IOException(string.Join(" | ", release.Failures)));
            throw;
        }
    }

    public void Bind(Action<TraceEvent> handler)
    {
        eventHandler = handler;
        session.Source.Dynamic.All += handler;
    }
    public void Process() => session.Source.Process();
    public void Dispose()
    {
        try
        {
            if (eventHandler is { } handler) session.Source.Dynamic.All -= handler;
            eventHandler = null;
        }
        finally
        {
            var release = cleanup.Stop(Timeout.InfiniteTimeSpan);
            if (!release.IsReleased) throw new IOException("VidMm session release was not confirmed: " + string.Join(" | ", release.Failures));
        }
    }
}
