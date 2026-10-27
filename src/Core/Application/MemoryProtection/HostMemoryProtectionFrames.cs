namespace ResourceManager.App.Application.MemoryProtection;

// ReadVolatility: Sampled. Records and the normal running point have their own event-driven frames.
public sealed record HostMemoryProtectionLiveStatus(bool Enabled, double ThresholdPercent, double CheckIntervalSeconds,
    HostMemoryReserveStatus Reserve, decimal? TriggerBytes, decimal? RemainingBytes, HostMemoryCommit? CurrentCommit,
    string? InactiveReason, IReadOnlyList<HostMemoryExclusion> ExcludedCandidates, string ActionMode, bool Active,
    bool? AtOrAboveThreshold, string? CurrentActionId, long MinimumProcessGrowthBytes,
    IReadOnlyList<string> ProtectedProcesses, string? DecisionReason)
{
    public static HostMemoryProtectionLiveStatus From(HostMemoryProtectionStatus status)
        => new(status.Enabled, status.ThresholdPercent, status.CheckIntervalSeconds, status.Reserve,
            status.TriggerBytes, status.RemainingBytes, status.CurrentCommit, status.InactiveReason,
            status.ExcludedCandidates, status.ActionMode, status.Active, status.AtOrAboveThreshold,
            status.CurrentActionId, status.MinimumProcessGrowthBytes, status.ProtectedProcesses, status.DecisionReason);
}

public sealed record HostMemoryProtectionStatusFrame(HostMemoryProtectionLiveStatus Status)
{
    public string Type => "status";
}

// ReadVolatility: EventDriven, change event: action. Replay is newest first, matching GET.
public sealed record HostMemoryProtectionRecordsFrame(IReadOnlyList<HostMemoryActionRecord> Records, int Limit)
{
    public string Type => "records";
}
public sealed record HostMemoryProtectionRecordFrame(HostMemoryActionRecord Record)
{
    public string Type => "record";
}

