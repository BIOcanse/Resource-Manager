using System.Security.Cryptography;
using System.Text;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.Optimization;

internal static class ProcessEffectValidationScopeDocuments
{
    internal const int MaximumAllowedProcessCount = 64;
    internal const int MaximumAuditRecordCount = 4096;
    internal const int MaximumDocumentBytes = 4 * 1024 * 1024;
    internal const int MaximumAuditFenceBytes = 4 * 1024 * 1024;
    internal const int MaximumAuthorityManifestBytes = 64 * 1024;
    internal const int MaximumJobNameCharacters = 256;
    internal const int MaximumStageCharacters = 96;
    internal const int StorageSchemaVersion = 2;
    internal const string StorageContract =
        "host-manager-process-effect-validation-scope-storage-v2";
    internal const int AuditFenceSchemaVersion = 6;
    internal const string AuditFenceContract =
        "host-manager-process-effect-validation-admission-fence-v6";
    internal const int AuthorityManifestSchemaVersion = 1;
    internal const string AuthorityManifestContract =
        "host-manager-process-effect-validation-authority-manifest-v1";
    internal const string AuditFencePathSuffix = ".audit-fence.json";
    internal const string RequiredJobNamePrefix = "Global\\ResourceManager-NonAdaptedOptimizationLab-";
    internal static void ValidateCleanAuditFenceBinding(
        HostManagerProcessEffectValidationScopeDocument document,
        HostManagerProcessEffectValidationAuditFenceDocument fence)
        => ValidateSettledAuditFenceBinding(
            document,
            fence,
            HostManagerProcessEffectValidationAuditFenceState.Clean,
            requireEmptyHandoffs: false);

    internal static void ValidateClosingAuditFenceBinding(
        HostManagerProcessEffectValidationScopeDocument document,
        HostManagerProcessEffectValidationAuditFenceDocument fence)
        => ValidateSettledAuditFenceBinding(
            document,
            fence,
            HostManagerProcessEffectValidationAuditFenceState.Closing,
            requireEmptyHandoffs: true);

    internal static void ValidateClosedAuditFenceBinding(
        HostManagerProcessEffectValidationScopeDocument document,
        HostManagerProcessEffectValidationAuditFenceDocument fence)
        => ValidateSettledAuditFenceBinding(
            document,
            fence,
            HostManagerProcessEffectValidationAuditFenceState.Closed,
            requireEmptyHandoffs: true);

    internal static void ValidateRetiringClosedAuditFenceBinding(
        HostManagerProcessEffectValidationScopeDocument document,
        HostManagerProcessEffectValidationAuditFenceDocument fence)
        => ValidateSettledAuditFenceBinding(
            document,
            fence,
            HostManagerProcessEffectValidationAuditFenceState.RetiringClosed,
            requireEmptyHandoffs: true);

    internal static void ValidateOpeningAuditFenceBinding(
        HostManagerProcessEffectValidationScopeDocument document,
        HostManagerProcessEffectValidationAuditFenceDocument fence)
        => ValidateSettledAuditFenceBinding(
            document,
            fence,
            HostManagerProcessEffectValidationAuditFenceState.Opening,
            requireEmptyHandoffs: true);

