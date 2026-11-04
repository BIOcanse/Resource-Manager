using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ResourceManager.ReadWrite.Native;

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct NativeMetricCurrentKey : IEquatable<NativeMetricCurrentKey>
{
    public ulong DatasetHandle, CellHandle;

    public readonly bool Equals(NativeMetricCurrentKey other)
        => DatasetHandle == other.DatasetHandle && CellHandle == other.CellHandle;
    public override readonly bool Equals(object? obj) => obj is NativeMetricCurrentKey other && Equals(other);
    public override readonly int GetHashCode() => HashCode.Combine(DatasetHandle, CellHandle);
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct NativeMetricCurrentValue
{
    public ulong DatasetHandle, CellHandle, ValueBits, ValueHighBits;
    public uint ValueKind, Flags;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct NativeMetricCurrentCommitInput
{
    public uint AbiVersion, StructSize, CellCapacity, ByteCapacity, Flags, Reserved;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct NativeMetricCurrentReadOutput
{
    public uint StructSize, CellCount, ByteCount, Reserved;
}

public sealed record NativeMetricCurrentFrame(NativeMetricCurrentValue[] Values, byte[] Bytes);

public sealed partial class NativeMetricSnapshotSession
{
    private CurrentStorage? currentStorage;
    private readonly List<CurrentStorage> retiredCurrentStorage = [];

    public static NativeMetricSnapshotSession CreateCurrent(in NativeMetricSnapshotConfiguration configuration)
        => CreateCurrent(in configuration, NativeMetricSnapshotScratch.CreateOrdinary);

    internal static NativeMetricSnapshotSession CreateCurrent(in NativeMetricSnapshotConfiguration configuration,
        Func<ulong, uint, INativeMetricSnapshotScratch> createScratch)
    {
        // Current publication has its own budgeted table. It does not need a
        // second set of collector maximum-sized workspaces and indexes.
        var current = configuration;
        current.MaximumSourceCount = current.MaximumMetricCount = current.MaximumRuleCount = 1;
        current.MaximumRequestedCount = current.MaximumObservationCount = current.MaximumGpuAdapterCount = 1;
        current.MaximumPersistenceSourceCount = current.MaximumPersistenceRuleCount = current.MaximumPersistenceGpuCount = 1;
        current.MaximumPlanMetricCount = current.MaximumSourceModeCount = current.MaximumSourcePlanCount = current.MaximumMetricPlanCount = 1;
        current.SourceIndexCapacity = current.MetricIndexCapacity = current.RuleIndexCapacity = 2;
        current.GpuIndexCapacity = current.GpuLuidIndexCapacity = current.GpuKeyIndexCapacity = 2;
        return new NativeMetricSnapshotSession(in current, createScratch);
    }

    public unsafe NativeMetricCurrentFrame ReadCurrent()
    {
        lock (sync)
        {
            var status = (NativeMetricSnapshotStatus)NativeMethods.ReadCurrent(RequireHandle(),
                NativeMetricSnapshotAbi.Version, out var output, null, 0, null, 0);
            if (status is not (NativeMetricSnapshotStatus.Ok or NativeMetricSnapshotStatus.BufferTooSmall))
                throw new InvalidOperationException($"Reading native current failed: {status}.");
            var values = new NativeMetricCurrentValue[checked((int)output.CellCount)];
            var bytes = new byte[checked((int)output.ByteCount)];
            fixed (NativeMetricCurrentValue* vp = values)
            fixed (byte* bp = bytes)
                status = (NativeMetricSnapshotStatus)NativeMethods.ReadCurrent(RequireHandle(),
                    NativeMetricSnapshotAbi.Version, out output, vp, (uint)values.Length, bp, (uint)bytes.Length);
            if (status != NativeMetricSnapshotStatus.Ok)
                throw new InvalidOperationException($"Reading native current failed: {status}.");
            return new(values, bytes);
        }
    }

    /// <summary>Reads found cells in selection order, with a compact byte payload.</summary>
    public unsafe NativeMetricCurrentFrame ReadCurrent(ReadOnlySpan<NativeMetricCurrentKey> selection)
    {
        lock (sync)
        {
            NativeMetricSnapshotStatus status;
            NativeMetricCurrentReadOutput output;
            fixed (NativeMetricCurrentKey* kp = selection)
                status = (NativeMetricSnapshotStatus)NativeMethods.ReadCurrentSelected(RequireHandle(),
                    NativeMetricSnapshotAbi.Version, kp, (uint)selection.Length, out output, null, 0, null, 0);
            if (status is not (NativeMetricSnapshotStatus.Ok or NativeMetricSnapshotStatus.BufferTooSmall))
                throw new InvalidOperationException($"Reading selected native current failed: {status}.");
            var values = new NativeMetricCurrentValue[checked((int)output.CellCount)];
            var bytes = new byte[checked((int)output.ByteCount)];
            fixed (NativeMetricCurrentKey* kp = selection)
            fixed (NativeMetricCurrentValue* vp = values)
            fixed (byte* bp = bytes)
                status = (NativeMetricSnapshotStatus)NativeMethods.ReadCurrentSelected(RequireHandle(),
                    NativeMetricSnapshotAbi.Version, kp, (uint)selection.Length, out output,
                    vp, (uint)values.Length, bp, (uint)bytes.Length);
            if (status != NativeMetricSnapshotStatus.Ok)
                throw new InvalidOperationException($"Reading selected native current failed: {status}.");
            return new(values, bytes);
        }
    }

    public void CommitCurrent(ReadOnlySpan<ulong> requested, ReadOnlySpan<ulong> retired,
        ReadOnlySpan<NativeMetricCurrentValue> values, ReadOnlySpan<byte> bytes)
        => CommitCurrent(requested, retired, values, bytes, onCommitted: null);

    internal unsafe void CommitCurrent(ReadOnlySpan<ulong> requested, ReadOnlySpan<ulong> retired,
        ReadOnlySpan<NativeMetricCurrentValue> values, ReadOnlySpan<byte> bytes, Action? onCommitted)
    {
        lock (sync)
        {
            RequireHandle();
            var maximumCells = Math.Min(uint.MaxValue, configuration.ResidentByteBudget
                / (2UL * (uint)Unsafe.SizeOf<NativeMetricCurrentValue>()));
            if ((ulong)values.Length > maximumCells || (ulong)requested.Length > maximumCells
                || (ulong)retired.Length > maximumCells || (ulong)bytes.Length > maximumCells * NativeMetricSnapshotAbi.MaximumValueBytes)
                throw new ArgumentOutOfRangeException(nameof(values), "The current-value transaction exceeds the configured capacity.");
            // A read is temporary preparation, never another current owner.
            var prior = ReadCurrent();
            var removed = requested.ToArray().Concat(retired.ToArray()).ToHashSet();
            var cellsNeeded = checked((uint)(values.Length + prior.Values.Count(row => !removed.Contains(row.DatasetHandle))));
            var bytesNeeded = checked((uint)(bytes.Length + prior.Values.Where(row => !removed.Contains(row.DatasetHandle)
                    && row.ValueKind == (uint)NativeMetricSnapshotValueKind.Bytes).Sum(static row => checked((long)row.ValueHighBits))));
            var cellCapacity = Math.Max(cellsNeeded, 1U);
            var byteCapacity = checked((bytesNeeded + 7U) & ~7U);
            if (cellCapacity > maximumCells || byteCapacity > maximumCells * NativeMetricSnapshotAbi.MaximumValueBytes)
                throw new ArgumentOutOfRangeException(nameof(values), "The resulting current values exceed the configured capacity.");
            var requiredRegionBytes = checked(2UL * (cellCapacity * (ulong)Unsafe.SizeOf<NativeMetricCurrentValue>() + byteCapacity));
            var baseBytes = checked(capacity.ResidentByteCount - (currentStorage?.Length ?? 0UL) + capacity.ScratchByteCount);
            if (baseBytes > configuration.ResidentByteBudget || requiredRegionBytes > configuration.ResidentByteBudget - baseBytes)
                throw new InvalidOperationException("The current-value transaction exceeds the configured resident byte budget.");
            CurrentStorage? replacement = null;
            if (currentStorage is null || currentStorage.CellCapacity < cellCapacity || currentStorage.ByteCapacity < byteCapacity
                || currentStorage.CellCapacity > Math.Max(64U, checked(cellCapacity * 2U)))
                replacement = new CurrentStorage(createScratch, cellCapacity, byteCapacity);
            var target = replacement ?? currentStorage!;
            try
            {
                if (replacement is not null && currentStorage is not null)
                    retiredCurrentStorage.EnsureCapacity(checked(retiredCurrentStorage.Count + 1));
                var input = new NativeMetricCurrentCommitInput
                {
                    AbiVersion = NativeMetricSnapshotAbi.Version,
                    StructSize = (uint)Unsafe.SizeOf<NativeMetricCurrentCommitInput>(),
                    CellCapacity = target.CellCapacity, ByteCapacity = target.ByteCapacity
                };
                NativeMetricSnapshotStatus status;
                fixed (ulong* rp = requested)
                fixed (ulong* tp = retired)
                fixed (NativeMetricCurrentValue* vp = values)
                fixed (byte* bp = bytes)
                    status = (NativeMetricSnapshotStatus)NativeMethods.CommitCurrent(RequireHandle(), in input,
                        rp, (uint)requested.Length, tp, (uint)retired.Length, vp, (uint)values.Length,
                        bp, (uint)bytes.Length, (byte*)target.Address, target.Length);
                if (status != NativeMetricSnapshotStatus.Ok)
                    throw new InvalidOperationException($"Committing native current failed: {status}.");
                if (replacement is not null)
                {
                    var previous = currentStorage;
                    currentStorage = replacement;
                    replacement = null;
                    if (previous is not null) retiredCurrentStorage.Add(previous);
                }
                capacity = QueryAndValidateCapacity(RequireHandle(), in configuration);
                // Native values are already authoritative. Align their declaration
                // owner before fallible storage completion or retirement runs.
                onCommitted?.Invoke();
                target.Lease.Complete();
                CloseRetiredCurrentStorage();
            }
            finally { replacement?.Dispose(); }
        }
    }

    private void CloseRetiredCurrentStorage()
    {
        while (retiredCurrentStorage.Count != 0)
        {
            retiredCurrentStorage[0].Dispose();
            retiredCurrentStorage.RemoveAt(0);
        }
    }

    private sealed class CurrentStorage : IDisposable
    {
        private readonly INativeMetricSnapshotScratch resource;
        internal readonly NativeMetricSnapshotScratchLease Lease;
        internal MemoryHandle Pin;
        internal uint CellCapacity { get; }
        internal uint ByteCapacity { get; }
        internal ulong Length { get; }
        internal IntPtr Address { get; }
        private bool pinClosed, leaseClosed, resourceClosed;

        internal unsafe CurrentStorage(Func<ulong, uint, INativeMetricSnapshotScratch> factory, uint cells, uint bytes)
        {
            CellCapacity = cells; ByteCapacity = bytes;
            Length = checked(2UL * (cells * (ulong)Unsafe.SizeOf<NativeMetricCurrentValue>() + bytes));
            resource = factory(Length, 8)
                ?? throw new InvalidOperationException("The current-value storage factory returned no resource.");
            try
            {
                Lease = resource.Acquire();
                try
                {
                    Pin = Lease.Pin();
                    var first = (nuint)Pin.Pointer;
                    var aligned = checked(first + 7U) & ~(nuint)7;
                    var skip = checked((ulong)(aligned - first));
                    if (first == 0 || skip > Lease.Length || Length > Lease.Length - skip)
                        throw new InvalidOperationException("The current-value storage extent is invalid.");
                    Address = (IntPtr)aligned;
                }
                catch { Pin.Dispose(); Lease.Dispose(); throw; }
            }
            catch { resource.Dispose(); throw; }
        }

        public void Dispose()
        {
            if (!pinClosed) { Pin.Dispose(); pinClosed = true; }
            if (!leaseClosed) { Lease.Dispose(); leaseClosed = true; }
            if (!resourceClosed) { resource.Dispose(); resourceClosed = true; }
        }
    }

    private static partial class NativeMethods
    {
        [LibraryImport(LibraryName, EntryPoint = "rm_metric_snapshot_commit_current")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial int CommitCurrent(IntPtr handle, in NativeMetricCurrentCommitInput input,
            ulong* requested, uint requestedCount, ulong* retired, uint retiredCount, NativeMetricCurrentValue* values,
            uint valueCount, byte* bytes, uint byteCount, byte* resident, ulong residentBytes);

        [LibraryImport(LibraryName, EntryPoint = "rm_metric_snapshot_read_current")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial int ReadCurrent(IntPtr handle, uint abiVersion, out NativeMetricCurrentReadOutput output,
            NativeMetricCurrentValue* values, uint valueCapacity, byte* bytes, uint byteCapacity);

        [LibraryImport(LibraryName, EntryPoint = "rm_metric_snapshot_read_current_selected")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial int ReadCurrentSelected(IntPtr handle, uint abiVersion,
            NativeMetricCurrentKey* selection, uint selectionCount, out NativeMetricCurrentReadOutput output,
            NativeMetricCurrentValue* values, uint valueCapacity, byte* bytes, uint byteCapacity);
    }
}
