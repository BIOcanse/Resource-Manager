using System.Buffers.Binary;
using ResourceManager.Adapter;
using ResourceManager.Adapter.NativeScheduling;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.Optimization.NativeScheduling;

internal sealed class HostManagerResourceJournalPrepareProof
{
    private HostManagerResourceJournalPrepareProof(
        HostManagerResourceTransactionPayload payload,
        NativeTransactionJournalPayloadReference reference,
        NativeTransactionJournalPayloadProvenance provenance,
        ulong journalInstanceLow,
        ulong journalInstanceHigh,
        ulong entryRevision)
    {
        Payload = payload;
        Reference = reference;
        Provenance = provenance;
        JournalInstanceLow = journalInstanceLow;
        JournalInstanceHigh = journalInstanceHigh;
        EntryRevision = entryRevision;
    }

    public HostManagerResourceTransactionPayload Payload { get; }

    public NativeTransactionJournalPayloadReference Reference { get; }

    public NativeTransactionJournalPayloadProvenance Provenance { get; }

    public ulong JournalInstanceLow { get; }

    public ulong JournalInstanceHigh { get; }

    public ulong EntryRevision { get; }

    public bool Matches(
        ResourceSchedulerReservationToken reservation,
        ResourceSchedulerSelection selection)
        => EntryRevision != 0
            && (JournalInstanceLow | JournalInstanceHigh) != 0
            && Reference.IsValid
            && Provenance.IsValid
            && Payload.Reservation == reservation
            && Payload.Selection == selection
            && Payload.JournalTransactionIdLow ==
                selection.Authority.ActionAttemptIdLow
            && Payload.JournalTransactionIdHigh ==
                selection.Authority.ActionAttemptIdHigh;

    internal static HostManagerResourceJournalPrepareProof Create(
        NativeTransactionJournalSnapshotHeader header,
        NativeTransactionJournalRecord record,
        HostManagerResourceTransactionPayload payload,
        NativeTransactionJournalPayloadReference expectedReference)
    {
        HostManagerResourceTransactionProjection.RequireRecordMatches(
            header,
            record,
            payload);
        var recordReference =
            HostManagerTransactionJournalProjection.CreatePayloadReference(
                in record);
        if (record.Phase !=
                (uint)NativeTransactionJournalPhase.Prepared
            || record.EntryRevision == 0
            || !expectedReference.IsValid
            || recordReference != expectedReference)
        {
            throw new InvalidDataException(
                "The resource journal record is not an exact durable prepare.");
        }
        return new HostManagerResourceJournalPrepareProof(
            payload,
            recordReference,
            HostManagerTransactionJournalProjection.CreatePayloadProvenance(
                in record),
            header.JournalInstanceLow,
            header.JournalInstanceHigh,
            record.EntryRevision);
    }
}

internal readonly record struct HostManagerResourceTransactionPayload(
    ulong JournalTransactionIdLow,
    ulong JournalTransactionIdHigh,
    ulong JournalActionId,
    ResourceSchedulerReservationToken Reservation,
    ResourceSchedulerSelection Selection,
    ResourceSchedulerCapacity Capacity,
    ulong DeadlineTimestamp,
    ulong PlanLease,
    ulong PlanEpoch,
    ulong JournalConfigurationGeneration,
    ulong HostSessionIncarnation)
{
    public bool IsValid =>
        (JournalTransactionIdLow | JournalTransactionIdHigh) != 0
        && JournalActionId != 0
        && JournalTransactionIdLow == Selection.Authority.ActionAttemptIdLow
        && JournalTransactionIdHigh == Selection.Authority.ActionAttemptIdHigh
        && Reservation.PendingGeneration == JournalActionId
        && Reservation.StateGeneration != 0
        && Reservation.SlotGeneration != 0
        && Reservation.PendingGeneration != 0
        && Selection.TargetKey != 0
        && Selection.RequestId != 0
        && Selection.ConfigurationGeneration != 0
        && Selection.SizeBytes != 0
        && Selection.EstimatedReleaseBytes != 0
        && Selection.EstimatedReleaseBytes <= Selection.SizeBytes
        && HostManagerResourceTransactionProjection.IsStrictResourceAuthority(
            Selection.Authority)
        && HostManagerResourceTransactionProjection.TargetIdentityMatches(
            Selection.Authority,
            Selection.TargetKey)
        && HostManagerResourceTransactionProjection.IsValidCapacity(Capacity)
        && DeadlineTimestamp != 0
        && PlanLease != 0
        && PlanEpoch != 0
        && JournalConfigurationGeneration != 0
        && HostSessionIncarnation != 0;
}

