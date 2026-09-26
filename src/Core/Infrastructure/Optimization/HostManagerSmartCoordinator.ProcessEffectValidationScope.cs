using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Infrastructure.GpuPlacement;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization.Transactions;

namespace ResourceManager.App.Infrastructure.Optimization;

public sealed partial class HostManagerSmartCoordinator
{
    public async Task<HostManagerProcessEffectValidationScopeStatus>
        OpenProcessEffectValidationScopeAsync(
        HostManagerProcessEffectValidationScopeOpenRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var _ = EnterForegroundControlRequest();
        await gate.WaitAsync(cancellationToken);
        try
        {
            RequireOperationAdmission();
            await RequireProcessEffectValidationScopeCleanStateAsync(
                cancellationToken);
            var status = processEffectValidationScopeAuthorityOwner.Open(request);
            memoryCleanupValidationEvidence.Reset(
                status.ScopeId
                    ?? throw new InvalidDataException(
                        "The opened validation effect scope has no scope identity."),
                status.Generation);
            return status;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<HostManagerProcessEffectValidationScopeStatus>
        GetProcessEffectValidationScopeAsync(
        CancellationToken cancellationToken)
    {
        RequireOperationAdmission();
        await gate.WaitAsync(cancellationToken);
        try
        {
            RequireOperationAdmission();
            return processEffectValidationScopeAuthorityOwner.GetStatus();
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<HostManagerProcessEffectValidationScopeStatus>
        CloseProcessEffectValidationScopeAsync(
        HostManagerProcessEffectValidationScopeCloseRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var _ = EnterForegroundControlRequest();
        await gate.WaitAsync(cancellationToken);
        try
        {
            RequireOperationAdmission();
            await RequireProcessEffectValidationScopeCleanStateAsync(
                cancellationToken);
            return processEffectValidationScopeAuthorityOwner.Close(request);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<HostManagerMemoryCleanupValidationEvidenceSnapshot>
        GetMemoryCleanupValidationEvidenceAsync(
        CancellationToken cancellationToken)
    {
        RequireOperationAdmission();
        await gate.WaitAsync(cancellationToken);
        try
        {
            RequireOperationAdmission();
            return memoryCleanupValidationEvidence.SealAndCapture();
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task RequireProcessEffectValidationScopeCleanStateAsync(
        CancellationToken cancellationToken)
    {
        if (!string.Equals(
                runtimePlanProvider.Current.OptimizationMode.Mode,
                AppOptimizationModes.Normal,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "A validation effect scope can only change while optimization mode is Normal.");
        }
        HostManagerTransactionJournalAdmission? admission = null;
        var journalState = nativeActionTransactions.JournalState;
        var admissionAcquired = journalState switch
        {
            HostManagerTransactionJournalRuntimeState.ReadyForExecution =>
                nativeActionTransactions.TryAcquireExecutionAdmission(out admission),
            HostManagerTransactionJournalRuntimeState.RecoveryRequired =>
                nativeActionTransactions.TryAcquireRecoveryAdmission(out admission),
            _ => false
        };
        if (!admissionAcquired || admission is null)
        {
            throw new InvalidOperationException(
                "The validation effect scope requires a readable transaction journal.");
        }

        NativeTransactionJournalSnapshot journal;
        await using (admission)
        {
            journal = await admission.ReadSnapshotAsync(cancellationToken);
            if (journal.Records.Count != 0)
            {
                throw new InvalidOperationException(
                    "The validation effect scope requires an empty transaction journal.");
            }
        }

        var ownership = await nativeActionTransactions.AppliedOwnership.ReadSnapshotAsync(
            cancellationToken);
        if (ownership.Records.Count != 0)
        {
            throw new InvalidOperationException(
                "The validation effect scope requires no applied ownership.");
        }
        var placementState = await LoadRollbackStateAsync(cancellationToken);
        if (GpuActionFacts.HasPlacementEffects(placementState.AppliedPlacements)
            || GpuActionFacts.HasUnsettledActions(placementState.AppliedPlacements))
        {
            throw new InvalidOperationException(
                "The validation effect scope requires no applied hardware placement ownership or unconfirmed window actions.");
        }
        _ = memoryCleanupAttemptJournal.ReconcileAndCaptureBlocked(
            processPolicyWriter.ReadProcessInstanceForRecovery);
        var cleanupBatches = memoryCleanupAttemptJournal.CapturePendingBatches();
        if (cleanupBatches.Count != 0)
        {
            throw new InvalidOperationException(
                "The validation effect scope requires an empty memory-cleanup attempt journal.");
        }
        if (!processEffectValidationScopeAuthorityOwner.ReconcileDeclaredNativeHandoff(
                CreateAuthoritativeProcessEffectHandoffs(journal, ownership))
            || !processEffectValidationScopeAuthorityOwner
                .ReconcileDeclaredMemoryCleanupHandoff(cleanupBatches))
        {
            throw new InvalidOperationException(
                "The validation effect scope could not settle its durable handoff ledger.");
        }
    }

    private bool TryBeginProcessEffectAdmission(
        in HostManagerProcessEffectValidationCycleSnapshot scope,
        HostManagerProcessEffectValidationFamily family,
        string stage,
        int processId,
        ulong processStartKey,
        out HostManagerProcessEffectValidationAdmissionPermit? permit)
        => processEffectValidationScopeAuthorityOwner.TryBeginAdmission(
            scope,
            family,
            stage,
            new HostManagerComputeProcessIdentity(processId, processStartKey),
            out permit);

    private bool TryBeginProcessEffectBatchAdmission(
        in HostManagerProcessEffectValidationCycleSnapshot scope,
        HostManagerProcessEffectValidationFamily family,
        string stage,
        IReadOnlyList<HostManagerComputeProcessIdentity> identities,
        out HostManagerProcessEffectValidationAdmissionPermit? permit)
        => processEffectValidationScopeAuthorityOwner.TryBeginBatchAdmission(
            scope,
            family,
            stage,
            identities,
            out permit);

    private bool TryDeclareProcessEffectAdmission(
        HostManagerProcessEffectValidationAdmissionPermit permit,
        in NativeTransactionJournalIdentity journalIdentity)
        => processEffectValidationScopeAuthorityOwner
            .TryDeclareNativeTransactionJournal(permit, journalIdentity);

    private bool TryCommitProcessEffectAdmission(
        HostManagerProcessEffectValidationAdmissionPermit permit,
        in NativeTransactionJournalIdentity journalIdentity)
        => processEffectValidationScopeAuthorityOwner
            .TryCommitNativeTransactionJournal(permit, journalIdentity);

    private bool TryDeclareProcessEffectAdmission(
        HostManagerProcessEffectValidationAdmissionPermit permit,
        HostManagerMemoryCleanupAttemptBatch batch,
        ulong attemptGeneration)
        => processEffectValidationScopeAuthorityOwner
            .TryDeclareMemoryCleanupAttemptBatch(
                permit,
                batch,
                attemptGeneration);

    private bool TryCommitProcessEffectAdmission(
        HostManagerProcessEffectValidationAdmissionPermit permit,
        HostManagerMemoryCleanupAttemptBatch batch,
        ulong attemptGeneration)
        => processEffectValidationScopeAuthorityOwner
            .TryCommitMemoryCleanupAttemptBatch(
                permit,
                batch,
                attemptGeneration);

    private bool TryAuthorizeProcessEffectRecovery(
        HostManagerProcessEffectValidationFamily family,
        in NativeTransactionJournalIdentity journalIdentity)
        => processEffectValidationScopeAuthorityOwner.TryAuthorizeNativeRecovery(
            family,
            journalIdentity);

    private bool TryBeginProcessEffectRecoveryAdmission(
        HostManagerProcessEffectValidationFamily family,
        string stage,
        int processId,
        ulong processStartKey,
        in NativeTransactionJournalIdentity sourceIdentity,
        out HostManagerProcessEffectValidationAdmissionPermit? permit)
        => processEffectValidationScopeAuthorityOwner.TryBeginRecoveryAdmission(
            family,
            stage,
            new HostManagerComputeProcessIdentity(processId, processStartKey),
            sourceIdentity,
            out permit);

    private void MarkProcessEffectAdmissionUnsettled(
        HostManagerProcessEffectValidationAdmissionPermit? permit,
        string reason)
        => processEffectValidationScopeAuthorityOwner.MarkAdmissionUnsettled(
            permit,
            reason);

    private static NativeTransactionJournalIdentity
        CreateProcessEffectSourceIdentity(in NativeAppliedOwnershipRecord ownership)
    {
        var binding = HostManagerAppliedOwnershipProjection.CreatePayloadBinding(in ownership);
        var action = binding.ActionIdentity;
        return new NativeTransactionJournalIdentity
        {
            ConfigurationGeneration = action.ConfigurationGeneration,
            PlanEpoch = action.PlanEpoch,
            ActionId = action.ActionId,
            HostSessionIncarnation = action.HostSessionIncarnation,
            TargetId = action.TargetId,
            SoftwareId = action.SoftwareId,
            ProcessStartKey = action.ProcessStartKey,
            ProcessId = action.ProcessId
        };
    }

    private static IReadOnlyList<NativeTransactionJournalIdentity>
        CreateAuthoritativeProcessEffectHandoffs(
        NativeTransactionJournalSnapshot journal,
        NativeAppliedOwnershipSnapshot ownership)
    {
        var identities = new HashSet<NativeTransactionJournalIdentity>();
        foreach (var record in journal.Records)
        {
            if (record.Scope == (uint)NativeTransactionJournalScope.Process)
            {
                identities.Add(record.Identity);
            }
        }
        foreach (var record in ownership.Records)
        {
            if (record.Primary.Scope == (uint)NativeAppliedOwnershipScope.Process)
            {
                identities.Add(CreateProcessEffectSourceIdentity(in record));
            }
        }
        return identities.ToArray();
    }
}
