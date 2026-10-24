namespace ResourceManager.App.Domain.Optimization;

public static class OptimizationReportTargetTypes
{
    public const string ProcessInstance = "process_instance";
    public const string Software = "software";
    public const string Drive = "drive";
    public const string System = "system";
    public const string Device = "device";
    public const string Display = "display";
    public const string PhysicalDisk = "physical_disk";
    public const string Volume = "volume";
    public const string CpuPackage = "cpu_package";
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