internal static class HostManagerResourceTransactionProjection
{
    internal const int EncodedPayloadSize = 408;
    private const ulong PayloadMagic = 0x31505452534D5248;
    private const uint PayloadVersion = 5;
    private const int AuthorityOffset = 80;
    private const int AuthoritySize = 152;

    public static byte[] EncodePayload(
        HostResourceSchedulerPlanResult plan,
        ResourceSchedulerReservationToken reservation,
        ResourceSchedulerSelection selection,
        ResourceSchedulerCapacity capacity,
        ulong journalActionId,
        ulong deadlineTimestamp,
        ulong journalConfigurationGeneration,
        ulong hostSessionIncarnation)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var payload = new HostManagerResourceTransactionPayload(
            selection.Authority.ActionAttemptIdLow,
            selection.Authority.ActionAttemptIdHigh,
            journalActionId,
            reservation,
            selection,
            capacity,
            deadlineTimestamp,
            plan.PlanLease,
            plan.PlanEpoch,
            journalConfigurationGeneration,
            hostSessionIncarnation);
        if (!payload.IsValid
            || reservation.StateGeneration != plan.StateGeneration
            || selection.ConfigurationGeneration !=
                (plan.Dispatch.Configuration?.Generation ?? 0)
            || !double.IsFinite(selection.FinalImportance)
            || selection.FinalImportance < 0
            || !Enum.IsDefined(selection.Tier)
            || plan.NativePlan.Selections.Count(candidate => candidate == selection) != 1)
        {
            throw new InvalidDataException(
                "The resource transaction payload does not match one exact scheduler selection.");
        }

        var encoded = new byte[EncodedPayloadSize];
        var destination = encoded.AsSpan();
        BinaryPrimitives.WriteUInt64LittleEndian(destination[0..8], PayloadMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[8..12], PayloadVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(
            destination[12..16],
            EncodedPayloadSize);
        BinaryPrimitives.WriteUInt64LittleEndian(
            destination[16..24],
            payload.JournalTransactionIdLow);
        BinaryPrimitives.WriteUInt64LittleEndian(
            destination[24..32],
            payload.JournalTransactionIdHigh);
        BinaryPrimitives.WriteUInt64LittleEndian(
            destination[32..40],
            reservation.StateGeneration);
        BinaryPrimitives.WriteUInt64LittleEndian(
            destination[40..48],
            reservation.SlotGeneration);
        BinaryPrimitives.WriteUInt64LittleEndian(
            destination[48..56],
            reservation.PendingGeneration);
        BinaryPrimitives.WriteUInt32LittleEndian(
            destination[56..60],
            reservation.SlotIndex);
        BinaryPrimitives.WriteUInt64LittleEndian(
            destination[64..72],
            selection.TargetKey);
        BinaryPrimitives.WriteUInt64LittleEndian(
            destination[72..80],
            selection.RequestId);
        WriteAuthority(destination.Slice(AuthorityOffset, AuthoritySize), selection.Authority);
        BinaryPrimitives.WriteUInt64LittleEndian(
            destination[232..240],
            selection.SizeBytes);
        BinaryPrimitives.WriteUInt64LittleEndian(
            destination[240..248],
            selection.EstimatedReleaseBytes);
        BinaryPrimitives.WriteUInt64LittleEndian(
            destination[248..256],
            selection.ConfigurationGeneration);
        BinaryPrimitives.WriteUInt64LittleEndian(
            destination[256..264],
            checked((ulong)BitConverter.DoubleToInt64Bits(selection.FinalImportance)));
        BinaryPrimitives.WriteUInt32LittleEndian(
            destination[264..268],
            selection.CandidateIndex);
        BinaryPrimitives.WriteUInt32LittleEndian(
            destination[268..272],
            selection.TargetInputIndex);
        destination[272] = (byte)selection.Tier;
        destination[273] = selection.ActivityScore;
        BinaryPrimitives.WriteUInt64LittleEndian(
            destination[280..288],
            deadlineTimestamp);
        BinaryPrimitives.WriteUInt64LittleEndian(
            destination[288..296],
            plan.PlanLease);
        BinaryPrimitives.WriteUInt64LittleEndian(
            destination[296..304],
            plan.PlanEpoch);
        BinaryPrimitives.WriteUInt64LittleEndian(
            destination[304..312],
            journalConfigurationGeneration);
        BinaryPrimitives.WriteUInt64LittleEndian(
            destination[312..320],
            hostSessionIncarnation);
        BinaryPrimitives.WriteUInt64LittleEndian(
            destination[320..328],
            journalActionId);
        WriteCapacity(destination[328..408], capacity);
        return encoded;
    }

