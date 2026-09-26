namespace ResourceManager.App.Domain.Optimization;

public static class OptimizationReportTargetTypes
{
    public const string Software = "Software";
    public const string Drive = "Drive";
    public const string System = "System";
    public const string Device = "Device";
    public const string Display = "Display";
    public const string PhysicalDisk = "PhysicalDisk";
    public const string Volume = "Volume";
    public const string CpuPackage = "CpuPackage";
}

public sealed record OptimizationReportTarget(
    string TargetType,
    string TargetKey,
    string DisplayName,
    string? SoftwareId,
    string? SoftwareName,
    string? SoftwareKind,
    string? DisplayKind,
    IReadOnlyList<string> ProcessNames,
    IReadOnlyList<int> ProcessIds,
    string? DriveLetter);
