using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.Operations;

public sealed partial class HostManagerOperationCoordinatorOwner
{
    internal delegate NativeOperationCoordinatorWorkspace WorkspaceFactory(
        ResourceManager.App.Domain.RuntimeSpecialization.CompiledHostManagerOperationCoordinatorPlan plan,
        NativeOperationHandle128 sessionInstanceId,
        NativeOperationHandle128 clockInstanceId,
        Func<ulong, ResourceManager.App.Infrastructure.Adaptation.IComputeScratch> createScratch);

    // All holder transitions use mutationGate. There is no owner/baseline reference:
    // only a hosted run and commands that have entered own workspace references.
    private async ValueTask<WorkspaceHold> AcquireWorkspaceAsync(CancellationToken cancellationToken)
    {
        await mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return AcquireWorkspaceLocked(); }
        finally { mutationGate.Release(); }
    }

    private WorkspaceHold AcquireWorkspaceLocked()
    {
        ThrowIfRetiringLocked();
        if (!ready || persistenceFaulted || store is null || appliedPlan is null)
            throw new InvalidOperationException("The operation coordinator owner is not ready.");
        if (workspaceHolder is null)
        {
            OpenWorkspaceLocked();
            workspaceHolder = new WorkspaceHolder(workspace!);
            workspaceDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        workspaceHolder.AddReference();
        return new WorkspaceHold(this, workspaceHolder);
    }

    private void OpenWorkspaceLocked()
    {
        if (pendingWorkspaceClose is not null || workspace is not null)
            throw new InvalidOperationException("An operation workspace still requires explicit closure.");
        var desired = appliedPlan!;
        var persisted = store!.LoadValidated();
        var sessionId = persisted?.SessionInstanceId
            ?? NativeOperationCoordinatorWorkspace.CreateRandomHandle();
        var clockId = workspaceClockInstanceId;
        workspace = createWorkspace(desired, sessionId, clockId, createScratch);
        try
        {
            if (persisted is not null)
            {
                try
                {
                    workspace.ImportCanonicalNativeImageLocked(persisted.NativeImage,
                        timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                        NativeOperationCoordinatorWorkspace.MonotonicMilliseconds());
                }
                catch (Exception error) when (
                    persisted.ConfigurationGeneration == desired.HotPublish.ConfigurationGeneration
                    && error is InvalidDataException or ArgumentException or OverflowException)
                {
                    // A valid bank rejected under a different plan is an
                    // incompatible reopen, not permission to reset its truth.
                    store.RejectLoadedImage(error);
                    workspace.Dispose();
                    workspace = createWorkspace(desired, sessionId, clockId, createScratch);
                    persisted = null;
                }
            }
            payloads = persisted?.Payloads.Clone() ?? new();
            receipts = persisted?.Receipts.Clone() ?? new();
            publicationRevision = persisted?.PublicationRevision ?? 0;
            lastEnvelopeSha256 = persisted?.FullEnvelopeSha256.ToArray()
                ?? HostManagerOperationCanonicalEnvelopeCodec.ZeroDigest();
            try { CommitCurrentLocked("initial-recovery-publication"); }
            catch (HostManagerOperationMigrationDeferredException)
            {
                workspace.Dispose();
                workspace = createWorkspace(desired, sessionId, clockId, createScratch);
                payloads = new();
                receipts = new();
                publicationRevision = 0;
                lastEnvelopeSha256 = HostManagerOperationCanonicalEnvelopeCodec.ZeroDigest();
                CommitCurrentLocked("initial-recovery-publication");
            }
        }
        catch (Exception error)
        {
            var failed = workspace;
            workspace = null;
            try { RetireWorkspaceLocked(failed!); }
            catch (Exception cleanup) { throw new AggregateException(error, cleanup); }
            throw;
        }
    }

    private async ValueTask ReleaseWorkspaceAsync(WorkspaceHolder holder)
    {
        await mutationGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            try
            {
                if (holder.Release())
                {
                    workspace = null;
                    workspaceHolder = null;
                    workspaceDrained.TrySetResult();
                }
            }
            catch
            {
                // No caller remains to retry this command's final release.
                // Keep the exact failed close for explicit owner disposal,
                // while completing the in-flight call drain.
                // The active field retains this exact failed close. A previous
                // replacement may already occupy pendingWorkspaceClose.
                workspaceHolder = null;
                workspaceDrained.TrySetResult();
                throw;
            }
        }
        finally { mutationGate.Release(); }
    }

    private static TaskCompletionSource CompletedWorkspaceDrain()
    {
        var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        result.SetResult();
        return result;
    }

    private sealed class WorkspaceHolder(NativeOperationCoordinatorWorkspace workspace)
    {
        private int referenceCount;
        internal NativeOperationCoordinatorWorkspace Workspace { get; set; } = workspace;
        internal void AddReference() => referenceCount = checked(referenceCount + 1);
        internal bool Release()
        {
            if (referenceCount == 1) Workspace.Dispose();
            return --referenceCount == 0;
        }
    }

    private sealed class WorkspaceHold(HostManagerOperationCoordinatorOwner owner, WorkspaceHolder holder) : IAsyncDisposable
    {
        private bool released;
        public async ValueTask DisposeAsync()
        {
            if (released) return;
            // A failed final close transfers its obligation back to the owner;
            // this finite call has still finished and must not return twice.
            released = true;
            await owner.ReleaseWorkspaceAsync(holder).ConfigureAwait(false);
        }
    }
}