    public static HostManagerResourceTransactionPayload DecodePayload(
        ReadOnlySpan<byte> source)
    {
        if (source.Length != EncodedPayloadSize
            || BinaryPrimitives.ReadUInt64LittleEndian(source[0..8]) != PayloadMagic
            || BinaryPrimitives.ReadUInt32LittleEndian(source[8..12]) != PayloadVersion
            || BinaryPrimitives.ReadUInt32LittleEndian(source[12..16]) !=
                EncodedPayloadSize
            || !AllZero(source[60..64])
            || !AllZero(source[274..280]))
        {
            throw new InvalidDataException(
                "The durable resource transaction payload header or reserved bytes are invalid.");
        }

        var authority = ReadAuthority(source.Slice(AuthorityOffset, AuthoritySize));
        var selection = new ResourceSchedulerSelection(
            BinaryPrimitives.ReadUInt64LittleEndian(source[64..72]),
            BinaryPrimitives.ReadUInt64LittleEndian(source[72..80]),
            authority,
            BinaryPrimitives.ReadUInt64LittleEndian(source[232..240]),
            BinaryPrimitives.ReadUInt64LittleEndian(source[240..248]),
            BinaryPrimitives.ReadUInt64LittleEndian(source[248..256]),
            BitConverter.Int64BitsToDouble(checked((long)
                BinaryPrimitives.ReadUInt64LittleEndian(source[256..264]))),
            BinaryPrimitives.ReadUInt32LittleEndian(source[264..268]),
            BinaryPrimitives.ReadUInt32LittleEndian(source[268..272]),
            (AdapterResourceTier)source[272],
            source[273]);
        var payload = new HostManagerResourceTransactionPayload(
            BinaryPrimitives.ReadUInt64LittleEndian(source[16..24]),
            BinaryPrimitives.ReadUInt64LittleEndian(source[24..32]),
            BinaryPrimitives.ReadUInt64LittleEndian(source[320..328]),
            new ResourceSchedulerReservationToken(
                BinaryPrimitives.ReadUInt64LittleEndian(source[32..40]),
                BinaryPrimitives.ReadUInt64LittleEndian(source[40..48]),
                BinaryPrimitives.ReadUInt64LittleEndian(source[48..56]),
                BinaryPrimitives.ReadUInt32LittleEndian(source[56..60])),
            selection,
            ReadCapacity(source[328..408]),
            BinaryPrimitives.ReadUInt64LittleEndian(source[280..288]),
            BinaryPrimitives.ReadUInt64LittleEndian(source[288..296]),
            BinaryPrimitives.ReadUInt64LittleEndian(source[296..304]),
            BinaryPrimitives.ReadUInt64LittleEndian(source[304..312]),
            BinaryPrimitives.ReadUInt64LittleEndian(source[312..320]));
        if (!payload.IsValid
            || !double.IsFinite(payload.Selection.FinalImportance)
            || payload.Selection.FinalImportance < 0
            || !Enum.IsDefined(payload.Selection.Tier))
        {
            throw new InvalidDataException(
                "The durable resource transaction payload has an invalid selection shape.");
        }
        return payload;
    }

    internal static bool IsValidCapacity(ResourceSchedulerCapacity capacity)
    {
        if ((capacity.FreeBytesValidMask & ~0b111) != 0
            || !IsRatio(capacity.FallbackVramFreeRatio)
            || !IsRatio(capacity.FallbackPhysicalFreeRatio)
            || !IsRatio(capacity.FallbackVirtualFreeRatio))
        {
            return false;
        }
        return IsValidCapacityTier(
                capacity.TotalVramBytes,
                capacity.FreeVramBytes,
                capacity.FreeBytesValidMask,
                0)
            && IsValidCapacityTier(
                capacity.TotalPhysicalBytes,
                capacity.FreePhysicalBytes,
                capacity.FreeBytesValidMask,
                1)
            && IsValidCapacityTier(
                capacity.TotalVirtualBytes,
                capacity.FreeVirtualBytes,
                capacity.FreeBytesValidMask,
                2);
    }

