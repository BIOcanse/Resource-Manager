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

public static class OptimizationValueUnits
{
    /// <summary>百分比。</summary>
    public const string Percent = "%";

    /// <summary>原始字节。换算与单位标签由前端按用户选的进制给出。</summary>
    public const string Bytes = "B";

    public const string Celsius = "°C";

    public const string Milliseconds = "ms";

    /// <summary>次数，没有量纲。</summary>
    public const string Count = "count";
}

/// <summary>
/// 报告证据只带事实：数值和它的单位标记。
/// 怎么写成一句话、用哪个进制、显示成什么单位，全部由前端决定。
/// </summary>
public sealed record OptimizationReportEvidence(
    string ResourceKind,
    double AverageValue,
    double PeakValue,
    double CurrentValue,
    string ValueUnit,
    int ActiveSampleCount,
    int SampleCount,
    double DurationSeconds,
    IReadOnlyList<string> Details);
