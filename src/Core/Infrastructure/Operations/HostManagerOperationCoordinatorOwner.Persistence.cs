using System.Collections.Frozen;
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
        await mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (retiring || disposed)
            {
                if (throwOnFailure)
                {
                    ThrowIfRetiringLocked();
                }
                return;
            }
            if (pendingWorkspaceClose is not null
                || workspace is not null && workspaceHolder is null)
            {
                throw new InvalidOperationException(
                    "An operation workspace still requires explicit closure before replacement.");
            }
            if (!desired.IsPublished)
            {
                throw new InvalidOperationException(
                    "The Host Manager operation-coordinator plan is not published.");
            }
            ValidateRouter(desired);

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
            if (store is null)
            {
                ApplyInitialLocked(hostPlan, desired);
                Volatile.Write(ref lastPlanApplicationError, null);
                return;
            }
            if (workspace is null)
            {
                ApplyIdlePlanLocked(hostPlan, desired);
                Volatile.Write(ref pendingQuiescentRecreate, 0);
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
        var token = deployment.BeginCreate(hostPlan);
        HostManagerOperationCanonicalStore? replacementStore = null;
        var settlementAttempted = false;
        try
        {
            replacementStore = new HostManagerOperationCanonicalStore(
                ResolveEnvelopePath(desired.Recreate.CanonicalEnvelopeRelativePath),
                desired.Recreate.Capacity, desired.HotPublish.ConfigurationGeneration, logger);
            // Cross-process canonical writer lock: keep it independently of native state.
            replacementStore.AcquireOwnerLease();
            var persisted = replacementStore.LoadValidated();
            store = replacementStore;
            replacementStore = null;
            appliedPlan = desired;
            payloads = persisted?.Payloads.Clone() ?? new();
            receipts = persisted?.Receipts.Clone() ?? new();
            publicationRevision = persisted?.PublicationRevision ?? 0;
            lastEnvelopeSha256 = persisted?.FullEnvelopeSha256.ToArray()
                ?? HostManagerOperationCanonicalEnvelopeCodec.ZeroDigest();
            ready = true;
            Volatile.Write(ref committedState, BuildCommittedState(
                persisted?.NativeImage.Records ?? [], payloads,
                desired.HotPublish.ConfigurationGeneration, publicationRevision,
                NextCommittedStateRevisionLocked(), timeProvider.GetUtcNow()));
            PublishPendingOperationsLocked();
            publicationSignal.Publish();
            settlementAttempted = true;
            deployment.CompleteSucceeded(token);
        }
        catch
        {
            ready = false;
            replacementStore?.Dispose();
            store?.Dispose();
            store = null;
            appliedPlan = null;
            if (!settlementAttempted)
                deployment.CompleteFailed(token, "operation-coordinator-initial-create-failed");
            throw;
        }
    }

    private void ApplyIdlePlanLocked(
        CompiledHostManagerPlan hostPlan,
        CompiledHostManagerOperationCoordinatorPlan desired)
    {
        var previous = appliedPlan!;
        if (!string.Equals(ResolveEnvelopePath(previous.Recreate.CanonicalEnvelopeRelativePath),
                ResolveEnvelopePath(desired.Recreate.CanonicalEnvelopeRelativePath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The operation canonical envelope path requires an explicit offline migration.");
        var token = previous.Build == desired.Build && previous.Recreate == desired.Recreate
            ? deployment.BeginHotPublish(hostPlan)
            : deployment.BeginHostRecreateAndHotPublish(hostPlan);
        deployment.CompleteSucceeded(token);
        appliedPlan = desired;
        var current = Volatile.Read(ref committedState);
        var operations = current.Recent.Select(operation => operation with
            { ConfigurationGeneration = desired.HotPublish.ConfigurationGeneration }).ToArray();
        Volatile.Write(ref committedState, current with
        {
            ConfigurationGeneration = desired.HotPublish.ConfigurationGeneration,
            Recent = operations,
            ById = operations.ToFrozenDictionary(static operation => operation.Id, StringComparer.Ordinal),
            Health = CreateHealth(publicationRevision, desired.HotPublish.ConfigurationGeneration),
            PublicationRevision = NextCommittedStateRevisionLocked()
        });
        publicationSignal.Publish();
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
            RetireWorkspaceLocked(currentWorkspace);
            SignalPlanner();
        }
        catch (Exception error)
        {
            Exception failure = error;
            try
            {
                if (replacement is not null) RetireWorkspaceLocked(replacement);
            }
            catch (Exception cleanup) { failure = new AggregateException(error, cleanup); }
            if (canonicalCommitted)
            {
                LatchPersistenceFaultLocked(failure, "hot-publication");
            }
            // Success settlement precedes old-workspace closure. Do not settle it twice.
            if (!canonicalCommitted)
            {
                try
                {
                    deployment.CompleteFailed(
                        token,
                        "operation-coordinator-hot-publish-failed");
                }
                catch (Exception settlementError)
                {
                    throw new AggregateException(failure, settlementError);
                }
            }
            if (!ReferenceEquals(failure, error)) throw failure;
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
            RetireWorkspaceLocked(currentWorkspace);
            SignalPlanner();
        }
        catch (Exception error)
        {
            Exception failure = error;
            try
            {
                if (replacement is not null) RetireWorkspaceLocked(replacement);
            }
            catch (Exception cleanup) { failure = new AggregateException(error, cleanup); }
            if (canonicalCommitted)
            {
                LatchPersistenceFaultLocked(failure, "host-recreate-publication");
            }
            // Success settlement precedes old-workspace closure. Do not settle it twice.
            if (!canonicalCommitted)
            {
                try
                {
                    deployment.CompleteFailed(
                        token,
                        "operation-coordinator-host-recreate-failed");
                }
                catch (Exception settlementError)
                {
                    throw new AggregateException(failure, settlementError);
                }
            }
            if (!ReferenceEquals(failure, error)) throw failure;
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
            if (stage == "initial-recovery-publication" && store!.TryDeferUncommittedLegacyMigration(error))
                throw new HostManagerOperationMigrationDeferredException(error);
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
        workspaceHolder!.Workspace = replacement;
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
        PublishPendingOperationsLocked();
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
                timeProvider.GetUtcNow()));
    }

    private NativeOperationCoordinatorWorkspace
        CreateReplacementWorkspaceLocked(
            NativeOperationCoordinatorWorkspace current,
            CompiledHostManagerOperationCoordinatorPlan desired)
    {
        var exported = current.ExportCanonicalNativeImageLocked();
        var replacement = createWorkspace(
            desired,
            current.SessionInstanceId,
            current.ClockInstanceId,
            createScratch);
        try
        {
            replacement.ImportCanonicalNativeImageLocked(
                exported,
                timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                NativeOperationCoordinatorWorkspace.MonotonicMilliseconds());
            return replacement;
        }
        catch (Exception error)
        {
            try { RetireWorkspaceLocked(replacement); }
            catch (Exception cleanup) { throw new AggregateException(error, cleanup); }
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

    private void RetireWorkspaceLocked(NativeOperationCoordinatorWorkspace candidate)
    {
        if (pendingWorkspaceClose is not null)
            throw new InvalidOperationException("An operation workspace is already awaiting closure.");
        pendingWorkspaceClose = candidate;
        candidate.Dispose();
        pendingWorkspaceClose = null;
    }

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

        // 每个种类还要有自己的效果类型编码（收据里要存它）。少了会在排产时才炸，
        // 把协调器整个打成故障；在这里一起验，缺了就启动即失败。
        foreach (var kind in configured)
        {
            _ = EffectKindCode(kind);
        }
    }

    private string ResolveEnvelopePath(string relativePath)
    {
        try
        {
            return ResourceManager.App.Infrastructure.Optimization.HostManagerDurableStorePaths.ResolveRelativePath(
                hostManagerDataRoot, relativePath);
        }
        catch (InvalidDataException exception)
        {
            throw new InvalidOperationException(exception.Message, exception);
        }
    }
}
