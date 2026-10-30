namespace ResourceManager.Ledger;

/// <summary>Stable status values for the current ABI v1 pairing draft.</summary>
public enum LedgerStatus : int
{
    Ok = 0,
    InMemory = 1,
    NewlyAllocated = 2,
    NotInMemory = 3,
    Loading = 4,
    Completed = 5,
    ForceReleased = 6,
    Updated = 7,
    RegistrationOnly = 8,
    InUse = 9,
    Blocked = 10,
    Nonexistent = 11,
    InputError = 12,
    AllocationFailed = 13,
    SpecificationLimit = 14,
    ChangedBeyondTolerance = 15,
    UnsupportedPlatform = 16,
    InvalidState = 17,
    PartitionCreated = 18,
    PartitionExists = 19,
    EndedCleanupFailed = 20,
    Abandoned = 21,
    CleanupPending = 22,
    InternalError = 255,
}

public enum LedgerCandidate : uint
{
    C2Chain = 0,
}

public enum LedgerTargetKind : uint
{
    Table = 0,
    Partition = 1,
}

public enum LedgerTableMode : uint
{
    Unrestricted = 0,
    Normal = 1,
    Optimize = 2,
}

public enum LedgerRecoverability : uint
{
    NotRecoverable = 0,
    Recoverable = 1,
}

public enum LedgerAccessLossImpact : uint
{
    Fatal = 0,
    ObservableNow = 1,
    UnobservableNow = 2,
}

public readonly record struct LedgerAttributes(
    LedgerRecoverability Recoverability,
    uint RecoveryCostCoefficient,
    LedgerAccessLossImpact AccessLossImpact);

/// <summary>Partial update; null fields preserve their current values.</summary>
public readonly record struct LedgerUpdate(
    ulong? AllocationBytes = null,
    LedgerRecoverability? Recoverability = null,
    uint? RecoveryCostCoefficient = null,
    LedgerAccessLossImpact? AccessLossImpact = null)
{
    internal bool HasAnyValue => AllocationBytes.HasValue || Recoverability.HasValue ||
        RecoveryCostCoefficient.HasValue || AccessLossImpact.HasValue;
}

/// <summary>A table target or a partition target identified by its parent UID.</summary>
public readonly struct LedgerTarget<TUid> where TUid : unmanaged, Identity.ILedgerUid<TUid>
{
    private LedgerTarget(LedgerTargetKind kind, uint table, TUid parentUid)
    {
        Kind = kind;
        Table = table;
        ParentUid = parentUid;
    }

    public LedgerTargetKind Kind { get; }
    public uint Table { get; }
    public TUid ParentUid { get; }

    public static LedgerTarget<TUid> ForTable(uint table) => new(LedgerTargetKind.Table, table, default);

    public static LedgerTarget<TUid> ForPartition(TUid parentUid) =>
        new(LedgerTargetKind.Partition, 0, parentUid);
}

public readonly record struct LedgerPushResult<TUid>(LedgerStatus Status, LedgerLease<TUid>? Lease)
    where TUid : unmanaged, Identity.ILedgerUid<TUid>;

public readonly record struct LedgerUseResult<TUid>(LedgerStatus Status, LedgerLease<TUid>? Lease)
    where TUid : unmanaged, Identity.ILedgerUid<TUid>;

public readonly record struct LedgerPartitionPushResult(LedgerStatus Status, ulong AllocationNumber);

public readonly record struct LedgerTableView(ulong RegisteredBytes, ulong SystemPageBytes);

public readonly record struct LedgerTickResult(ulong Released, ulong Scanned);

public readonly record struct LedgerEndResult(LedgerStatus Status)
{
    public bool HasEnded => Status is LedgerStatus.Completed or LedgerStatus.ForceReleased
        or LedgerStatus.EndedCleanupFailed or LedgerStatus.Abandoned;
    public bool CleanupFailed => Status == LedgerStatus.EndedCleanupFailed;
}

public readonly record struct LedgerNotice<TUid>(TUid Uid, ulong AllocationNumber)
    where TUid : unmanaged, Identity.ILedgerUid<TUid>;

public class LedgerException : Exception
{
    public LedgerException(LedgerStatus status, string operation)
        : base($"Ledger operation '{operation}' returned {status} ({(int)status}).")
    {
        Status = status;
        Operation = operation;
    }

    public LedgerStatus Status { get; }
    public string Operation { get; }
}

/// <summary>Preserves whether the exact use ended, including failures after that boundary.</summary>
public sealed class LedgerEndException : LedgerException
{
    public LedgerEndException(LedgerEndResult result)
        : base(result.Status, result.HasEnded ? "clean up ended use" : "end use before commit") => Result = result;
    public LedgerEndResult Result { get; }
    public bool HasEnded => Result.HasEnded;
}

/// <summary>Build-generated expectations required to pair managed and Native artifacts.</summary>
public sealed class LedgerBuildIdentity
{
    private readonly byte[] _structureHash;
    private readonly byte[] _policyHash;

    public LedgerBuildIdentity(
        uint tableCount,
        uint fixedCount,
        uint resourceCapacity,
        uint partitionCapacity,
        uint indexNodeCount,
        uint indexHomeCount,
        ReadOnlySpan<byte> structureHash,
        ReadOnlySpan<byte> policyHash,
        LedgerCandidate candidate = LedgerCandidate.C2Chain)
    {
        if (structureHash.Length != 32) throw new ArgumentException("A structure hash is exactly 32 bytes.", nameof(structureHash));
        if (policyHash.Length != 32) throw new ArgumentException("A policy hash is exactly 32 bytes.", nameof(policyHash));
        if (!Enum.IsDefined(candidate)) throw new ArgumentOutOfRangeException(nameof(candidate));
        TableCount = tableCount;
        FixedCount = fixedCount;
        ResourceCapacity = resourceCapacity;
        PartitionCapacity = partitionCapacity;
        IndexNodeCount = indexNodeCount;
        IndexHomeCount = indexHomeCount;
        Candidate = candidate;
        _structureHash = structureHash.ToArray();
        _policyHash = policyHash.ToArray();
    }

    public uint TableCount { get; }
    public uint FixedCount { get; }
    public uint ResourceCapacity { get; }
    public uint PartitionCapacity { get; }
    public uint IndexNodeCount { get; }
    public uint IndexHomeCount { get; }
    public LedgerCandidate Candidate { get; }
    public ReadOnlySpan<byte> StructureHash => _structureHash;
    public ReadOnlySpan<byte> PolicyHash => _policyHash;
}
