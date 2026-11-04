namespace ResourceManager.ReadWrite.Vendors.Intel;

public enum IntelGraphicsControlProbe { DriverMissing, TargetMissing, Ready, NotSupported }
public readonly record struct IntelGraphicsControlProbeResult(IntelGraphicsControlProbe Status, int? ReleaseErrorCode = null);
public enum IntelGraphicsControlUnreadable { DriverMissing, TargetMissing, ReadingMissing }
public readonly record struct IntelGraphicsControlRead(double? OffsetMhz, IntelGraphicsControlUnreadable? Unreadable = null,
    int? ReleaseErrorCode = null);
public enum IntelGraphicsControlWriteFailure { DriverMissing, TargetMissing, WaiverRejected, DriverRejected }
public readonly record struct IntelGraphicsControlWrite(int DriverResult, double? ReadbackMhz,
    IntelGraphicsControlWriteFailure? Failure = null, int? ReleaseErrorCode = null);

/// <summary>
/// Each hardware operation owns and closes its API session; adapter handles never escape a call.
/// Completed operations carry release errors separately from their work results.
/// When work throws, the original exception retains any release exception in Data under
/// "IntelGclReleaseFailure"; callers must log it without replacing the original exception.
/// Cancellation is checked before set; after set the receipt is completed.
/// </summary>
public interface IIntelGraphicsControlHardware
{
    /// <summary>Checks the DLL and base exports only; never opens an API session.</summary>
    bool IsDriverAvailable { get; }
    IntelGraphicsControlProbeResult Probe(ulong? adapterKey, CancellationToken token = default);
    IntelGraphicsControlRead ReadFrequencyOffsetMhz(ulong? adapterKey, CancellationToken token = default);
    /// <summary>Call only after explicit consent. Includes waiver, set and readback.</summary>
    IntelGraphicsControlWrite WriteFrequencyOffsetMhz(ulong? adapterKey, double offsetMhz, CancellationToken token = default);
}
