using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Domain.Control;
using ResourceManager.App.Infrastructure.Security;

namespace ResourceManager.App.Infrastructure.Control;

public sealed partial class GpuVfCurveService
{
    // Integration seam, not a wire credential. Only a trusted adapter may create/retain this handle
    // for a real client lifetime. Neither a request token, transport connection nor Windows session
    // supplies that lifetime. HTTP binds it below to an attested process; CLI/MCP remain separate integration gates.
    internal EditingClient OpenEditingClient(TrustedAdapterCallerIdentity caller)
    {
        if (caller.HostInstanceId == 0 || caller.ProcessId <= 0 || caller.ProcessCreatedUtcTicks <= 0
            || string.IsNullOrWhiteSpace(caller.WindowsSid))
            throw GpuVfCurveValidation.Error("gpu-vf-curve-edit-session-required");
        return new(this, caller);
    }

    internal EditingClient OpenHttpEditingClient(LoopbackApiProcess caller, IDisposable lifetime)
        => new(this, caller) { Lifetime = lifetime };

    internal async Task<EditingClient?> FindHttpEditingClientAsync(LoopbackApiProcess caller,
        string? draftId, string objectId, bool closing, CancellationToken cancellationToken)
    {
        await mutationGate.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (draftId is not null && drafts.TryGetValue(draftId, out var draft)
                && draft.Descriptor.ObjectId == objectId)
                return RequireEditingClient(draft.Client, caller);
            if (closing) return null; // Repeated close is harmless, including a lost close response.
            throw GpuVfCurveValidation.Error("gpu-vf-curve-draft-not-found");
        }
        finally { mutationGate.Semaphore.Release(); }
    }

    internal async Task<GpuVfCurveDraft> ReadDraftAsync(EditingClient client, object caller,
        string draftId, string objectId, CancellationToken cancellationToken)
    {
        await mutationGate.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return FindDraft(RequireEditingClient(client, caller), draftId, objectId).View(); }
        finally { mutationGate.Semaphore.Release(); }
    }

    // Mark admission closed before waiting for the gate. Already-admitted saves may commit;
    // the gate then drains them before transient state is released. Cleanup is not request-cancellable.
    internal async Task EndEditingClientAsync(EditingClient client, object caller)
    {
        RequireEditingClient(client, caller, allowEnded: true);
        Interlocked.Exchange(ref client.Ending, 1);
        await mutationGate.Semaphore.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            client.ReleaseLifetime();
            foreach (var draft in drafts.Values.Where(draft => ReferenceEquals(draft.Client, client)).ToArray())
                ReleaseDraft(draft);
        }
        finally { mutationGate.Semaphore.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        EditingClient[] clients;
        await mutationGate.Semaphore.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            clients = drafts.Values.Select(draft => draft.Client).Distinct().ToArray();
            foreach (var client in clients)
            {
                Interlocked.Exchange(ref client.Ending, 1);
                client.ReleaseLifetime();
            }
            foreach (var draft in drafts.Values.ToArray()) ReleaseDraft(draft);
        }
        finally { mutationGate.Semaphore.Release(); }
        await Task.WhenAll(clients.Select(client => client.LifetimeEnd ?? Task.CompletedTask)).ConfigureAwait(false);
    }

    private EditingClient RequireEditingClient(EditingClient? client, object? caller,
        bool allowEnded = false)
    {
        if (client is null || caller is null)
            throw GpuVfCurveValidation.Error("gpu-vf-curve-edit-session-required");
        if (!ReferenceEquals(client.Owner, this) || !client.Matches(caller)
            || !allowEnded && Volatile.Read(ref client.Ending) != 0)
            throw GpuVfCurveValidation.Error("gpu-vf-curve-draft-not-found");
        return client;
    }

    private void ReleaseDraft(DraftState draft)
    {
        drafts.Remove(draft.Id);
        draft.Undo.Clear();
        draft.Redo.Clear();
        draft.Points = [];
        draft.Client.ReleaseLifetime();
    }

    // One existing begin/close draft is one explicit editing session. Multiple sessions may belong
    // to one client; only the existing bounded drafts dictionary retains their points/history.
    internal sealed class EditingClient(GpuVfCurveService owner, object identity)
    {
        internal GpuVfCurveService Owner { get; } = owner;
        internal int Ending;
        internal IDisposable? Lifetime;
        internal Task? LifetimeEnd;
        internal void ReleaseLifetime() => Interlocked.Exchange(ref Lifetime, null)?.Dispose();
        internal bool Matches(object caller) => (identity, caller) switch
        {
            (LoopbackApiProcess expected, LoopbackApiProcess observed) => expected == observed,
            (TrustedAdapterCallerIdentity expected, TrustedAdapterCallerIdentity observed) =>
                expected.HostInstanceId == observed.HostInstanceId && expected.ProcessId == observed.ProcessId
                && expected.ProcessCreatedUtcTicks == observed.ProcessCreatedUtcTicks
                && expected.WindowsSid == observed.WindowsSid && expected.WindowsSessionId == observed.WindowsSessionId
                && expected.AuthenticationIdLuid == observed.AuthenticationIdLuid
                && expected.IntegrityLevelRid == observed.IntegrityLevelRid && expected.IsElevated == observed.IsElevated
                && expected.CanonicalExecutablePath == observed.CanonicalExecutablePath
                && expected.ExecutableFileIdentity == observed.ExecutableFileIdentity,
            _ => false
        };
        // TransportConnectionId/attestation timestamp identify a request, not the editing lifetime.
    }
}
