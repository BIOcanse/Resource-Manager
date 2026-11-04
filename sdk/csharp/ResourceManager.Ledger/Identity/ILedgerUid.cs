namespace ResourceManager.Ledger.Identity;

/// <summary>A fixed-width, opaque UID value suitable for the Native ledger boundary.</summary>
public interface ILedgerUid<TSelf> : IEquatable<TSelf>
    where TSelf : unmanaged, ILedgerUid<TSelf>
{
    static abstract int ByteLength { get; }

    static abstract TSelf FromBytes(ReadOnlySpan<byte> bytes);

    void CopyTo(Span<byte> destination);
}
