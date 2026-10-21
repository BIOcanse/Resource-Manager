using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace ResourceManager.Ledger.Identity;

/// <summary>An opaque 32-byte UID. Its bytes have no ledger-defined business structure.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct LedgerUid256 : ILedgerUid<LedgerUid256>
{
    private readonly ulong word0;
    private readonly ulong word1;
    private readonly ulong word2;
    private readonly ulong word3;

    public static int ByteLength => 32;

    public static LedgerUid256 FromBytes(ReadOnlySpan<byte> bytes) => new(bytes);

    public LedgerUid256(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != 32)
            throw new ArgumentException("A 256-bit UID contains exactly 32 bytes.", nameof(bytes));

        word0 = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
        word1 = BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]);
        word2 = BinaryPrimitives.ReadUInt64LittleEndian(bytes[16..]);
        word3 = BinaryPrimitives.ReadUInt64LittleEndian(bytes[24..]);
    }

    public void CopyTo(Span<byte> destination)
    {
        if (destination.Length < 32)
            throw new ArgumentException("The destination needs at least 32 bytes.", nameof(destination));

        BinaryPrimitives.WriteUInt64LittleEndian(destination, word0);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[8..], word1);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[16..], word2);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[24..], word3);
    }

    public bool Equals(LedgerUid256 other) =>
        word0 == other.word0 && word1 == other.word1 && word2 == other.word2 && word3 == other.word3;

    public override bool Equals(object? obj) => obj is LedgerUid256 other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(word0, word1, word2, word3);

    public static bool operator ==(LedgerUid256 left, LedgerUid256 right) => left.Equals(right);

    public static bool operator !=(LedgerUid256 left, LedgerUid256 right) => !left.Equals(right);
}
