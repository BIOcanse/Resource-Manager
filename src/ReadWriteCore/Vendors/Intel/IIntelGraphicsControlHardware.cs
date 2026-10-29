namespace ResourceManager.ReadWrite.Vendors.Intel;

public enum IntelGraphicsControlProbe { DriverMissing, TargetMissing, Ready }
public enum IntelGraphicsControlUnreadable { DriverMissing, TargetMissing, ReadingMissing }
public readonly record struct IntelGraphicsControlRead(double? OffsetMhz, IntelGraphicsControlUnreadable? Unreadable = null);
public enum IntelGraphicsControlWriteFailure { DriverMissing, TargetMissing, WaiverRejected, DriverRejected }
public readonly record struct IntelGraphicsControlWrite(int DriverResult, double? ReadbackMhz,
    IntelGraphicsControlWriteFailure? Failure = null, int? ReleaseErrorCode = null);

/// <summary>
/// Each call owns and closes its API session; adapter handles never escape a call.
/// Probe/read throw ExternalException on failed release. Completed writes carry the release
/// error separately from the work result; callers must report it without discarding an applied setting.
/// Cancellation is checked before set; after set the receipt is completed.
/// </summary>
public interface IIntelGraphicsControlHardware
{
    IntelGraphicsControlProbe Probe(ulong? adapterKey, CancellationToken token = default);
    IntelGraphicsControlRead ReadFrequencyOffsetMhz(ulong? adapterKey, CancellationToken token = default);
    /// <summary>Call only after explicit consent. Includes waiver, set and readback.</summary>
    IntelGraphicsControlWrite WriteFrequencyOffsetMhz(ulong? adapterKey, double offsetMhz, CancellationToken token = default);
}
