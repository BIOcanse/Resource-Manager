using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace ResourceManager.Ledger.Identity;

/// <summary>An opaque 16-byte UID. Its bytes have no ledger-defined business structure.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct LedgerUid128 : ILedgerUid<LedgerUid128>
{
    private readonly ulong word0;
    private readonly ulong word1;

    public static int ByteLength => 16;

    public static LedgerUid128 FromBytes(ReadOnlySpan<byte> bytes) => new(bytes);

    public LedgerUid128(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != 16)
            throw new ArgumentException("A 128-bit UID contains exactly 16 bytes.", nameof(bytes));

        word0 = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
        word1 = BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]);
    }

    public void CopyTo(Span<byte> destination)
    {
        if (destination.Length < 16)
            throw new ArgumentException("The destination needs at least 16 bytes.", nameof(destination));

        BinaryPrimitives.WriteUInt64LittleEndian(destination, word0);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[8..], word1);
    }

    public bool Equals(LedgerUid128 other) =>
        word0 == other.word0 && word1 == other.word1;

    public override bool Equals(object? obj) => obj is LedgerUid128 other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(word0, word1);

    public static bool operator ==(LedgerUid128 left, LedgerUid128 right) => left.Equals(right);

    public static bool operator !=(LedgerUid128 left, LedgerUid128 right) => !left.Equals(right);
}
