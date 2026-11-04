namespace ResourceManager.App.Infrastructure.ResourceBreakdown;

/// <summary>Identifies one process creation using its PID and Windows creation FILETIME in 100ns units.</summary>
public readonly record struct ProcessInstanceKey(int ProcessId, long StartKey);
