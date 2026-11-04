namespace ResourceManager.App.Application.CpuMonopolyProtection;

// Sampled status deliberately omits persisted records. Discrete changes use their own frames.
public sealed record CpuMonopolyProtectionLiveStatus(bool Enabled, bool Active, string? InactiveReason,
    double ThresholdPercent, double SustainedSeconds, double CheckIntervalSeconds, string? CurrentActionId,
    IReadOnlyList<CpuMonopolyExclusion> ExcludedCandidates)
{
    public static CpuMonopolyProtectionLiveStatus From(CpuMonopolyProtectionStatus status)
        => new(status.Enabled, status.Active, status.InactiveReason, status.ThresholdPercent, status.SustainedSeconds,
            status.CheckIntervalSeconds, status.CurrentActionId, status.ExcludedCandidates);
}
public sealed record CpuMonopolyProtectionStatusFrame(CpuMonopolyProtectionLiveStatus Status)
{
    public string Type => "status";
}
public sealed record CpuMonopolyProtectionRecordsFrame(IReadOnlyList<CpuMonopolyActionRecord> Records,
    int Limit, int UnrecordedRecordCount)
{
    public string Type => "records";
}
public sealed record CpuMonopolyProtectionRecordFrame(CpuMonopolyActionRecord Record)
{
    public string Type => "record";
}
public sealed record CpuMonopolyProtectionEventFrame(CpuMonopolyProtectionEvent Event)
{
    public string Type => "event";
}
