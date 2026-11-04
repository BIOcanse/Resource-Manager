using System.Runtime.InteropServices;

namespace ResourceManager.ReadWrite.Native.SharedSection;

public enum SharedSectionReaderOperation { RequiredStorage, Read, PositionValue }
public enum SharedSectionReaderStatus
{
    Success, DependencyNotReady, InvalidSelection, StorageTooSmall, NativeFailure, InvalidBackendResult, Disposed
}

public readonly record struct SharedSectionReaderResult(
    SharedSectionReaderStatus Status, SharedSectionFailure Failure = SharedSectionFailure.Success)
{
    public bool IsSuccess => Status == SharedSectionReaderStatus.Success;
}

/// <summary>Counts refer only to explicitly selected sources, including caller-declared identity sources.</summary>
public readonly record struct SharedSectionReaderRequirements(ulong DataBytes, uint SourceCount, uint PositionCount)
{
    /// <summary>One aligned caller region: data, native held/selected-position tables and cached header.</summary>
    public UInt128 AlignedStorageBytes => DataBytes == 0 && SourceCount == 0 && PositionCount == 0 ? 0 : Align(DataBytes)
        + (UInt128)SourceCount * (uint)System.Runtime.CompilerServices.Unsafe.SizeOf<SharedSectionSourceCopy>()
        + (UInt128)PositionCount * (uint)System.Runtime.CompilerServices.Unsafe.SizeOf<SharedSectionSelectedPosition>()
        + (uint)System.Runtime.CompilerServices.Unsafe.SizeOf<SharedSectionHeader>();

    private static UInt128 Align(ulong bytes) => ((UInt128)bytes + 7) & ~(UInt128)7;
}

/// <summary>
/// Caller memory, zeroed before first use. Native records retain global source/position indices.
/// Header holds the package selection header across calls; nonzero magic means Select completed.
/// Preserve only for the identical mapping incarnation/selection/layout. No lease or pin is acquired here.
/// </summary>
public readonly ref struct SharedSectionReadStorage(
    Span<byte> data, Span<SharedSectionSourceCopy> sources, Span<SharedSectionSelectedPosition> positions,
    Span<SharedSectionHeader> header)
{
    public Span<byte> Data { get; } = data;
    public Span<SharedSectionSourceCopy> Sources { get; } = sources;
    public Span<SharedSectionSelectedPosition> Positions { get; } = positions;
    public Span<SharedSectionHeader> Header { get; } = header;

    internal bool Fits(SharedSectionReaderRequirements size) =>
        size.DataBytes <= (ulong)Data.Length && size.SourceCount <= Sources.Length &&
        size.PositionCount <= Positions.Length && Header.Length >= 1;

    internal SharedSectionReadStorage Slice(SharedSectionReaderRequirements size) => new(
        Data[..(int)size.DataBytes], Sources[..(int)size.SourceCount], Positions[..(int)size.PositionCount],
        Header[..1]);
}

/// <summary>
/// One package-binding call shape for sizing, reading and native position validation.
/// Backends must not retain any argument, allocate reader storage, or implement a managed seqlock.
/// Position is a region position index; offsets in Value refer to compact destination data.
/// Requirements and Value are outputs. Read preserves per-source outcomes in Storage.Sources.
/// </summary>
public unsafe ref struct SharedSectionReaderCall
{
    public SharedSectionReaderOperation Operation;
    public byte* Region;
    public ulong RegionLength;
    public ReadOnlySpan<uint> SelectedSources;
    public SharedSectionReadStorage Storage;
    public uint RetryLimit;
    public uint Position;
    public SharedSectionReaderRequirements Requirements;
    public SharedSectionValueView Value;
}

public interface ISharedSectionReaderBackend
{
    SharedSectionReaderResult Invoke(ref SharedSectionReaderCall call);
}

/// <summary>Controller has not activated the package selected-source reader. No fallback.</summary>
internal sealed class UnavailableSharedSectionReader : ISharedSectionReaderBackend
{
    internal static readonly UnavailableSharedSectionReader Instance = new();
    public SharedSectionReaderResult Invoke(ref SharedSectionReaderCall call) =>
        new(SharedSectionReaderStatus.DependencyNotReady);
}

