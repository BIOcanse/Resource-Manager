namespace ResourceManager.ReadWrite.Vendors.Nvidia;

public sealed record NvidiaGpuVfCurvePoint(int PointId, uint VoltageMicrovolts,
    long DefaultFrequencyKhz, long CurrentFrequencyKhz, int FrequencyOffsetKhz,
    int? MinimumOffsetKhz, int? MaximumOffsetKhz);

public sealed record NvidiaGpuVfCurveSnapshot(string DeviceIdentity, string Abi,
    IReadOnlyList<NvidiaGpuVfCurvePoint> Points, bool CanWrite, string? UnavailableReason,
    int ScalarOffsetKhz);

public sealed record NvidiaGpuVfCurveReadResult(NvidiaGpuVfCurveSnapshot? Snapshot,
    string? UnavailableReason = null, int? NativeStatus = null, bool IsUnsupported = false);

public sealed record NvidiaGpuVfCurveOffset(int PointId, uint VoltageMicrovolts, int FrequencyOffsetKhz);

/// <param name="ReadbackToleranceKhz">Supplied by the application's freedom point; never inferred from ABI units.</param>
public sealed record NvidiaGpuVfCurveTransactionOptions(int ReadbackToleranceKhz);

public sealed record NvidiaGpuVfCurveTransactionResult(bool Applied, string? FailureReason,
    bool RestorationAttempted = false, bool RestorationSucceeded = false,
    NvidiaGpuVfCurveSnapshot? Readback = null);

/// <summary>Explicit reads only. Implementing this interface cannot grant hardware-write authority.</summary>
public interface INvidiaGpuVfCurveAdapter
{
    NvidiaGpuVfCurveReadResult Read(NvidiaControlObject target);
}

public static class NvidiaGpuVfCurveAbis
{
    public const string Modern = "nvidia-vf-status-v3";
    public const string Legacy = "nvidia-vf-legacy-v1";
    public const string Scalar = "nvidia-vf-scalar";
    public const string LiveWriteUnavailable = "nvidia-vf-restore-protocol-unverified";
}
