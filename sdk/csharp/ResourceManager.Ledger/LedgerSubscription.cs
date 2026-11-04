using System.Runtime.InteropServices;
using ResourceManager.Ledger.Identity;
using ResourceManager.Ledger.Interop;

namespace ResourceManager.Ledger;

/// <summary>A rooted callback registration removed only after Native unsubscribe or successful owner destruction.</summary>
public sealed unsafe class LedgerSubscription<TUid> : IDisposable
    where TUid : unmanaged, ILedgerUid<TUid>
{
    private readonly object _gate = new();
    private LedgerOwner<TUid>? _owner;
    private readonly Action<LedgerNotice<TUid>> _callback;
    private GCHandle _callbackContext;
    private NativeSubscriptionHandle _handle;
    private bool _handleReady;
    private bool _unsubscribing;

    internal LedgerSubscription(LedgerOwner<TUid> owner, Action<LedgerNotice<TUid>> callback)
    {
        _owner = owner;
        _callback = callback;
    }

    public uint Table { get { lock (_gate) return _handle.Table; } }
    public bool IsDisposed { get { lock (_gate) return _owner is null; } }

    internal void AllocateCallbackContext()
    {
        lock (_gate)
        {
            if (_callbackContext.IsAllocated) throw new InvalidOperationException("Callback context is already allocated.");
            _callbackContext = GCHandle.Alloc(new LedgerCallbackContext<TUid>(this), GCHandleType.Normal);
        }
    }

    internal void* ContextPointer
    {
        get
        {
            lock (_gate)
            {
                if (!_callbackContext.IsAllocated) throw new InvalidOperationException("Callback context is not rooted.");
                return (void*)GCHandle.ToIntPtr(_callbackContext);
            }
        }
    }

    internal void SetHandle(NativeSubscriptionHandle handle)
    {
        lock (_gate)
        {
            if (_owner is null || !_callbackContext.IsAllocated) throw new InvalidOperationException("Subscription is not active.");
            _handle = handle;
            _handleReady = true;
        }
    }

    internal void Receive(byte* uidBytes, ulong uidLength, ulong allocationNumber)
    {
        if (uidBytes == null || uidLength != (ulong)TUid.ByteLength)
            throw new InvalidOperationException("Native callback supplied an invalid UID span.");
        TUid uid = TUid.FromBytes(new ReadOnlySpan<byte>(uidBytes, checked((int)uidLength)));
        _callback(new LedgerNotice<TUid>(uid, allocationNumber));
    }

    internal void CompleteUnsubscribe()
    {
        lock (_gate)
        {
            _owner = null;
            _handleReady = false;
            ReleaseContextLocked();
        }
    }

    internal void ReleaseCallbackContext()
    {
        lock (_gate) ReleaseContextLocked();
    }

    internal void MarkOwnerDestroyed()
    {
        lock (_gate)
        {
            _owner = null;
            _handleReady = false;
            ReleaseContextLocked();
        }
    }

    public void Dispose()
    {
        if (NativeCallbackBridge.IsInCallback)
            throw new InvalidOperationException("Ledger callbacks must not reenter the ledger.");
        LedgerOwner<TUid> owner;
        NativeSubscriptionHandle handle;
        lock (_gate)
        {
            while (_unsubscribing) Monitor.Wait(_gate);
            if (_owner is null) return;
            if (!_handleReady) throw new InvalidOperationException("Native subscription has not completed registration.");
            _unsubscribing = true;
            owner = _owner;
            handle = _handle;
        }

        try
        {
            // Do not hold the subscription lock here: Native unsubscribe drains
            // callbacks, and a callback may read this subscription's metadata.
            owner.Unsubscribe(this, handle);
        }
        catch
        {
            lock (_gate)
            {
                _unsubscribing = false;
                Monitor.PulseAll(_gate);
            }
            throw;
        }

        lock (_gate)
        {
            CompleteUnsubscribe();
            _unsubscribing = false;
            Monitor.PulseAll(_gate);
        }
    }

    private void ReleaseContextLocked()
    {
        if (_callbackContext.IsAllocated) _callbackContext.Free();
    }
}

internal static unsafe class NativeCallbackBridge
{
    [ThreadStatic]
    private static bool _isInCallback;

    internal static bool IsInCallback => _isInCallback;

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static void Dispatch(void* context, byte* uidBytes, ulong uidLength, ulong allocationNumber)
    {
        bool wasInCallback = _isInCallback;
        _isInCallback = true;
        try
        {
            if (context == null) return;
            GCHandle handle = GCHandle.FromIntPtr((nint)context);
            if (handle.Target is ILedgerCallbackContext callback) callback.Receive(uidBytes, uidLength, allocationNumber);
        }
        catch
        {
            // No managed exception may cross the C callback boundary.
        }
        finally
        {
            _isInCallback = wasInCallback;
        }
    }

}

internal unsafe interface ILedgerCallbackContext
{
    void Receive(byte* uidBytes, ulong uidLength, ulong allocationNumber);
}

internal sealed unsafe class LedgerCallbackContext<TUid> : ILedgerCallbackContext
    where TUid : unmanaged, ILedgerUid<TUid>
{
    private readonly LedgerSubscription<TUid> _subscription;

    internal LedgerCallbackContext(LedgerSubscription<TUid> subscription) => _subscription = subscription;

    public void Receive(byte* uidBytes, ulong uidLength, ulong allocationNumber) =>
        _subscription.Receive(uidBytes, uidLength, allocationNumber);
}
