namespace ResourceManager.ReadWrite.Windows.Processes;

/// <summary>ETW's reported OS disk domain. This is not a Storage DeviceId, PnP identity or medium identity.</summary>
public sealed record PhysicalDiskIoScope(long SessionGeneration, uint DiskNumber);

public sealed record PhysicalDiskProcessIoObservation(PhysicalDiskIoScope Disk, int ProcessId,
    string? CreationWindowsFileTime100Ns, string? ReadBytes, string? WriteBytes);

/// <summary>Completed DiskIO transfer sizes delivered in one uninterrupted two-second window.
/// Completion does not establish media persistence. Missing process identity is unavailable, not zero.</summary>
public sealed record PhysicalDiskProcessIoWindow(DateTimeOffset CapturedAt, long WindowMilliseconds,
    IReadOnlyList<PhysicalDiskProcessIoObservation> Processes)
{
    public bool Complete { get; init; } = true;
    public IReadOnlyList<PhysicalDiskIoScope> Disks { get; init; } = [];
}
