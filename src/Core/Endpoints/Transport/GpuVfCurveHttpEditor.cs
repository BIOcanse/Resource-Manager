using System.ComponentModel;
using System.Diagnostics;
using System.Security;
using ResourceManager.App.Application.Control;
using ResourceManager.App.Domain.Control;
using ResourceManager.App.Infrastructure.Control;
using ResourceManager.App.Infrastructure.Security;

namespace ResourceManager.App.Endpoints.Transport;

/// <summary>HTTP requests borrow an attested process identity; only explicit close or process exit ends editing.</summary>
public sealed class GpuVfCurveHttpEditor
{
    private readonly GpuVfCurveService service;
    private readonly Func<HttpContext, Peer?> attest;

    public GpuVfCurveHttpEditor(GpuVfCurveService service) : this(service, ReadPeer) { }
    internal GpuVfCurveHttpEditor(GpuVfCurveService service, Func<HttpContext, Peer?> attest)
    { this.service = service; this.attest = attest; }

    internal async Task<GpuVfCurveDraft?> ExecuteAsync(HttpContext context, GpuVfCurveEditorCommand command,
        CancellationToken token)
    {
        var peer = attest(context) ?? throw Required();
        if (command.Operation == "begin")
        {
            var client = service.OpenHttpEditingClient(peer.Identity, peer);
            client.LifetimeEnd = EndOnExitAsync(client, peer);
            try { return await service.ExecuteAsync(client, peer.Identity, command, token).ConfigureAwait(false); }
            catch
            {
                await service.EndEditingClientAsync(client, peer.Identity).ConfigureAwait(false);
                await client.LifetimeEnd!.ConfigureAwait(false);
                throw;
            }
        }
        using (peer)
        {
            var client = await service.FindHttpEditingClientAsync(peer.Identity, command.DraftId,
                command.ObjectId, command.Operation == "close", token).ConfigureAwait(false);
            if (client is null) return null;
            GpuVfCurveDraft? draft;
            try { draft = await service.ExecuteAsync(client, peer.Identity, command, token).ConfigureAwait(false); }
            catch (GpuVfCurveRequestException error) when (command.Operation == "close"
                && error.Reason.Code == "gpu-vf-curve-draft-not-found") { draft = null; }
            if (command.Operation == "close" && client.LifetimeEnd is { } ended)
                await ended.ConfigureAwait(false);
            return draft;
        }
    }

    internal async Task<GpuVfCurvePreset> SaveAsync(HttpContext context, string curveId,
        GpuVfCurveSaveRequest request, CancellationToken token)
    {
        using var peer = attest(context) ?? throw Required();
        var client = await service.FindHttpEditingClientAsync(peer.Identity, request.DraftId,
            request.ObjectId, false, token).ConfigureAwait(false);
        return await service.SaveAsync(client!, peer.Identity, curveId, request, token).ConfigureAwait(false);
    }

    private async Task EndOnExitAsync(GpuVfCurveService.EditingClient client, Peer peer)
    {
        try { await peer.Exited.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        finally { await service.EndEditingClientAsync(client, peer.Identity).ConfigureAwait(false); }
    }

    private static GpuVfCurveRequestException Required()
        => new(new("gpu-vf-curve-edit-session-required"));

    private static Peer? ReadPeer(HttpContext context)
    {
        if (!WindowsLoopbackApiAdministratorReader.TryReadConnectedCaller(context, out var identity)) return null;
        Process? process = null;
        try
        {
            process = Process.GetProcessById(identity.Caller.ProcessId);
            // Hold the actual process handle before checking creation time, so PID reuse cannot switch the waiter.
            _ = process.SafeHandle;
            if (process.StartTime.ToUniversalTime().Ticks != identity.Caller.ProcessCreatedUtcTicks || process.HasExited)
            { process.Dispose(); return null; }
            var ended = new CancellationTokenSource();
            var exited = process.WaitForExitAsync(ended.Token);
            var owned = process;
            return new(identity, exited, () => { ended.Cancel(); owned.Dispose(); ended.Dispose(); });
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException
            or ArgumentException or UnauthorizedAccessException or SecurityException or NotSupportedException)
        { process?.Dispose(); return null; }
    }

    internal sealed class Peer(LoopbackApiProcess identity, Task exited, Action release) : IDisposable
    {
        private Action? release = release;
        internal LoopbackApiProcess Identity { get; } = identity;
        internal Task Exited { get; } = exited;
        public void Dispose() => Interlocked.Exchange(ref release, null)?.Invoke();
    }
}
