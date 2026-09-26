using System.Runtime.InteropServices;
using ResourceManager.App.Infrastructure.Monitoring;

namespace ResourceManager.App.Infrastructure.NativeCore;

internal static class NativeCoreAbi
{
    public const uint Version = 0x0001_0000;
    public const int MaximumFrameRows = 262_144;
}

internal enum NativePdhResultCode
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

internal enum NativePdhProviderAvailability
{
    NotRequested = 0,
    Available = 1,
    WarmingUp = 2,
    LastGood = 3,
    Unavailable = 4
}

[Flags]
internal enum NativePdhFrameFlags : uint
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

internal enum NativePdhEngineClass : uint
{
    Other = 0,
    RayTracing = 1,
    Compute = 2
}

[Flags]
internal enum NativePdhIoValidMask : uint
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
internal struct NativePdhConfig
{
    public uint AbiVersion;
    public uint StructSize;
    public uint BaselineResetIntervalMilliseconds;
    public uint Reserved;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativePdhFrameHeader
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
internal struct NativePdhGpuEngineRow
{
    public ulong AdapterLuid;
    public uint ProcessId;
    public NativePdhEngineClass EngineClass;
    public double UsagePercent;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativePdhGpuMemoryRow
{
    public ulong AdapterLuid;
    public uint ProcessId;
    public uint Reserved;
    public double DedicatedBytes;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativePdhSystemIo
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
internal struct NativePdhDiagnostics
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

internal readonly record struct NativePdhDatasetObservation(
    NativePdhProviderAvailability Availability,
    DateTimeOffset? ObservedAt,
    ulong Generation)
{
    internal static NativePdhDatasetObservation Current(
        DateTimeOffset observedAt,
        ulong generation)
        => new(
            NativePdhProviderAvailability.Available,
            observedAt,
            generation);

    internal static NativePdhDatasetObservation LastGood(
        DateTimeOffset observedAt,
        ulong generation)
        => new(
            NativePdhProviderAvailability.LastGood,
            observedAt,
            generation);

    internal static NativePdhDatasetObservation Unavailable { get; } = new(
        NativePdhProviderAvailability.Unavailable,
        null,
        0);
}

internal sealed record NativePdhSnapshot
{
    internal NativePdhSnapshot(
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

    internal NativePdhSnapshot(
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

    internal DateTimeOffset CapturedAt { get; }

    internal ulong Sequence { get; }

    internal uint TopologyGeneration { get; }

    internal IReadOnlyList<NativePdhGpuEngineRow> EngineRows { get; }

    internal IReadOnlyList<NativePdhGpuMemoryRow> MemoryRows { get; }

    internal NativePdhSystemIo SystemIo { get; }

    internal NativePdhFrameFlags FrameFlags { get; }

    internal NativePdhDatasetObservation GpuEngineObservation { get; }

    internal NativePdhDatasetObservation GpuMemoryObservation { get; }

    internal NativePdhDatasetObservation DiskObservation { get; }

    internal NativePdhDatasetObservation NetworkObservation { get; }
}

internal readonly record struct NativePdhReadResult(
    NativePdhSnapshot? Snapshot,
    NativePdhProviderAvailability Availability,
    NativePdhResultCode ResultCode,
    int NativeStatus);

internal interface INativePdhSnapshotSource
{
    NativePdhReadResult Read(ulong? adapterTopologyFingerprint = null);
}

internal sealed class UnavailableNativePdhSnapshotSource : INativePdhSnapshotSource
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

internal static class NativePdhAdapterIdentity
{
    private const ulong FnvOffsetBasis = 0xcbf29ce484222325;
    private const ulong FnvPrime = 0x100000001b3;

    public static ulong Pack(AdapterLuid luid)
    {
        return ((ulong)(uint)luid.HighPart << 32) | luid.LowPart;
    }

    public static ulong ComputeTopologyFingerprint(IReadOnlyList<WindowsGpuAdapter> adapters)
    {
        var hash = FnvOffsetBasis;
        foreach (var adapter in adapters
            .Where(static adapter => !adapter.IsSoftware)
            .OrderBy(static adapter => Pack(adapter.Luid)))
        {
            var luid = Pack(adapter.Luid);
            Add(ref hash, (uint)(luid >> 32));
            Add(ref hash, (uint)luid);
            Add(ref hash, adapter.VendorId);
            Add(ref hash, adapter.DeviceId);
            Add(ref hash, adapter.SubSystemId);
            Add(ref hash, (uint)adapter.Kind);
            var identities = adapter.PhysicalIdentities
                .OrderBy(
                    static identity => identity.PnpInstanceId,
                    StringComparer.OrdinalIgnoreCase)
                .ThenBy(static identity => identity.PhysicalAdapterIndex)
                .ToArray();
            Add(ref hash, checked((uint)identities.Length));
            foreach (var identity in identities)
            {
                Add(ref hash, identity.PnpInstanceId);
                Add(ref hash, identity.VendorId);
                Add(ref hash, identity.DeviceId);
                Add(ref hash, identity.SubVendorId);
                Add(ref hash, identity.SubsystemId);
                Add(ref hash, identity.RevisionId);
            }
        }

        return hash;
    }

    private static void Add(ref ulong hash, uint value)
    {
        for (var offset = 0; offset < sizeof(uint); offset++)
        {
            hash ^= (byte)(value >> (offset * 8));
            hash *= FnvPrime;
        }
    }

    private static void Add(ref ulong hash, string value)
    {
        Add(ref hash, checked((uint)value.Length));
        foreach (var character in value)
        {
            var normalized = char.ToUpperInvariant(character);
            hash ^= (byte)normalized;
            hash *= FnvPrime;
            hash ^= (byte)(normalized >> 8);
            hash *= FnvPrime;
        }
    }

}
