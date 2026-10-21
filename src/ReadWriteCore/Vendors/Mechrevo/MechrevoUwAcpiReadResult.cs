namespace ResourceManager.ReadWrite.Vendors.Mechrevo;

/// <summary>A session whose library remains loaded until Dispose. The reader serializes calls and release.</summary>
public interface IUwAcpiReadSession : IDisposable
{
    int ReadEc(int address);
}

public enum MechrevoUwAcpiReadStatus
{
    LibraryUnavailable,
    NoData,
    Active,
    ReadFailed
}

public sealed record MechrevoUwAcpiReadResult(
    MechrevoUwAcpiReadStatus Status,
    double? CpuFanSpeedRpm,
    double? CpuFanSpeedPercent,
    double? GpuFanSpeedRpm,
    double? GpuFanSpeedPercent,
    string? Error = null);
