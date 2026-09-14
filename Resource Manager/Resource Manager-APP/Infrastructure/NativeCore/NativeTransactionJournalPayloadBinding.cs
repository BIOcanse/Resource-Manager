using System.Buffers.Binary;
using System.Security.Cryptography;

namespace ResourceManager.App.Infrastructure.NativeCore;

internal readonly record struct NativeTransactionJournalPayloadProvenance(
    ulong DigestLow,
    ulong DigestHigh)
{
    public bool IsValid => DigestLow != 0 && DigestHigh != 0;

    public static NativeTransactionJournalPayloadProvenance Create(
        NativeTransactionJournalPayloadBinding binding)
    {
        if (!binding.IsValid)
        {
            throw new ArgumentException("The rollback payload binding is invalid.", nameof(binding));
        }

        Span<byte> encoded = stackalloc byte[NativeTransactionJournalPayloadBinding.EncodedSize];
        Span<byte> hash = stackalloc byte[32];
        binding.WriteTo(encoded);
        SHA256.HashData(encoded, hash);
        var provenance = new NativeTransactionJournalPayloadProvenance(
            BinaryPrimitives.ReadUInt64LittleEndian(hash[..8]),
            BinaryPrimitives.ReadUInt64LittleEndian(hash.Slice(8, 8)));
        if (!provenance.IsValid)
        {
            throw new InvalidOperationException(
                "The rollback payload provenance cannot be represented by the transaction journal ABI.");
        }
        return provenance;
    }
}

