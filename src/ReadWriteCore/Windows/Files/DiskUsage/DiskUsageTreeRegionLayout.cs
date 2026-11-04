using System.Collections;
using System.Runtime.InteropServices;

namespace ResourceManager.App.Domain.DiskUsage;

/// <summary>A region view valid only while its caller holds the owning read or loading lease.</summary>
public interface IDiskUsageTreeRegion
{
    ulong Length { get; }
    Span<byte> GetSpanChunk(ulong offset, int length);
}

/// <summary>The checked, aligned layout of the actual published tree payload.
/// Columns contain values only; nullable identities have an explicit validity bit.</summary>
public sealed class DiskUsageTreeRegionLayout
{
    private const int HeaderLength = 16;
    private const int Format = 1;
    private readonly ulong parents, firstChildren, nextSiblings, sizes, allocated;
    private readonly ulong nameOffsets, nameLengths, fileCounts, flags, volumes, records, sequences, roots, names;
    public int Count { get; }
    public int RootCount { get; }
    public int NameCharacterCount { get; }
    public ulong Length { get; }

    private DiskUsageTreeRegionLayout(int count, int rootCount, int nameCharacterCount)
    {
        if (count < 0 || rootCount < 0 || rootCount > count || nameCharacterCount < 0)
            throw new ArgumentOutOfRangeException(nameof(count));
        Count = count; RootCount = rootCount; NameCharacterCount = nameCharacterCount;
        ulong end = HeaderLength;
        ulong Column(int width, int length)
        {
            var alignment = (ulong)Math.Min(width, 8);
            var start = checked((end + alignment - 1) / alignment * alignment);
            end = checked(start + (ulong)width * (ulong)length);
            return start;
        }
        parents = Column(4, count); firstChildren = Column(4, count); nextSiblings = Column(4, count);
        sizes = Column(8, count); allocated = Column(8, count);
        nameOffsets = Column(4, count); nameLengths = Column(4, count); fileCounts = Column(4, count);
        flags = Column(1, count); volumes = Column(8, count); records = Column(8, count);
        sequences = Column(2, count); roots = Column(4, rootCount); names = Column(2, nameCharacterCount);
        Length = checked((end + 7) / 8 * 8);
    }

