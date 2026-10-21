using System.Buffers;
using System.Runtime.ExceptionServices;
using ResourceManager.Ledger.Identity;
using ResourceManager.Ledger.Interop;

namespace ResourceManager.Ledger;

/// <summary>One counted Native use and its region view.</summary>
public sealed unsafe class LedgerLease<TUid> : IDisposable
    where TUid : unmanaged, ILedgerUid<TUid>
{
    private readonly object _gate = new();
    private readonly LedgerOwner<TUid> _owner;
    private readonly NativeLease _credential;
    private readonly bool _loading;
    private byte* _region;
    private ulong _length;
    private bool _publishOnDispose;
    private bool _disposeRequested;
    private bool _endIssued;
    private int _pins;
    private LedgerEndResult? _endResult;
    private Exception? _endException;
    private TaskCompletionSource<LedgerEndResult>? _endCompletion;

    internal LedgerLease(LedgerOwner<TUid> owner, NativeLease credential, byte* region, ulong length, bool loading)
    {
        _owner = owner;
        _credential = credential;
        _region = region;
        _length = length;
        _loading = loading;
    }

    public uint Slot => _credential.Slot;
    public ulong AllocationNumber => _credential.AllocationNumber;
    public ulong UseGeneration => _credential.UseGeneration;
    public bool IsLoading => _loading;
    public ulong Length { get { lock (_gate) return _length; } }
    public int ActiveMemoryPins { get { lock (_gate) return _pins; } }

    /// <summary>Returns the entire region as a span when it fits in a managed span.</summary>
    public Span<byte> Span
    {
        get
        {
            lock (_gate)
            {
                EnsureViewOpen();
                int length = checked((int)_length);
                return new Span<byte>(_region, length);
            }
        }
    }

    /// <summary>Returns the region as memory when its full length fits in a managed memory value.</summary>
    public Memory<byte> Memory => GetMemoryChunk(0, checked((int)Length));

    /// <summary>Gets a bounded span chunk; use chunks for regions larger than Int32.MaxValue.</summary>
    public Span<byte> GetSpanChunk(ulong offset, int length)
    {
        lock (_gate)
        {
            EnsureViewOpen();
            ValidateRange(offset, length, _length);
            return new Span<byte>(PointerAt(offset), length);
        }
    }

    /// <summary>Gets a bounded asynchronous-memory chunk; pinning it protects the region until unpin.</summary>
    public Memory<byte> GetMemoryChunk(ulong offset, int length)
    {
        lock (_gate)
        {
            EnsureViewOpen();
            ValidateRange(offset, length, _length);
            return new LedgerRegionMemoryManager<TUid>(this, offset, length).Memory;
        }
    }

    /// <summary>Marks a newly allocated loading lease for publication when its final Native count is returned.</summary>
    public void PublishOnDispose()
    {
        if (NativeCallbackBridge.IsInCallback)
            throw new InvalidOperationException("Ledger callbacks must not reenter the ledger.");
        lock (_gate)
        {
            EnsureViewOpen();
            if (!_loading) throw new InvalidOperationException("Only a newly allocated loading lease can be published.");
            _publishOnDispose = true;
        }
    }

    /// <summary>Applies a partial update through this lease and refreshes its own region after a successful move.</summary>
    public LedgerStatus Update(LedgerUpdate update)
    {
        if (NativeCallbackBridge.IsInCallback)
            throw new InvalidOperationException("Ledger callbacks must not reenter the ledger.");
        lock (_gate)
        {
            EnsureViewOpen();
            if (update.AllocationBytes is ulong nextBytes && _pins != 0 &&
                PageCount(_length) != PageCount(nextBytes))
            {
                return LedgerStatus.InUse;
            }

            (LedgerStatus status, NativeBorrow borrow) = _owner.UpdateLease(_credential, update);
            if (status == LedgerStatus.Updated && update.AllocationBytes.HasValue)
            {
                if (!SameCredential(borrow.Lease, _credential) || borrow.Lease.Reserved != 0 ||
                    borrow.Length > (ulong)nuint.MaxValue || (borrow.Length != 0 && borrow.Data == null))
                {
                    throw new InvalidOperationException("Native update returned a borrow for a different lease or an invalid region.");
                }
                _region = (byte*)borrow.Data;
                _length = borrow.Length;
            }
            else if (status == LedgerStatus.ForceReleased)
            {
                CompleteLocallyAfterForceRelease();
            }
            return status;
        }
    }

    /// <summary>Requests the one Native end. If memory is pinned, completion waits for the final unpin.</summary>
    public ValueTask<LedgerEndResult> EndUseAsync()
    {
        if (NativeCallbackBridge.IsInCallback)
            throw new InvalidOperationException("Ledger callbacks must not reenter the ledger.");
        lock (_gate)
        {
            if (_endResult is LedgerEndResult result) return ValueTask.FromResult(result);
            if (_endException is Exception exception)
                return new ValueTask<LedgerEndResult>(Task.FromException<LedgerEndResult>(exception));
            if (_endIssued) throw new InvalidOperationException("Ledger lease ended without a recorded result.");
            _disposeRequested = true;
            if (_pins == 0) return ValueTask.FromResult(CompleteEndUse());
            _endCompletion ??= new TaskCompletionSource<LedgerEndResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            return new ValueTask<LedgerEndResult>(_endCompletion.Task);
        }
    }

    public void Dispose()
    {
        if (NativeCallbackBridge.IsInCallback)
            throw new InvalidOperationException("Ledger callbacks must not reenter the ledger.");
        lock (_gate)
        {
            if (_endIssued)
            {
                if (_endException is Exception exception) ExceptionDispatchInfo.Capture(exception).Throw();
                return;
            }
            _disposeRequested = true;
            if (_pins == 0) _ = CompleteEndUse();
        }
    }

    internal Span<byte> GetSpanForMemory(ulong offset, int length)
    {
        lock (_gate)
        {
            EnsureViewOpen();
            ValidateRange(offset, length, _length);
            return new Span<byte>(PointerAt(offset), length);
        }
    }

    internal void* PinForMemory(ulong offset, int chunkLength, int elementIndex)
    {
        lock (_gate)
        {
            EnsureViewOpen();
            if (elementIndex < 0 || elementIndex > chunkLength) throw new ArgumentOutOfRangeException(nameof(elementIndex));
            ValidateRange(offset, chunkLength, _length);
            ulong absolute = checked(offset + (uint)elementIndex);
            byte* pointer = PointerAt(absolute);
            _pins = checked(_pins + 1);
            return pointer;
        }
    }

    internal void UnpinFromMemory()
    {
        if (NativeCallbackBridge.IsInCallback)
            throw new InvalidOperationException("Ledger callbacks must not reenter the ledger.");
        lock (_gate)
        {
            if (_pins <= 0) throw new InvalidOperationException("Memory pin count was unbalanced.");
            _pins--;
            if (_pins == 0 && _disposeRequested && !_endIssued) _ = CompleteEndUse();
        }
    }

    internal void MarkOwnerDestroyed()
    {
        lock (_gate)
        {
            if (_endIssued) return;
            _disposeRequested = true;
            _endIssued = true;
            LedgerEndResult result = new(LedgerStatus.ForceReleased);
            _endResult = result;
            _endCompletion?.TrySetResult(result);
        }
    }

    private LedgerEndResult CompleteEndUse()
    {
        if (_endIssued)
        {
            if (_endException is Exception exception) ExceptionDispatchInfo.Capture(exception).Throw();
            return _endResult ?? throw new InvalidOperationException("Ledger lease ended without a recorded result.");
        }
        _endIssued = true;
        try
        {
            uint disposition = _loading && !_publishOnDispose ? 1u : 0u;
            LedgerEndResult result = _owner.EndUse(_credential, disposition);
            _endResult = result;
            _endCompletion?.TrySetResult(result);
            return result;
        }
        catch (Exception exception)
        {
            _endException = exception;
            _endCompletion?.TrySetException(exception);
            throw;
        }
    }

    private void CompleteLocallyAfterForceRelease()
    {
        _disposeRequested = true;
        _endIssued = true;
        _endResult = new LedgerEndResult(LedgerStatus.ForceReleased);
        _endCompletion?.TrySetResult(_endResult.Value);
    }

    private void EnsureViewOpen()
    {
        if (NativeCallbackBridge.IsInCallback)
            throw new InvalidOperationException("Ledger callbacks must not reenter the ledger.");
        if (_disposeRequested || _endIssued || _owner.IsDestroyed)
            throw new ObjectDisposedException(nameof(LedgerLease<TUid>));
    }

    private ulong PageCount(ulong bytes) => bytes == 0 ? 0 : 1 + ((bytes - 1) / _owner.NativePageSize);

    private byte* PointerAt(ulong offset)
    {
        if (_region == null) return null;
        return (byte*)checked((nuint)_region + checked((nuint)offset));
    }

    private static void ValidateRange(ulong offset, int length, ulong totalLength)
    {
        if (length < 0 || offset > totalLength || (ulong)length > totalLength - offset)
            throw new ArgumentOutOfRangeException(nameof(offset));
    }

    private static bool SameCredential(NativeLease left, NativeLease right) =>
        left.Slot == right.Slot && left.AllocationNumber == right.AllocationNumber && left.UseGeneration == right.UseGeneration;
}

internal sealed unsafe class LedgerRegionMemoryManager<TUid> : MemoryManager<byte>
    where TUid : unmanaged, ILedgerUid<TUid>
{
    private readonly LedgerLease<TUid> _lease;
    private readonly ulong _offset;
    private readonly int _length;

    internal LedgerRegionMemoryManager(LedgerLease<TUid> lease, ulong offset, int length)
    {
        _lease = lease;
        _offset = offset;
        _length = length;
    }

    public override Span<byte> GetSpan() => _lease.GetSpanForMemory(_offset, _length);

    public override MemoryHandle Pin(int elementIndex = 0)
    {
        void* pointer = _lease.PinForMemory(_offset, _length, elementIndex);
        return new MemoryHandle(pointer, default, this);
    }

    public override void Unpin() => _lease.UnpinFromMemory();

    protected override void Dispose(bool disposing) { }
}
