using System.Runtime.InteropServices;

namespace ResourceManager.ReadWrite.Native.Pdh;

public static class NativeCoreAbi
{
    public const uint Version = 0x0002_0000;
    public const int MaximumFrameRows = 262_144;
}

public enum NativePdhResultCode
{
    Ok = 0,
    InvalidArgument = 1,
    AbiMismatch = 2,
    Unavailable = 3,
    NoData = 4,
    BufferTooSmall = 5,
    StaleFrame = 6,
    OutOfMemory = 7,
    PdhError = 8
}

public enum NativePdhProviderAvailability
{
    NotRequested = 0,
    Available = 1,
    WarmingUp = 2,
    LastGood = 3,
    Unavailable = 4
}

[Flags]
public enum NativePdhFrameFlags : uint
{
    None = 0,
    HasData = 1 << 0,
    TopologyRebuilt = 1 << 1,
    BaselineCollected = 1 << 2,
    Partial = 1 << 3,
    DiskComplete = 1 << 4,
    NetworkComplete = 1 << 5,
    GpuEngineComplete = 1 << 6,
    GpuMemoryComplete = 1 << 7,
    AllDomainsComplete = DiskComplete
        | NetworkComplete
        | GpuEngineComplete
        | GpuMemoryComplete
}

public enum NativePdhEngineClass : uint
{
    Other = 0,
    RayTracing = 1,
    Compute = 2
}

[Flags]
public enum NativePdhIoValidMask : uint
{
    None = 0,
    DiskActive = 1 << 0,
    DiskRead = 1 << 1,
    DiskWrite = 1 << 2,
    DiskQueue = 1 << 3,
    NetworkReceive = 1 << 4,
    NetworkSend = 1 << 5,
    NetworkUtilization = 1 << 6,
    NetworkBandwidth = 1 << 7
}

[StructLayout(LayoutKind.Sequential)]
public struct NativePdhConfig
{
    public uint AbiVersion;
    public uint StructSize;
    public uint BaselineResetIntervalMilliseconds;
    public uint Reserved;
}

[StructLayout(LayoutKind.Sequential)]
public struct NativePdhFrameHeader
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong Sequence;
    public ulong CapturedTickMilliseconds;
    public uint Flags;
    public int NativeStatus;
    public uint EngineRowCount;
    public uint MemoryRowCount;
    public uint TopologyGeneration;
    public uint Reserved;
}

[StructLayout(LayoutKind.Sequential)]
public struct NativePdhGpuEngineRow
{
    public ulong AdapterLuid;
    public uint ProcessId;
    public NativePdhEngineClass EngineClass;
    public uint PhysicalIndex;
    public uint EngineIndex;
    public double UsagePercent;
}

/// <summary>Optional sidecar for the same native query and frame; never a managed delta baseline.
/// FILETIME endpoints are accepted only after confirmation against the actual UTC collection bounds.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct NativePdhGpuEngineWindow
{
    public ulong AdapterLuid;
    public uint ProcessId;
    public uint PhysicalIndex;
    public uint EngineIndex;
    public uint Reserved;
    public long BeginFileTime;
    public long EndFileTime;
    public long BeginCounter;
    public long EndCounter;
    public long CounterFrequency;
}

[StructLayout(LayoutKind.Sequential)]
public struct NativePdhGpuMemoryRow
{
    public ulong AdapterLuid;
    public uint ProcessId;
    public uint Reserved;
    public double DedicatedBytes;
}

[StructLayout(LayoutKind.Sequential)]
public struct NativePdhSystemIo
{
    public uint StructSize;
    public NativePdhIoValidMask ValidMask;
    public double DiskActivePercent;
    public double DiskReadBytesPerSecond;
    public double DiskWriteBytesPerSecond;
    public double DiskQueueLength;
    public double NetworkReceiveBytesPerSecond;
    public double NetworkSendBytesPerSecond;
    public double NetworkUtilizationPercent;
    public double NetworkBandwidthBitsPerSecond;
}

[StructLayout(LayoutKind.Sequential)]
public struct NativePdhDiagnostics
{
    public uint AbiVersion;
    public uint StructSize;
    public uint TopologyGeneration;
    public uint CounterCount;
    public uint ValidValueCount;
    public uint EngineRowCount;
    public uint MemoryRowCount;
    public uint Reserved;
    public ulong CollectDurationNanoseconds;
    public ulong ReadDurationNanoseconds;
    public ulong FrameDurationNanoseconds;
}

