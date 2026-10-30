using System.Runtime.CompilerServices;
using ResourceManager.Ledger.Identity;
using ResourceManager.Ledger.Interop;

namespace ResourceManager.Ledger;

/// <summary>One explicitly paired Native ledger instance.</summary>
public sealed unsafe partial class LedgerOwner<TUid> : IDisposable
    where TUid : unmanaged, ILedgerUid<TUid>
{
    private readonly object _lifecycleGate = new();
    private readonly object _subscriptionGate = new();
    private readonly HashSet<LedgerSubscription<TUid>> _subscriptions = [];
    private readonly object _workspaceToken = new();
    private readonly NativeApi _api;
    private readonly NativeDescriptor _descriptor;
    private readonly AlignedNativeBuffer _storage;
    private readonly ulong _nativePageSize;
    private void* _instance;
    private int _activeCalls;
    // 0 = open, 1 = shutdown in progress, 2 = destroyed.
    private int _lifecycleState;

    private LedgerOwner(NativeApi api, NativeDescriptor descriptor, AlignedNativeBuffer storage, void* instance)
    {
        _api = api;
        _descriptor = descriptor;
        _storage = storage;
        _instance = instance;
        _nativePageSize = descriptor.NativePageSize;
    }

    /// <summary>Loads the named Native library and validates it before creating state.</summary>
    public static LedgerOwner<TUid> Open(string nativeLibraryPath, LedgerBuildIdentity identity)
    {
        if (NativeCallbackBridge.IsInCallback)
            throw new InvalidOperationException("Ledger callbacks must not reenter the ledger.");
        ArgumentException.ThrowIfNullOrWhiteSpace(nativeLibraryPath);
        ArgumentNullException.ThrowIfNull(identity);
        if (TUid.ByteLength is not (16 or 32))
            throw new ArgumentException("Ledger UIDs must contain exactly 16 or 32 opaque bytes.", nameof(TUid));

        NativeApi api = NativeApi.Load(nativeLibraryPath);
        AlignedNativeBuffer? storage = null;
        try
        {
            NativeDescriptor descriptor = NativeDescriptorValidator.QueryAndValidate<TUid>(api, identity);
            storage = AlignedNativeBuffer.Allocate(descriptor.InstanceSize, descriptor.InstanceAlignment, clear: true);
            void* instance = null;
            LedgerStatus status = (LedgerStatus)api.Create(storage.Pointer, storage.DeclaredSize, &instance);
            if (status != LedgerStatus.Ok) throw new LedgerException(status, "create");
            if (instance != storage.Pointer || instance == null)
            {
                throw new InvalidOperationException("Native create returned an instance outside the caller-owned state block.");
            }

            var owner = new LedgerOwner<TUid>(api, descriptor, storage, instance);
            storage = null;
            return owner;
        }
        catch
        {
            storage?.Dispose();
            api.Free();
            throw;
        }
    }

    public uint TableCount => _descriptor.TableCount;
    public ulong NativePageSize => _nativePageSize;
    public bool IsDestroyed => Volatile.Read(ref _lifecycleState) == 2;

    public LedgerWorkspace CreateOperationWorkspace() => CreateWorkspace(LedgerWorkspaceKind.Operation,
        _descriptor.OperationWorkspaceSize, _descriptor.OperationWorkspaceAlignment);

    public LedgerWorkspace CreateTickWorkspace() => CreateWorkspace(LedgerWorkspaceKind.Tick,
        _descriptor.TickWorkspaceSize, _descriptor.TickWorkspaceAlignment);

    public LedgerWorkspace CreateDestroyWorkspace() => CreateWorkspace(LedgerWorkspaceKind.Destroy,
        _descriptor.DestroyWorkspaceSize, _descriptor.DestroyWorkspaceAlignment);

    public LedgerUseResult<TUid> Use(TUid uid)
    {
        using CallScope call = EnterCall();
        Span<byte> bytes = stackalloc byte[TUid.ByteLength];
        uid.CopyTo(bytes);
        NativeBorrow borrow = default;
        LedgerStatus status;
        fixed (byte* uidPointer = bytes)
        {
            status = (LedgerStatus)_api.Use(_instance, uidPointer, (ulong)TUid.ByteLength, &borrow);
        }

        if (status == LedgerStatus.InMemory)
            return new LedgerUseResult<TUid>(status, CreateLease(borrow, loading: false));
        return new LedgerUseResult<TUid>(status, null);
    }

    public LedgerUseResult<TUid> UseFixed(uint declaration)
    {
        using CallScope call = EnterCall();
        NativeBorrow borrow = default;
        LedgerStatus status = (LedgerStatus)_api.UseFixed(_instance, declaration, &borrow);
        if (status == LedgerStatus.InMemory)
            return new LedgerUseResult<TUid>(status, CreateLease(borrow, loading: false));
        return new LedgerUseResult<TUid>(status, null);
    }

    public LedgerPushResult<TUid> Push(
        LedgerTarget<TUid> target,
        TUid uid,
        ulong? allocationBytes = null,
        LedgerWorkspace? workspace = null)
    {
        using CallScope call = EnterCall();
        using WorkspaceUse scratch = EnterWorkspace(workspace, LedgerWorkspaceKind.Operation,
            _descriptor.OperationWorkspaceSize, _descriptor.OperationWorkspaceAlignment);

        Span<byte> uidBytes = stackalloc byte[TUid.ByteLength];
        uid.CopyTo(uidBytes);
        Span<byte> parentBytes = stackalloc byte[TUid.ByteLength];
        NativeTarget nativeTarget = TargetToNative(target, parentBytes);
        NativeBorrow borrow = default;
        LedgerStatus status;
        fixed (byte* uidPointer = uidBytes)
        fixed (byte* parentPointer = parentBytes)
        {
            nativeTarget.ParentUid = target.Kind == LedgerTargetKind.Partition ? parentPointer : null;
            nativeTarget.ParentUidLength = target.Kind == LedgerTargetKind.Partition ? (ulong)TUid.ByteLength : 0;
            status = (LedgerStatus)_api.Push(_instance, &nativeTarget, uidPointer, (ulong)TUid.ByteLength,
                allocationBytes.HasValue ? 1u : 0u, allocationBytes.GetValueOrDefault(), scratch.Workspace.Pointer,
                scratch.Workspace.ByteLength, &borrow);
        }

        LedgerLease<TUid>? lease = status is LedgerStatus.InMemory or LedgerStatus.NewlyAllocated
            ? CreateLease(borrow, loading: status == LedgerStatus.NewlyAllocated)
            : null;
        return new LedgerPushResult<TUid>(status, lease);
    }

    public LedgerPartitionPushResult PushPartition(
        LedgerTarget<TUid> target,
        TUid uid,
        uint capacity,
        LedgerAttributes attributes,
        LedgerWorkspace? workspace = null)
    {
        ValidateAttributes(attributes);
        using CallScope call = EnterCall();
        using WorkspaceUse scratch = EnterWorkspace(workspace, LedgerWorkspaceKind.Operation,
            _descriptor.OperationWorkspaceSize, _descriptor.OperationWorkspaceAlignment);

        Span<byte> uidBytes = stackalloc byte[TUid.ByteLength];
        uid.CopyTo(uidBytes);
        Span<byte> parentBytes = stackalloc byte[TUid.ByteLength];
        NativeTarget nativeTarget = TargetToNative(target, parentBytes);
        NativeAttributes nativeAttributes = AttributesToNative(attributes);
        NativePartitionResult result = default;
        LedgerStatus status;
        fixed (byte* uidPointer = uidBytes)
        fixed (byte* parentPointer = parentBytes)
        {
            nativeTarget.ParentUid = target.Kind == LedgerTargetKind.Partition ? parentPointer : null;
            nativeTarget.ParentUidLength = target.Kind == LedgerTargetKind.Partition ? (ulong)TUid.ByteLength : 0;
            status = (LedgerStatus)_api.PushPartition(_instance, &nativeTarget, uidPointer, (ulong)TUid.ByteLength,
                capacity, &nativeAttributes, scratch.Workspace.Pointer, scratch.Workspace.ByteLength, &result);
        }
        return new LedgerPartitionPushResult(status, result.AllocationNumber);
    }

    public LedgerStatus UpdateUid(TUid uid, LedgerUpdate update)
    {
        NativeUpdate nativeUpdate = UpdateToNative(update);
        using CallScope call = EnterCall();
        Span<byte> bytes = stackalloc byte[TUid.ByteLength];
        uid.CopyTo(bytes);
        fixed (byte* uidPointer = bytes)
            return (LedgerStatus)_api.UpdateUid(_instance, uidPointer, (ulong)TUid.ByteLength, &nativeUpdate);
    }

    /// <summary>Closes a resource only when its outstanding leases permit safe release.</summary>
    public LedgerStatus Close(TUid uid, LedgerWorkspace? workspace = null) => ChangeByUid(uid, workspace, force: false);

    /// <summary>
    /// Invalidates all leases for this UID. The caller must stop every access to their regions
    /// and wait for their memory pins to be released before calling this method.
    /// </summary>
    public LedgerStatus ForceRelease(TUid uid, LedgerWorkspace? workspace = null) => ChangeByUid(uid, workspace, force: true);

    public LedgerStatus SetMode(uint table, LedgerTableMode mode)
    {
        if (!Enum.IsDefined(mode)) return LedgerStatus.InputError;
        using CallScope call = EnterCall();
        return (LedgerStatus)_api.SetMode(_instance, table, (uint)mode);
    }

    public LedgerTableView GetTableView(uint table)
    {
        using CallScope call = EnterCall();
        NativeTableView view = default;
        LedgerStatus status = (LedgerStatus)_api.GetTableView(_instance, table, &view);
        if (status != LedgerStatus.Ok) throw new LedgerException(status, "get table view");
        return new LedgerTableView(view.RegisteredBytes, view.SystemPageBytes);
    }

    /// <summary>The paired Native build contains the optional development query.</summary>
    public bool DevelopmentStatisticsAvailable => _api.GetDevelopmentStatistics != null;

    /// <summary>Reads a consistent, non-resetting table snapshot. No counters live in managed code.</summary>
    public LedgerDevelopmentStatistics GetDevelopmentStatistics(uint table)
    {
        using CallScope call = EnterCall();
        if (_api.GetDevelopmentStatistics == null)
            throw new NotSupportedException("This Native build has development allocation statistics disabled.");
        NativeDevelopmentStatistics value = default;
        LedgerStatus status = (LedgerStatus)_api.GetDevelopmentStatistics(_instance, table, &value);
        if (status != LedgerStatus.Ok) throw new LedgerException(status, "get development allocation statistics");
        return new(value.NewAllocations, value.SameEpochReleases, value.SameEpochAllocationBytes,
            value.SubpageAllocations, value.SubpageAllocationBytes);
    }

    public LedgerTickResult Tick(LedgerWorkspace? workspace = null)
    {
        using CallScope call = EnterCall();
        using WorkspaceUse scratch = EnterWorkspace(workspace, LedgerWorkspaceKind.Tick,
            _descriptor.TickWorkspaceSize, _descriptor.TickWorkspaceAlignment);
        NativeTickResult result = default;
        LedgerStatus status = (LedgerStatus)_api.Tick(_instance, scratch.Workspace.Pointer, scratch.Workspace.ByteLength, &result);
        if (status != LedgerStatus.Ok) throw new LedgerException(status, "tick");
        return new LedgerTickResult(result.Released, result.Scanned);
    }

    public LedgerSubscription<TUid> Subscribe(uint table, Action<LedgerNotice<TUid>> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        using CallScope call = EnterCall();
        var subscription = new LedgerSubscription<TUid>(this, callback);
        NativeSubscriptionHandle handle = default;
        lock (_subscriptionGate) _subscriptions.Add(subscription);
        bool registered = false;
        try
        {
            subscription.AllocateCallbackContext();
            LedgerStatus status = (LedgerStatus)_api.Subscribe(_instance, table,
                (delegate* unmanaged[Cdecl]<void*, byte*, ulong, ulong, void>)&NativeCallbackBridge.Dispatch,
                subscription.ContextPointer, &handle);
            if (status != LedgerStatus.Ok) throw new LedgerException(status, "subscribe");
            registered = true;
            subscription.SetHandle(handle);
            return subscription;
        }
        catch
        {
            if (!registered)
            {
                lock (_subscriptionGate) _subscriptions.Remove(subscription);
                subscription.ReleaseCallbackContext();
            }
            throw;
        }
    }

    /// <summary>Attempts normal destruction. A blocked owner remains open and can be used again.</summary>
    public LedgerStatus Destroy(LedgerWorkspace? workspace = null) => DestroyCore(forced: false, workspace);

    /// <summary>Explicitly invalidates all Native state after the caller has stopped every region access.</summary>
    public LedgerStatus ForceDispose(LedgerWorkspace? workspace = null) => DestroyCore(forced: true, workspace);

    public void Dispose()
    {
        LedgerStatus status = DestroyCore(forced: false, workspace: null);
        if (status != LedgerStatus.Ok) throw new LedgerException(status, "destroy");
    }

    internal LedgerEndResult EndUse(NativeLease lease, uint disposition)
    {
        if (NativeCallbackBridge.IsInCallback)
            throw new InvalidOperationException("Ledger callbacks must not reenter the ledger.");
        CallScope call;
        lock (_lifecycleGate)
        {
            while (_lifecycleState == 1) Monitor.Wait(_lifecycleGate);
            if (_lifecycleState == 2) return new LedgerEndResult(LedgerStatus.ForceReleased);
            checked { _activeCalls++; }
            call = new CallScope(this);
        }
        using (call)
        {
            int code = _api.EndUse(_instance, &lease, disposition);
            return new LedgerEndResult((LedgerStatus)code);
        }
    }

    internal (LedgerStatus Status, NativeBorrow Borrow) UpdateLease(NativeLease lease, LedgerUpdate update)
    {
        NativeUpdate nativeUpdate = UpdateToNative(update);
        using CallScope call = EnterCall();
        NativeBorrow borrow = default;
        LedgerStatus status = (LedgerStatus)_api.UpdateLease(_instance, &lease, &nativeUpdate, &borrow);
        return (status, borrow);
    }

    internal void Unsubscribe(LedgerSubscription<TUid> subscription, NativeSubscriptionHandle handle)
    {
        using CallScope call = EnterCall();
        LedgerStatus status = (LedgerStatus)_api.Unsubscribe(_instance, &handle);
        if (status != LedgerStatus.Ok) throw new LedgerException(status, "unsubscribe");
        lock (_subscriptionGate) _subscriptions.Remove(subscription);
    }

    private LedgerStatus ChangeByUid(TUid uid, LedgerWorkspace? workspace, bool force)
    {
        using CallScope call = EnterCall();
        using WorkspaceUse scratch = EnterWorkspace(workspace, LedgerWorkspaceKind.Operation,
            _descriptor.OperationWorkspaceSize, _descriptor.OperationWorkspaceAlignment);
        Span<byte> bytes = stackalloc byte[TUid.ByteLength];
        uid.CopyTo(bytes);
        fixed (byte* uidPointer = bytes)
        {
            return (LedgerStatus)(force
                ? _api.ForceRelease(_instance, uidPointer, (ulong)TUid.ByteLength, scratch.Workspace.Pointer, scratch.Workspace.ByteLength)
                : _api.Close(_instance, uidPointer, (ulong)TUid.ByteLength, scratch.Workspace.Pointer, scratch.Workspace.ByteLength));
        }
    }

    private LedgerStatus DestroyCore(bool forced, LedgerWorkspace? workspace)
    {
        if (NativeCallbackBridge.IsInCallback)
            throw new InvalidOperationException("Ledger callbacks must not reenter the ledger.");
        if (IsDestroyed) return LedgerStatus.Ok;
        using WorkspaceUse scratch = EnterWorkspace(workspace, LedgerWorkspaceKind.Destroy,
            _descriptor.DestroyWorkspaceSize, _descriptor.DestroyWorkspaceAlignment);

        lock (_lifecycleGate)
        {
            while (_lifecycleState == 1) Monitor.Wait(_lifecycleGate);
            if (_lifecycleState == 2) return LedgerStatus.Ok;
            _lifecycleState = 1;
            try
            {
                while (_activeCalls != 0) Monitor.Wait(_lifecycleGate);
            }
            catch
            {
                _lifecycleState = 0;
                Monitor.PulseAll(_lifecycleGate);
                throw;
            }
        }

        LedgerStatus status;
        try
        {
            status = (LedgerStatus)_api.Destroy(_instance, forced ? 1u : 0u, scratch.Workspace.Pointer, scratch.Workspace.ByteLength);
        }
        catch
        {
            ReopenAfterFailedDestroy();
            throw;
        }

        if (status != LedgerStatus.Ok)
        {
            ReopenAfterFailedDestroy();
            return status;
        }

        lock (_lifecycleGate)
        {
            Volatile.Write(ref _lifecycleState, 2);
            _instance = null;
            Monitor.PulseAll(_lifecycleGate);
        }

        lock (_subscriptionGate)
        {
            foreach (LedgerSubscription<TUid> subscription in _subscriptions)
                subscription.MarkOwnerDestroyed();
            _subscriptions.Clear();
        }

        _storage.Dispose();
        _api.Free();
        return LedgerStatus.Ok;
    }

    private void ReopenAfterFailedDestroy()
    {
        lock (_lifecycleGate)
        {
            Volatile.Write(ref _lifecycleState, 0);
            Monitor.PulseAll(_lifecycleGate);
        }
    }

    private LedgerWorkspace CreateWorkspace(LedgerWorkspaceKind kind, ulong size, ulong alignment)
    {
        lock (_lifecycleGate)
        {
            if (_lifecycleState != 0) throw new ObjectDisposedException(nameof(LedgerOwner<TUid>));
        }
        return new LedgerWorkspace(_workspaceToken, kind, size, alignment);
    }

    private CallScope EnterCall()
    {
        if (NativeCallbackBridge.IsInCallback)
            throw new InvalidOperationException("Ledger callbacks must not reenter the ledger.");
        lock (_lifecycleGate)
        {
            if (_lifecycleState != 0) throw new ObjectDisposedException(nameof(LedgerOwner<TUid>));
            checked { _activeCalls++; }
        }
        return new CallScope(this);
    }

    private void ExitCall()
    {
        lock (_lifecycleGate)
        {
            if (_activeCalls <= 0) throw new InvalidOperationException("Ledger call admission was unbalanced.");
            _activeCalls--;
            if (_activeCalls == 0) Monitor.PulseAll(_lifecycleGate);
        }
    }

    private WorkspaceUse EnterWorkspace(LedgerWorkspace? supplied, LedgerWorkspaceKind kind, ulong size, ulong alignment)
    {
        LedgerWorkspace actual = supplied ?? new LedgerWorkspace(_workspaceToken, kind, size, alignment);
        try
        {
            var use = new WorkspaceUse(actual, supplied is null);
            actual.Enter(_workspaceToken, kind);
            return use;
        }
        catch
        {
            if (supplied is null) actual.Dispose();
            throw;
        }
    }

    private LedgerLease<TUid> CreateLease(NativeBorrow borrow, bool loading)
    {
        if (borrow.Lease.Reserved != 0 || borrow.Length > (ulong)nuint.MaxValue || (borrow.Length != 0 && borrow.Data == null))
            throw new InvalidOperationException("Native returned an invalid lease or region record.");
        return new LedgerLease<TUid>(this, borrow.Lease, (byte*)borrow.Data, borrow.Length, loading);
    }

    private static NativeTarget TargetToNative(LedgerTarget<TUid> target, Span<byte> parentBytes)
    {
        if (!Enum.IsDefined(target.Kind)) throw new ArgumentOutOfRangeException(nameof(target));
        if (target.Kind == LedgerTargetKind.Table)
            return new NativeTarget { Kind = (uint)LedgerTargetKind.Table, Table = target.Table };
        target.ParentUid.CopyTo(parentBytes);
        return new NativeTarget { Kind = (uint)LedgerTargetKind.Partition, Table = 0 };
    }

    private static NativeAttributes AttributesToNative(LedgerAttributes attributes)
    {
        ValidateAttributes(attributes);
        return new NativeAttributes
        {
            Recoverability = (uint)attributes.Recoverability,
            RecoveryCostCoefficient = attributes.RecoveryCostCoefficient,
            AccessLossImpact = (uint)attributes.AccessLossImpact,
        };
    }

    private static void ValidateAttributes(LedgerAttributes attributes)
    {
        if (!Enum.IsDefined(attributes.Recoverability)) throw new ArgumentOutOfRangeException(nameof(attributes));
        if (!Enum.IsDefined(attributes.AccessLossImpact)) throw new ArgumentOutOfRangeException(nameof(attributes));
    }

    private static NativeUpdate UpdateToNative(LedgerUpdate update)
    {
        uint mask = 0;
        if (update.AllocationBytes.HasValue) mask |= 0x01;
        if (update.Recoverability.HasValue)
        {
            if (!Enum.IsDefined(update.Recoverability.Value)) throw new ArgumentOutOfRangeException(nameof(update));
            mask |= 0x02;
        }
        if (update.RecoveryCostCoefficient.HasValue) mask |= 0x04;
        if (update.AccessLossImpact.HasValue)
        {
            if (!Enum.IsDefined(update.AccessLossImpact.Value)) throw new ArgumentOutOfRangeException(nameof(update));
            mask |= 0x08;
        }
        return new NativeUpdate
        {
            PresentMask = mask,
            Recoverability = (uint)update.Recoverability.GetValueOrDefault(),
            RecoveryCostCoefficient = update.RecoveryCostCoefficient.GetValueOrDefault(),
            AccessLossImpact = (uint)update.AccessLossImpact.GetValueOrDefault(),
            AllocationBytes = update.AllocationBytes.GetValueOrDefault(),
        };
    }

    private readonly struct CallScope : IDisposable
    {
        private readonly LedgerOwner<TUid> _owner;
        internal CallScope(LedgerOwner<TUid> owner) => _owner = owner;
        public void Dispose() => _owner.ExitCall();
    }

    private sealed class WorkspaceUse : IDisposable
    {
        private readonly bool _ownsWorkspace;
        private LedgerWorkspace? _workspace;
        internal WorkspaceUse(LedgerWorkspace workspace, bool ownsWorkspace)
        {
            _workspace = workspace;
            _ownsWorkspace = ownsWorkspace;
        }
        internal LedgerWorkspace Workspace => _workspace ?? throw new ObjectDisposedException(nameof(WorkspaceUse));
        public void Dispose()
        {
            LedgerWorkspace? workspace = Interlocked.Exchange(ref _workspace, null);
            if (workspace is null) return;
            workspace.Exit();
            if (_ownsWorkspace) workspace.Dispose();
        }
    }
}