    private static void WriteCapacity(
        Span<byte> destination,
        ResourceSchedulerCapacity capacity)
    {
        if (destination.Length != 80 || !IsValidCapacity(capacity))
        {
            throw new InvalidDataException(
                "The durable resource capacity snapshot is invalid.");
        }
        BinaryPrimitives.WriteUInt64LittleEndian(
            destination[0..8],
            capacity.TotalVramBytes);
        BinaryPrimitives.WriteUInt64LittleEndian(
            destination[8..16],
            capacity.FreeVramBytes);
        BinaryPrimitives.WriteUInt64LittleEndian(
            destination[16..24],
            capacity.TotalPhysicalBytes);
        BinaryPrimitives.WriteUInt64LittleEndian(
            destination[24..32],
            capacity.FreePhysicalBytes);
        BinaryPrimitives.WriteUInt64LittleEndian(
            destination[32..40],
            capacity.TotalVirtualBytes);
        BinaryPrimitives.WriteUInt64LittleEndian(
            destination[40..48],
            capacity.FreeVirtualBytes);
        BinaryPrimitives.WriteUInt64LittleEndian(
            destination[48..56],
            checked((ulong)BitConverter.DoubleToInt64Bits(
                capacity.FallbackVramFreeRatio)));
        BinaryPrimitives.WriteUInt64LittleEndian(
            destination[56..64],
            checked((ulong)BitConverter.DoubleToInt64Bits(
                capacity.FallbackPhysicalFreeRatio)));
        BinaryPrimitives.WriteUInt64LittleEndian(
            destination[64..72],
            checked((ulong)BitConverter.DoubleToInt64Bits(
                capacity.FallbackVirtualFreeRatio)));
        destination[72] = capacity.FreeBytesValidMask;
    }

    private static ResourceSchedulerCapacity ReadCapacity(
        ReadOnlySpan<byte> source)
    {
        if (source.Length != 80 || !AllZero(source[73..80]))
        {
            throw new InvalidDataException(
                "The durable resource capacity snapshot has invalid reserved bytes.");
        }
        var capacity = new ResourceSchedulerCapacity(
            BinaryPrimitives.ReadUInt64LittleEndian(source[0..8]),
            BinaryPrimitives.ReadUInt64LittleEndian(source[8..16]),
            BinaryPrimitives.ReadUInt64LittleEndian(source[16..24]),
            BinaryPrimitives.ReadUInt64LittleEndian(source[24..32]),
            BinaryPrimitives.ReadUInt64LittleEndian(source[32..40]),
            BinaryPrimitives.ReadUInt64LittleEndian(source[40..48]),
            BitConverter.Int64BitsToDouble(checked((long)
                BinaryPrimitives.ReadUInt64LittleEndian(source[48..56]))),
            BitConverter.Int64BitsToDouble(checked((long)
                BinaryPrimitives.ReadUInt64LittleEndian(source[56..64]))),
            BitConverter.Int64BitsToDouble(checked((long)
                BinaryPrimitives.ReadUInt64LittleEndian(source[64..72]))),
            source[72]);
        if (!IsValidCapacity(capacity))
        {
            throw new InvalidDataException(
                "The durable resource capacity snapshot is invalid.");
        }
        return capacity;
    }


    private static bool IsRatio(double value)
        => double.IsFinite(value) && value >= 0 && value <= 1;

    private static bool IsValidCapacityTier(
        ulong total,
        ulong free,
        byte validMask,
        int index)
        => (validMask & (1 << index)) == 0
            || (total == 0 ? free == 0 : free <= total);

    public static NativeTransactionJournalPayloadBinding CreatePayloadBinding(
        HostManagerResourceTransactionPayload payload,
        ulong journalInstanceLow,
        ulong journalInstanceHigh,
        ulong recoveryDeadlineUtcMilliseconds,
        uint maximumRecoveryAttempts)
    {
        if (!payload.IsValid
            || (journalInstanceLow | journalInstanceHigh) == 0
            || payload.JournalConfigurationGeneration == 0
            || payload.HostSessionIncarnation == 0)
        {
            throw new InvalidDataException(
                "The durable resource payload binding identity is invalid.");
        }
        return new NativeTransactionJournalPayloadBinding(
            journalInstanceLow,
            journalInstanceHigh,
            CreateJournalIdentity(
                payload,
                payload.JournalConfigurationGeneration,
                payload.HostSessionIncarnation),
            NativeTransactionJournalScope.Resource,
            NativeTransactionJournalDisposition.Apply,
            ResourceDomain(payload.Selection),
            NativeTransactionJournalGradeValidity.None,
            0,
            0,
            0,
            0,
            0,
            0,
            StableSystemStatus: 0,
            StableSystemError: 0,
            maximumRecoveryAttempts,
            recoveryDeadlineUtcMilliseconds,
            AtomicGroupId: 0,
            GroupMemberIndex: 0,
            GroupMemberCount: 0);
    }