    internal static void ValidateSettledAuditFenceBinding(
        HostManagerProcessEffectValidationScopeDocument document,
        HostManagerProcessEffectValidationAuditFenceDocument fence,
        HostManagerProcessEffectValidationAuditFenceState expectedState,
        bool requireEmptyHandoffs)
    {
        if (fence.State != expectedState
            || fence.OwnerRuntimeIncarnation != document.OwnerRuntimeIncarnation
            || fence.AdmissionId != Guid.Empty
            || fence.ParentAdmissionId != Guid.Empty
            || fence.FirstAuditSequence != 0
            || fence.AdmissionAudits.Count != 0
            || fence.DurableHandoff is not null
            || fence.ScopeId != document.ScopeId
            || fence.RunNonce != document.RunNonce
            || fence.Generation != document.Generation
            || fence.CommittedAuditRecordCount != document.AuditRecords.Count
            || requireEmptyHandoffs && fence.CommittedHandoffs.Count != 0
            || !string.Equals(
                fence.CommittedScopeDocumentSha256,
                document.ChecksumSha256,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The validation effect-scope audit fence does not match the committed scope document.");
        }
    }

    internal static void ValidateOrphanLifecycleAuditFence(
        HostManagerProcessEffectValidationAuditFenceDocument fence)
    {
        if (fence.State is not HostManagerProcessEffectValidationAuditFenceState.Opening
                and not HostManagerProcessEffectValidationAuditFenceState.RetiringClosed
            || fence.AdmissionId != Guid.Empty
            || fence.ParentAdmissionId != Guid.Empty
            || fence.FirstAuditSequence != 0
            || fence.AdmissionAudits.Count != 0
            || fence.DurableHandoff is not null
            || fence.CommittedHandoffs.Count != 0)
        {
            throw new InvalidDataException(
                "Only a settled lifecycle fence may survive its scope document.");
        }
    }

    internal static void ValidateAdmissionAuditFenceBinding(
        HostManagerProcessEffectValidationScopeDocument document,
        HostManagerProcessEffectValidationAuditFenceDocument fence,
        HostManagerProcessEffectValidationAuditFenceState expectedState,
        Guid admissionId,
        long firstAuditSequence,
        int auditCount,
        Guid parentAdmissionId,
        IReadOnlyList<HostManagerComputeProcessIdentity> expectedProcesses)
    {
        if (fence.State != expectedState
            || fence.ScopeId != document.ScopeId
            || fence.RunNonce != document.RunNonce
            || fence.OwnerRuntimeIncarnation != document.OwnerRuntimeIncarnation
            || fence.Generation != document.Generation
            || fence.CommittedAuditRecordCount != document.AuditRecords.Count
            || !string.Equals(
                fence.CommittedScopeDocumentSha256,
                document.ChecksumSha256,
                StringComparison.Ordinal)
            || fence.AdmissionId != admissionId
            || fence.ParentAdmissionId != parentAdmissionId
            || fence.FirstAuditSequence != firstAuditSequence
            || fence.AdmissionAudits.Count != auditCount
            || expectedProcesses.Count != auditCount)
        {
            throw new InvalidDataException(
                "The validation admission fence does not match its permit or scope document.");
        }

        var firstIndex = checked(document.AuditRecords.Count - auditCount);
        var expectedDecision = parentAdmissionId == Guid.Empty
            ? HostManagerProcessEffectValidationDecision.AllowedScoped
            : HostManagerProcessEffectValidationDecision.AllowedPriorHandoffRecovery;
        var actualProcesses = new HashSet<HostManagerComputeProcessIdentity>();
        for (var index = 0; index < auditCount; index++)
        {
            var audit = fence.AdmissionAudits[index];
            if (audit != document.AuditRecords[firstIndex + index]
                || audit.AdmissionId != admissionId
                || audit.Decision != expectedDecision
                || !actualProcesses.Add(new(
                    audit.ProcessId,
                    audit.ProcessStartKey)))
            {
                throw new InvalidDataException(
                    "The validation admission fence audit tail is not exact.");
            }
        }
        if (!actualProcesses.SetEquals(expectedProcesses))
        {
            throw new InvalidDataException(
                "The validation admission handoff process set does not match its audit.");
        }
    }

    internal static void ValidateAuditFenceScopeBinding(
        HostManagerProcessEffectValidationScopeDocument document,
        HostManagerProcessEffectValidationAuditFenceDocument fence)
    {
        if (fence.ScopeId != document.ScopeId
            || fence.RunNonce != document.RunNonce
            || fence.OwnerRuntimeIncarnation != document.OwnerRuntimeIncarnation
            || fence.Generation != document.Generation)
        {
            throw new InvalidDataException(
                "The validation audit fence belongs to another scope.");
        }
        ValidateCommittedHandoffLedger(document, fence.CommittedHandoffs);
    }

    internal static void ValidatePreHandoffAuditFenceBinding(
        HostManagerProcessEffectValidationScopeDocument document,
        HostManagerProcessEffectValidationAuditFenceDocument fence)
    {
        if (fence.State == HostManagerProcessEffectValidationAuditFenceState.AuditCommitted)
        {
            ValidatePendingAuditTail(document, fence, requireCommittedDocument: true);
            return;
        }
        if (fence.State != HostManagerProcessEffectValidationAuditFenceState.Intent)
        {
            throw new InvalidDataException(
                "The validation audit fence is not a pre-handoff state.");
        }

        var beforeWrite = fence.CommittedAuditRecordCount == document.AuditRecords.Count
            && string.Equals(
                fence.CommittedScopeDocumentSha256,
                document.ChecksumSha256,
                StringComparison.Ordinal);
        if (beforeWrite)
        {
            return;
        }
        if (document.AuditRecords.Count !=
                fence.CommittedAuditRecordCount + fence.AdmissionAudits.Count)
        {
            throw new InvalidDataException(
                "The intent fence cannot be reconciled with the scope audit ledger.");
        }
        ValidatePendingAuditTail(document, fence, requireCommittedDocument: false);
    }

    internal static void ValidateLoadedEffectAdmissionFenceBinding(
        HostManagerProcessEffectValidationScopeDocument document,
        HostManagerProcessEffectValidationAuditFenceDocument fence)
    {
        var processes = fence.AdmissionAudits.Select(static audit =>
            new HostManagerComputeProcessIdentity(
                audit.ProcessId,
                audit.ProcessStartKey)).ToArray();
        ValidateAdmissionAuditFenceBinding(
            document,
            fence,
            fence.State,
            fence.AdmissionId,
            fence.FirstAuditSequence,
            fence.AdmissionAudits.Count,
            fence.ParentAdmissionId,
            processes);
    }

    internal static void ValidatePendingAuditTail(
        HostManagerProcessEffectValidationScopeDocument document,
        HostManagerProcessEffectValidationAuditFenceDocument fence,
        bool requireCommittedDocument)
    {
        if (requireCommittedDocument
            && (fence.CommittedAuditRecordCount != document.AuditRecords.Count
                || !string.Equals(
                    fence.CommittedScopeDocumentSha256,
                    document.ChecksumSha256,
                    StringComparison.Ordinal)))
        {
            throw new InvalidDataException(
                "The committed admission fence does not bind the scope document.");
        }
        var firstIndex = checked(document.AuditRecords.Count - fence.AdmissionAudits.Count);
        if (firstIndex < 0)
        {
            throw new InvalidDataException(
                "The pending admission audit range exceeds the scope ledger.");
        }
        for (var index = 0; index < fence.AdmissionAudits.Count; index++)
        {
            if (fence.AdmissionAudits[index] != document.AuditRecords[firstIndex + index])
            {
                throw new InvalidDataException(
                    "The pending admission audit range is not the committed ledger tail.");
            }
        }
    }

    internal static void ValidateAuditFenceDocument(
        HostManagerProcessEffectValidationAuditFenceDocument document)
    {
        if (document.SchemaVersion != AuditFenceSchemaVersion
            || !string.Equals(document.Contract, AuditFenceContract, StringComparison.Ordinal)
            || document.ScopeId == Guid.Empty
            || document.RunNonce == Guid.Empty
            || document.OwnerRuntimeIncarnation == Guid.Empty
            || document.Generation <= 0
            || document.State is < HostManagerProcessEffectValidationAuditFenceState.Clean
                or > HostManagerProcessEffectValidationAuditFenceState.RetiringClosed
            || document.CommittedAuditRecordCount is < 0 or > MaximumAuditRecordCount
            || !IsSha256(document.CommittedScopeDocumentSha256)
            || document.AdmissionAudits is null
            || document.CommittedHandoffs is null
            || document.CommittedHandoffs.Count > MaximumAuditRecordCount
            || !IsSha256(document.ChecksumSha256)
            || !string.Equals(
                document.ChecksumSha256,
                ComputeAuditFenceChecksum(document),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The validation effect-scope audit fence failed its hash-closed contract.");
        }

        if (document.State is HostManagerProcessEffectValidationAuditFenceState.Clean
            or HostManagerProcessEffectValidationAuditFenceState.Opening
            or HostManagerProcessEffectValidationAuditFenceState.Closing
            or HostManagerProcessEffectValidationAuditFenceState.Closed
            or HostManagerProcessEffectValidationAuditFenceState.RetiringClosed)
        {
            if (document.AdmissionId != Guid.Empty
                || document.ParentAdmissionId != Guid.Empty
                || document.FirstAuditSequence != 0
                || document.AdmissionAudits.Count != 0
                || document.DurableHandoff is not null
                || (document.State is HostManagerProcessEffectValidationAuditFenceState.Opening
                        or HostManagerProcessEffectValidationAuditFenceState.Closing
                        or HostManagerProcessEffectValidationAuditFenceState.Closed
                        or HostManagerProcessEffectValidationAuditFenceState.RetiringClosed)
                    && document.CommittedHandoffs.Count != 0)
            {
                throw new InvalidDataException(
                    "A settled validation effect-scope audit fence contains pending work.");
            }
            return;
        }

        if (document.AdmissionId == Guid.Empty
            || document.AdmissionAudits.Count is < 1 or > MaximumAllowedProcessCount
            || document.FirstAuditSequence <= 0
            || document.State == HostManagerProcessEffectValidationAuditFenceState.Intent
                && document.CommittedAuditRecordCount >
                    MaximumAuditRecordCount - document.AdmissionAudits.Count
            || document.State != HostManagerProcessEffectValidationAuditFenceState.Intent
                && document.CommittedAuditRecordCount < document.AdmissionAudits.Count)
        {
            throw new InvalidDataException(
                "A non-clean validation admission fence is incomplete.");
        }
        var expectedFirst = document.State ==
            HostManagerProcessEffectValidationAuditFenceState.Intent
            ? checked(document.CommittedAuditRecordCount + 1L)
            : checked(document.CommittedAuditRecordCount -
                document.AdmissionAudits.Count + 1L);
        if (document.FirstAuditSequence != expectedFirst)
        {
            throw new InvalidDataException(
                "The validation admission fence audit range is not contiguous.");
        }
        for (var index = 0; index < document.AdmissionAudits.Count; index++)
        {
            var audit = document.AdmissionAudits[index];
            ValidateAuditRecord(audit, checked(expectedFirst + index));
            if (audit.AdmissionId != document.AdmissionId)
            {
                throw new InvalidDataException(
                    "The validation admission fence contains a foreign audit record.");
            }
        }

        if (document.ParentAdmissionId != Guid.Empty
            && !document.CommittedHandoffs.Any(item =>
                item.AdmissionId == document.ParentAdmissionId))
        {
            throw new InvalidDataException(
                "A recovery admission does not name a prior committed handoff.");
        }

        if (document.State is HostManagerProcessEffectValidationAuditFenceState
                .HandoffDeclared
            or HostManagerProcessEffectValidationAuditFenceState.JournalPrepared)
        {
            ValidateDurableHandoff(document.DurableHandoff);
            var expectedDecision = document.ParentAdmissionId == Guid.Empty
                ? HostManagerProcessEffectValidationDecision.AllowedScoped
                : HostManagerProcessEffectValidationDecision.AllowedPriorHandoffRecovery;
            if (document.AdmissionAudits.Any(audit => audit.Decision != expectedDecision))
            {
                throw new InvalidDataException(
                    "A declared handoff contains a non-effect admission decision.");
            }
        }
        else if (document.DurableHandoff is not null)
        {
            throw new InvalidDataException(
                "A validation admission fence exposes a handoff before declaration.");
        }


        var committedAdmissionIds = new HashSet<Guid>();
        var committedTargets = new HashSet<
            HostManagerProcessEffectValidationDurableHandoffDocument>();
        foreach (var committed in document.CommittedHandoffs)
        {
            ValidateDurableHandoff(committed.DurableHandoff);
            if (committed.AdmissionId == Guid.Empty
                || committed.FirstAuditSequence <= 0
                || committed.AuditCount is < 1 or > MaximumAllowedProcessCount
                || !committedAdmissionIds.Add(committed.AdmissionId)
                || !committedTargets.Add(committed.DurableHandoff)
                || committed.ParentAdmissionId != Guid.Empty
                    && !committedAdmissionIds.Contains(committed.ParentAdmissionId))
            {
                throw new InvalidDataException(
                    "The committed validation handoff ledger is not monotonic and unique.");
            }
        }
    }

    internal static void ValidateCommittedHandoffLedger(
        HostManagerProcessEffectValidationScopeDocument document,
        IReadOnlyList<HostManagerProcessEffectValidationCommittedHandoffDocument> ledger)
    {
        var priorAdmissions = new HashSet<Guid>();
        foreach (var committed in ledger)
        {
            var firstIndex = checked((int)committed.FirstAuditSequence - 1);
            if (firstIndex < 0
                || committed.AuditCount is < 1 or > MaximumAllowedProcessCount
                || firstIndex > document.AuditRecords.Count - committed.AuditCount)
            {
                throw new InvalidDataException(
                    "A committed validation handoff names an invalid audit range.");
            }
            var expectedDecision = committed.ParentAdmissionId == Guid.Empty
                ? HostManagerProcessEffectValidationDecision.AllowedScoped
                : HostManagerProcessEffectValidationDecision.AllowedPriorHandoffRecovery;
            var processes = new HashSet<HostManagerComputeProcessIdentity>();
            HostManagerProcessEffectValidationFamily? family = null;
            for (var offset = 0; offset < committed.AuditCount; offset++)
            {
                var audit = document.AuditRecords[firstIndex + offset];
                family ??= audit.Family;
                if (audit.AdmissionId != committed.AdmissionId
                    || audit.Sequence != committed.FirstAuditSequence + offset
                    || audit.Decision != expectedDecision
                    || audit.Family != family
                    || !processes.Add(new(audit.ProcessId, audit.ProcessStartKey)))
                {
                    throw new InvalidDataException(
                        "A committed validation handoff is not bound to one exact audit range.");
                }
            }
            if (committed.ParentAdmissionId != Guid.Empty
                && !priorAdmissions.Contains(committed.ParentAdmissionId))
            {
                throw new InvalidDataException(
                    "A committed recovery handoff does not follow its parent admission.");
            }
            if (committed.DurableHandoff.Kind ==
                    HostManagerProcessEffectValidationDurableHandoffKind.NativeTransactionJournal
                && (committed.AuditCount != 1
                    || !processes.Contains(new(
                        checked((int)committed.DurableHandoff.NativeProcessId),
                        committed.DurableHandoff.NativeProcessStartKey))))
            {
                throw new InvalidDataException(
                    "A native validation handoff is not bound to its exact process audit.");
            }
            if (committed.DurableHandoff.Kind ==
                    HostManagerProcessEffectValidationDurableHandoffKind.MemoryCleanupAttemptBatch
                && (committed.AuditCount !=
                        committed.DurableHandoff.MemoryCleanupProcessCount
                    || !string.Equals(
                        HostManagerProcessEffectValidationDurableHandoffDocument
                            .ComputeMemoryCleanupProcessSetSha256(processes),
                        committed.DurableHandoff.MemoryCleanupProcessSetSha256,
                        StringComparison.Ordinal)))
            {
                throw new InvalidDataException(
                    "A cleanup validation handoff is not bound to its exact process audit set.");
            }
            priorAdmissions.Add(committed.AdmissionId);
        }
    }

    internal static void ValidateDurableHandoff(
        HostManagerProcessEffectValidationDurableHandoffDocument? handoff)
    {
        if (handoff is not { } value
            || value.Kind is < HostManagerProcessEffectValidationDurableHandoffKind
                    .NativeTransactionJournal
                or > HostManagerProcessEffectValidationDurableHandoffKind
                    .MemoryCleanupAttemptBatch)
        {
            throw new InvalidDataException(
                "The validation admission durable handoff is invalid.");
        }

        if (value.Kind == HostManagerProcessEffectValidationDurableHandoffKind
                .NativeTransactionJournal)
        {
            if (value.MemoryCleanupBatchId != Guid.Empty
                || value.MemoryCleanupAttemptGeneration != 0
                || value.MemoryCleanupProcessCount != 0
                || !string.IsNullOrEmpty(value.MemoryCleanupProcessSetSha256)
                || value.NativeProcessId == 0
                || value.NativeProcessId > int.MaxValue
                || value.NativeProcessStartKey == 0
                || value.NativeActionId == 0
                || value.NativeHostSessionIncarnation == 0)
            {
                throw new InvalidDataException(
                    "The validation admission native-journal handoff is incomplete.");
            }
            return;
        }

        if (value.MemoryCleanupBatchId == Guid.Empty
            || value.MemoryCleanupAttemptGeneration == 0
            || value.MemoryCleanupProcessCount is < 1 or > MaximumAllowedProcessCount
            || value.MemoryCleanupProcessSetSha256 is null
            || value.MemoryCleanupProcessSetSha256.Length != 64
            || value.MemoryCleanupProcessSetSha256.Any(static character =>
                character is not (>= '0' and <= '9') and not (>= 'A' and <= 'F'))
            || value.NativeConfigurationGeneration != 0
            || value.NativePlanEpoch != 0
            || value.NativeActionId != 0
            || value.NativeHostSessionIncarnation != 0
            || value.NativeTargetId != 0
            || value.NativeSoftwareId != 0
            || value.NativeProcessStartKey != 0
            || value.NativeProcessId != 0)
        {
            throw new InvalidDataException(
                "The validation admission cleanup-batch handoff is incomplete.");
        }
    }

    internal static void ValidateDocument(
        HostManagerProcessEffectValidationScopeDocument document)
    {
        if (document.SchemaVersion != StorageSchemaVersion
            || !string.Equals(document.Contract, StorageContract, StringComparison.Ordinal)
            || document.ScopeId == Guid.Empty
            || document.RunNonce == Guid.Empty
            || document.OwnerRuntimeIncarnation == Guid.Empty
            || !IsValidationJobName(document.JobName)
            || !IsSha256(document.JobNameSha256)
            || !string.Equals(
                document.JobNameSha256,
                ComputeSha256(document.JobName),
                StringComparison.Ordinal)
            || document.ExpiresAtUtcTicks <= 0
            || !IsSha256(document.ReleaseTokenSha256)
            || document.Generation <= 0
            || document.AllowedProcesses is null
            || document.AllowedProcesses.Count is < 1 or > MaximumAllowedProcessCount
            || document.AuditRecords is null
            || document.AuditRecords.Count > MaximumAuditRecordCount
            || !IsSha256(document.ChecksumSha256)
            || !string.Equals(
                document.ChecksumSha256,
                ComputeChecksum(document),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The validation effect-scope document failed its hash-closed contract.");
        }

        var identities = new HashSet<HostManagerComputeProcessIdentity>();
        HostManagerComputeProcessIdentity? previous = null;
        foreach (var item in document.AllowedProcesses)
        {
            var identity = item.ToIdentity();
            if (identity.ProcessId <= 0
                || identity.ProcessStartKey == 0
                || !identities.Add(identity)
                || previous is { } prior
                    && (identity.ProcessId < prior.ProcessId
                        || identity.ProcessId == prior.ProcessId
                            && identity.ProcessStartKey <= prior.ProcessStartKey))
            {
                throw new InvalidDataException(
                    "The validation effect-scope process set is not canonical.");
            }
            previous = identity;
        }
        for (var index = 0; index < document.AuditRecords.Count; index++)
        {
            ValidateAuditRecord(document.AuditRecords[index], index + 1L);
        }
    }

    internal static void ValidateAuditRecord(
        HostManagerProcessEffectValidationScopeAuditDocument record,
        long expectedSequence)
    {
        if (record.Sequence != expectedSequence
            || record.RecordedAtUtcTicks <= 0
            || record.AdmissionId == Guid.Empty
            || record.Family is < HostManagerProcessEffectValidationFamily.NonAdaptedMemoryTransaction
                or > HostManagerProcessEffectValidationFamily.NativeProcessPolicyTransaction
            || string.IsNullOrWhiteSpace(record.Stage)
            || record.Stage.Length > MaximumStageCharacters
            || record.ProcessId <= 0
            || record.ProcessStartKey == 0
            || record.Decision is < HostManagerProcessEffectValidationDecision.AllowedScoped
                or > HostManagerProcessEffectValidationDecision.AllowedPriorHandoffRecovery
            || record.SystemError < 0)
        {
            throw new InvalidDataException(
                "The validation effect-scope audit sequence is invalid.");
        }
    }

    internal static string ComputeChecksum(
        HostManagerProcessEffectValidationScopeDocument document)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(document.SchemaVersion);
            writer.Write(document.Contract);
            writer.Write(document.ScopeId.ToByteArray());
            writer.Write(document.RunNonce.ToByteArray());
            writer.Write(document.OwnerRuntimeIncarnation.ToByteArray());
            writer.Write(document.JobName);
            writer.Write(document.JobNameSha256);
            writer.Write(document.ExpiresAtUtcTicks);
            writer.Write(document.ReleaseTokenSha256);
            writer.Write(document.Generation);
            writer.Write(document.AllowedProcesses.Count);
            foreach (var process in document.AllowedProcesses)
            {
                writer.Write(process.ProcessId);
                writer.Write(process.ProcessStartKey);
            }
            writer.Write(document.AuditRecords.Count);
            foreach (var record in document.AuditRecords)
            {
                writer.Write(record.Sequence);
                writer.Write(record.RecordedAtUtcTicks);
                writer.Write(record.AdmissionId.ToByteArray());
                writer.Write((byte)record.Family);
                writer.Write(record.Stage);
                writer.Write(record.ProcessId);
                writer.Write(record.ProcessStartKey);
                writer.Write((byte)record.Decision);
                writer.Write(record.SystemError);
            }
        }
        return Convert.ToHexString(SHA256.HashData(
            stream.GetBuffer().AsSpan(0, checked((int)stream.Length))));
    }

    internal static string ComputeAuthorityManifestChecksum(
        HostManagerProcessEffectValidationAuthorityManifest manifest)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(manifest.SchemaVersion);
            writer.Write(manifest.Contract);
            writer.Write(manifest.StoreId.ToByteArray());
            writer.Write(manifest.StatePathSha256);
            writer.Write((byte)manifest.State);
        }
        return Convert.ToHexString(SHA256.HashData(
            stream.GetBuffer().AsSpan(0, checked((int)stream.Length))));
    }

    internal static string ComputeAuditFenceChecksum(
        HostManagerProcessEffectValidationAuditFenceDocument document)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(document.SchemaVersion);
            writer.Write(document.Contract);
            writer.Write(document.ScopeId.ToByteArray());
            writer.Write(document.RunNonce.ToByteArray());
            writer.Write(document.OwnerRuntimeIncarnation.ToByteArray());
            writer.Write(document.Generation);
            writer.Write((byte)document.State);
            writer.Write(document.CommittedAuditRecordCount);
            writer.Write(document.CommittedScopeDocumentSha256);
            writer.Write(document.AdmissionId.ToByteArray());
            writer.Write(document.ParentAdmissionId.ToByteArray());
            writer.Write(document.FirstAuditSequence);
            writer.Write(document.AdmissionAudits.Count);
            foreach (var audit in document.AdmissionAudits)
            {
                writer.Write(audit.Sequence);
                writer.Write(audit.RecordedAtUtcTicks);
                writer.Write(audit.AdmissionId.ToByteArray());
                writer.Write((byte)audit.Family);
                writer.Write(audit.Stage);
                writer.Write(audit.ProcessId);
                writer.Write(audit.ProcessStartKey);
                writer.Write((byte)audit.Decision);
                writer.Write(audit.SystemError);
            }
            writer.Write(document.DurableHandoff is not null);
            if (document.DurableHandoff is { } handoff)
            {
                writer.Write((byte)handoff.Kind);
                writer.Write(handoff.MemoryCleanupBatchId.ToByteArray());
                writer.Write(handoff.MemoryCleanupAttemptGeneration);
                writer.Write(handoff.MemoryCleanupProcessCount);
                writer.Write(handoff.MemoryCleanupProcessSetSha256);
                writer.Write(handoff.NativeConfigurationGeneration);
                writer.Write(handoff.NativePlanEpoch);
                writer.Write(handoff.NativeActionId);
                writer.Write(handoff.NativeHostSessionIncarnation);
                writer.Write(handoff.NativeTargetId);
                writer.Write(handoff.NativeSoftwareId);
                writer.Write(handoff.NativeProcessStartKey);
                writer.Write(handoff.NativeProcessId);
            }
            writer.Write(document.CommittedHandoffs.Count);
            foreach (var committed in document.CommittedHandoffs)
            {
                writer.Write(committed.AdmissionId.ToByteArray());
                writer.Write(committed.FirstAuditSequence);
                writer.Write(committed.AuditCount);
                writer.Write(committed.ParentAdmissionId.ToByteArray());
                var committedHandoff = committed.DurableHandoff;
                writer.Write((byte)committedHandoff.Kind);
                writer.Write(committedHandoff.MemoryCleanupBatchId.ToByteArray());
                writer.Write(committedHandoff.MemoryCleanupAttemptGeneration);
                writer.Write(committedHandoff.MemoryCleanupProcessCount);
                writer.Write(committedHandoff.MemoryCleanupProcessSetSha256);
                writer.Write(committedHandoff.NativeConfigurationGeneration);
                writer.Write(committedHandoff.NativePlanEpoch);
                writer.Write(committedHandoff.NativeActionId);
                writer.Write(committedHandoff.NativeHostSessionIncarnation);
                writer.Write(committedHandoff.NativeTargetId);
                writer.Write(committedHandoff.NativeSoftwareId);
                writer.Write(committedHandoff.NativeProcessStartKey);
                writer.Write(committedHandoff.NativeProcessId);
            }
        }
        return Convert.ToHexString(SHA256.HashData(
            stream.GetBuffer().AsSpan(0, checked((int)stream.Length))));
    }

    internal static string ComputeSha256(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    internal static string ComputeStatePathIdentitySha256(string path)
        => ComputeSha256(
            Path.GetFullPath(path)
                .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
                .ToUpperInvariant());

    internal static bool IsSha256(string? value)
        => value is { Length: 64 } && value.All(static character =>
            character is >= '0' and <= '9' or >= 'A' and <= 'F');

    internal static bool IsValidationJobName(string? value)
    {
        if (value is null
            || value.Length > MaximumJobNameCharacters
            || !value.StartsWith(RequiredJobNamePrefix, StringComparison.Ordinal))
        {
            return false;
        }
        return Guid.TryParseExact(
            value.AsSpan(RequiredJobNamePrefix.Length),
            "N",
            out _);
    }

}