public readonly record struct NativePdhDatasetObservation(
    NativePdhProviderAvailability Availability,
    DateTimeOffset? ObservedAt,
    ulong Generation)
{
    public static NativePdhDatasetObservation Current(
        DateTimeOffset observedAt,
        ulong generation)
        => new(
            NativePdhProviderAvailability.Available,
            observedAt,
            generation);

    public static NativePdhDatasetObservation LastGood(
        DateTimeOffset observedAt,
        ulong generation)
        => new(
            NativePdhProviderAvailability.LastGood,
            observedAt,
            generation);

    public static NativePdhDatasetObservation Unavailable { get; } = new(
        NativePdhProviderAvailability.Unavailable,
        null,
        0);
}

public sealed record NativePdhSnapshot
{
    public IReadOnlyList<NativePdhGpuEngineWindow> EngineWindows { get; init; } = [];
    public NativePdhSnapshot(
        DateTimeOffset capturedAt,
        ulong sequence,
        uint topologyGeneration,
        IReadOnlyList<NativePdhGpuEngineRow> engineRows,
        IReadOnlyList<NativePdhGpuMemoryRow> memoryRows,
        NativePdhSystemIo systemIo)
        : this(
            capturedAt,
            sequence,
            topologyGeneration,
            engineRows,
            memoryRows,
            systemIo,
            NativePdhFrameFlags.HasData
                | NativePdhFrameFlags.AllDomainsComplete,
            NativePdhDatasetObservation.Current(capturedAt, sequence),
            NativePdhDatasetObservation.Current(capturedAt, sequence),
            NativePdhDatasetObservation.Current(capturedAt, sequence),
            NativePdhDatasetObservation.Current(capturedAt, sequence))
    {
    }

    public NativePdhSnapshot(
        DateTimeOffset capturedAt,
        ulong sequence,
        uint topologyGeneration,
        IReadOnlyList<NativePdhGpuEngineRow> engineRows,
        IReadOnlyList<NativePdhGpuMemoryRow> memoryRows,
        NativePdhSystemIo systemIo,
        NativePdhFrameFlags frameFlags,
        NativePdhDatasetObservation gpuEngineObservation,
        NativePdhDatasetObservation gpuMemoryObservation,
        NativePdhDatasetObservation diskObservation,
        NativePdhDatasetObservation networkObservation)
    {
        CapturedAt = capturedAt;
        Sequence = sequence;
        TopologyGeneration = topologyGeneration;
        EngineRows = engineRows;
        MemoryRows = memoryRows;
        SystemIo = systemIo;
        FrameFlags = frameFlags;
        GpuEngineObservation = gpuEngineObservation;
        GpuMemoryObservation = gpuMemoryObservation;
        DiskObservation = diskObservation;
        NetworkObservation = networkObservation;
    }

    public DateTimeOffset CapturedAt { get; }

    public ulong Sequence { get; }

    public uint TopologyGeneration { get; }

    public IReadOnlyList<NativePdhGpuEngineRow> EngineRows { get; }

    public IReadOnlyList<NativePdhGpuMemoryRow> MemoryRows { get; }

    public NativePdhSystemIo SystemIo { get; }

    public NativePdhFrameFlags FrameFlags { get; }

    public NativePdhDatasetObservation GpuEngineObservation { get; }

    public NativePdhDatasetObservation GpuMemoryObservation { get; }

    public NativePdhDatasetObservation DiskObservation { get; }

    public NativePdhDatasetObservation NetworkObservation { get; }
}

public readonly record struct NativePdhReadResult(
    NativePdhSnapshot? Snapshot,
    NativePdhProviderAvailability Availability,
    NativePdhResultCode ResultCode,
    int NativeStatus);

public interface INativePdhSnapshotSource
{
    NativePdhReadResult Read(ulong? adapterTopologyFingerprint = null);
}

public sealed class UnavailableNativePdhSnapshotSource : INativePdhSnapshotSource
{
    public static UnavailableNativePdhSnapshotSource Instance { get; } = new();

    private UnavailableNativePdhSnapshotSource()
    {
    }

    public NativePdhReadResult Read(ulong? adapterTopologyFingerprint = null)
    {
        return new NativePdhReadResult(
            null,
            NativePdhProviderAvailability.Unavailable,
            NativePdhResultCode.Unavailable,
            0);
    }
}