    public static DiskUsageTreeRegionLayout Create(DiskUsageTree source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var characters = 0;
        for (var node = 0; node < source.Count; node++)
        {
            if ((node & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            characters = checked(characters + source.NameOf(node).Length);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(source.Count, source.Roots.Count, characters);
    }

    public void Write(DiskUsageTree source, IDiskUsageTreeRegion region, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source); ValidateRegion(region);
        cancellationToken.ThrowIfCancellationRequested();
        if (source.Count != Count || source.Roots.Count != RootCount)
            throw new ArgumentException("The source does not match this layout.", nameof(source));
        WriteValue(region, 0, Format); WriteValue(region, 4, Count);
        WriteValue(region, 8, RootCount); WriteValue(region, 12, NameCharacterCount);
        void Column<T>(ulong offset, int count, Func<int, T> value) where T : unmanaged
            => WriteColumn(region, offset, count, value, cancellationToken);
        Column(parents, Count, source.ParentOf);
        Column(firstChildren, Count, source.FirstChildOf);
        Column(nextSiblings, Count, source.NextSiblingOf);
        Column(sizes, Count, source.SizeOf);
        Column(allocated, Count, source.AllocatedOf);
        Column(fileCounts, Count, source.FileCountOf);
        Column(nameLengths, Count, node => source.NameOf(node).Length);
        Column(flags, Count, node => (byte)((source.IsDirectory(node) ? 1 : 0)
            | (source.LogicalSizeKnownOf(node) ? 2 : 0) | (source.AllocationKnownOf(node) ? 4 : 0)
            | (source.CountKnownOf(node) ? 8 : 0) | (source.IdentityOf(node).HasValue ? 16 : 0)));
        Column(volumes, Count, node => source.IdentityOf(node)?.VolumeSerialNumber ?? 0);
        Column(records, Count, node => source.IdentityOf(node)?.RecordNumber ?? 0);
        Column(sequences, Count, node => source.IdentityOf(node)?.SequenceNumber ?? (ushort)0);
        var character = 0;
        Column(nameOffsets, Count, node =>
        {
            var start = character;
            character = checked(character + source.NameOf(node).Length);
            if (character > NameCharacterCount) throw new ArgumentException("The source names do not match this layout.", nameof(source));
            return start;
        });
        if (character != NameCharacterCount) throw new ArgumentException("The source names do not match this layout.", nameof(source));
        character = 0;
        for (var node = 0; node < Count; node++)
        {
            if ((node & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            var name = source.NameOf(node);
            // A name is bounded by the filesystem's component limit. The entire
            // names column and region may exceed one Span's maximum length.
            MemoryMarshal.AsBytes(name).CopyTo(region.GetSpanChunk(At(names, character, 2), checked(name.Length * 2)));
            character = checked(character + name.Length);
        }
        Column(roots, RootCount, root => source.Roots[root]);
        cancellationToken.ThrowIfCancellationRequested();
    }

    public DiskUsageTree Open(IDiskUsageTreeRegion region)
    {
        ValidateRegion(region);
        if (ReadValue<int>(region, 0) != Format || ReadValue<int>(region, 4) != Count
            || ReadValue<int>(region, 8) != RootCount || ReadValue<int>(region, 12) != NameCharacterCount)
            throw new InvalidDataException("The published tree header does not match its layout.");
        return new DiskUsageTree(this, region);
    }

    private void ValidateRegion(IDiskUsageTreeRegion region)
    {
        ArgumentNullException.ThrowIfNull(region);
        if (region.Length < Length) throw new ArgumentException("The region is smaller than the tree layout.", nameof(region));
    }
    private static ulong At(ulong start, int index, int width) => checked(start + (ulong)index * (ulong)width);
    private static T ReadValue<T>(IDiskUsageTreeRegion region, ulong offset) where T : unmanaged
        => MemoryMarshal.Read<T>(region.GetSpanChunk(offset, System.Runtime.CompilerServices.Unsafe.SizeOf<T>()));
    private static void WriteValue<T>(IDiskUsageTreeRegion region, ulong offset, T value) where T : unmanaged
        => MemoryMarshal.Write(region.GetSpanChunk(offset, System.Runtime.CompilerServices.Unsafe.SizeOf<T>()), in value);
    private static void WriteColumn<T>(IDiskUsageTreeRegion region, ulong offset, int count, Func<int, T> value,
        CancellationToken cancellationToken) where T : unmanaged
    {
        var width = System.Runtime.CompilerServices.Unsafe.SizeOf<T>();
        var position = 0;
        while (position < count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var length = Math.Min(65_536 / width, count - position);
            var chunk = MemoryMarshal.Cast<byte, T>(region.GetSpanChunk(At(offset, position, width), length * width));
            for (var index = 0; index < length; index++) chunk[index] = value(position + index);
            position += length;
        }
    }
    private int Index(int node) => (uint)node < (uint)Count ? node : throw new ArgumentOutOfRangeException(nameof(node));
    internal int ParentOf(IDiskUsageTreeRegion r, int n) => ReadValue<int>(r, At(parents, Index(n), 4));
    internal int FirstChildOf(IDiskUsageTreeRegion r, int n) => ReadValue<int>(r, At(firstChildren, Index(n), 4));
    internal int NextSiblingOf(IDiskUsageTreeRegion r, int n) => ReadValue<int>(r, At(nextSiblings, Index(n), 4));
    internal long SizeOf(IDiskUsageTreeRegion r, int n) => ReadValue<long>(r, At(sizes, Index(n), 8));
    internal long AllocatedOf(IDiskUsageTreeRegion r, int n) => ReadValue<long>(r, At(allocated, Index(n), 8));
    internal int FileCountOf(IDiskUsageTreeRegion r, int n) => ReadValue<int>(r, At(fileCounts, Index(n), 4));
    internal bool FlagOf(IDiskUsageTreeRegion r, int n, byte mask) => (ReadValue<byte>(r, At(flags, Index(n), 1)) & mask) != 0;
    internal DiskUsageObjectIdentity? IdentityOf(IDiskUsageTreeRegion r, int n)
        => !FlagOf(r, n, 16) ? null : new(ReadValue<ulong>(r, At(volumes, n, 8)), ReadValue<long>(r, At(records, n, 8)), ReadValue<ushort>(r, At(sequences, n, 2)));
    internal ReadOnlySpan<char> NameOf(IDiskUsageTreeRegion r, int n)
    {
        Index(n);
        var offset = ReadValue<int>(r, At(nameOffsets, n, 4));
        var length = ReadValue<int>(r, At(nameLengths, n, 4));
        if (offset < 0 || length < 0 || (long)offset + length > NameCharacterCount)
            throw new InvalidDataException("The published name lies outside the names column.");
        return MemoryMarshal.Cast<byte, char>(r.GetSpanChunk(At(names, offset, 2), checked(length * 2)));
    }
    internal IReadOnlyList<int> Roots(IDiskUsageTreeRegion region) => new RegionRoots(this, region);
    private sealed class RegionRoots(DiskUsageTreeRegionLayout layout, IDiskUsageTreeRegion region) : IReadOnlyList<int>
    {
        public int Count => layout.RootCount;
        public int this[int index] => (uint)index < (uint)Count
            ? ReadValue<int>(region, At(layout.roots, index, 4)) : throw new ArgumentOutOfRangeException(nameof(index));
        public IEnumerator<int> GetEnumerator() { for (var i = 0; i < Count; i++) yield return this[i]; }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