internal enum HostManagerProcessEffectValidationFamily : byte
{
    NonAdaptedMemoryTransaction = 1,
    AutomaticMemoryCleanup = 2,
    NativeProcessPolicyTransaction = 3
}

internal enum HostManagerProcessEffectValidationDecision : byte
{
    AllowedScoped = 1,
    DeniedNotAllowlisted = 2,
    DeniedIdentityMismatch = 3,
    DeniedNotJobMember = 4,
    DeniedMembershipUnknown = 5,
    DeniedExpired = 6,
    DeniedScopeFault = 7,
    DeniedSnapshotMismatch = 8,
    AllowedPriorHandoffRecovery = 9
}

internal enum HostManagerProcessEffectValidationAuthorityManifestState : byte
{
    Initializing = 1,
    Ready = 2
}

internal sealed record HostManagerProcessEffectValidationAuthorityManifest(
    int SchemaVersion,
    string Contract,
    Guid StoreId,
    string StatePathSha256,
    HostManagerProcessEffectValidationAuthorityManifestState State,
    string ChecksumSha256);

internal sealed record HostManagerProcessEffectValidationScopeDocument(
    int SchemaVersion,
    string Contract,
    Guid ScopeId,
    Guid RunNonce,
    Guid OwnerRuntimeIncarnation,
    string JobName,
    string JobNameSha256,
    long ExpiresAtUtcTicks,
    string ReleaseTokenSha256,
    long Generation,
    IReadOnlyList<HostManagerProcessEffectValidationScopeIdentityDocument> AllowedProcesses,
    IReadOnlyList<HostManagerProcessEffectValidationScopeAuditDocument> AuditRecords,
    string ChecksumSha256);