    public static NativeTransactionJournalPrepareInput CreatePrepare(
        HostManagerResourceTransactionPayload payload,
        NativeTransactionJournalPayloadBinding binding,
        NativeTransactionJournalPayloadReference reference,
        NativeTransactionJournalPayloadProvenance provenance,
        ulong expectedJournalRevision,
        ulong nowUtcMilliseconds)
    {
        if (!payload.IsValid
            || !binding.IsValid
            || !reference.IsValid
            || !provenance.IsValid
            || expectedJournalRevision == 0
            || nowUtcMilliseconds == 0
            || !SameJournalIdentity(
                binding.ActionIdentity,
                CreateJournalIdentity(
                    payload,
                    binding.ActionIdentity.ConfigurationGeneration,
                    binding.ActionIdentity.HostSessionIncarnation)))
        {
            throw new InvalidDataException(
                "The resource transaction prepare input is not exactly bound.");
        }
        return new NativeTransactionJournalPrepareInput
        {
            AbiVersion = NativeTransactionJournalAbi.Version,
            StructSize = NativeTransactionJournalSession
                .SizeOf<NativeTransactionJournalPrepareInput>(),
            ExpectedJournalRevision = expectedJournalRevision,
            Identity = binding.ActionIdentity,
            Scope = (uint)binding.Scope,
            Disposition = (uint)binding.Disposition,
            DomainMask = (uint)binding.Domain,
            GradeValidMask = (uint)binding.GradeValidMask,
            StableSystemStatus = binding.StableSystemStatus,
            StableSystemError = binding.StableSystemError,
            PayloadKind = (uint)NativeTransactionJournalPayloadKind.Durable,
            PayloadSlot = reference.Slot,
            PayloadGeneration = reference.Generation,
            PayloadLength = reference.Length,
            PayloadDigestLow = reference.DigestLow,
            PayloadDigestHigh = reference.DigestHigh,
            NowUtcMilliseconds = nowUtcMilliseconds,
            MaximumRecoveryAttempts = binding.MaximumRecoveryAttempts,
            RecoveryDeadlineUtcMilliseconds =
                binding.RecoveryDeadlineUtcMilliseconds,
            PayloadProvenanceDigestLow = provenance.DigestLow,
            PayloadProvenanceDigestHigh = provenance.DigestHigh
        };
    }

    public static ResourceSchedulerPendingAction CreateJournalPending(
        NativeTransactionJournalSnapshotHeader header,
        NativeTransactionJournalRecord record,
        HostManagerResourceTransactionPayload payload)
    {
        RequireRecordMatches(header, record, payload);
        var state = (NativeTransactionJournalPhase)record.Phase switch
        {
            NativeTransactionJournalPhase.Prepared
                or NativeTransactionJournalPhase.PreviousEffectRestored
                or NativeTransactionJournalPhase.EffectObserved
                or NativeTransactionJournalPhase.FeedbackPending =>
                ResourceSchedulerPendingState.JournalPending,
            NativeTransactionJournalPhase.ReconciliationPending
                or NativeTransactionJournalPhase.AuthoritativeResyncPending
                or NativeTransactionJournalPhase.RecoveryRetryPending
                or NativeTransactionJournalPhase.RecoveryBlocked
                or NativeTransactionJournalPhase.EffectInvocationUncertain =>
                ResourceSchedulerPendingState.EffectUncertain,
            _ => throw new InvalidDataException(
                "The resource transaction journal phase cannot be imported.")
        };
        return new ResourceSchedulerPendingAction(
            payload.Selection.Authority,
            payload.JournalTransactionIdLow,
            payload.JournalTransactionIdHigh,
            payload.Selection.TargetKey,
            payload.Selection.SizeBytes,
            payload.DeadlineTimestamp,
            payload.Reservation.PendingGeneration,
            state,
            payload.Selection.Tier);
    }

