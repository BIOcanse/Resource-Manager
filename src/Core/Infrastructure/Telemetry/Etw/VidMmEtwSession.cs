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
    private readonly EtwFixedLayoutGate layouts;

    // The provider is enabled by GUID: a provider name makes TraceEvent enumerate every
    // registered provider through TDH into a process-lifetime name table.
    internal TraceEventVidMmSession(string name, Guid provider, ulong keywords, EtwSessionBuffers buffers, ILogger logger)
    {
        // The declared pool (vidmm_session_buffers), not TraceEvent's 64 MiB default.
        session = new TraceEventSession(name)
            { StopOnDispose = true, BufferQuantumKB = buffers.BufferKib, BufferSizeMB = buffers.PoolMib };
        cleanup = new(session, blockId: BlockUids.DxgkrnlVidMmEtwTelemetryZone);
        drops = EtwEventDropDiagnostics.Start(name, () => session.EventsLost);
        layouts = new(logger, "DxgKrnl VidMm", drops);
        try
        {
            session.EnableProvider(provider, TraceEventLevel.Verbose, keywords);
            drops.EnableProvider(provider.ToString("D"), keywords);
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
        // No parser is registered on this source, so every event arrives here undecoded.
        // A consumed event whose version or length does not match its fixed layout
        // (DxgkrnlVidMmEtwLayouts) is counted and never reaches the handler.
        eventHandler = data =>
        {
            var counter = drops.Kind(data.ProviderGuid, (int)data.ID, (int)data.Opcode);
            counter.Receive();
            if (layouts.Admit(data, counter, DxgkrnlVidMmEtwLayouts.Catalog.Find(data, out _)) && !handler(data)) counter.Drop();
        };
        session.Source.UnhandledEvents += eventHandler;
    }
    public void Process() => session.Source.Process();
    public void Dispose()
    {
        try
        {
            if (eventHandler is { } handler) session.Source.UnhandledEvents -= handler;
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