internal readonly record struct HostManagerProcessEffectValidationScopeIdentityDocument(
    int ProcessId,
    ulong ProcessStartKey)
{
    internal HostManagerComputeProcessIdentity ToIdentity()
        => new(ProcessId, ProcessStartKey);
}

internal readonly record struct HostManagerProcessEffectValidationScopeAuditDocument(
    long Sequence,
    long RecordedAtUtcTicks,
    Guid AdmissionId,
    HostManagerProcessEffectValidationFamily Family,
    string Stage,
    int ProcessId,
    ulong ProcessStartKey,
    HostManagerProcessEffectValidationDecision Decision,
    int SystemError);

internal enum HostManagerProcessEffectValidationAuditFenceState : byte
{
    Clean = 1,
    Intent = 2,
    AuditCommitted = 3,
    HandoffDeclared = 4,
    JournalPrepared = 5,
    Opening = 6,
    Closing = 7,
    Closed = 8,
    RetiringClosed = 9
}

internal enum HostManagerProcessEffectValidationDurableHandoffKind : byte
{
    NativeTransactionJournal = 1,
    MemoryCleanupAttemptBatch = 2
}

internal readonly record struct HostManagerProcessEffectValidationDurableHandoffDocument(
    HostManagerProcessEffectValidationDurableHandoffKind Kind,
    Guid MemoryCleanupBatchId,
    ulong MemoryCleanupAttemptGeneration,
    int MemoryCleanupProcessCount,
    string MemoryCleanupProcessSetSha256,
    ulong NativeConfigurationGeneration,
    ulong NativePlanEpoch,
    ulong NativeActionId,
    ulong NativeHostSessionIncarnation,
    ulong NativeTargetId,
    ulong NativeSoftwareId,
    ulong NativeProcessStartKey,
    uint NativeProcessId)
{
    internal static HostManagerProcessEffectValidationDurableHandoffDocument
        CreateMemoryCleanupBatch(
            Guid batchId,
            ulong attemptGeneration,
            IReadOnlyCollection<HostManagerComputeProcessIdentity> processes)
        => new(
            HostManagerProcessEffectValidationDurableHandoffKind.MemoryCleanupAttemptBatch,
            batchId,
            attemptGeneration,
            processes.Count,
            ComputeMemoryCleanupProcessSetSha256(processes),
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0);

    internal static string ComputeMemoryCleanupProcessSetSha256(
        IEnumerable<HostManagerComputeProcessIdentity> processes)
    {
        ArgumentNullException.ThrowIfNull(processes);
        var ordered = processes.OrderBy(static item => item.ProcessId)
            .ThenBy(static item => item.ProcessStartKey)
            .ToArray();
        using var stream = new MemoryStream(capacity: checked(4 + ordered.Length * 12));
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(ordered.Length);
            foreach (var process in ordered)
            {
                writer.Write(process.ProcessId);
                writer.Write(process.ProcessStartKey);
            }
        }
        return Convert.ToHexString(SHA256.HashData(
            stream.GetBuffer().AsSpan(0, checked((int)stream.Length))));
    }
}

internal readonly record struct HostManagerProcessEffectValidationCommittedHandoffDocument(
    Guid AdmissionId,
    long FirstAuditSequence,
    int AuditCount,
    Guid ParentAdmissionId,
    HostManagerProcessEffectValidationDurableHandoffDocument DurableHandoff);

internal sealed record HostManagerProcessEffectValidationAuditFenceDocument(
    int SchemaVersion,
    string Contract,
    Guid ScopeId,
    Guid RunNonce,
    Guid OwnerRuntimeIncarnation,
    long Generation,
    HostManagerProcessEffectValidationAuditFenceState State,
    long CommittedAuditRecordCount,
    string CommittedScopeDocumentSha256,
    Guid AdmissionId,
    Guid ParentAdmissionId,
    long FirstAuditSequence,
    IReadOnlyList<HostManagerProcessEffectValidationScopeAuditDocument> AdmissionAudits,
    HostManagerProcessEffectValidationDurableHandoffDocument? DurableHandoff,
    IReadOnlyList<HostManagerProcessEffectValidationCommittedHandoffDocument> CommittedHandoffs,
    string ChecksumSha256);