    public static void RequireRecordMatches(
        NativeTransactionJournalSnapshotHeader header,
        NativeTransactionJournalRecord record,
        HostManagerResourceTransactionPayload payload)
    {
        if (!payload.IsValid
            || (header.JournalInstanceLow | header.JournalInstanceHigh) == 0
            || record.Scope != (uint)NativeTransactionJournalScope.Resource
            || record.Disposition !=
                (uint)NativeTransactionJournalDisposition.Apply
            || record.DomainMask !=
                (uint)ResourceDomain(payload.Selection)
            || record.GradeValidMask != 0
            || record.Identity.ConfigurationGeneration !=
                payload.JournalConfigurationGeneration
            || record.Identity.HostSessionIncarnation !=
                payload.HostSessionIncarnation
            || record.Identity.PlanEpoch != payload.PlanEpoch
            || record.Identity.ActionId != payload.JournalActionId
            || record.Identity.TargetId != payload.Selection.TargetKey
            || record.Identity.SoftwareId !=
                payload.Selection.Authority.OwnerApplicationKey
            || record.Identity.ProcessStartKey != 0
            || record.Identity.ProcessId != 0
            || record.Identity.Reserved != 0)
        {
            throw new InvalidDataException(
                "The durable resource payload does not match its journal record.");
        }
        var exactBinding = CreatePayloadBinding(
            payload,
            header.JournalInstanceLow,
            header.JournalInstanceHigh,
            record.RecoveryDeadlineUtcMilliseconds,
            record.MaximumRecoveryAttempts);
        var exactProvenance =
            NativeTransactionJournalPayloadProvenance.Create(exactBinding);
        if (record.PayloadProvenanceDigestLow != exactProvenance.DigestLow
            || record.PayloadProvenanceDigestHigh != exactProvenance.DigestHigh)
        {
            throw new InvalidDataException(
                "The resource journal record lost its exact immutable payload binding.");
        }
    }

    private static NativeTransactionJournalIdentity CreateJournalIdentity(
        HostManagerResourceTransactionPayload payload,
        ulong journalConfigurationGeneration,
        ulong hostSessionIncarnation)
        => new()
        {
            ConfigurationGeneration = journalConfigurationGeneration,
            PlanEpoch = payload.PlanEpoch,
            ActionId = payload.JournalActionId,
            HostSessionIncarnation = hostSessionIncarnation,
            TargetId = payload.Selection.TargetKey,
            SoftwareId = payload.Selection.Authority.OwnerApplicationKey,
            ProcessStartKey = 0,
            ProcessId = 0,
            Reserved = 0
        };

    private static NativeTransactionJournalDomain ResourceDomain(
        ResourceSchedulerSelection selection)
    {
        var tier = selection.Tier switch
        {
            AdapterResourceTier.PhysicalMemory =>
                NativeTransactionJournalDomain.PhysicalMemory,
            AdapterResourceTier.VirtualMemory =>
                NativeTransactionJournalDomain.VirtualMemory,
            AdapterResourceTier.Vram =>
                NativeTransactionJournalDomain.VideoMemory,
            _ => throw new InvalidDataException("The resource tier is invalid.")
        };
        return tier;
    }