internal readonly record struct NativeTransactionJournalPayloadBinding(
    ulong JournalInstanceLow,
    ulong JournalInstanceHigh,
    NativeTransactionJournalIdentity ActionIdentity,
    NativeTransactionJournalScope Scope,
    NativeTransactionJournalDisposition Disposition,
    NativeTransactionJournalDomain Domain,
    NativeTransactionJournalGradeValidity GradeValidMask,
    int ProcessFromGrade,
    int ProcessToGrade,
    int CpuFromGrade,
    int CpuToGrade,
    int GpuFromGrade,
    int GpuToGrade,
    uint StableSystemStatus,
    uint StableSystemError,
    uint MaximumRecoveryAttempts,
    ulong RecoveryDeadlineUtcMilliseconds,
    ulong AtomicGroupId,
    uint GroupMemberIndex,
    uint GroupMemberCount)
{
    internal const int EncodedSize = 160;

    private const NativeTransactionJournalDomain SoftwareDomains =
        NativeTransactionJournalDomain.Cpu |
        NativeTransactionJournalDomain.Gpu;
    private const NativeTransactionJournalDomain ResourceDomains =
        NativeTransactionJournalDomain.PhysicalMemory |
        NativeTransactionJournalDomain.VirtualMemory |
        NativeTransactionJournalDomain.VideoMemory |
        NativeTransactionJournalDomain.Placement |
        NativeTransactionJournalDomain.SharedResource;
    private const NativeTransactionJournalDomain KnownDomains =
        NativeTransactionJournalDomain.Process |
        SoftwareDomains |
        ResourceDomains;
    private const NativeTransactionJournalGradeValidity KnownGradeValidity =
        NativeTransactionJournalGradeValidity.Process |
        NativeTransactionJournalGradeValidity.Cpu |
        NativeTransactionJournalGradeValidity.Gpu |
        NativeTransactionJournalGradeValidity.Memory;

    public bool IsValid =>
        (JournalInstanceLow != 0 || JournalInstanceHigh != 0) &&
        IsActionIdentityValidForScope() &&
        Disposition is NativeTransactionJournalDisposition.Apply or NativeTransactionJournalDisposition.Restore &&
        IsDomainValidForScope() &&
        IsGradeSetValidForScope() &&
        MaximumRecoveryAttempts != 0 &&
        RecoveryDeadlineUtcMilliseconds != 0 &&
        IsAtomicGroupValid();

    internal void WriteTo(Span<byte> destination)
    {
        if (destination.Length != EncodedSize)
        {
            throw new ArgumentException(
                $"A payload binding must be encoded into exactly {EncodedSize} bytes.",
                nameof(destination));
        }

        BinaryPrimitives.WriteUInt64LittleEndian(destination.Slice(0, 8), JournalInstanceLow);
        BinaryPrimitives.WriteUInt64LittleEndian(destination.Slice(8, 8), JournalInstanceHigh);
        BinaryPrimitives.WriteUInt64LittleEndian(
            destination.Slice(16, 8),
            ActionIdentity.ConfigurationGeneration);
        BinaryPrimitives.WriteUInt64LittleEndian(destination.Slice(24, 8), ActionIdentity.PlanEpoch);
        BinaryPrimitives.WriteUInt64LittleEndian(destination.Slice(32, 8), ActionIdentity.ActionId);
        BinaryPrimitives.WriteUInt64LittleEndian(
            destination.Slice(40, 8),
            ActionIdentity.HostSessionIncarnation);
        BinaryPrimitives.WriteUInt64LittleEndian(destination.Slice(48, 8), ActionIdentity.TargetId);
        BinaryPrimitives.WriteUInt64LittleEndian(destination.Slice(56, 8), ActionIdentity.SoftwareId);
        BinaryPrimitives.WriteUInt64LittleEndian(destination.Slice(64, 8), ActionIdentity.ProcessStartKey);
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(72, 4), ActionIdentity.ProcessId);
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(76, 4), ActionIdentity.Reserved);
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(80, 4), (uint)Scope);
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(84, 4), (uint)Disposition);
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(88, 4), (uint)Domain);
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(92, 4), (uint)GradeValidMask);
        BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(96, 4), ProcessFromGrade);
        BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(100, 4), ProcessToGrade);
        BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(104, 4), CpuFromGrade);
        BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(108, 4), CpuToGrade);
        BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(112, 4), GpuFromGrade);
        BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(116, 4), GpuToGrade);
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(120, 4), StableSystemStatus);
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(124, 4), StableSystemError);
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(128, 4), MaximumRecoveryAttempts);
        destination.Slice(132, 4).Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(
            destination.Slice(136, 8),
            RecoveryDeadlineUtcMilliseconds);
        BinaryPrimitives.WriteUInt64LittleEndian(destination.Slice(144, 8), AtomicGroupId);
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(152, 4), GroupMemberIndex);
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(156, 4), GroupMemberCount);
    }

    private bool IsActionIdentityValidForScope()
    {
        if (ActionIdentity.ConfigurationGeneration == 0 ||
            ActionIdentity.PlanEpoch == 0 ||
            ActionIdentity.ActionId == 0 ||
            ActionIdentity.HostSessionIncarnation == 0 ||
            ActionIdentity.TargetId == 0 ||
            ActionIdentity.Reserved != 0)
        {
            return false;
        }

        return Scope switch
        {
            NativeTransactionJournalScope.Process =>
                ActionIdentity.ProcessStartKey != 0 &&
                ActionIdentity.ProcessId != 0,
            NativeTransactionJournalScope.Software or NativeTransactionJournalScope.Resource =>
                ActionIdentity.SoftwareId != 0 &&
                ActionIdentity.ProcessStartKey == 0 &&
                ActionIdentity.ProcessId == 0,
            _ => false
        };
    }

    private bool IsDomainValidForScope()
    {
        if (Domain == NativeTransactionJournalDomain.None || (Domain & ~KnownDomains) != 0)
        {
            return false;
        }

        return Scope switch
        {
            NativeTransactionJournalScope.Process =>
                Domain is NativeTransactionJournalDomain.Process or
                    NativeTransactionJournalDomain.PhysicalMemory,
            NativeTransactionJournalScope.Software => (Domain & ~SoftwareDomains) == 0,
            NativeTransactionJournalScope.Resource => (Domain & ~ResourceDomains) == 0,
            _ => false
        };
    }

    private bool IsGradeSetValidForScope()
    {
        if ((GradeValidMask & ~KnownGradeValidity) != 0)
        {
            return false;
        }

        return Scope switch
        {
            NativeTransactionJournalScope.Process => IsProcessGradeSetValid(),
            NativeTransactionJournalScope.Software => IsSoftwareGradeSetValid(),
            NativeTransactionJournalScope.Resource =>
                GradeValidMask == NativeTransactionJournalGradeValidity.None &&
                ProcessFromGrade == 0 &&
                ProcessToGrade == 0 &&
                CpuFromGrade == 0 &&
                CpuToGrade == 0 &&
                GpuFromGrade == 0 &&
                GpuToGrade == 0,
            _ => false
        };
    }

    private bool IsProcessGradeSetValid()
    {
        if (CpuFromGrade != 0 || CpuToGrade != 0 || GpuFromGrade != 0 || GpuToGrade != 0)
        {
            return false;
        }

        return Domain switch
        {
            NativeTransactionJournalDomain.Process =>
                GradeValidMask == NativeTransactionJournalGradeValidity.Process &&
                IsProcessGradePairValid(ProcessFromGrade, ProcessToGrade),
            NativeTransactionJournalDomain.PhysicalMemory =>
                GradeValidMask == NativeTransactionJournalGradeValidity.Memory &&
                IsMemoryPriorityPairValid(
                    Disposition,
                    ProcessFromGrade,
                    ProcessToGrade),
            _ => false
        };
    }

    private bool IsSoftwareGradeSetValid()
    {
        var required = NativeTransactionJournalGradeValidity.None;
        if ((Domain & NativeTransactionJournalDomain.Cpu) != 0)
        {
            required |= NativeTransactionJournalGradeValidity.Cpu;
        }

        if ((Domain & NativeTransactionJournalDomain.Gpu) != 0)
        {
            required |= NativeTransactionJournalGradeValidity.Gpu;
        }

        return GradeValidMask == required &&
            ProcessFromGrade == 0 &&
            ProcessToGrade == 0 &&
            IsAdapterGradePairValid(
                (required & NativeTransactionJournalGradeValidity.Cpu) != 0,
                CpuFromGrade,
                CpuToGrade) &&
            IsAdapterGradePairValid(
                (required & NativeTransactionJournalGradeValidity.Gpu) != 0,
                GpuFromGrade,
                GpuToGrade);
    }

    private bool IsAtomicGroupValid()
        => AtomicGroupId == 0
            ? GroupMemberIndex == 0 && GroupMemberCount == 0
            : GroupMemberCount > 1 && GroupMemberIndex < GroupMemberCount;

    private static bool IsProcessGradePairValid(int from, int to)
        => from >= (int)NativeTransactionJournalProcessGrade.Level4 &&
            from <= (int)NativeTransactionJournalProcessGrade.A1 &&
            to >= (int)NativeTransactionJournalProcessGrade.Level4 &&
            to <= (int)NativeTransactionJournalProcessGrade.A1 &&
            from != to;

    private static bool IsAdapterGradePairValid(bool present, int from, int to)
        => present
            ? from >= (int)NativeTransactionJournalAdapterGrade.Freeze &&
                from <= (int)NativeTransactionJournalAdapterGrade.Extreme &&
                to >= (int)NativeTransactionJournalAdapterGrade.Freeze &&
                to <= (int)NativeTransactionJournalAdapterGrade.Extreme &&
                from != to
            : from == 0 && to == 0;

    private static bool IsMemoryPriorityPairValid(
        NativeTransactionJournalDisposition disposition,
        int from,
        int to)
        => disposition switch
        {
            NativeTransactionJournalDisposition.Apply =>
                from is >= 0 and <= 5 && to is >= 1 and <= 5 && from != to,
            NativeTransactionJournalDisposition.Restore => from is >= 1 and <= 5 && to == 0,
            _ => false
        };
}