/// <summary>
/// Borrows a caller-held mapping and explicitly selected, bounded storage. Caller keeps both alive
/// and exclusively borrowed until scope and all views expire. Refresh, Dispose or storage reuse
/// invalidates previous views. No allocation, mapping ownership, Ledger lease or pin is held here.
/// The package owns native validation; caller supplies the complete source list, including identities.
/// </summary>
public unsafe ref struct SharedSectionReadScope
{
    private ISharedSectionReaderBackend? _backend;
    private byte* _region;
    private ulong _regionLength;
    private ReadOnlySpan<uint> _selectedSources;
    private SharedSectionReadStorage _storage;
    private uint _retryLimit;
    private SharedSectionReaderResult _readResult;

    public static SharedSectionReaderResult RequiredStorage(
        byte* region, ulong regionLength, ReadOnlySpan<uint> sources,
        out SharedSectionReaderRequirements requirements, ISharedSectionReaderBackend? backend = null)
    {
        requirements = default;
        if (!ValidSelection(sources)) return new(SharedSectionReaderStatus.InvalidSelection);
        var call = new SharedSectionReaderCall
        {
            Operation = SharedSectionReaderOperation.RequiredStorage,
            Region = region, RegionLength = regionLength, SelectedSources = sources
        };
        var result = (backend ?? UnavailableSharedSectionReader.Instance).Invoke(ref call);
        if (!result.IsSuccess) return result;
        if (call.Requirements.SourceCount != sources.Length)
            return new(SharedSectionReaderStatus.InvalidBackendResult);
        requirements = call.Requirements;
        return result;
    }

    public static SharedSectionReaderResult TryOpen(
        byte* region, ulong regionLength, ReadOnlySpan<uint> sources, SharedSectionReadStorage storage,
        uint retryLimit, out SharedSectionReadScope scope, ISharedSectionReaderBackend? backend = null)
    {
        scope = default;
        var result = RequiredStorage(region, regionLength, sources, out var size, backend);
        if (!result.IsSuccess) return result;
        if (!storage.Fits(size)) return new(SharedSectionReaderStatus.StorageTooSmall);
        var candidate = new SharedSectionReadScope
        {
            _backend = backend ?? UnavailableSharedSectionReader.Instance,
            _region = region, _regionLength = regionLength, _selectedSources = sources,
            _storage = storage.Slice(size), _retryLimit = retryLimit
        };
        result = candidate.Refresh();
        if (result.IsSuccess) scope = candidate;
        return result;
    }

    public SharedSectionReaderResult Refresh()
    {
        if (_backend is null) return new(SharedSectionReaderStatus.Disposed);
        var call = Call(SharedSectionReaderOperation.Read);
        _readResult = _backend.Invoke(ref call);
        return _readResult;
    }

    public bool TryGetSource(uint regionSource, out SharedSectionSourceCopy source)
    {
        source = default;
        if (_backend is null || !_readResult.IsSuccess) return false;
        var index = 0;
        while (index < _storage.Sources.Length && _storage.Sources[index].Source != regionSource) index++;
        if (index == _storage.Sources.Length) return false;
        source = _storage.Sources[index];
        return true;
    }

    public SharedSectionReaderResult ReadPosition(uint regionPosition, out SharedSectionPositionView view)
    {
        view = default;
        if (_backend is null) return new(SharedSectionReaderStatus.Disposed);
        if (!_readResult.IsSuccess) return _readResult;
        var index = 0;
        while (index < _storage.Positions.Length && _storage.Positions[index].Index != regionPosition) index++;
        if (index == _storage.Positions.Length) return new(SharedSectionReaderStatus.NativeFailure, SharedSectionFailure.NotSelected);
        var call = Call(SharedSectionReaderOperation.PositionValue);
        call.Position = regionPosition;
        var result = _backend.Invoke(ref call);
        if (!result.IsSuccess) return result;
        var value = call.Value;
        if (value.Status is not (SharedSectionPositionStatus.HasValue or SharedSectionPositionStatus.Unavailable or
            SharedSectionPositionStatus.Ended or SharedSectionPositionStatus.Failed or SharedSectionPositionStatus.NotArrived))
            return new(SharedSectionReaderStatus.InvalidBackendResult);
        ReadOnlySpan<byte> bytes = default;
        var selected = _storage.Positions[index];
        var spec = selected.Entry.Spec;
        if (value.Status == SharedSectionPositionStatus.HasValue)
        {
            if (spec.ElementWidth is not (1 or 2 or 4 or 8) || value.Offset != selected.Offset ||
                value.ByteLength != (ulong)value.Length * spec.ElementWidth || value.ByteLength > spec.Size ||
                value.Offset > (ulong)_storage.Data.Length || value.ByteLength > (ulong)_storage.Data.Length - value.Offset)
                return new(SharedSectionReaderStatus.InvalidBackendResult);
            bytes = _storage.Data.Slice((int)value.Offset, (int)value.ByteLength);
        }
        view = new SharedSectionPositionView(value, spec, bytes);
        return result;
    }

    public void Dispose() => this = default;

    private readonly SharedSectionReaderCall Call(SharedSectionReaderOperation operation) => new()
    {
        Operation = operation, Region = _region, RegionLength = _regionLength,
        SelectedSources = _selectedSources, Storage = _storage, RetryLimit = _retryLimit
    };

    private static bool ValidSelection(ReadOnlySpan<uint> sources)
    {
        if (sources.IsEmpty) return false;
        var index = 1;
        while (index < sources.Length)
        {
            if (sources[index - 1] >= sources[index]) return false;
            index++;
        }
        return true;
    }

    internal static int Find(ReadOnlySpan<uint> indices, uint target)
    {
        var index = 0;
        while (index < indices.Length)
        {
            if (indices[index] == target) return index;
            index++;
        }
        return -1;
    }
}

/// <summary>A local readonly view; valid only during its scope and until the next read/storage reuse.</summary>
public readonly ref struct SharedSectionPositionView
{
    private readonly SharedSectionValueView _value;
    private readonly SharedSectionPositionSpec _spec;
    public ReadOnlySpan<byte> Bytes { get; }
    public SharedSectionPositionStatus Status => _value.Status;
    public uint Reason => _value.Reason;
    public uint Length => _value.Length;
    public SharedSectionPositionKind Kind => _spec.Kind;

    internal SharedSectionPositionView(SharedSectionValueView value, SharedSectionPositionSpec spec, ReadOnlySpan<byte> bytes)
    {
        _value = value;
        _spec = spec;
        Bytes = bytes;
    }

    public T Element<T>(int index) where T : unmanaged
    {
        if (Status != SharedSectionPositionStatus.HasValue)
            throw new InvalidOperationException("The position has no value in this read scope.");
        if ((uint)index >= Length) throw new ArgumentOutOfRangeException(nameof(index));
        if ((uint)System.Runtime.CompilerServices.Unsafe.SizeOf<T>() != _spec.ElementWidth)
            throw new ArgumentException("Element width must match the native position.");
        return MemoryMarshal.Read<T>(Bytes.Slice(checked(index * (int)_spec.ElementWidth)));
    }
}
