using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace ResourceManager.Adapter.SharedMemory.LocalResources;

/// <summary>Opaque 32-byte identity for the new ledger. The ABI10 interfaces retain their own types.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct LocalResourceUid256 : IEquatable<LocalResourceUid256>
{
    private readonly ulong word0;
    private readonly ulong word1;
    private readonly ulong word2;
    private readonly ulong word3;

    public LocalResourceUid256(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != 32) throw new ArgumentException("A 256-bit UID contains 32 bytes.", nameof(bytes));
        word0 = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
        word1 = BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]);
        word2 = BinaryPrimitives.ReadUInt64LittleEndian(bytes[16..]);
        word3 = BinaryPrimitives.ReadUInt64LittleEndian(bytes[24..]);
    }

    public void CopyTo(Span<byte> destination)
    {
        if (destination.Length < 32) throw new ArgumentException("The destination needs 32 bytes.", nameof(destination));
        BinaryPrimitives.WriteUInt64LittleEndian(destination, word0);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[8..], word1);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[16..], word2);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[24..], word3);
    }

    public bool Equals(LocalResourceUid256 other) =>
        word0 == other.word0 && word1 == other.word1 && word2 == other.word2 && word3 == other.word3;
    public override bool Equals(object? obj) => obj is LocalResourceUid256 other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(word0, word1, word2, word3);
}
