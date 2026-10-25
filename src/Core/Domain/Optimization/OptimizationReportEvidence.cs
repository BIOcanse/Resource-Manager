namespace ResourceManager.App.Domain.Optimization;

public static class OptimizationResourceKinds
{
    public const string Cpu = "cpu";
    public const string Gpu = "gpu";
    public const string Memory = "memory";
    public const string Vram = "vram";
    public const string Disk = "disk";
    public const string DiskWrite = "disk_write";
    public const string Network = "network";
    public const string NetworkActivity = "network_activity";
    public const string SoftwareFootprint = "software_footprint";
    public const string PowerProfile = "power_profile";
    public const string DeviceDriver = "device_driver";
    public const string DiskHealth = "disk_health";
    public const string DiskLink = "disk_link";
    public const string DiskLatency = "disk_latency";
    public const string DiskPower = "disk_power";
    public const string CpuThermal = "cpu_thermal";
    public const string SystemInterrupt = "system_interrupt";
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
    double DurationSeconds)
{
    public OptimizationProcessEvidence? Process { get; init; }
}

public sealed record OptimizationProcessEvidence(
    int ProcessId,
    string ProcessStartKey,
    string Name,
    double? MemoryBytes,
    double? IdleDurationSeconds,
    string? CommandLine);
