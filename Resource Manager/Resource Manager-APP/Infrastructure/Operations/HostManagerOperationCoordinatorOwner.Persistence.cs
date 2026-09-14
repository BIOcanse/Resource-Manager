using System.Security.Cryptography;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.Operations;

public sealed partial class HostManagerOperationCoordinatorOwner
{
    private async Task ApplyPlanAsync(
        CompiledHostManagerPlan hostPlan,
        bool throwOnFailure,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(hostPlan);
        var desired = hostPlan.OperationCoordinator;
        if (!desired.IsPublished)
        {
            throw new InvalidOperationException(
                "The Host Manager operation-coordinator plan is not published.");
        }
        ValidateRouter(desired);

        await mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (persistenceFaulted)
            {
                throw new InvalidOperationException(
                    "The operation coordinator is permanently persistence-faulted.");
            }
            if (appliedPlan?.ConfigurationSha256 == desired.ConfigurationSha256)
            {
                Volatile.Write(ref pendingQuiescentRecreate, 0);
                Volatile.Write(ref lastPlanApplicationError, null);
                return;
            }
            if (workspace is null)
            {
                ApplyInitialLocked(hostPlan, desired);
                Volatile.Write(ref lastPlanApplicationError, null);
                return;
            }
            if (appliedPlan is null || store is null)
            {
                throw new InvalidOperationException(
                    "The operation coordinator owner has a partial runtime.");
            }
            if (started
                && RequiresProcessTopologyRestart(appliedPlan, desired))
            {
                throw new InvalidOperationException(
                    "The operation coordinator process topology requires a Host Manager restart.");
            }
            if (desired.Build == appliedPlan.Build
                && desired.Recreate == appliedPlan.Recreate)
            {
                Volatile.Write(ref pendingQuiescentRecreate, 0);
                ApplyHotLocked(hostPlan, desired);
                Volatile.Write(ref lastPlanApplicationError, null);
                return;
            }
            var currentSnapshot = workspace.SnapshotLocked();
            if (currentSnapshot.ActiveOperationCount != 0 || !attemptArbiter.IsEmpty)
            {
                Volatile.Write(ref pendingQuiescentRecreate, 1);
                return;
            }
            Volatile.Write(ref pendingQuiescentRecreate, 0);
            ApplyRecreateLocked(hostPlan, desired);
            Volatile.Write(ref lastPlanApplicationError, null);
        }
        catch (Exception error)
        {
            Volatile.Write(ref lastPlanApplicationError, error.Message);
            if (throwOnFailure)
            {
                throw;
            }
            logger.LogError(
                error,
                "Host Manager operation-coordinator plan application was rejected.");
        }
        finally
        {
            mutationGate.Release();
        }
    }

    private void ApplyInitialLocked(
        CompiledHostManagerPlan hostPlan,
        CompiledHostManagerOperationCoordinatorPlan desired)
    {
        var token = deployment.BeginInitialCreate(hostPlan);
        HostManagerOperationCanonicalStore? replacementStore = null;
        NativeOperationCoordinatorWorkspace? replacementWorkspace = null;
        try
        {
            var envelopePath =
                ResolveEnvelopePath(desired.Recreate.CanonicalEnvelopeRelativePath);
            replacementStore = new HostManagerOperationCanonicalStore(
                envelopePath,
                desired.Recreate.Capacity,
                desired.HotPublish.ConfigurationGeneration);
            replacementStore.AcquireOwnerLease();
            var persisted = replacementStore.LoadValidated();
            var sessionId = persisted?.SessionInstanceId
                ?? NativeOperationCoordinatorWorkspace.CreateRandomHandle();
            var clockId = NativeOperationCoordinatorWorkspace.CreateRandomHandle();
            replacementWorkspace = new NativeOperationCoordinatorWorkspace(
                desired,
                sessionId,
                clockId);
            if (persisted is not null)
            {
                replacementWorkspace.ImportCanonicalNativeImageLocked(
                    persisted.NativeImage,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    NativeOperationCoordinatorWorkspace.MonotonicMilliseconds());
            }

            workspace = replacementWorkspace;
            store = replacementStore;
            appliedPlan = desired;
            payloads = persisted?.Payloads.Clone()
                ?? new HostManagerOperationPayloadCatalog();
            receipts = persisted?.Receipts.Clone()
                ?? new HostManagerOperationEffectReceiptTable();
            publicationRevision = persisted?.PublicationRevision ?? 0;
            lastEnvelopeSha256 = persisted?.FullEnvelopeSha256.ToArray()
                ?? HostManagerOperationCanonicalEnvelopeCodec.ZeroDigest();
            replacementWorkspace = null;
            replacementStore = null;
            CommitCurrentLocked("initial-recovery-publication");
            ready = true;
            PublishHealthOnlyLocked();
            deployment.CompleteSucceeded(token);
        }
        catch (Exception error)
        {
            ready = false;
            replacementWorkspace?.Dispose();
            replacementStore?.Dispose();
            workspace?.Dispose();
            workspace = null;
            store?.Dispose();
            store = null;
            appliedPlan = null;
            try
            {
                deployment.CompleteFailed(
                    token,
                    "operation-coordinator-initial-create-failed");
            }
            catch (Exception settlementError)
            {
                throw new AggregateException(error, settlementError);
            }
            throw;
        }
    }

    private void ApplyHotLocked(
        CompiledHostManagerPlan hostPlan,
        CompiledHostManagerOperationCoordinatorPlan desired)
    {
        var currentWorkspace = workspace
            ?? throw new InvalidOperationException(
                "The operation coordinator workspace is absent.");
        _ = store
            ?? throw new InvalidOperationException(
                "The operation coordinator store is absent.");
        var token = deployment.BeginHotPublish(hostPlan);
        NativeOperationCoordinatorWorkspace? replacement = null;
        var canonicalCommitted = false;
        try
        {
            replacement = CreateReplacementWorkspaceLocked(
                currentWorkspace,
                desired);
            var committed = CommitCandidateLocked(
                replacement,
                desired);
            var acceptedState = PrepareCommittedState(committed);
            canonicalCommitted = true;
            deployment.CompleteSucceeded(token);
            AcceptCommittedRuntimeLocked(
                replacement,
                desired,
                acceptedState);
            replacement = null;
            currentWorkspace.Dispose();
            SignalPlanner();
        }
        catch (Exception error)
        {
            replacement?.Dispose();
            if (canonicalCommitted)
            {
                LatchPersistenceFaultLocked(error, "hot-publication");
            }
            try
            {
                deployment.CompleteFailed(
                    token,
                    "operation-coordinator-hot-publish-failed");
            }
            catch (Exception settlementError)
            {
                throw new AggregateException(error, settlementError);
            }
            throw;
        }
    }

    private void ApplyRecreateLocked(
        CompiledHostManagerPlan hostPlan,
        CompiledHostManagerOperationCoordinatorPlan desired)
    {
        var currentPlan = appliedPlan
            ?? throw new InvalidOperationException(
                "The operation coordinator plan is absent.");
        var currentWorkspace = workspace
            ?? throw new InvalidOperationException(
                "The operation coordinator workspace is absent.");
        _ = store
            ?? throw new InvalidOperationException(
                "The operation coordinator store is absent.");
        var currentSnapshot = currentWorkspace.SnapshotLocked();
        if (currentSnapshot.ActiveOperationCount != 0 || !attemptArbiter.IsEmpty)
        {
            throw new InvalidOperationException(
                "Operation-coordinator recreate is pending until all active attempts are quiescent.");
        }
        var currentPath =
            ResolveEnvelopePath(currentPlan.Recreate.CanonicalEnvelopeRelativePath);
        var desiredPath =
            ResolveEnvelopePath(desired.Recreate.CanonicalEnvelopeRelativePath);
        if (!string.Equals(currentPath, desiredPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The operation canonical envelope path requires an explicit offline migration.");
        }

        var token = deployment.BeginHostRecreateAndHotPublish(hostPlan);
        NativeOperationCoordinatorWorkspace? replacement = null;
        var canonicalCommitted = false;
        try
        {
            replacement = CreateReplacementWorkspaceLocked(
                currentWorkspace,
                desired);
            var committed = CommitCandidateLocked(
                replacement,
                desired);
            var acceptedState = PrepareCommittedState(committed);
            canonicalCommitted = true;
            deployment.CompleteSucceeded(token);
            AcceptCommittedRuntimeLocked(
                replacement,
                desired,
                acceptedState);
            replacement = null;
            currentWorkspace.Dispose();
            SignalPlanner();
        }
        catch (Exception error)
        {
            replacement?.Dispose();
            if (canonicalCommitted)
            {
                LatchPersistenceFaultLocked(error, "host-recreate-publication");
            }
            try
            {
                deployment.CompleteFailed(
                    token,
                    "operation-coordinator-host-recreate-failed");
            }
            catch (Exception settlementError)
            {
                throw new AggregateException(error, settlementError);
            }
            throw;
        }
    }

    private void CommitCurrentLocked(
        string stage,
        HostManagerOperationPayloadCatalog? candidatePayloads = null,
        HostManagerOperationEffectReceiptTable? candidateReceipts = null)
    {
        RequireRuntimeLocked();
        try
        {
            var committed = CommitCandidateLocked(
                workspace!,
                appliedPlan!,
                candidatePayloads,
                candidateReceipts);
            AcceptCommittedStateLocked(
                PrepareCommittedState(committed));
        }
        catch (Exception error)
        {
            LatchPersistenceFaultLocked(error, stage);
            throw;
        }
    }

    private HostManagerOperationCanonicalEnvelope CommitCandidateLocked(
        NativeOperationCoordinatorWorkspace candidateWorkspace,
        CompiledHostManagerOperationCoordinatorPlan candidatePlan,
        HostManagerOperationPayloadCatalog? candidatePayloads = null,
        HostManagerOperationEffectReceiptTable? candidateReceipts = null)
    {
        var nextPayloads = candidatePayloads ?? payloads.Clone();
        var nextReceipts = candidateReceipts ?? receipts.Clone();
        var nativeImage =
            candidateWorkspace.ExportCanonicalNativeImageLocked();
        var liveOperationIds = nativeImage.Records
            .Select(static record => record.OperationId)
            .ToHashSet();
        nextReceipts.PruneTo(liveOperationIds);
        var roots =
            HostManagerOperationPayloadCatalog.CollectNativeRoots(
                nativeImage.Records);
        nextReceipts.AddPayloadRoots(roots);
        nextPayloads.PruneTo(roots);
        var nextPublicationRevision = publicationRevision == ulong.MaxValue
            ? throw new InvalidOperationException(
                "The operation envelope publication revision is exhausted.")
            : publicationRevision + 1;
        var candidate = new HostManagerOperationCanonicalEnvelope(
            candidatePlan.HotPublish.ConfigurationGeneration,
            nextPublicationRevision,
            candidateWorkspace.SessionInstanceId,
            candidateWorkspace.ClockInstanceId,
            nativeImage,
            nextPayloads,
            nextReceipts,
            lastEnvelopeSha256.ToArray(),
            HostManagerOperationCanonicalEnvelopeCodec.ZeroDigest());
        return store!.Commit(
            candidate,
            lastEnvelopeSha256,
            candidatePlan.Recreate.Capacity,
            candidatePlan.HotPublish.ConfigurationGeneration);
    }

    private void AcceptCommittedRuntimeLocked(
        NativeOperationCoordinatorWorkspace replacement,
        CompiledHostManagerOperationCoordinatorPlan desired,
        PreparedCommittedState committed)
    {
        workspace = replacement;
        appliedPlan = desired;
        AcceptCommittedStateLocked(committed);
    }

    private void AcceptCommittedStateLocked(
        PreparedCommittedState committed)
    {
        payloads = committed.Payloads;
        receipts = committed.Receipts;
        publicationRevision = committed.PublicationRevision;
        lastEnvelopeSha256 = committed.FullEnvelopeSha256;
        Volatile.Write(ref committedState, committed.State);
    }

    private PreparedCommittedState PrepareCommittedState(
        HostManagerOperationCanonicalEnvelope committed)
    {
        var committedPayloads = committed.Payloads.Clone();
        return new PreparedCommittedState(
            committedPayloads,
            committed.Receipts.Clone(),
            committed.PublicationRevision,
            committed.FullEnvelopeSha256.ToArray(),
            BuildCommittedState(
                committed.NativeImage.Records,
                committedPayloads,
                committed.ConfigurationGeneration,
                committed.PublicationRevision,
                NextCommittedStateRevisionLocked(),
                DateTimeOffset.UtcNow));
    }

    private static NativeOperationCoordinatorWorkspace
        CreateReplacementWorkspaceLocked(
            NativeOperationCoordinatorWorkspace current,
            CompiledHostManagerOperationCoordinatorPlan desired)
    {
        var exported = current.ExportCanonicalNativeImageLocked();
        var replacement = new NativeOperationCoordinatorWorkspace(
            desired,
            current.SessionInstanceId,
            current.ClockInstanceId);
        try
        {
            replacement.ImportCanonicalNativeImageLocked(
                exported,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                NativeOperationCoordinatorWorkspace.MonotonicMilliseconds());
            return replacement;
        }
        catch
        {
            replacement.Dispose();
            throw;
        }
    }

    internal static bool RequiresProcessTopologyRestart(
        CompiledHostManagerOperationCoordinatorPlan current,
        CompiledHostManagerOperationCoordinatorPlan desired)
        => current.Recreate.Capacity.MaximumActionCount
                != desired.Recreate.Capacity.MaximumActionCount
            || ResolveWorkerCount(current)
                != ResolveWorkerCount(desired);

    private static ulong ResolveWorkerCount(
        CompiledHostManagerOperationCoordinatorPlan plan)
        => checked(
            (ulong)plan.HotPublish.MaximumGlobalRunningCount
            + plan.HotPublish.MaximumCancelActionsPerPlan
            + plan.HotPublish.MaximumRecoverActionsPerPlan);

    private sealed record PreparedCommittedState(
        HostManagerOperationPayloadCatalog Payloads,
        HostManagerOperationEffectReceiptTable Receipts,
        ulong PublicationRevision,
        byte[] FullEnvelopeSha256,
        CommittedState State);

    private void LatchPersistenceFaultLocked(Exception error, string stage)
    {
        persistenceFaulted = true;
        ready = false;
        faultStage = stage;
        faultMessage = error.Message;
        PublishHealthOnlyLocked();
        actionChannel?.Writer.TryComplete(error);
        workerCancellation?.Cancel();
    }

    private void RequireRuntimeLocked()
    {
        if (workspace is null || store is null || appliedPlan is null)
        {
            throw new InvalidOperationException(
                "The operation coordinator runtime is incomplete.");
        }
    }

    private void ValidateRouter(
        CompiledHostManagerOperationCoordinatorPlan desired)
    {
        var configured = desired.HotPublish.Kinds
            .Select(static kind => kind.Name)
            .ToHashSet(StringComparer.Ordinal);
        if (!configured.SetEquals(HostManagerOperationKinds.All)
            || !configured.SetEquals(effectRouter.Kinds))
        {
            throw new InvalidOperationException(
                "The configured operation kind set and registered effect executors differ.");
        }
    }

    private string ResolveEnvelopePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)
            || Path.IsPathRooted(relativePath)
            || relativePath.Contains('\\'))
        {
            throw new InvalidOperationException(
                "The compiled operation envelope path is not canonical.");
        }
        var segments = relativePath.Split('/', StringSplitOptions.None);
        if (segments.Any(static segment =>
                string.IsNullOrEmpty(segment) || segment is "." or ".."))
        {
            throw new InvalidOperationException(
                "The compiled operation envelope path contains an invalid segment.");
        }
        var result =
            Path.GetFullPath(Path.Combine(hostManagerDataRoot, Path.Combine(segments)));
        var prefix = Path.EndsInDirectorySeparator(hostManagerDataRoot)
            ? hostManagerDataRoot
            : hostManagerDataRoot + Path.DirectorySeparatorChar;
        if (!result.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The compiled operation envelope path escapes the Host Manager data root.");
        }
        return result;
    }
}
