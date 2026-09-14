namespace ResourceManager.App.Application.Optimization;

[Flags]
public enum ProcessResourcePolicyBatchFields
{
    None = 0,
    PriorityClass = 1 << 0,
    AffinityMask = 1 << 1,
    MemoryPriority = 1 << 2,
    PowerThrottling = 1 << 3,
    TrimWorkingSet = 1 << 4,
    CpuSets = 1 << 5
}

public sealed record ProcessResourcePolicyBatchRequest(
    int ProcessId,
    DateTimeOffset? ExpectedStartedAt,
    string? PriorityClass = null,
    long? AffinityMask = null,
    uint? MemoryPriority = null,
    uint? PowerControlMask = null,
    uint? PowerStateMask = null,
    bool TrimWorkingSet = false,
    IReadOnlyList<uint>? CpuSetIds = null,
    uint? ExpectedMemoryPriority = null)
{
    public ProcessResourcePolicyBatchRequest(
        int ProcessId,
        DateTimeOffset? ExpectedStartedAt,
        string? PriorityClass,
        long? AffinityMask,
        uint? MemoryPriority,
        uint? PowerControlMask,
        uint? PowerStateMask,
        bool TrimWorkingSet,
        IReadOnlyList<uint>? CpuSetIds)
        : this(
            ProcessId,
            ExpectedStartedAt,
            PriorityClass,
            AffinityMask,
            MemoryPriority,
            PowerControlMask,
            PowerStateMask,
            TrimWorkingSet,
            CpuSetIds,
            ExpectedMemoryPriority: null)
    {
    }

    public void Deconstruct(
        out int ProcessId,
        out DateTimeOffset? ExpectedStartedAt,
        out string? PriorityClass,
        out long? AffinityMask,
        out uint? MemoryPriority,
        out uint? PowerControlMask,
        out uint? PowerStateMask,
        out bool TrimWorkingSet,
        out IReadOnlyList<uint>? CpuSetIds)
    {
        ProcessId = this.ProcessId;
        ExpectedStartedAt = this.ExpectedStartedAt;
        PriorityClass = this.PriorityClass;
        AffinityMask = this.AffinityMask;
        MemoryPriority = this.MemoryPriority;
        PowerControlMask = this.PowerControlMask;
        PowerStateMask = this.PowerStateMask;
        TrimWorkingSet = this.TrimWorkingSet;
        CpuSetIds = this.CpuSetIds;
    }
}

public enum ProcessResourcePolicyBatchFieldStatus
{
    Failed = 0,
    Succeeded = 1,
    Conflict = 2
}

public sealed record ProcessResourcePolicyBatchFieldResult(
    ProcessResourcePolicyBatchFields Field,
    bool Succeeded,
    string Message)
{
    public ProcessResourcePolicyBatchFieldResult(
        ProcessResourcePolicyBatchFields Field,
        ProcessResourcePolicyBatchFieldStatus Status,
        string Message)
        : this(
            Field,
            Status == ProcessResourcePolicyBatchFieldStatus.Succeeded,
            Message)
    {
        this.Status = Status;
    }

    public ProcessResourcePolicyBatchFieldStatus Status { get; private init; } =
        Succeeded
            ? ProcessResourcePolicyBatchFieldStatus.Succeeded
            : ProcessResourcePolicyBatchFieldStatus.Failed;

    public bool Conflict => Status == ProcessResourcePolicyBatchFieldStatus.Conflict;

    public uint ErrorCode { get; init; }
}

public sealed record ProcessResourcePolicyBatchWriteResult(
    int ProcessId,
    IReadOnlyList<ProcessResourcePolicyBatchFieldResult> Fields)
{
    public ProcessResourcePolicyBatchFieldResult? Find(ProcessResourcePolicyBatchFields field)
    {
        for (var index = 0; index < Fields.Count; index++)
        {
            if (Fields[index].Field == field)
            {
                return Fields[index];
            }
        }

        return null;
    }
}