    public static NativeTransactionJournalStageFeedbackInput
        CreateFeedback(
            NativeTransactionJournalRecord record,
            ulong expectedJournalRevision,
            HostManagerResourceTransactionPayload payload,
            ResourceSchedulerActionFeedback feedback,
            ulong completedAtUtcMilliseconds)
    {
        var status = feedback.Status switch
        {
            AdapterResourceActionStatus.Completed =>
                NativeTransactionJournalFeedbackStatus.Succeeded,
            AdapterResourceActionStatus.ResourceNotFound =>
                NativeTransactionJournalFeedbackStatus.OwnershipLost,
            AdapterResourceActionStatus.ActionNotSupported
                or AdapterResourceActionStatus.InvalidRequest =>
                NativeTransactionJournalFeedbackStatus.Rejected,
            AdapterResourceActionStatus.ResourceBusy =>
                NativeTransactionJournalFeedbackStatus.StateUncertain,
            AdapterResourceActionStatus.Failed =>
                NativeTransactionJournalFeedbackStatus.FailedUnchanged,
            _ => throw new InvalidDataException(
                "The resource action feedback status is unknown.")
        };
        if (!payload.IsValid
            || expectedJournalRevision == 0
            || record.EntryRevision == 0
            || record.Phase is not (
                (uint)NativeTransactionJournalPhase.EffectObserved
                or (uint)NativeTransactionJournalPhase
                    .PreviousEffectRestored)
            || completedAtUtcMilliseconds == 0
            || completedAtUtcMilliseconds < record.UpdatedAtUtcMilliseconds
            || feedback.RequestId == 0
            || record.Identity.ActionId != payload.JournalActionId
            || record.Identity.ConfigurationGeneration !=
                payload.JournalConfigurationGeneration
            || record.Identity.HostSessionIncarnation !=
                payload.HostSessionIncarnation
            || record.Identity.PlanEpoch != payload.PlanEpoch
            || record.Identity.TargetId != payload.Selection.TargetKey
            || record.Identity.SoftwareId !=
                payload.Selection.Authority.OwnerApplicationKey
            || feedback.RequestId != payload.Selection.RequestId
            || feedback.Authority != payload.Selection.Authority
            || feedback.Authority.OwnerApplicationKey !=
                record.Identity.SoftwareId
            || !IsStrictResourceAuthority(feedback.Authority))
        {
            throw new InvalidDataException(
                "The resource journal feedback identity is invalid.");
        }
        if (record.Phase ==
                (uint)NativeTransactionJournalPhase.PreviousEffectRestored
            && status is not (
                NativeTransactionJournalFeedbackStatus.FailedUnchanged
                or NativeTransactionJournalFeedbackStatus.Rejected
                or NativeTransactionJournalFeedbackStatus.Skipped
                or NativeTransactionJournalFeedbackStatus.OwnershipLost))
        {
            throw new InvalidDataException(
                "A resource no-effect receipt cannot report an observed effect.");
        }
        return new NativeTransactionJournalStageFeedbackInput
        {
            AbiVersion = NativeTransactionJournalAbi.Version,
            StructSize = NativeTransactionJournalSession.SizeOf<
                NativeTransactionJournalStageFeedbackInput>(),
            Identity = record.Identity,
            ExpectedJournalRevision = expectedJournalRevision,
            ExpectedEntryRevision = record.EntryRevision,
            CompletedAtUtcMilliseconds = completedAtUtcMilliseconds,
            ExpectedPhase = record.Phase,
            FeedbackValidMask =
                (uint)NativeTransactionJournalFeedbackValidity.CompletedAt,
            FeedbackFlags =
                (uint)NativeTransactionJournalFeedbackFlags.None,
            FeedbackStatus = (uint)status,
            FeedbackSystemStatus = 0,
            FeedbackSystemError = feedback.DetailCode,
            FeedbackReserved = 0,
            ActualProcessGrade = 0,
            ActualCpuGrade = 0,
            ActualGpuGrade = 0,
            ActualReserved = 0
        };
    }

    private static void WriteAuthority(
        Span<byte> destination,
        ResourceSchedulerExecutionAuthority authority)
    {
        if (destination.Length != AuthoritySize
            || !IsStrictResourceAuthority(authority))
        {
            throw new InvalidDataException(
                "The resource execution authority cannot be encoded.");
        }
        destination[0] = (byte)authority.Source;
        destination[1] = (byte)authority.Action;
        destination[2] = (byte)authority.ActionRoute;
        destination[3] = authority.Flags;
        BinaryPrimitives.WriteUInt32LittleEndian(
            destination[4..8],
            authority.ResourceSlot);
        BinaryPrimitives.WriteUInt32LittleEndian(
            destination[8..12],
            authority.ResourceId);
        WriteU64(destination, 16, authority.LedgerInstanceId);
        WriteU64(destination, 24, authority.SourceSnapshotGeneration);
        WriteU64(destination, 32, authority.ResourceGeneration);
        WriteU64(destination, 40, authority.ResourceKey);
        WriteU64(destination, 48, authority.OwnerApplicationKey);
        WriteU64(destination, 56, authority.OwnerInstanceIdLow);
        WriteU64(destination, 64, authority.OwnerInstanceIdHigh);
        WriteU64(destination, 72, authority.OwnerContextGeneration);
        WriteU64(destination, 80, authority.LeaseGeneration);
        WriteU64(destination, 88, authority.BindingGeneration);
        WriteU64(destination, 96, authority.CapabilityGeneration);
        WriteU64(destination, 104, authority.SchedulingRevision);
        WriteU64(destination, 112, authority.ExecutorIdLow);
        WriteU64(destination, 120, authority.ExecutorIdHigh);
        WriteU64(destination, 128, authority.ActionAttemptIdLow);
        WriteU64(destination, 136, authority.ActionAttemptIdHigh);
        WriteU64(destination, 144, authority.ProjectionEpoch);
    }

