namespace ResourceManager.App.Domain.Optimization;

public static class OptimizationResourceKinds
{
    public const string Cpu = "CPU";
    public const string Gpu = "GPU";
    public const string Memory = "Memory";
    public const string Vram = "VRAM";
    public const string Disk = "Disk";
    public const string DiskWrite = "DiskWrite";
    public const string Network = "Network";
    public const string NetworkActivity = "NetworkActivity";
    public const string SoftwareFootprint = "SoftwareFootprint";
    public const string PowerProfile = "PowerProfile";
    public const string DeviceDriver = "DeviceDriver";
    public const string DiskHealth = "DiskHealth";
    public const string DiskLink = "DiskLink";
    public const string DiskLatency = "DiskLatency";
    public const string DiskPower = "DiskPower";
    public const string CpuThermal = "CpuThermal";
    public const string SystemInterrupt = "SystemInterrupt";
}

public sealed record OptimizationReportEvidence(
    string ResourceKind,
    double AverageValue,
    double PeakValue,
    double CurrentValue,
    string AverageDisplay,
    string PeakDisplay,
    string CurrentDisplay,
    int ActiveSampleCount,
    int SampleCount,
    double DurationSeconds,
    IReadOnlyList<string> Details);
