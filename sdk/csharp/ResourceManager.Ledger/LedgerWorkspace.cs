using ResourceManager.Ledger.Interop;

namespace ResourceManager.Ledger;

public enum LedgerWorkspaceKind
{
    Operation,
    Tick,
    Destroy,
}

/// <summary>Explicit aligned Native scratch. A workspace is single-call at a time and never waits for reuse.</summary>
public sealed class LedgerWorkspace : IDisposable
{
    private readonly object _ownerToken;
    private readonly LedgerWorkspaceKind _kind;
    private readonly AlignedNativeBuffer _buffer;
    // 0 = idle, 1 = in a Native call, 2 = disposed. One CAS protects both
    // entering and freeing so disposal cannot race scratch use.
    private int _state;

    internal LedgerWorkspace(object ownerToken, LedgerWorkspaceKind kind, ulong size, ulong alignment)
    {
        _ownerToken = ownerToken;
        _kind = kind;
        _buffer = AlignedNativeBuffer.Allocate(size, alignment, clear: true);
    }

    public LedgerWorkspaceKind Kind => _kind;
    public ulong ByteLength => _buffer.DeclaredSize;

    internal void Enter(object ownerToken, LedgerWorkspaceKind requiredKind)
    {
        if (!ReferenceEquals(ownerToken, _ownerToken)) throw new ArgumentException("Workspace belongs to a different ledger owner.", nameof(ownerToken));
        if (_kind != requiredKind) throw new ArgumentException($"This call requires a {requiredKind} workspace.", nameof(requiredKind));
        int previous = Interlocked.CompareExchange(ref _state, 1, 0);
        if (previous == 1) throw new InvalidOperationException("Ledger workspace is already in use.");
        if (previous == 2) throw new ObjectDisposedException(nameof(LedgerWorkspace));
        if (previous != 0)
        {
            throw new InvalidOperationException("Ledger workspace has an invalid lifecycle state.");
        }
    }

    internal void Exit() => Volatile.Write(ref _state, 0);

    internal unsafe void* Pointer => _buffer.Pointer;

    public void Dispose()
    {
        int previous = Interlocked.CompareExchange(ref _state, 2, 0);
        if (previous == 1) throw new InvalidOperationException("Cannot dispose a ledger workspace during a Native call.");
        if (previous == 2) return;
        if (previous != 0) throw new InvalidOperationException("Ledger workspace has an invalid lifecycle state.");
        _buffer.Dispose();
    }
}