    private static ResourceSchedulerExecutionAuthority ReadAuthority(
        ReadOnlySpan<byte> source)
    {
        if (source.Length != AuthoritySize || !AllZero(source[12..16]))
        {
            throw new InvalidDataException(
                "The durable resource authority has an invalid fixed layout.");
        }
        var authority = new ResourceSchedulerExecutionAuthority(
            (ResourceSchedulerSource)source[0],
            (AdapterResourceActionMask)source[1],
            (AdapterResourceActionRoute)source[2],
            source[3],
            BinaryPrimitives.ReadUInt32LittleEndian(source[4..8]),
            BinaryPrimitives.ReadUInt32LittleEndian(source[8..12]),
            ReadU64(source, 16),
            ReadU64(source, 24),
            ReadU64(source, 32),
            ReadU64(source, 40),
            ReadU64(source, 48),
            ReadU64(source, 56),
            ReadU64(source, 64),
            ReadU64(source, 72),
            ReadU64(source, 80),
            ReadU64(source, 88),
            ReadU64(source, 96),
            ReadU64(source, 104),
            ReadU64(source, 112),
            ReadU64(source, 120),
            ReadU64(source, 128),
            ReadU64(source, 136),
            ReadU64(source, 144));
        if (!IsStrictResourceAuthority(authority))
        {
            throw new InvalidDataException(
                "The durable resource authority is invalid.");
        }
        return authority;
    }

    internal static bool IsStrictResourceAuthority(
        ResourceSchedulerExecutionAuthority authority)
    {
        var action = (byte)authority.Action;
        var exactRouteAndProof =
            authority.Source == ResourceSchedulerSource.AdaptedPrivate
            && authority.ActionRoute == AdapterResourceActionRoute.ManagerDirect
            && authority.Flags ==
                ResourceSchedulerExecutionAuthority.HostSelfExecutorProof;
        return exactRouteAndProof
            && Enum.IsDefined(authority.ActionRoute)
            && action != 0
            && (action & (action - 1)) == 0
            && (authority.Action
                & ~(AdapterResourceActionMask.Discard
                    | AdapterResourceActionMask.Trim
                    | AdapterResourceActionMask.MoveDown)) == 0
            && authority.ResourceSlot != uint.MaxValue
            && authority.ResourceId != 0
            && authority.LedgerInstanceId != 0
            && authority.SourceSnapshotGeneration != 0
            && authority.ResourceGeneration != 0
            && authority.ResourceKey != 0
            && authority.OwnerApplicationKey != 0
            && (authority.OwnerInstanceIdLow | authority.OwnerInstanceIdHigh) != 0
            && authority.OwnerContextGeneration != 0
            && authority.LeaseGeneration != 0
            && authority.BindingGeneration != 0
            && authority.CapabilityGeneration != 0
            && authority.SchedulingRevision != 0
            && (authority.ExecutorIdLow | authority.ExecutorIdHigh) != 0
            && (authority.ActionAttemptIdLow |
                authority.ActionAttemptIdHigh) != 0
            && authority.ProjectionEpoch != 0;
    }

    internal static bool TargetIdentityMatches(
        ResourceSchedulerExecutionAuthority authority,
        ulong targetKey)
        => targetKey != 0
            && (targetKey == authority.OwnerInstanceIdLow
                || authority.OwnerInstanceIdLow == 0
                    && targetKey == authority.OwnerInstanceIdHigh);

    private static bool SameJournalIdentity(
        NativeTransactionJournalIdentity left,
        NativeTransactionJournalIdentity right)
        => left.ConfigurationGeneration == right.ConfigurationGeneration
            && left.PlanEpoch == right.PlanEpoch
            && left.ActionId == right.ActionId
            && left.HostSessionIncarnation == right.HostSessionIncarnation
            && left.TargetId == right.TargetId
            && left.SoftwareId == right.SoftwareId
            && left.ProcessStartKey == right.ProcessStartKey
            && left.ProcessId == right.ProcessId
            && left.Reserved == right.Reserved;

    private static void WriteU64(Span<byte> destination, int offset, ulong value)
        => BinaryPrimitives.WriteUInt64LittleEndian(
            destination.Slice(offset, sizeof(ulong)),
            value);

    private static ulong ReadU64(ReadOnlySpan<byte> source, int offset)
        => BinaryPrimitives.ReadUInt64LittleEndian(
            source.Slice(offset, sizeof(ulong)));

    private static bool AllZero(ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes)
        {
            if (value != 0)
            {
                return false;
            }
        }
        return true;
    }
}
