namespace ResourceManager.ReadWrite.Windows.Devices.Models;

/// <summary>OS page-file usage, separate from commit charge and physical residency.</summary>
public sealed record DeviceTopologyPageFileInformation(string DeviceIdentity)
{
    public string? PathReported { get; init; }
    public ulong? CapacityPages { get; init; }
    public ulong? UsedPages { get; init; }
    public ulong? PeakUsedPages { get; init; }
    public uint? PageSizeBytes { get; init; }
}
