using System.Runtime.InteropServices;

namespace ResourceManager.Adapter.LocalResources;

internal sealed class LocalResourceManagerSessionOwner
{
    internal LocalResourceManagerSessionOwner(
        NativeLocalResourceManagerSession session,
        ulong generation)
    {
        Session = session;
        Generation = generation;
    }

    internal NativeLocalResourceManagerSession Session { get; }
    internal ulong Generation { get; }
}

internal static class LocalResourceOperationAdmission
{
    internal const string ActiveCapabilityHandlerMessage =
        "A local resource manager operation cannot enter while a capability handler is active.";

    internal static async Task WaitAsync(
        SemaphoreSlim gate,
        Task capabilityHandlerStarted,
        Task processCapabilityHandlerStarted,
        Action validateAfterAdmission,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var linkedCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var gateWait = gate.WaitAsync(linkedCancellation.Token);
        var completed = await Task.WhenAny(
                gateWait,
                capabilityHandlerStarted,
                processCapabilityHandlerStarted)
            .ConfigureAwait(false);

        if (!ReferenceEquals(completed, gateWait) ||
            capabilityHandlerStarted.IsCompleted ||
            processCapabilityHandlerStarted.IsCompleted)
        {
            linkedCancellation.Cancel();
            var acquired = false;
            try
            {
                await gateWait.ConfigureAwait(false);
                acquired = true;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // The handler-start signal canceled this queued semaphore waiter.
            }

            if (acquired)
            {
                gate.Release();
            }
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException(ActiveCapabilityHandlerMessage);
        }

        await gateWait.ConfigureAwait(false);
        try
        {
            if (capabilityHandlerStarted.IsCompleted ||
                processCapabilityHandlerStarted.IsCompleted)
            {
                throw new InvalidOperationException(ActiveCapabilityHandlerMessage);
            }
            validateAfterAdmission();
            if (capabilityHandlerStarted.IsCompleted ||
                processCapabilityHandlerStarted.IsCompleted)
            {
                throw new InvalidOperationException(ActiveCapabilityHandlerMessage);
            }
        }
        catch
        {
            gate.Release();
            throw;
        }
    }

    internal static void Wait(
        SemaphoreSlim gate,
        Task capabilityHandlerStarted,
        Task processCapabilityHandlerStarted,
        Action validateAfterAdmission,
        CancellationToken cancellationToken = default)
        => WaitAsync(
                gate,
                capabilityHandlerStarted,
                processCapabilityHandlerStarted,
                validateAfterAdmission,
                cancellationToken)
            .ConfigureAwait(false)
            .GetAwaiter()
            .GetResult();

    internal static async ValueTask<T> WaitAsync<T>(
        SemaphoreSlim gate,
        Task capabilityHandlerStarted,
        Task processCapabilityHandlerStarted,
        Func<T> enterAfterAdmission,
        CancellationToken cancellationToken)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(enterAfterAdmission);
        T? result = null;
        Action publishAdmittedScope = () =>
        {
            result = enterAfterAdmission();
        };
        await WaitAsync(
                gate,
                capabilityHandlerStarted,
                processCapabilityHandlerStarted,
                publishAdmittedScope,
                cancellationToken)
            .ConfigureAwait(false);
        return result ?? throw new InvalidOperationException(
            "The local resource operation did not publish its admitted scope.");
    }
}

internal readonly record struct LocalResourceRegisteredTable(
    LocalResourceTableHandle Handle,
    LocalResourceDomain Domain);

public sealed unsafe class NativeLocalResourceManagerSession : IDisposable
{
    private const string ClaimedSessionMessage =
        "The native local resource manager session already has an active manager or pump owner.";
    private const byte PartitionAdmissionKindCreate = 1;
    private const byte PartitionAdmissionKindClose = 2;

    private static readonly object ContractSync = new();
    private static bool _contractValidated;
    private static long _nextManagerInstance = 1;

    private readonly object _sync = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly SemaphoreSlim _tableConcurrency;
    private readonly LocalResourcePlanProducer _planProducer = new();
    private readonly NativeLocalResourceIntent[] _scratch;
    private readonly NativeLocalResourceIntent[] _capacityOutput;
    private readonly NativeLocalResourceCapacityTablePlanView[] _capacityTableOutput;
    private readonly NativeLocalResourceIntent[] _modeOutput;
    private readonly NativeLocalResourceIntent[] _mergeOutput;
    private readonly NativeLocalResourcePartitionHandle[] _partitionVictimOutput;
    private readonly NativeLocalResourceIntent[] _partitionIntentOutput;
    private readonly NativeLocalResourcePartitionAdmissionScratch[] _partitionAdmissionScratch;
    private readonly LocalResourceUncertainExecution?[] _executionAuthorities;
    private readonly LocalResourceRegisteredTable?[] _registeredTables;
    private readonly void* _state;
    private readonly ulong _stateSize;
    private readonly ulong _managerInstanceId;
    private int _pendingExecutionCount;
    private ulong _nextOwnerGeneration;
    private LocalResourceManagerSessionOwner? _managerOwner;
    private NativeLocalResourceOperationToken _activeOperationToken;
    private LocalResourceManagerSessionOwner? _activeOperationOwner;
    private LocalResourceOperation? _activeStandaloneOperation;
    private LocalResourceUncertainExecution? _activeRecoveryExecution;
    private bool _operationAdmissionHeld;
    private int _activeCapabilityHandlerCount;
    private TaskCompletionSource _capabilityHandlerStarted = CreateHandlerStartedSignal();
    private bool _disposeSealed;
    private bool _disposed;

    public NativeLocalResourceManagerSession(LocalResourceManagerConfiguration configuration)
    {
        LocalResourceIntentExecutor.ThrowIfCapabilityHandlerLifecycleEntry();
        ArgumentNullException.ThrowIfNull(configuration);
        EnsureNativeContract();
        var native = CompileConfiguration(configuration);
        _tableConcurrency = new(configuration.MaximumConcurrentTables);
        ulong stateSize;
        ThrowIfFailed(
            NativeLocalResourceManagerInterop.rm_local_resource_manager_state_required_size(&native, &stateSize),
            "state-required-size");
        _stateSize = stateSize;
        var alignment = NativeLocalResourceManagerInterop.rm_local_resource_manager_state_alignment();
        if (alignment == 0 || (alignment & (alignment - 1)) != 0)
        {
            throw new InvalidOperationException($"Native local resource manager returned invalid alignment {alignment}.");
        }
        _scratch = new NativeLocalResourceIntent[configuration.ResourceCapacity];
        _capacityOutput = new NativeLocalResourceIntent[configuration.ResourceCapacity];
        _capacityTableOutput = new NativeLocalResourceCapacityTablePlanView[configuration.TableCapacity];
        _modeOutput = new NativeLocalResourceIntent[configuration.ResourceCapacity];
        _mergeOutput = new NativeLocalResourceIntent[configuration.ResourceCapacity];
        _partitionVictimOutput = new NativeLocalResourcePartitionHandle[
            configuration.EffectivePartitionCapacity];
        _partitionIntentOutput = new NativeLocalResourceIntent[configuration.ResourceCapacity];
        _partitionAdmissionScratch = new NativeLocalResourcePartitionAdmissionScratch[
            configuration.EffectivePartitionCapacity];
        _executionAuthorities = new LocalResourceUncertainExecution?[configuration.PendingCapacity];
        _registeredTables = new LocalResourceRegisteredTable?[configuration.TableCapacity];
        Configuration = configuration;
        Capabilities = new LocalResourceCapabilityRegistry(this);
        var instanceId = unchecked((ulong)Interlocked.Increment(ref _nextManagerInstance));
        _managerInstanceId = instanceId;
        _state = NativeMemory.AlignedAlloc(checked((nuint)_stateSize), alignment);
        if (_state is null)
        {
            throw new OutOfMemoryException($"Unable to allocate {_stateSize} bytes for local resource state.");
        }

        try
        {
            ThrowIfFailed(
                NativeLocalResourceManagerInterop.rm_local_resource_manager_state_initialize(
                    _state,
                    _stateSize,
                    &native,
                    instanceId),
                "state-initialize");
        }
        catch
        {
            NativeMemory.AlignedFree(_state);
            throw;
        }
    }

    public LocalResourceManagerConfiguration Configuration { get; }
    public LocalResourceCapabilityRegistry Capabilities { get; }
    public ulong ResidentBytes => _stateSize + checked((ulong)(
            (_scratch.Length + _capacityOutput.Length + _modeOutput.Length + _mergeOutput.Length +
                _partitionIntentOutput.Length) *
        sizeof(NativeLocalResourceIntent)) +
        checked((ulong)_partitionVictimOutput.Length *
            (ulong)sizeof(NativeLocalResourcePartitionHandle)) +
        checked((ulong)_partitionAdmissionScratch.Length *
            (ulong)sizeof(NativeLocalResourcePartitionAdmissionScratch)) +
        checked((ulong)_capacityTableOutput.Length *
            (ulong)sizeof(NativeLocalResourceCapacityTablePlanView)) +
        checked((ulong)_executionAuthorities.Length * (ulong)IntPtr.Size));
    public bool StartsBackgroundWorker => false;
    public static ulong PublishedLayoutFingerprint => ManagedLayoutFingerprint();
    public int PendingExecutionCount
    {
        get
        {
            lock (_sync)
            {
                return _pendingExecutionCount;
            }
        }
    }

    public LocalResourceOperation BeginOperation(LocalResourceOperationKind kind)
    {
        LocalResourceIntentExecutor.ThrowIfCapabilityHandlerLifecycleEntry();
        ValidateOperationKind(kind);
        var capabilityHandlerStarted = CaptureStandaloneOperationAdmission();
        LocalResourceOperation? operation = null;
        LocalResourceOperationAdmission.Wait(
            _operationGate,
            capabilityHandlerStarted,
            LocalResourceIntentExecutor.CaptureProcessCapabilityHandlerAdmission(),
            () =>
            {
                lock (_sync)
                {
                    ThrowIfDisposed();
                    RequireStandaloneAccess();
                    operation = BeginStandaloneOperationUnderLock(kind);
                }
            });
        return operation ?? throw new InvalidOperationException(
            "The native local resource operation was not created after admission.");
    }

    public ValueTask<LocalResourceOperation> BeginOperationAsync(
        LocalResourceOperationKind kind,
        CancellationToken cancellationToken = default)
    {
        LocalResourceIntentExecutor.ThrowIfCapabilityHandlerLifecycleEntry();
        ValidateOperationKind(kind);
        var capabilityHandlerStarted = CaptureStandaloneOperationAdmission();
        return LocalResourceOperationAdmission.WaitAsync(
            _operationGate,
            capabilityHandlerStarted,
            LocalResourceIntentExecutor.CaptureProcessCapabilityHandlerAdmission(),
            () =>
            {
                lock (_sync)
                {
                    ThrowIfDisposed();
                    RequireStandaloneAccess();
                    return BeginStandaloneOperationUnderLock(kind);
                }
            },
            cancellationToken);
    }

    public LocalResourceOperationSnapshot ReadOperation()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            return ReadOperationUnderLock();
        }
    }

    public IReadOnlyList<LocalResourceUncertainExecution> GetRecoveryRequiredExecutions()
    {
        LocalResourceIntentExecutor.ThrowIfCapabilityHandlerLifecycleEntry();
        lock (_sync)
        {
            ThrowIfDisposed();
            RequireStandaloneAccess();
            return SnapshotRecoveryRequiredExecutions();
        }
    }

    internal IReadOnlyList<LocalResourceUncertainExecution> GetRecoveryRequiredExecutionsOwned(
        LocalResourceManagerSessionOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        lock (_sync)
        {
            ThrowIfDisposed();
            RequireNoActiveCapabilityHandler();
            RequireManagerOwner(owner);
            return SnapshotRecoveryRequiredExecutions();
        }
    }

    internal LocalResourceManagerSessionOwner AcquireManagerOwner()
    {
        LocalResourceIntentExecutor.ThrowIfCapabilityHandlerLifecycleEntry();
        Task capabilityHandlerStarted;
        lock (_sync)
        {
            ThrowIfDisposed();
            RequireNoActiveCapabilityHandler();
            RequireUnclaimedSession();
            capabilityHandlerStarted = _capabilityHandlerStarted.Task;
        }
        LocalResourceOperationAdmission.Wait(
            _operationGate,
            capabilityHandlerStarted,
            LocalResourceIntentExecutor.CaptureProcessCapabilityHandlerAdmission(),
            () =>
            {
                lock (_sync)
                {
                    ThrowIfDisposed();
                    RequireNoActiveCapabilityHandler();
                    RequireUnclaimedSession();
                }
            });
        try
        {
            lock (_sync)
            {
                ThrowIfDisposed();
                RequireNoActiveCapabilityHandler();
                RequireUnclaimedSession();
                if (_pendingExecutionCount != 0)
                {
                    throw new LocalResourceRecoveryRequiredException(_pendingExecutionCount);
                }

                var generation = unchecked(_nextOwnerGeneration + 1);
                if (generation == 0)
                {
                    throw new InvalidOperationException(
                        "The native local resource manager owner generation is exhausted.");
                }
                _nextOwnerGeneration = generation;
                _managerOwner = new(this, generation);
                return _managerOwner;
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    internal Task WaitForManagerOperationAsync(
        LocalResourceManagerSessionOwner owner,
        CancellationToken cancellationToken,
        LocalResourceOperationKind kind = LocalResourceOperationKind.Maintenance)
    {
        ValidateOperationKind(kind);
        var capabilityHandlerStarted = CaptureManagerOperationAdmission(owner);
        return LocalResourceOperationAdmission.WaitAsync(
            _operationGate,
            capabilityHandlerStarted,
            LocalResourceIntentExecutor.CaptureProcessCapabilityHandlerAdmission(),
            () =>
            {
                lock (_sync)
                {
                    ThrowIfDisposed();
                    RequireNoActiveCapabilityHandler();
                    RequireManagerOwner(owner);
                    BeginManagerOperationUnderLock(owner, kind);
                }
            },
            cancellationToken);
    }

    internal void WaitForManagerOperation(
        LocalResourceManagerSessionOwner owner,
        LocalResourceOperationKind kind = LocalResourceOperationKind.Maintenance)
    {
        ValidateOperationKind(kind);
        var capabilityHandlerStarted = CaptureManagerOperationAdmission(owner);
        LocalResourceOperationAdmission.Wait(
            _operationGate,
            capabilityHandlerStarted,
            LocalResourceIntentExecutor.CaptureProcessCapabilityHandlerAdmission(),
            () =>
            {
                lock (_sync)
                {
                    ThrowIfDisposed();
                    RequireNoActiveCapabilityHandler();
                    RequireManagerOwner(owner);
                    BeginManagerOperationUnderLock(owner, kind);
                }
            });
    }

    internal void WaitForOwnedCloseAdmission(
        LocalResourceManagerSessionOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        _operationGate.Wait();
        try
        {
            ValidateManagerOwner(owner);
        }
        catch
        {
            _operationGate.Release();
            throw;
        }
    }

    internal void ReleaseOperation(LocalResourceManagerSessionOwner owner)
        => CompleteManagerOperation(owner);

    internal void ReleaseOwnedCloseAdmission()
        => _operationGate.Release();

    internal Task WaitForTableExecutionAsync(CancellationToken cancellationToken = default)
        => _tableConcurrency.WaitAsync(cancellationToken);

    internal void ReleaseTableExecution() => _tableConcurrency.Release();

    internal bool OwnsManagerInstance(LocalResourceTableHandle table)
        => table.Native.ManagerInstanceId != 0
            && table.Native.ManagerInstanceId == _managerInstanceId;

    internal IReadOnlyList<LocalResourceRegisteredTable> SnapshotRegisteredTablesOwned(
        LocalResourceManagerSessionOwner owner)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            RequireManagerOwner(owner);
            return _registeredTables
                .Where(static table => table.HasValue)
                .Select(static table => table!.Value)
                .ToArray();
        }
    }

    internal void ValidateManagerOwner(LocalResourceManagerSessionOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        lock (_sync)
        {
            ThrowIfDisposed();
            RequireNoActiveCapabilityHandler();
            RequireManagerOwner(owner);
        }
    }

    internal void ValidateStandaloneOperation(
        LocalResourceOperation operation,
        LocalResourceOperationKind? expectedKind = null)
    {
        ArgumentNullException.ThrowIfNull(operation);
        operation.RequireActive();
        lock (_sync)
        {
            ThrowIfDisposed();
            RequireStandaloneAccess();
            if (!_operationAdmissionHeld ||
                !ReferenceEquals(_activeStandaloneOperation, operation) ||
                !SameOperationToken(_activeOperationToken, operation.NativeToken))
            {
                throw new NativeLocalResourceManagerException(
                    "validate-standalone-operation",
                    (int)LocalResourceManagerError.StaleOperation);
            }
            if (expectedKind.HasValue &&
                _activeOperationToken.Kind != (byte)expectedKind.Value)
            {
                throw new NativeLocalResourceManagerException(
                    "validate-standalone-operation-kind",
                    (int)LocalResourceManagerError.InvalidOperationPhase);
            }
        }
    }

    internal void EnterCapabilityHandler(LocalResourceManagerSessionOwner? owner)
    {
        LocalResourceIntentExecutor.EnterProcessCapabilityHandler(this);
        try
        {
            TaskCompletionSource? handlerStarted = null;
            lock (_sync)
            {
                ThrowIfDisposed();
                RequireExecutionOwnership(owner);
                if (_activeCapabilityHandlerCount == 0)
                {
                    handlerStarted = _capabilityHandlerStarted;
                }
                _activeCapabilityHandlerCount = checked(_activeCapabilityHandlerCount + 1);
            }
            handlerStarted?.TrySetResult();
        }
        catch
        {
            LocalResourceIntentExecutor.ExitProcessCapabilityHandler(this);
            throw;
        }
    }

    internal void ExitCapabilityHandler()
    {
        try
        {
            lock (_sync)
            {
                if (_activeCapabilityHandlerCount <= 0)
                {
                    throw new InvalidOperationException(
                        "The local resource capability handler admission is unbalanced.");
                }
                _activeCapabilityHandlerCount--;
                if (_activeCapabilityHandlerCount == 0)
                {
                    _capabilityHandlerStarted = CreateHandlerStartedSignal();
                }
            }
        }
        finally
        {
            LocalResourceIntentExecutor.ExitProcessCapabilityHandler(this);
        }
    }

    internal void ReleaseManagerOwner(LocalResourceManagerSessionOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        lock (_sync)
        {
            RequireManagerOwner(owner);
            _managerOwner = null;
        }
    }

    public LocalResourceTableHandle RegisterTable(LocalResourceTableDefinition definition)
    {
        LocalResourceIntentExecutor.ThrowIfCapabilityHandlerLifecycleEntry();
        using var operation = BeginOperation(LocalResourceOperationKind.Maintenance);
        return RegisterTableScoped(operation, definition);
    }

    internal LocalResourceTableHandle RegisterTableScoped(
        LocalResourceOperation operation,
        LocalResourceTableDefinition definition)
    {
        ValidateStandaloneOperation(operation, LocalResourceOperationKind.Maintenance);
        return RegisterTableCore(null, definition);
    }

    internal LocalResourceTableHandle RegisterTableOwned(
        LocalResourceManagerSessionOwner owner,
        LocalResourceTableDefinition definition)
        => RegisterTableCore(owner, definition);

    private LocalResourceTableHandle RegisterTableCore(
        LocalResourceManagerSessionOwner? owner,
        LocalResourceTableDefinition definition)
    {
        ValidateTableDefinition(definition);
        var partitionReservation = definition.PartitionReservation;
        var native = new NativeLocalResourceTableSpec
        {
            AbiVersion = LocalResourceManagerProtocol.Version,
            StructSize = checked((uint)sizeof(NativeLocalResourceTableSpec)),
            TableId = ToNative(definition.TableId),
            TableIncarnation = definition.TableIncarnation,
            AdapterKey = ToNative(definition.AdapterKey),
            TopologyGeneration = definition.TopologyGeneration,
            Capacity = checked((uint)definition.Capacity),
            PartitionReservationStart = checked((uint)(partitionReservation?.Start ?? 0)),
            PartitionReservationCapacity = checked((uint)(partitionReservation?.Capacity ?? 0)),
            Domain = (byte)definition.Domain
        };
        var handle = default(NativeLocalResourceTableHandle);
        lock (_sync)
        {
            ThrowIfDisposed();
            RequireAccess(owner);
            var operation = RequireCurrentOperationUnderLock(
                owner,
                LocalResourceOperationKind.Maintenance);
            ThrowIfFailed(
                NativeLocalResourceManagerInterop.rm_local_resource_manager_register_table(
                    _state, _stateSize, &operation, &native, &handle),
                "register-table");
            var slotIndex = checked((int)handle.SlotIndex);
            if ((uint)slotIndex >= (uint)_registeredTables.Length
                || _registeredTables[slotIndex].HasValue)
            {
                throw new InvalidOperationException(
                    "Native local resource manager returned an invalid registered table slot.");
            }
            _registeredTables[slotIndex] = new(new(handle), definition.Domain);
        }
        return new(handle);
    }

    public void UnregisterTable(LocalResourceTableHandle handle)
    {
        LocalResourceIntentExecutor.ThrowIfCapabilityHandlerLifecycleEntry();
        using var operation = BeginOperation(LocalResourceOperationKind.Maintenance);
        UnregisterTableScoped(operation, handle);
    }

    internal void UnregisterTableScoped(
        LocalResourceOperation operation,
        LocalResourceTableHandle handle)
    {
        ValidateStandaloneOperation(operation, LocalResourceOperationKind.Maintenance);
        UnregisterTableCore(null, handle);
    }

    internal void UnregisterTableOwned(
        LocalResourceManagerSessionOwner owner,
        LocalResourceTableHandle handle)
        => UnregisterTableCore(owner, handle);

    private void UnregisterTableCore(
        LocalResourceManagerSessionOwner? owner,
        LocalResourceTableHandle handle)
    {
        var native = handle.Native;
        lock (_sync)
        {
            ThrowIfDisposed();
            RequireAccess(owner);
            var operation = RequireCurrentOperationUnderLock(
                owner,
                LocalResourceOperationKind.Maintenance);
            ThrowIfFailed(
                NativeLocalResourceManagerInterop.rm_local_resource_manager_unregister_table(
                    _state, _stateSize, &operation, &native),
                "unregister-table");
            var slotIndex = checked((int)native.SlotIndex);
            if ((uint)slotIndex >= (uint)_registeredTables.Length
                || _registeredTables[slotIndex] is not { } registered
                || !SameTableHandle(registered.Handle, handle))
            {
                if ((uint)slotIndex < (uint)_registeredTables.Length)
                {
                    _registeredTables[slotIndex] = null;
                }
                throw new InvalidOperationException(
                    "Managed registered table state diverged from native truth.");
            }
            _registeredTables[slotIndex] = null;
        }
        Capabilities.RemoveTable(handle);
    }

    private static bool SameTableHandle(
        LocalResourceTableHandle left,
        LocalResourceTableHandle right)
    {
        var leftNative = left.Native;
        var rightNative = right.Native;
        return leftNative.ManagerInstanceId == rightNative.ManagerInstanceId
            && leftNative.SlotGeneration == rightNative.SlotGeneration
            && leftNative.TableId.Low == rightNative.TableId.Low
            && leftNative.TableId.High == rightNative.TableId.High
            && leftNative.TableIncarnation == rightNative.TableIncarnation
            && leftNative.SlotIndex == rightNative.SlotIndex;
    }

    public LocalResourceHandle RegisterResource(
        LocalResourceTableHandle table,
        LocalResourceDefinition definition)
    {
        LocalResourceIntentExecutor.ThrowIfCapabilityHandlerLifecycleEntry();
        using var operation = BeginOperation(LocalResourceOperationKind.Maintenance);
        return RegisterResourceScoped(operation, table, definition);
    }

    internal LocalResourceHandle RegisterResourceScoped(
        LocalResourceOperation operation,
        LocalResourceTableHandle table,
        LocalResourceDefinition definition)
    {
        ValidateStandaloneOperation(operation, LocalResourceOperationKind.Maintenance);
        return RegisterResourceCore(null, table, definition);
    }

    internal LocalResourceHandle RegisterResourceOwned(
        LocalResourceManagerSessionOwner owner,
        LocalResourceTableHandle table,
        LocalResourceDefinition definition)
        => RegisterResourceCore(owner, table, definition);

    private LocalResourceHandle RegisterResourceCore(
        LocalResourceManagerSessionOwner? owner,
        LocalResourceTableHandle table,
        LocalResourceDefinition definition)
    {
        var nativeTable = table.Native;
        var nativeDefinition = CompileResourceDefinition(definition);
        var handle = default(NativeLocalResourceHandle);
        lock (_sync)
        {
            ThrowIfDisposed();
            RequireAccess(owner);
            var operation = RequireCurrentOperationUnderLock(
                owner,
                LocalResourceOperationKind.Maintenance);
            ThrowIfFailed(
                NativeLocalResourceManagerInterop.rm_local_resource_manager_register_resource(
                    _state, _stateSize, &operation, &nativeTable, &nativeDefinition, &handle),
                "register-resource");
        }
        return new(handle);
    }

    public LocalResourceHandle RegisterPartitionResource(
        LocalResourcePartitionHandle partition,
        int localOrdinal,
        LocalResourceDefinition definition)
    {
        LocalResourceIntentExecutor.ThrowIfCapabilityHandlerLifecycleEntry();
        using var operation = BeginOperation(LocalResourceOperationKind.Maintenance);
        return RegisterPartitionResourceScoped(operation, partition, localOrdinal, definition);
    }

    internal LocalResourceHandle RegisterPartitionResourceScoped(
        LocalResourceOperation operation,
        LocalResourcePartitionHandle partition,
        int localOrdinal,
        LocalResourceDefinition definition)
    {
        ValidateStandaloneOperation(operation, LocalResourceOperationKind.Maintenance);
        return RegisterPartitionResourceCore(null, partition, localOrdinal, definition);
    }

    internal LocalResourceHandle RegisterPartitionResourceOwned(
        LocalResourceManagerSessionOwner owner,
        LocalResourcePartitionHandle partition,
        int localOrdinal,
        LocalResourceDefinition definition)
        => RegisterPartitionResourceCore(owner, partition, localOrdinal, definition);

    private LocalResourceHandle RegisterPartitionResourceCore(
        LocalResourceManagerSessionOwner? owner,
        LocalResourcePartitionHandle partition,
        int localOrdinal,
        LocalResourceDefinition definition)
    {
        if (localOrdinal < 0) throw new ArgumentOutOfRangeException(nameof(localOrdinal));
        var nativePartition = partition.Native;
        var nativeDefinition = CompileResourceDefinition(definition);
        var handle = default(NativeLocalResourceHandle);
        lock (_sync)
        {
            ThrowIfDisposed();
            RequireAccess(owner);
            var operation = RequireCurrentOperationUnderLock(
                owner,
                LocalResourceOperationKind.Maintenance);
            ThrowIfFailed(
                NativeLocalResourceManagerInterop.rm_local_resource_manager_register_partition_resource(
                    _state,
                    _stateSize,
                    &operation,
                    &nativePartition,
                    checked((uint)localOrdinal),
                    &nativeDefinition,
                    &handle),
                "register-partition-resource");
        }
        return new(handle);
    }

    public void UpdateResource(LocalResourceHandle handle, LocalResourceDefinition definition)
    {
        LocalResourceIntentExecutor.ThrowIfCapabilityHandlerLifecycleEntry();
        using var operation = BeginOperation(LocalResourceOperationKind.Maintenance);
        UpdateResourceScoped(operation, handle, definition);
    }

    internal void UpdateResourceScoped(
        LocalResourceOperation operation,
        LocalResourceHandle handle,
        LocalResourceDefinition definition)
    {
        ValidateStandaloneOperation(operation, LocalResourceOperationKind.Maintenance);
        UpdateResourceCore(null, handle, definition);
    }

    internal void UpdateResourceOwned(
        LocalResourceManagerSessionOwner owner,
        LocalResourceHandle handle,
        LocalResourceDefinition definition)
        => UpdateResourceCore(owner, handle, definition);

    private void UpdateResourceCore(
        LocalResourceManagerSessionOwner? owner,
        LocalResourceHandle handle,
        LocalResourceDefinition definition)
    {
        var nativeHandle = handle.Native;
        var nativeDefinition = CompileResourceDefinition(definition);
        lock (_sync)
        {
            ThrowIfDisposed();
            RequireAccess(owner);
            var operation = RequireCurrentOperationUnderLock(
                owner,
                LocalResourceOperationKind.Maintenance);
            ThrowIfFailed(
                NativeLocalResourceManagerInterop.rm_local_resource_manager_update_resource(
                    _state, _stateSize, &operation, &nativeHandle, &nativeDefinition),
                "update-resource");
        }
    }

    public void UnregisterResource(LocalResourceHandle handle)
    {
        LocalResourceIntentExecutor.ThrowIfCapabilityHandlerLifecycleEntry();
        using var operation = BeginOperation(LocalResourceOperationKind.Maintenance);
        UnregisterResourceScoped(operation, handle);
    }

    internal void UnregisterResourceScoped(
        LocalResourceOperation operation,
        LocalResourceHandle handle)
    {
        ValidateStandaloneOperation(operation, LocalResourceOperationKind.Maintenance);
        UnregisterResourceCore(null, handle);
    }

    internal void UnregisterResourceOwned(
        LocalResourceManagerSessionOwner owner,
        LocalResourceHandle handle)
        => UnregisterResourceCore(owner, handle);

    private void UnregisterResourceCore(
        LocalResourceManagerSessionOwner? owner,
        LocalResourceHandle handle)
    {
        var native = handle.Native;
        lock (_sync)
        {
            ThrowIfDisposed();
            RequireAccess(owner);
            var operation = RequireCurrentOperationUnderLock(
                owner,
                LocalResourceOperationKind.Maintenance);
            ThrowIfFailed(
                NativeLocalResourceManagerInterop.rm_local_resource_manager_unregister_resource(
                    _state, _stateSize, &operation, &native),
                "unregister-resource");
        }
    }

    public void Touch(LocalResourceHandle handle, uint weight = 1)
    {
        LocalResourceIntentExecutor.ThrowIfCapabilityHandlerLifecycleEntry();
        using var operation = BeginOperation(LocalResourceOperationKind.Maintenance);
        TouchScoped(operation, handle, weight);
    }

    internal void TouchScoped(
        LocalResourceOperation operation,
        LocalResourceHandle handle,
        uint weight)
    {
        ValidateStandaloneOperation(operation, LocalResourceOperationKind.Maintenance);
        TouchCore(null, handle, weight);
    }

    internal void TouchOwned(
        LocalResourceManagerSessionOwner owner,
        LocalResourceHandle handle,
        uint weight = 1)
        => TouchCore(owner, handle, weight);

    private void TouchCore(
        LocalResourceManagerSessionOwner? owner,
        LocalResourceHandle handle,
        uint weight)
    {
        var native = handle.Native;
        lock (_sync)
        {
            ThrowIfDisposed();
            RequireAccess(owner);
            var operation = RequireCurrentOperationUnderLock(
                owner,
                LocalResourceOperationKind.Maintenance);
            ThrowIfFailed(
                NativeLocalResourceManagerInterop.rm_local_resource_manager_touch_resource(
                    _state, _stateSize, &operation, &native, weight),
                "touch-resource");
        }
    }

    public void AdvanceActivityEpoch(ulong count = 1)
    {
        LocalResourceIntentExecutor.ThrowIfCapabilityHandlerLifecycleEntry();
        using var operation = BeginOperation(LocalResourceOperationKind.Maintenance);
        AdvanceActivityEpochScoped(operation, count);
    }

    internal void AdvanceActivityEpochScoped(
        LocalResourceOperation operation,
        ulong count)
    {
        ValidateStandaloneOperation(operation, LocalResourceOperationKind.Maintenance);
        AdvanceActivityEpochCore(null, count);
    }

    internal void AdvanceActivityEpochOwned(
        LocalResourceManagerSessionOwner owner,
        ulong count = 1)
        => AdvanceActivityEpochCore(owner, count);

    private void AdvanceActivityEpochCore(
        LocalResourceManagerSessionOwner? owner,
        ulong count)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            RequireAccess(owner);
            var operation = RequireCurrentOperationUnderLock(owner);
            ThrowIfFailed(
                NativeLocalResourceManagerInterop.rm_local_resource_manager_advance_activity_epoch(
                    _state, _stateSize, &operation, count),
                "advance-activity-epoch");
        }
    }

    public LocalResourceUseLease BeginUse(LocalResourceHandle handle)
    {
        LocalResourceIntentExecutor.ThrowIfCapabilityHandlerLifecycleEntry();
        using var operation = BeginOperation(LocalResourceOperationKind.Maintenance);
        return BeginUseScoped(operation, handle);
    }

    internal LocalResourceUseLease BeginUseScoped(
        LocalResourceOperation operation,
        LocalResourceHandle handle)
    {
        ValidateStandaloneOperation(operation, LocalResourceOperationKind.Maintenance);
        return BeginUseCore(null, handle);
    }

    internal LocalResourceUseLease BeginUseOwned(
        LocalResourceManagerSessionOwner owner,
        LocalResourceHandle handle)
        => BeginUseCore(owner, handle);

    private LocalResourceUseLease BeginUseCore(
        LocalResourceManagerSessionOwner? owner,
        LocalResourceHandle handle)
    {
        var native = handle.Native;
        var lease = default(NativeLocalResourceUseLease);
        lock (_sync)
        {
            ThrowIfDisposed();
            RequireAccess(owner);
            var operation = RequireCurrentOperationUnderLock(
                owner,
                LocalResourceOperationKind.Maintenance);
            ThrowIfFailed(
                NativeLocalResourceManagerInterop.rm_local_resource_manager_begin_use(
                    _state, _stateSize, &operation, &native, &lease),
                "begin-use");
        }
        return new(lease);
    }

    public void EndUse(LocalResourceUseLease lease)
    {
        LocalResourceIntentExecutor.ThrowIfCapabilityHandlerLifecycleEntry();
        using var operation = BeginOperation(LocalResourceOperationKind.Maintenance);
        EndUseScoped(operation, lease);
    }

    internal void EndUseScoped(
        LocalResourceOperation operation,
        LocalResourceUseLease lease)
    {
        ValidateStandaloneOperation(operation, LocalResourceOperationKind.Maintenance);
        EndUseCore(null, lease);
    }

    internal void EndUseOwned(
        LocalResourceManagerSessionOwner owner,
        LocalResourceUseLease lease)
        => EndUseCore(owner, lease);

    private void EndUseCore(
        LocalResourceManagerSessionOwner? owner,
        LocalResourceUseLease lease)
    {
        var native = lease.Native;
        lock (_sync)
        {
            ThrowIfDisposed();
            RequireAccess(owner);
            var operation = RequireCurrentOperationUnderLock(
                owner,
                LocalResourceOperationKind.Maintenance);
            ThrowIfFailed(
                NativeLocalResourceManagerInterop.rm_local_resource_manager_end_use(
                    _state, _stateSize, &operation, &native),
                "end-use");
        }
    }

    internal LocalResourceCapacityPlanningResult PlanCapacityScoped(
        LocalResourceOperation operation,
        LocalResourceCapacityStrategy strategy)
    {
        ValidateStandaloneOperation(operation, LocalResourceOperationKind.Cleanup);
        return PlanCapacityDetailedCore(null, strategy);
    }

    internal LocalResourceCapacityPlanningResult PlanCapacityDetailed(
        LocalResourceManagerSessionOwner owner,
        LocalResourceCapacityStrategy strategy)
        => PlanCapacityDetailedCore(owner, strategy);

    private LocalResourceCapacityPlanningResult PlanCapacityDetailedCore(
        LocalResourceManagerSessionOwner? owner,
        LocalResourceCapacityStrategy strategy)
    {
        var input = new NativeLocalResourceCapacityPlanInput
        {
            AbiVersion = LocalResourceManagerProtocol.Version,
            StructSize = checked((uint)sizeof(NativeLocalResourceCapacityPlanInput)),
            Strategy = (byte)strategy
        };
        NativeLocalResourcePlanSummary summary;
        lock (_sync)
        {
            ThrowIfDisposed();
            RequirePlanningAccess(owner);
            var operation = RequireCurrentOperationUnderLock(
                owner,
                LocalResourceOperationKind.Cleanup);
            fixed (NativeLocalResourceIntent* scratch = _scratch)
            fixed (NativeLocalResourceIntent* output = _capacityOutput)
            fixed (NativeLocalResourceCapacityTablePlanView* tableOutput = _capacityTableOutput)
            {
                ThrowIfFailed(
                    NativeLocalResourceManagerInterop.rm_local_resource_manager_plan_capacity_detailed(
                        _state,
                        _stateSize,
                        &operation,
                        &input,
                        scratch,
                        checked((uint)_scratch.Length),
                        output,
                        checked((uint)_capacityOutput.Length),
                        tableOutput,
                        checked((uint)_capacityTableOutput.Length),
                        &summary),
                    "plan-capacity-detailed");
            }
            var plan = CopyPlan(LocalResourcePlanKind.Capacity, summary, _capacityOutput);
            var tables = CopyCapacityTablePlans(summary, _capacityTableOutput);
            ValidateCapacityTableBindings(plan, tables);
            return new(plan, tables);
        }
    }

    internal LocalResourcePlan PlanModeScoped(
        LocalResourceOperation operation,
        LocalResourceModeRequest request)
    {
        ValidateStandaloneOperation(operation, LocalResourceOperationKind.Cleanup);
        return PlanModeCore(null, request);
    }

    internal LocalResourcePlan PlanModeOwned(
        LocalResourceManagerSessionOwner owner,
        LocalResourceModeRequest request)
        => PlanModeCore(owner, request);

    private LocalResourcePlan PlanModeCore(
        LocalResourceManagerSessionOwner? owner,
        LocalResourceModeRequest request)
    {
        if (request.MaximumIntents <= 0 && request.Mode is not LocalResourceCleanupMode.Normal and not LocalResourceCleanupMode.Unrestricted)
        {
            throw new ArgumentOutOfRangeException(nameof(request));
        }
        var maximumIntents = Math.Min(
            Math.Max(0, request.MaximumIntents),
            Configuration.ResourceCapacity);
        var input = new NativeLocalResourceModePlanInput
        {
            AbiVersion = LocalResourceManagerProtocol.Version,
            StructSize = checked((uint)sizeof(NativeLocalResourceModePlanInput)),
            Table = request.Table.Native,
            MaximumIntents = checked((uint)maximumIntents),
            Mode = (byte)request.Mode
        };
        NativeLocalResourcePlanSummary summary;
        lock (_sync)
        {
            ThrowIfDisposed();
            RequirePlanningAccess(owner);
            var operation = RequireCurrentOperationUnderLock(
                owner,
                LocalResourceOperationKind.Cleanup);
            fixed (NativeLocalResourceIntent* output = _modeOutput)
            {
                ThrowIfFailed(
                    NativeLocalResourceManagerInterop.rm_local_resource_manager_plan_mode(
                        _state,
                        _stateSize,
                        &operation,
                        &input,
                        output,
                        checked((uint)maximumIntents),
                        &summary),
                    "plan-mode");
            }
            if (summary.IntentCount > checked((uint)maximumIntents))
            {
                throw new InvalidOperationException(
                    "Native local resource manager exceeded the requested mode intent limit.");
            }
            return CopyPlan(LocalResourcePlanKind.Mode, summary, _modeOutput);
        }
    }

    internal LocalResourcePlan MergePlansScoped(
        LocalResourceOperation operationScope,
        LocalResourcePlan capacity,
        LocalResourcePlan mode)
    {
        ValidateStandaloneOperation(operationScope, LocalResourceOperationKind.Cleanup);
        ArgumentNullException.ThrowIfNull(capacity);
        ArgumentNullException.ThrowIfNull(mode);
        ValidatePlanOwnership(capacity, LocalResourcePlanKind.Capacity, nameof(capacity));
        ValidatePlanOwnership(mode, LocalResourcePlanKind.Mode, nameof(mode));
        if (capacity.SnapshotGeneration != mode.SnapshotGeneration)
        {
            throw new ArgumentException(
                "Local resource plans from different snapshot generations cannot be merged.");
        }
        var capacityValues = ToNativeIntents(capacity.Intents);
        var modeValues = ToNativeIntents(mode.Intents);
        var capacityStamp = CreatePlanStamp(capacity);
        var modeStamp = CreatePlanStamp(mode);
        NativeLocalResourcePlanSummary summary;
        lock (_sync)
        {
            ThrowIfDisposed();
            RequireStandaloneAccess();
            var operation = RequireCurrentOperationUnderLock(
                null,
                LocalResourceOperationKind.Cleanup);
            if (capacity.OperationId != operation.OperationId ||
                capacity.OperationGeneration != operation.OperationGeneration ||
                mode.OperationId != operation.OperationId ||
                mode.OperationGeneration != operation.OperationGeneration)
            {
                throw new NativeLocalResourceManagerException(
                    "merge-plans-operation",
                    (int)LocalResourceManagerError.StaleOperation);
            }
            fixed (NativeLocalResourceIntent* capacityPointer = capacityValues)
            fixed (NativeLocalResourceIntent* modePointer = modeValues)
            fixed (NativeLocalResourceIntent* output = _mergeOutput)
            {
                ThrowIfFailed(
                    NativeLocalResourceManagerInterop.rm_local_resource_manager_merge_plans_v8(
                        &capacityStamp,
                        &modeStamp,
                        capacityPointer,
                        checked((uint)capacityValues.Length),
                        modePointer,
                        checked((uint)modeValues.Length),
                        output,
                        checked((uint)_mergeOutput.Length),
                        &summary),
                    "merge-intents");
            }
            return CopyPlan(
                LocalResourcePlanKind.Merged,
                summary,
                _mergeOutput,
                capacity.SnapshotGeneration,
                capacity.Intents,
                mode.Intents);
        }
    }

    internal LocalResourcePartitionAdmissionPlan PlanPartitionAdmissionScoped(
        LocalResourceOperation operation,
        LocalResourceTableHandle table,
        int capacity,
        LocalResourcePartitionHandle? parent = null)
    {
        ValidateStandaloneOperation(operation, LocalResourceOperationKind.PartitionAdmission);
        return PlanPartitionAdmissionCore(null, table, capacity, parent);
    }

    internal LocalResourcePartitionAdmissionPlan PlanPartitionAdmissionOwned(
        LocalResourceManagerSessionOwner owner,
        LocalResourceTableHandle table,
        int capacity,
        LocalResourcePartitionHandle? parent = null)
        => PlanPartitionAdmissionCore(owner, table, capacity, parent);

    private LocalResourcePartitionAdmissionPlan PlanPartitionAdmissionCore(
        LocalResourceManagerSessionOwner? owner,
        LocalResourceTableHandle table,
        int capacity,
        LocalResourcePartitionHandle? parent)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        var input = new NativeLocalResourcePartitionCreateInput
        {
            AbiVersion = LocalResourceManagerProtocol.Version,
            StructSize = checked((uint)sizeof(NativeLocalResourcePartitionCreateInput)),
            Table = table.Native,
            Parent = parent?.Native ?? default,
            Capacity = checked((uint)capacity),
            HasParent = parent.HasValue ? (byte)1 : (byte)0
        };
        NativeLocalResourcePartitionAdmissionSummary summary;
        NativeLocalResourcePartitionAdmissionTicket ticket;
        lock (_sync)
        {
            ThrowIfDisposed();
            RequirePlanningAccess(owner);
            var operation = RequireCurrentOperationUnderLock(
                owner,
                LocalResourceOperationKind.PartitionAdmission);
            fixed (NativeLocalResourcePartitionHandle* victims = _partitionVictimOutput)
            fixed (NativeLocalResourceIntent* intents = _partitionIntentOutput)
            fixed (NativeLocalResourcePartitionAdmissionScratch* scratch = _partitionAdmissionScratch)
            {
                ThrowIfFailed(
                    NativeLocalResourceManagerInterop.rm_local_resource_manager_plan_partition_admission(
                        _state,
                        _stateSize,
                        &operation,
                        &input,
                        victims,
                        checked((uint)_partitionVictimOutput.Length),
                        intents,
                        checked((uint)_partitionIntentOutput.Length),
                        scratch,
                        checked((uint)_partitionAdmissionScratch.Length),
                        &summary,
                        &ticket),
                    "plan-partition-admission");
            }
            return CopyPartitionAdmission(table, parent, capacity, summary, ticket);
        }
    }

    internal LocalResourcePartitionHandle CommitPartitionAdmissionScoped(
        LocalResourceOperation operation,
        LocalResourcePartitionAdmissionPlan plan)
    {
        ValidateStandaloneOperation(operation, LocalResourceOperationKind.PartitionAdmission);
        return CommitPartitionAdmissionCore(null, plan);
    }

    internal LocalResourcePartitionHandle CommitPartitionAdmissionOwned(
        LocalResourceManagerSessionOwner owner,
        LocalResourcePartitionAdmissionPlan plan)
        => CommitPartitionAdmissionCore(owner, plan);

    private LocalResourcePartitionHandle CommitPartitionAdmissionCore(
        LocalResourceManagerSessionOwner? owner,
        LocalResourcePartitionAdmissionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ValidatePartitionPlanOwnership(plan, nameof(plan));
        var ticket = plan.Ticket;
        var victims = plan.Victims.Select(static value => value.Native).ToArray();
        var output = default(NativeLocalResourcePartitionHandle);
        lock (_sync)
        {
            ThrowIfDisposed();
            RequireAccess(owner);
            var operation = RequireCurrentOperationUnderLock(
                owner,
                LocalResourceOperationKind.PartitionAdmission);
            if (ticket.OperationId != operation.OperationId ||
                ticket.OperationGeneration != operation.OperationGeneration)
            {
                throw new NativeLocalResourceManagerException(
                    "commit-partition-admission-operation",
                    (int)LocalResourceManagerError.StaleOperation);
            }
            fixed (NativeLocalResourcePartitionHandle* victimPointer = victims)
            {
                ThrowIfFailed(
                    NativeLocalResourceManagerInterop.rm_local_resource_manager_commit_partition_admission(
                        _state,
                        _stateSize,
                        &operation,
                        &ticket,
                        victimPointer,
                        checked((uint)victims.Length),
                        &output),
                    "commit-partition-admission");
            }
        }
        return new(output);
    }

    internal LocalResourcePartitionClosePlan PlanPartitionCloseScoped(
        LocalResourceOperation operation,
        LocalResourcePartitionHandle partition)
    {
        ValidateStandaloneOperation(operation, LocalResourceOperationKind.PartitionAdmission);
        return PlanPartitionCloseCore(null, partition);
    }

    internal LocalResourcePartitionClosePlan PlanPartitionCloseOwned(
        LocalResourceManagerSessionOwner owner,
        LocalResourcePartitionHandle partition)
        => PlanPartitionCloseCore(owner, partition);

    private LocalResourcePartitionClosePlan PlanPartitionCloseCore(
        LocalResourceManagerSessionOwner? owner,
        LocalResourcePartitionHandle partition)
    {
        var nativePartition = partition.Native;
        NativeLocalResourcePartitionAdmissionSummary summary;
        NativeLocalResourcePartitionAdmissionTicket ticket;
        lock (_sync)
        {
            ThrowIfDisposed();
            RequirePlanningAccess(owner);
            var operation = RequireCurrentOperationUnderLock(
                owner,
                LocalResourceOperationKind.PartitionAdmission);
            fixed (NativeLocalResourceIntent* intents = _partitionIntentOutput)
            {
                ThrowIfFailed(
                    NativeLocalResourceManagerInterop.rm_local_resource_manager_plan_partition_close(
                        _state,
                        _stateSize,
                        &operation,
                        &nativePartition,
                        intents,
                        checked((uint)_partitionIntentOutput.Length),
                        &summary,
                        &ticket),
                    "plan-partition-close");
            }
            return CopyPartitionClose(partition, summary, ticket);
        }
    }

    internal void CommitPartitionCloseScoped(
        LocalResourceOperation operation,
        LocalResourcePartitionClosePlan plan)
    {
        ValidateStandaloneOperation(operation, LocalResourceOperationKind.PartitionAdmission);
        CommitPartitionCloseCore(null, plan);
    }

    internal void CommitPartitionCloseOwned(
        LocalResourceManagerSessionOwner owner,
        LocalResourcePartitionClosePlan plan)
        => CommitPartitionCloseCore(owner, plan);

    private void CommitPartitionCloseCore(
        LocalResourceManagerSessionOwner? owner,
        LocalResourcePartitionClosePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ValidatePartitionClosePlanOwnership(plan, nameof(plan));
        var ticket = plan.Ticket;
        var target = plan.Target.Native;
        lock (_sync)
        {
            ThrowIfDisposed();
            RequireAccess(owner);
            var operation = RequireCurrentOperationUnderLock(
                owner,
                LocalResourceOperationKind.PartitionAdmission);
            if (ticket.OperationId != operation.OperationId ||
                ticket.OperationGeneration != operation.OperationGeneration)
            {
                throw new NativeLocalResourceManagerException(
                    "commit-partition-close-operation",
                    (int)LocalResourceManagerError.StaleOperation);
            }
            ThrowIfFailed(
                NativeLocalResourceManagerInterop.rm_local_resource_manager_commit_partition_close(
                    _state,
                    _stateSize,
                    &operation,
                    &ticket,
                    &target),
                "commit-partition-close");
        }
    }

    internal LocalResourcePartitionResourceAdmissionPlan PlanPartitionResourceAdmissionOwned(
        LocalResourceManagerSessionOwner owner,
        LocalResourcePartitionHandle partition,
        LocalResourceDefinition definition)
        => PlanPartitionResourceAdmissionCore(owner, partition, definition);

    private LocalResourcePartitionResourceAdmissionPlan PlanPartitionResourceAdmissionCore(
        LocalResourceManagerSessionOwner? owner,
        LocalResourcePartitionHandle partition,
        LocalResourceDefinition definition)
    {
        var nativePartition = partition.Native;
        var nativeDefinition = CompileResourceDefinition(definition);
        NativeLocalResourcePartitionResourceAdmission output;
        lock (_sync)
        {
            ThrowIfDisposed();
            RequirePlanningAccess(owner);
            var operation = RequireCurrentOperationUnderLock(
                owner,
                LocalResourceOperationKind.PartitionResourceAdmission);
            ThrowIfFailed(
                NativeLocalResourceManagerInterop.rm_local_resource_manager_plan_partition_resource_admission(
                    _state,
                    _stateSize,
                    &operation,
                    &nativePartition,
                    &nativeDefinition,
                    &output),
                "plan-partition-resource-admission");
            ValidatePartitionResourceAdmission(
                partition,
                nativeDefinition,
                output,
                operation);
        }
        return new(
            partition,
            checked((int)output.LocalOrdinal),
            output.RequiresRelease == 0 ? null : new LocalResourceIntent(output.Intent),
            output.SnapshotGeneration,
            output.StructureRevision,
            output);
    }

    internal LocalResourceHandle CommitPartitionResourceAdmissionOwned(
        LocalResourceManagerSessionOwner owner,
        LocalResourcePartitionResourceAdmissionPlan plan)
    {
        var admission = plan.Native;
        var output = default(NativeLocalResourceHandle);
        lock (_sync)
        {
            ThrowIfDisposed();
            RequireAccess(owner);
            var operation = RequireCurrentOperationUnderLock(
                owner,
                LocalResourceOperationKind.PartitionResourceAdmission);
            if (admission.OperationId != operation.OperationId ||
                admission.OperationGeneration != operation.OperationGeneration)
            {
                throw new NativeLocalResourceManagerException(
                    "commit-partition-resource-admission-operation",
                    (int)LocalResourceManagerError.StaleOperation);
            }
            ThrowIfFailed(
                NativeLocalResourceManagerInterop.rm_local_resource_manager_commit_partition_resource_admission(
                    _state,
                    _stateSize,
                    &operation,
                    &admission,
                    &output),
                "commit-partition-resource-admission");
        }
        return new(output);
    }

    public LocalResourcePartitionSnapshot ReadPartition(LocalResourcePartitionHandle partition)
    {
        var nativePartition = partition.Native;
        NativeLocalResourcePartitionView output;
        lock (_sync)
        {
            ThrowIfDisposed();
            ThrowIfFailed(
                NativeLocalResourceManagerInterop.rm_local_resource_manager_read_partition(
                    _state,
                    _stateSize,
                    &nativePartition,
                    &output),
                "read-partition");
            ValidatePartitionView(partition, output);
        }
        return new(
            partition,
            output.HasParent == 0 ? null : new LocalResourcePartitionHandle(output.Parent),
            output.SettledActivity,
            output.StructureRevision,
            checked((int)output.LocalStart),
            checked((int)output.Capacity),
            checked((int)output.DirectChildCount),
            checked((int)output.DescendantResourceCount),
            output.ReclaimProtected != 0);
    }

    public LocalResourcePartitionCell ReadPartitionCell(
        LocalResourcePartitionHandle partition,
        int localOrdinal)
    {
        if (localOrdinal < 0) throw new ArgumentOutOfRangeException(nameof(localOrdinal));
        var nativePartition = partition.Native;
        NativeLocalResourcePartitionCellView output;
        lock (_sync)
        {
            ThrowIfDisposed();
            ThrowIfFailed(
                NativeLocalResourceManagerInterop.rm_local_resource_manager_read_partition_cell(
                    _state,
                    _stateSize,
                    &nativePartition,
                    checked((uint)localOrdinal),
                    &output),
                "read-partition-cell");
            ValidatePartitionCellView(partition, checked((uint)localOrdinal), output);
        }
        var kind = (LocalResourcePartitionCellKind)output.Kind;
        return new(
            partition,
            localOrdinal,
            kind,
            kind == LocalResourcePartitionCellKind.Resource
                ? new LocalResourceHandle(output.Resource)
                : null,
            kind == LocalResourcePartitionCellKind.ChildPartition
                ? new LocalResourcePartitionHandle(output.ChildPartition)
                : null);
    }

    public LocalResourceTableCapacity ReadCapacity(LocalResourceTableHandle handle)
    {
        var native = handle.Native;
        NativeLocalResourceTableCapacityView output;
        lock (_sync)
        {
            ThrowIfDisposed();
            ThrowIfFailed(
                NativeLocalResourceManagerInterop.rm_local_resource_manager_read_table_capacity(
                    _state, _stateSize, &native, &output),
                "read-table-capacity");
        }
        return ToManagedCapacity(output);
    }

    internal void ValidateTableHandleOwned(
        LocalResourceManagerSessionOwner owner,
        LocalResourceTableHandle handle)
    {
        var native = handle.Native;
        NativeLocalResourceTableCapacityView output;
        lock (_sync)
        {
            ThrowIfDisposed();
            RequireManagerOwner(owner);
            ThrowIfFailed(
                NativeLocalResourceManagerInterop.rm_local_resource_manager_read_table_capacity(
                    _state, _stateSize, &native, &output),
                "validate-table-handle");
        }
    }

    public LocalResourceSnapshot ReadResource(LocalResourceHandle handle)
    {
        var native = handle.Native;
        NativeLocalResourceView output;
        lock (_sync)
        {
            ThrowIfDisposed();
            ThrowIfFailed(
                NativeLocalResourceManagerInterop.rm_local_resource_manager_read_resource(
                    _state, _stateSize, &native, &output),
                "read-resource");
        }
        return new(
            output.SizeBytes,
            output.SettledActivity,
            output.RowRevision,
            output.ActivityRevision,
            output.RecoveryCostCoefficient,
            (LocalResourceRecoverability)output.Recoverability,
            (LocalResourceAccessLossImpact)output.AccessLossImpact,
            output.ProtectedUse != 0,
            output.Pending != 0);
    }

    internal LocalResourceCapabilityHandle RegisterCapabilityCore(
        LocalResourceManagerSessionOwner? owner,
        LocalResourceTableHandle table,
        LocalResourceCapabilityDefinition definition)
    {
        ValidateCapabilityDefinition(definition);
        var nativeTable = table.Native;
        var spec = CompileCapabilityDefinition(definition);
        var handle = default(NativeLocalResourceCapabilityHandle);
        lock (_sync)
        {
            ThrowIfDisposed();
            RequireAccess(owner);
            var operation = RequireCurrentOperationUnderLock(
                owner,
                LocalResourceOperationKind.Maintenance);
            ThrowIfFailed(
                NativeLocalResourceManagerInterop.rm_local_resource_manager_register_capability(
                    _state, _stateSize, &operation, &nativeTable, &spec, &handle),
                "register-capability");
        }
        return new(handle);
    }

    internal LocalResourceCapabilityHandle ReplaceCapabilityCore(
        LocalResourceManagerSessionOwner? owner,
        LocalResourceCapabilityHandle handle,
        LocalResourceCapabilityDefinition definition)
    {
        ValidateCapabilityDefinition(definition);
        var nativeHandle = handle.Native;
        var spec = CompileCapabilityDefinition(definition);
        var replacement = default(NativeLocalResourceCapabilityHandle);
        lock (_sync)
        {
            ThrowIfDisposed();
            RequireAccess(owner);
            var operation = RequireCurrentOperationUnderLock(
                owner,
                LocalResourceOperationKind.Maintenance);
            ThrowIfFailed(
                NativeLocalResourceManagerInterop.rm_local_resource_manager_replace_capability(
                    _state, _stateSize, &operation, &nativeHandle, &spec, &replacement),
                "replace-capability");
        }
        return new(replacement);
    }

    internal void UnregisterCapabilityCore(
        LocalResourceManagerSessionOwner? owner,
        LocalResourceCapabilityHandle handle)
    {
        var native = handle.Native;
        lock (_sync)
        {
            ThrowIfDisposed();
            RequireAccess(owner);
            var operation = RequireCurrentOperationUnderLock(
                owner,
                LocalResourceOperationKind.Maintenance);
            ThrowIfFailed(
                NativeLocalResourceManagerInterop.rm_local_resource_manager_unregister_capability(
                    _state, _stateSize, &operation, &native),
                "unregister-capability");
        }
    }

    internal LocalResourceUncertainExecution BeginIntentCore(
        LocalResourceManagerSessionOwner? owner,
        LocalResourceIntent intent,
        LocalResourceExecutionContext context)
    {
        var execution = new LocalResourceUncertainExecution(this, context);
        var native = intent.Native;
        var token = default(NativeLocalResourceExecutionToken);
        lock (_sync)
        {
            ThrowIfDisposed();
            RequireExecutionOwnership(owner);
            var operation = RequireCurrentOperationUnderLock(owner);
            ThrowIfFailed(
                NativeLocalResourceManagerInterop.rm_local_resource_manager_begin_intent(
                    _state, _stateSize, &operation, &native, &token),
                "begin-intent");
            if (token.PendingSlotIndex >= _executionAuthorities.Length ||
                _executionAuthorities[token.PendingSlotIndex] is not null)
            {
                throw new InvalidOperationException(
                    "Native local resource manager returned an occupied execution authority slot.");
            }
            execution.BindToken(token);
            _executionAuthorities[token.PendingSlotIndex] = execution;
            _pendingExecutionCount = checked(_pendingExecutionCount + 1);
        }
        return execution;
    }

    internal LocalResourceCommitResult MarkEffectStartedCore(
        LocalResourceManagerSessionOwner? owner,
        LocalResourceUncertainExecution execution)
    {
        NativeLocalResourceCommitReceipt receipt;
        lock (_sync)
        {
            ThrowIfDisposed();
            RequireExecutionOwnership(owner);
            RequirePendingExecution();
            RequireExecutionAuthority(execution);
            var operation = RequireCurrentOperationUnderLock(owner);
            var token = execution.Token;
            ThrowIfFailed(
                NativeLocalResourceManagerInterop.rm_local_resource_manager_mark_effect_started(
                    _state, _stateSize, &operation, &token, &receipt),
                "mark-effect-started");
            ValidateCommitReceipt(operation, receipt);
            return ToManaged(receipt);
        }
    }

    internal LocalResourceCommitResult CommitEffectCore(
        LocalResourceManagerSessionOwner? owner,
        LocalResourceUncertainExecution execution,
        LocalResourceEffect effect)
    {
        var native = new NativeLocalResourceTypedEffect
        {
            AbiVersion = LocalResourceManagerProtocol.Version,
            StructSize = checked((uint)sizeof(NativeLocalResourceTypedEffect)),
            SizeBytesAfter = effect.SizeBytesAfter,
            ReleasedBytes = effect.ReleasedBytes,
            Outcome = (byte)effect.Outcome,
            Changes = (byte)effect.Changes
        };
        NativeLocalResourceCommitReceipt receipt;
        lock (_sync)
        {
            ThrowIfDisposed();
            RequireExecutionOwnership(owner);
            RequirePendingExecution();
            RequireExecutionAuthority(execution);
            var operation = RequireCurrentOperationUnderLock(owner);
            var token = execution.Token;
            ThrowIfFailed(
                NativeLocalResourceManagerInterop.rm_local_resource_manager_commit_effect(
                    _state, _stateSize, &operation, &token, &native, &receipt),
                "commit-effect");
            ValidateCommitReceipt(operation, receipt);
            var result = ToManaged(receipt);
            if (result.EffectUncertain)
            {
                execution.MarkRecoveryRequired();
            }
            else
            {
                CompleteExecutionAuthority(execution);
            }
            return result;
        }
    }

    internal LocalResourceCommitResult AbortIntentCore(
        LocalResourceManagerSessionOwner? owner,
        LocalResourceUncertainExecution execution)
    {
        NativeLocalResourceCommitReceipt receipt;
        lock (_sync)
        {
            ThrowIfDisposed();
            RequireExecutionOwnership(owner);
            RequirePendingExecution();
            RequireExecutionAuthority(execution);
            var operation = RequireCurrentOperationUnderLock(owner);
            var token = execution.Token;
            try
            {
                ThrowIfFailed(
                    NativeLocalResourceManagerInterop.rm_local_resource_manager_abort_intent(
                        _state, _stateSize, &operation, &token, &receipt),
                    "abort-intent");
            }
            catch
            {
                execution.MarkRecoveryRequired();
                throw;
            }
            ValidateCommitReceipt(operation, receipt);
            var result = ToManaged(receipt);
            if (result.EffectUncertain)
            {
                execution.MarkRecoveryRequired();
            }
            else
            {
                CompleteExecutionAuthority(execution);
            }
            return result;
        }
    }

    internal LocalResourceCommitResult MarkEffectUncertainCore(
        LocalResourceManagerSessionOwner? owner,
        LocalResourceUncertainExecution execution)
    {
        NativeLocalResourceCommitReceipt receipt;
        lock (_sync)
        {
            ThrowIfDisposed();
            RequireExecutionOwnership(owner);
            RequirePendingExecution();
            RequireExecutionAuthority(execution);
            var operation = RequireCurrentOperationUnderLock(owner);
            var token = execution.Token;
            try
            {
                ThrowIfFailed(
                    NativeLocalResourceManagerInterop.rm_local_resource_manager_mark_effect_uncertain(
                        _state, _stateSize, &operation, &token, &receipt),
                    "mark-effect-uncertain");
                ValidateCommitReceipt(operation, receipt);
                return ToManaged(receipt);
            }
            finally
            {
                execution.MarkRecoveryRequired();
            }
        }
    }

    public LocalResourceManagerCloseResult TryClose()
    {
        LocalResourceIntentExecutor.ThrowIfCapabilityHandlerLifecycleEntry();
        return TryClose(out _);
    }

    internal LocalResourceManagerCloseResult TryClose(out int pendingExecutionCount)
    {
        var clearCapabilities = false;
        Task capabilityHandlerStarted;
        lock (_sync)
        {
            if (_disposed)
            {
                pendingExecutionCount = 0;
                return LocalResourceManagerCloseResult.Closed;
            }
            RequireStandaloneAccess();
            capabilityHandlerStarted = _capabilityHandlerStarted.Task;
        }
        LocalResourceOperationAdmission.Wait(
            _operationGate,
            capabilityHandlerStarted,
            LocalResourceIntentExecutor.CaptureProcessCapabilityHandlerAdmission(),
            ValidateStandaloneCloseAdmission);
        try
        {
            lock (_sync)
            {
                if (_disposed)
                {
                    pendingExecutionCount = 0;
                    return LocalResourceManagerCloseResult.Closed;
                }
                RequireStandaloneAccess();
                if (_pendingExecutionCount != 0)
                {
                    pendingExecutionCount = _pendingExecutionCount;
                    return LocalResourceManagerCloseResult.RecoveryRequired;
                }
                pendingExecutionCount = 0;
                CompleteCloseUnderLock();
                clearCapabilities = true;
            }
        }
        finally
        {
            _operationGate.Release();
        }
        if (clearCapabilities) Capabilities.Clear();
        GC.SuppressFinalize(this);
        return LocalResourceManagerCloseResult.Closed;
    }

    public void Dispose()
    {
        LocalResourceIntentExecutor.ThrowIfCapabilityHandlerLifecycleEntry();
        if (TryClose(out var pendingExecutionCount) ==
            LocalResourceManagerCloseResult.RecoveryRequired)
        {
            throw new LocalResourceRecoveryRequiredException(pendingExecutionCount);
        }
    }

    internal LocalResourceManagerCloseResult TryCloseOwned(
        LocalResourceManagerSessionOwner owner,
        out int pendingExecutionCount)
    {
        var clearCapabilities = false;
        lock (_sync)
        {
            if (_disposed)
            {
                pendingExecutionCount = 0;
                return LocalResourceManagerCloseResult.Closed;
            }
            RequireNoActiveCapabilityHandler();
            RequireManagerOwner(owner);
            if (_pendingExecutionCount != 0)
            {
                pendingExecutionCount = _pendingExecutionCount;
                return LocalResourceManagerCloseResult.RecoveryRequired;
            }
            pendingExecutionCount = 0;
            _managerOwner = null;
            CompleteCloseUnderLock();
            clearCapabilities = true;
        }
        if (clearCapabilities) Capabilities.Clear();
        GC.SuppressFinalize(this);
        return LocalResourceManagerCloseResult.Closed;
    }

    internal void CloseAfterFailedManagerConstruction()
    {
        var clearCapabilities = false;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }
            RequireNoActiveCapabilityHandler();
            RequireUnclaimedSession();
            if (_pendingExecutionCount != 0)
            {
                throw new InvalidOperationException(
                    "A failed local resource manager construction retained pending execution authority.");
            }
            CompleteCloseUnderLock();
            clearCapabilities = true;
        }
        if (clearCapabilities) Capabilities.Clear();
        GC.SuppressFinalize(this);
    }

    internal static void ThrowIfFailed(int code, string operation)
    {
        if (code != 0) throw new NativeLocalResourceManagerException(operation, code);
    }

    private static NativeLocalResourceManagerConfig CompileConfiguration(
        LocalResourceManagerConfiguration configuration)
    {
        var capacityPolicy = configuration.EffectiveCapacityPolicy;
        if (configuration.Generation == 0 || configuration.TableCapacity <= 0 ||
            configuration.ResourceCapacity <= 0 || configuration.CapabilityCapacity <= 0 ||
            configuration.PendingCapacity <= 0 || configuration.UidBucketCapacity <= 0 ||
            configuration.EffectivePartitionCapacity <= 0 ||
            (configuration.UidBucketCapacity & (configuration.UidBucketCapacity - 1)) != 0 ||
            configuration.UidBucketCapacity < checked(configuration.ResourceCapacity * 2) ||
            configuration.MaximumConcurrentTables <= 0 ||
            configuration.MaximumConcurrentTables > configuration.TableCapacity ||
            configuration.SmoothReleaseIntervalEpochs <= 0 ||
            configuration.ActivityDecayDenominator == 0 ||
            configuration.ActivityDecayNumerator > configuration.ActivityDecayDenominator ||
            capacityPolicy.ConcentratedTriggerFreePercent == 0 ||
            capacityPolicy.SmoothEmergencyFreePercent >= capacityPolicy.ConcentratedTriggerFreePercent ||
            capacityPolicy.ConcentratedTriggerFreePercent >= capacityPolicy.SmoothTriggerFreePercent ||
            capacityPolicy.SmoothTriggerFreePercent >= capacityPolicy.ConcentratedTargetFreePercent ||
            capacityPolicy.ConcentratedTargetFreePercent > 100 ||
            capacityPolicy.SmoothMaximumReleasesPerInterval <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(configuration));
        }
        return new()
        {
            AbiVersion = LocalResourceManagerProtocol.Version,
            StructSize = checked((uint)sizeof(NativeLocalResourceManagerConfig)),
            ConfigurationGeneration = configuration.Generation,
            TableCapacity = checked((uint)configuration.TableCapacity),
            ResourceCapacity = checked((uint)configuration.ResourceCapacity),
            CapabilityCapacity = checked((uint)configuration.CapabilityCapacity),
            PendingCapacity = checked((uint)configuration.PendingCapacity),
            UidBucketCapacity = checked((uint)configuration.UidBucketCapacity),
            PartitionCapacity = checked((uint)configuration.EffectivePartitionCapacity),
            MaximumConcurrentTables = checked((uint)configuration.MaximumConcurrentTables),
            SmoothReleaseIntervalEpochs = checked((uint)configuration.SmoothReleaseIntervalEpochs),
            ActivityDecayNumerator = configuration.ActivityDecayNumerator,
            ActivityDecayDenominator = configuration.ActivityDecayDenominator,
            ConcentratedTriggerFreePercent = capacityPolicy.ConcentratedTriggerFreePercent,
            ConcentratedTargetFreePercent = capacityPolicy.ConcentratedTargetFreePercent,
            SmoothTriggerFreePercent = capacityPolicy.SmoothTriggerFreePercent,
            SmoothEmergencyFreePercent = capacityPolicy.SmoothEmergencyFreePercent,
            SmoothMaximumReleasesPerInterval = checked((uint)capacityPolicy.SmoothMaximumReleasesPerInterval)
        };
    }

    private static NativeLocalResourceSpec CompileResourceDefinition(LocalResourceDefinition definition)
    {
        if (definition.ResourceUid.IsEmpty ||
            definition.Recoverability is < LocalResourceRecoverability.NotRecoverable or > LocalResourceRecoverability.Recoverable ||
            definition.AccessLossImpact is < LocalResourceAccessLossImpact.Fatal or > LocalResourceAccessLossImpact.UnobservableNow)
        {
            throw new ArgumentOutOfRangeException(nameof(definition));
        }
        return new()
        {
            AbiVersion = LocalResourceManagerProtocol.Version,
            StructSize = checked((uint)sizeof(NativeLocalResourceSpec)),
            ResourceUid = ToNative(definition.ResourceUid),
            SizeBytes = definition.SizeBytes,
            RecoveryCostCoefficient = definition.RecoveryCostCoefficient,
            Recoverability = (byte)definition.Recoverability,
            AccessLossImpact = (byte)definition.AccessLossImpact
        };
    }

    private static NativeLocalResourceCapabilitySpec CompileCapabilityDefinition(
        LocalResourceCapabilityDefinition definition)
        => new()
        {
            AbiVersion = LocalResourceManagerProtocol.Version,
            StructSize = checked((uint)sizeof(NativeLocalResourceCapabilitySpec)),
            CapabilityId = definition.CapabilityId,
            ActionCode = definition.ActionCode,
            ExpectedEffects = (byte)definition.ExpectedEffects,
            Destructive = definition.Destructive ? (byte)1 : (byte)0
        };

    private static void ValidateCapabilityDefinition(LocalResourceCapabilityDefinition definition)
    {
        const LocalResourceEffects Supported = LocalResourceEffects.ReleasesLedgerSlot |
            LocalResourceEffects.ChangesSizeBytes;
        if (definition.CapabilityId == 0 || definition.ActionCode == 0 ||
            definition.ExpectedEffects == LocalResourceEffects.None ||
            (definition.ExpectedEffects & ~Supported) != 0 ||
            (definition.ExpectedEffects.HasFlag(LocalResourceEffects.ReleasesLedgerSlot) && !definition.Destructive))
        {
            throw new ArgumentOutOfRangeException(nameof(definition));
        }
    }

    private static void ValidateTableDefinition(LocalResourceTableDefinition definition)
    {
        var reservation = definition.PartitionReservation;
        if (definition.TableId.IsEmpty || definition.TableIncarnation == 0 || definition.Capacity <= 0 ||
            definition.Domain is < LocalResourceDomain.Memory or > LocalResourceDomain.Gpu ||
            (definition.Domain == LocalResourceDomain.Memory &&
                (!definition.AdapterKey.IsEmpty || definition.TopologyGeneration != 0)) ||
            (definition.Domain == LocalResourceDomain.Gpu &&
                (definition.AdapterKey.IsEmpty || definition.TopologyGeneration == 0)) ||
            (reservation is { } value &&
                (value.Start < 0 || value.Capacity <= 0 || value.Start >= definition.Capacity ||
                    value.Capacity > definition.Capacity - value.Start)))
        {
            throw new ArgumentOutOfRangeException(nameof(definition));
        }
    }

    private static NativeLocalResourceId ToNative(LocalResourceId value)
        => new() { Low = value.Low, High = value.High };

    private LocalResourcePartitionAdmissionPlan CopyPartitionAdmission(
        LocalResourceTableHandle table,
        LocalResourcePartitionHandle? parent,
        int requestedCapacity,
        NativeLocalResourcePartitionAdmissionSummary summary,
        NativeLocalResourcePartitionAdmissionTicket ticket)
    {
        if (summary.OperationId == 0 || summary.OperationGeneration == 0 ||
            summary.OperationId != _activeOperationToken.OperationId ||
            summary.OperationGeneration != _activeOperationToken.OperationGeneration ||
            summary.SnapshotGeneration == 0 ||
            summary.IntentCount > _partitionIntentOutput.Length ||
            summary.VictimCount > _partitionVictimOutput.Length ||
            summary.Capacity != requestedCapacity ||
            ticket.AbiVersion != LocalResourceManagerProtocol.Version ||
            ticket.StructSize != sizeof(NativeLocalResourcePartitionAdmissionTicket) ||
            ticket.ManagerInstanceId != _managerInstanceId ||
            ticket.OperationId != summary.OperationId ||
            ticket.OperationGeneration != summary.OperationGeneration ||
            ticket.AdmissionId == 0 ||
            ticket.ParentStructureRevision == 0 ||
            ticket.Capacity != summary.Capacity ||
            ticket.LocalStart != summary.LocalStart ||
            ticket.VictimCount != summary.VictimCount ||
            ticket.HasParent > 1 || ticket.Kind != PartitionAdmissionKindCreate ||
            ticket.Reserved0[0] != 0 || ticket.Reserved0[1] != 0 ||
            !SameNativeTableHandle(ticket.Table, table.Native) ||
            (parent.HasValue != (ticket.HasParent != 0)) ||
            (parent.HasValue
                ? !SameNativePartitionHandle(ticket.Parent, parent.Value.Native)
                : !IsZeroNativePartitionHandle(ticket.Parent)))
        {
            throw new InvalidOperationException(
                "Native local resource manager returned an invalid partition admission.");
        }

        var victimCount = checked((int)summary.VictimCount);
        var victims = new LocalResourcePartitionHandle[victimCount];
        var victimIdentities = new HashSet<(uint SlotIndex, ulong Generation)>();
        for (var index = 0; index < victimCount; index++)
        {
            var victim = _partitionVictimOutput[index];
            ValidateNativePartitionHandle(victim, table.Native);
            if (!victimIdentities.Add((victim.PartitionSlotIndex, victim.PartitionSlotGeneration)))
            {
                throw new InvalidOperationException(
                    "Native local resource manager returned a duplicate partition victim.");
            }
            victims[index] = new(victim);
        }

        var intentCount = checked((int)summary.IntentCount);
        var intents = new LocalResourceIntent[intentCount];
        for (var index = 0; index < intentCount; index++)
        {
            var intent = _partitionIntentOutput[index];
            ValidateNativePlanIntent(
                intent,
                LocalResourcePlanKind.Partition,
                summary.OperationId,
                summary.OperationGeneration,
                summary.SnapshotGeneration);
            if (!victimIdentities.Contains((
                intent.ReclaimPartitionSlotIndex,
                intent.ReclaimPartitionSlotGeneration)))
            {
                throw new InvalidOperationException(
                    "Native partition intent is not bound to a published victim.");
            }
            intents[index] = new(intent);
        }
        return new(
            _planProducer,
            _managerInstanceId,
            ticket,
            victims,
            intents,
            summary.SnapshotGeneration,
            checked((int)summary.LocalStart),
            checked((int)summary.Capacity));
    }

    private LocalResourcePartitionClosePlan CopyPartitionClose(
        LocalResourcePartitionHandle target,
        NativeLocalResourcePartitionAdmissionSummary summary,
        NativeLocalResourcePartitionAdmissionTicket ticket)
    {
        if (summary.OperationId == 0 || summary.OperationGeneration == 0 ||
            summary.OperationId != _activeOperationToken.OperationId ||
            summary.OperationGeneration != _activeOperationToken.OperationGeneration ||
            summary.SnapshotGeneration == 0 ||
            summary.IntentCount > _partitionIntentOutput.Length ||
            summary.VictimCount != 1 ||
            summary.Capacity == 0 ||
            ticket.AbiVersion != LocalResourceManagerProtocol.Version ||
            ticket.StructSize != sizeof(NativeLocalResourcePartitionAdmissionTicket) ||
            ticket.ManagerInstanceId != _managerInstanceId ||
            ticket.OperationId != summary.OperationId ||
            ticket.OperationGeneration != summary.OperationGeneration ||
            ticket.AdmissionId == 0 ||
            ticket.ParentStructureRevision == 0 ||
            ticket.Capacity != summary.Capacity ||
            ticket.LocalStart != summary.LocalStart ||
            ticket.VictimCount != 1 ||
            ticket.HasParent > 1 || ticket.Kind != PartitionAdmissionKindClose ||
            ticket.Reserved0[0] != 0 || ticket.Reserved0[1] != 0 ||
            !SameNativeTableHandle(ticket.Table, target.TableHandle.Native) ||
            ((ticket.HasParent == 0) != IsZeroNativePartitionHandle(ticket.Parent)))
        {
            throw new InvalidOperationException(
                "Native local resource manager returned an invalid partition close admission.");
        }

        var intentCount = checked((int)summary.IntentCount);
        var intents = new LocalResourceIntent[intentCount];
        for (var index = 0; index < intentCount; index++)
        {
            var intent = _partitionIntentOutput[index];
            ValidateNativePlanIntent(
                intent,
                LocalResourcePlanKind.Partition,
                summary.OperationId,
                summary.OperationGeneration,
                summary.SnapshotGeneration);
            if (intent.ReclaimPartitionSlotIndex != target.Native.PartitionSlotIndex ||
                intent.ReclaimPartitionSlotGeneration !=
                target.Native.PartitionSlotGeneration)
            {
                throw new InvalidOperationException(
                    "Native partition close intent is not bound to the target partition.");
            }
            intents[index] = new(intent);
        }
        return new(
            _planProducer,
            _managerInstanceId,
            ticket,
            target,
            intents,
            summary.SnapshotGeneration,
            checked((int)summary.LocalStart),
            checked((int)summary.Capacity));
    }

    private void ValidatePartitionPlanOwnership(
        LocalResourcePartitionAdmissionPlan plan,
        string parameterName)
    {
        if (!ReferenceEquals(plan.Producer, _planProducer) ||
            plan.ManagerInstanceId != _managerInstanceId ||
            plan.SnapshotGeneration == 0 ||
            plan.Ticket.ManagerInstanceId != _managerInstanceId ||
            plan.Ticket.OperationId == 0 ||
            plan.Ticket.OperationGeneration == 0 ||
            plan.Ticket.AdmissionId == 0 ||
            plan.Ticket.VictimCount != plan.Victims.Count ||
            plan.Ticket.Capacity != plan.Capacity ||
            plan.Ticket.LocalStart != plan.LocalStart)
        {
            throw new ArgumentException(
                "The partition admission belongs to a different manager session.",
                parameterName);
        }
        for (var index = 0; index < plan.Intents.Count; index++)
        {
            var intent = plan.Intents[index].Native;
            if (intent.OperationId != plan.Ticket.OperationId ||
                intent.OperationGeneration != plan.Ticket.OperationGeneration ||
                intent.SnapshotGeneration != plan.SnapshotGeneration)
            {
                throw new ArgumentException(
                    "The partition admission contains an intent from another operation or snapshot.",
                    parameterName);
            }
        }
    }

    private void ValidatePartitionClosePlanOwnership(
        LocalResourcePartitionClosePlan plan,
        string parameterName)
    {
        if (!ReferenceEquals(plan.Producer, _planProducer) ||
            plan.ManagerInstanceId != _managerInstanceId ||
            plan.SnapshotGeneration == 0 ||
            plan.Ticket.ManagerInstanceId != _managerInstanceId ||
            plan.Ticket.OperationId == 0 ||
            plan.Ticket.OperationGeneration == 0 ||
            plan.Ticket.AdmissionId == 0 ||
            plan.Ticket.Kind != PartitionAdmissionKindClose ||
            plan.Ticket.VictimCount != 1 ||
            plan.Ticket.Capacity != plan.Capacity ||
            plan.Ticket.LocalStart != plan.LocalStart ||
            !SameNativeTableHandle(plan.Ticket.Table, plan.Target.TableHandle.Native))
        {
            throw new ArgumentException(
                "The partition close admission belongs to a different manager session.",
                parameterName);
        }
        for (var index = 0; index < plan.Intents.Count; index++)
        {
            var intent = plan.Intents[index].Native;
            if (intent.OperationId != plan.Ticket.OperationId ||
                intent.OperationGeneration != plan.Ticket.OperationGeneration ||
                intent.SnapshotGeneration != plan.SnapshotGeneration ||
                intent.ReclaimPartitionSlotIndex != plan.Target.Native.PartitionSlotIndex ||
                intent.ReclaimPartitionSlotGeneration !=
                plan.Target.Native.PartitionSlotGeneration)
            {
                throw new ArgumentException(
                    "The partition close admission contains an intent from another operation, snapshot, or partition.",
                    parameterName);
            }
        }
    }

    private void ValidatePartitionResourceAdmission(
        LocalResourcePartitionHandle partition,
        NativeLocalResourceSpec definition,
        NativeLocalResourcePartitionResourceAdmission output,
        NativeLocalResourceOperationToken operation)
    {
        if (!SameNativePartitionHandle(partition.Native, output.Partition) ||
            !SameNativeResourceSpec(definition, output.Resource) ||
            output.OperationId != operation.OperationId ||
            output.OperationGeneration != operation.OperationGeneration ||
            output.SnapshotGeneration == 0 || output.StructureRevision == 0 ||
            output.RequiresRelease > 1 ||
            output.Reserved0[0] != 0 || output.Reserved0[1] != 0 ||
            output.Reserved0[2] != 0)
        {
            throw new InvalidOperationException(
                "Native local resource manager returned an invalid partition resource admission.");
        }
        if (output.RequiresRelease == 0)
        {
            if (output.Intent.AbiVersion != 0 || output.Intent.StructSize != 0 ||
                output.Intent.Resource.ManagerInstanceId != 0 || output.Intent.ReasonFlags != 0 ||
                output.Intent.ReclaimPartitionSlotGeneration != 0 ||
                output.Intent.ReclaimPartitionSlotIndex != 0 || output.Intent.Reserved1 != 0)
            {
                throw new InvalidOperationException(
                    "A no-release partition resource admission contained a non-empty intent.");
            }
            return;
        }

        ValidateNativePlanIntent(
            output.Intent,
            LocalResourcePlanKind.Partition,
            output.OperationId,
            output.OperationGeneration,
            output.SnapshotGeneration);
        if (output.Intent.ReclaimPartitionSlotGeneration != partition.Generation ||
            output.Intent.ReclaimPartitionSlotIndex != partition.SlotIndex)
        {
            throw new InvalidOperationException(
                "Partition resource admission intent is not bound to its target partition.");
        }
    }

    private static bool SameNativeResourceSpec(
        NativeLocalResourceSpec left,
        NativeLocalResourceSpec right)
        => left.AbiVersion == right.AbiVersion &&
            left.StructSize == right.StructSize &&
            left.ResourceUid.Low == right.ResourceUid.Low &&
            left.ResourceUid.High == right.ResourceUid.High &&
            left.SizeBytes == right.SizeBytes &&
            left.RecoveryCostCoefficient == right.RecoveryCostCoefficient &&
            left.Recoverability == right.Recoverability &&
            left.AccessLossImpact == right.AccessLossImpact;

    private void ValidatePartitionView(
        LocalResourcePartitionHandle partition,
        NativeLocalResourcePartitionView output)
    {
        if (!SameNativePartitionHandle(partition.Native, output.Partition) ||
            output.StructureRevision == 0 || output.Capacity == 0 ||
            output.DirectChildCount > Configuration.EffectivePartitionCapacity ||
            output.DescendantResourceCount > Configuration.ResourceCapacity ||
            output.HasParent > 1 || output.ReclaimProtected > 1 ||
            output.Reserved0[0] != 0 || output.Reserved0[1] != 0 ||
            output.Reserved0[2] != 0 || output.Reserved0[3] != 0 ||
            output.Reserved0[4] != 0 || output.Reserved0[5] != 0 ||
            (output.HasParent == 0
                ? !IsZeroNativePartitionHandle(output.Parent)
                : IsZeroNativePartitionHandle(output.Parent)))
        {
            throw new InvalidOperationException(
                "Native local resource manager returned an invalid partition view.");
        }
        if (output.HasParent != 0)
        {
            ValidateNativePartitionHandle(output.Parent, new()
            {
                ManagerInstanceId = partition.Native.ManagerInstanceId,
                SlotGeneration = partition.Native.TableSlotGeneration,
                TableId = partition.Native.TableId,
                TableIncarnation = partition.Native.TableIncarnation,
                SlotIndex = partition.Native.TableSlotIndex
            });
        }
    }

    private void ValidatePartitionCellView(
        LocalResourcePartitionHandle partition,
        uint localOrdinal,
        NativeLocalResourcePartitionCellView output)
    {
        var kind = (LocalResourcePartitionCellKind)output.Kind;
        if (!SameNativePartitionHandle(partition.Native, output.Partition) ||
            output.LocalOrdinal != localOrdinal || !Enum.IsDefined(kind) ||
            output.Reserved0[0] != 0 || output.Reserved0[1] != 0 ||
            output.Reserved0[2] != 0 ||
            (kind == LocalResourcePartitionCellKind.Empty &&
                (!IsZeroNativeResourceHandle(output.Resource) ||
                    !IsZeroNativePartitionHandle(output.ChildPartition))) ||
            (kind == LocalResourcePartitionCellKind.Resource &&
                (IsZeroNativeResourceHandle(output.Resource) ||
                    !IsZeroNativePartitionHandle(output.ChildPartition))) ||
            (kind == LocalResourcePartitionCellKind.ChildPartition &&
                (!IsZeroNativeResourceHandle(output.Resource) ||
                    IsZeroNativePartitionHandle(output.ChildPartition))))
        {
            throw new InvalidOperationException(
                "Native local resource manager returned an invalid partition cell view.");
        }
    }

    private void ValidateNativePartitionHandle(
        NativeLocalResourcePartitionHandle handle,
        NativeLocalResourceTableHandle table)
    {
        if (handle.ManagerInstanceId != _managerInstanceId ||
            handle.TableSlotGeneration == 0 || handle.PartitionSlotGeneration == 0 ||
            handle.PartitionSlotIndex >= Configuration.EffectivePartitionCapacity ||
            handle.TableSlotIndex >= Configuration.TableCapacity ||
            handle.TableSlotIndex != table.SlotIndex ||
            handle.TableSlotGeneration != table.SlotGeneration ||
            handle.TableId.Low != table.TableId.Low || handle.TableId.High != table.TableId.High ||
            handle.TableIncarnation != table.TableIncarnation)
        {
            throw new InvalidOperationException(
                "Native local resource manager returned an invalid partition handle.");
        }
    }

    private static bool SameNativeTableHandle(
        NativeLocalResourceTableHandle left,
        NativeLocalResourceTableHandle right)
        => left.ManagerInstanceId == right.ManagerInstanceId &&
            left.SlotGeneration == right.SlotGeneration &&
            left.TableId.Low == right.TableId.Low && left.TableId.High == right.TableId.High &&
            left.TableIncarnation == right.TableIncarnation &&
            left.SlotIndex == right.SlotIndex && left.Reserved0 == right.Reserved0;

    private static bool SameNativePartitionHandle(
        NativeLocalResourcePartitionHandle left,
        NativeLocalResourcePartitionHandle right)
        => left.ManagerInstanceId == right.ManagerInstanceId &&
            left.TableSlotGeneration == right.TableSlotGeneration &&
            left.PartitionSlotGeneration == right.PartitionSlotGeneration &&
            left.TableId.Low == right.TableId.Low && left.TableId.High == right.TableId.High &&
            left.TableIncarnation == right.TableIncarnation &&
            left.TableSlotIndex == right.TableSlotIndex &&
            left.PartitionSlotIndex == right.PartitionSlotIndex;

    private static bool IsZeroNativePartitionHandle(NativeLocalResourcePartitionHandle value)
        => value.ManagerInstanceId == 0 && value.TableSlotGeneration == 0 &&
            value.PartitionSlotGeneration == 0 && value.TableId.Low == 0 &&
            value.TableId.High == 0 && value.TableIncarnation == 0 &&
            value.TableSlotIndex == 0 && value.PartitionSlotIndex == 0;

    private static bool IsZeroNativeResourceHandle(NativeLocalResourceHandle value)
        => value.ManagerInstanceId == 0 && value.TableSlotGeneration == 0 &&
            value.ResourceSlotGeneration == 0 && value.TableId.Low == 0 &&
            value.TableId.High == 0 && value.TableIncarnation == 0 &&
            value.ResourceUid.Low == 0 && value.ResourceUid.High == 0 &&
            value.TableSlotIndex == 0 && value.ResourceSlotIndex == 0;

    private LocalResourcePlan CopyPlan(
        LocalResourcePlanKind kind,
        NativeLocalResourcePlanSummary summary,
        NativeLocalResourceIntent[] source,
        ulong? expectedSnapshotGeneration = null,
        IReadOnlyList<LocalResourceIntent>? mergedCapacityInput = null,
        IReadOnlyList<LocalResourceIntent>? mergedModeInput = null)
    {
        if (summary.OperationId == 0 || summary.OperationGeneration == 0 ||
            summary.OperationId != _activeOperationToken.OperationId ||
            summary.OperationGeneration != _activeOperationToken.OperationGeneration ||
            summary.SnapshotGeneration == 0 ||
            (expectedSnapshotGeneration.HasValue &&
                summary.SnapshotGeneration != expectedSnapshotGeneration.Value) ||
            summary.Reserved0 != 0 || summary.IntentCount > source.Length)
        {
            throw new InvalidOperationException(
                "Native local resource manager returned an invalid plan summary.");
        }
        if (kind == LocalResourcePlanKind.Merged &&
            (summary.TriggeredTableCount != 0 || summary.StalledTableCount != 0 ||
                summary.EmergencyTableCount != 0))
        {
            throw new InvalidOperationException(
                "Native local resource manager returned capacity-only counts for a merged plan.");
        }

        var count = checked((int)summary.IntentCount);
        var output = new LocalResourceIntent[count];
        var tables = new HashSet<(
            ulong ManagerInstanceId,
            uint TableSlotIndex,
            ulong TableSlotGeneration,
            ulong TableIdLow,
            ulong TableIdHigh,
            ulong TableIncarnation)>();
        for (var index = 0; index < count; index++)
        {
            ValidateNativePlanIntent(
                source[index],
                kind,
                summary.OperationId,
                summary.OperationGeneration,
                summary.SnapshotGeneration);
            output[index] = new(source[index]);
            tables.Add((
                source[index].Resource.ManagerInstanceId,
                source[index].Resource.TableSlotIndex,
                source[index].Resource.TableSlotGeneration,
                source[index].Resource.TableId.Low,
                source[index].Resource.TableId.High,
                source[index].Resource.TableIncarnation));
        }
        if (kind == LocalResourcePlanKind.Merged &&
            summary.AffectedTableCount != tables.Count)
        {
            throw new InvalidOperationException(
                "Native local resource manager returned an invalid merged table count.");
        }
        if (kind == LocalResourcePlanKind.Merged)
        {
            if (mergedCapacityInput is null || mergedModeInput is null)
            {
                throw new InvalidOperationException(
                    "Merged plan publication requires both frozen input plans.");
            }
            ValidateMergedIntents(
                mergedCapacityInput,
                mergedModeInput,
                output,
                summary.OperationId,
                summary.OperationGeneration,
                summary.SnapshotGeneration,
                checked((int)summary.AffectedTableCount));
        }
        return new(
            _planProducer,
            _managerInstanceId,
            summary.OperationId,
            summary.OperationGeneration,
            summary.SnapshotGeneration,
            kind,
            output,
            checked((int)summary.AffectedTableCount),
            checked((int)summary.TriggeredTableCount),
            checked((int)summary.StalledTableCount),
            checked((int)summary.EmergencyTableCount));
    }

    private void ValidatePlanOwnership(
        LocalResourcePlan plan,
        LocalResourcePlanKind expectedKind,
        string parameterName)
    {
        if (!ReferenceEquals(plan.Producer, _planProducer) ||
            plan.Kind != expectedKind ||
            plan.ManagerInstanceId != _managerInstanceId ||
            plan.OperationId == 0 || plan.OperationGeneration == 0 ||
            plan.SnapshotGeneration == 0)
        {
            throw new ArgumentException(
                "The local resource plan belongs to a different manager session.",
                parameterName);
        }
        if (plan.Intents is null)
        {
            throw new ArgumentException(
                "The local resource plan has no intent collection.",
                parameterName);
        }
        for (var index = 0; index < plan.Intents.Count; index++)
        {
            var intent = plan.Intents[index].Native;
            if (intent.Resource.ManagerInstanceId != _managerInstanceId ||
                intent.OperationId != plan.OperationId ||
                intent.OperationGeneration != plan.OperationGeneration ||
                intent.SnapshotGeneration != plan.SnapshotGeneration)
            {
                throw new ArgumentException(
                    "The local resource plan contains an intent from a different owner or snapshot.",
                    parameterName);
            }
        }
    }

    private static NativeLocalResourcePlanStamp CreatePlanStamp(LocalResourcePlan plan)
        => new()
        {
            AbiVersion = LocalResourceManagerProtocol.Version,
            StructSize = (uint)sizeof(NativeLocalResourcePlanStamp),
            ManagerInstanceId = plan.ManagerInstanceId,
            OperationId = plan.OperationId,
            OperationGeneration = plan.OperationGeneration,
            SnapshotGeneration = plan.SnapshotGeneration,
            Kind = (NativeLocalResourcePlanKind)plan.Kind,
        };

    private void ValidateNativePlanIntent(
        NativeLocalResourceIntent intent,
        LocalResourcePlanKind kind,
        ulong operationId,
        ulong operationGeneration,
        ulong snapshotGeneration)
    {
        const byte MaximumCapacityPhase = 3;
        const LocalResourceEffects SupportedEffects =
            LocalResourceEffects.ReleasesLedgerSlot |
            LocalResourceEffects.ChangesSizeBytes;
        var reasons = (LocalResourceIntentReason)intent.ReasonFlags;
        const LocalResourceIntentReason KnownReasons =
            LocalResourceIntentReason.Capacity |
            LocalResourceIntentReason.Mode |
            LocalResourceIntentReason.Partition;
        var hasCapacityReason =
            (reasons & LocalResourceIntentReason.Capacity) != 0;
        var hasPartitionReason =
            (reasons & LocalResourceIntentReason.Partition) != 0;
        var phaseValid = kind switch
        {
            LocalResourcePlanKind.Capacity =>
                reasons == LocalResourceIntentReason.Capacity && intent.CapacityPhase != 0,
            LocalResourcePlanKind.Mode =>
                reasons == LocalResourceIntentReason.Mode && intent.CapacityPhase == 0,
            LocalResourcePlanKind.Merged =>
                reasons != 0 &&
                (reasons & ~(LocalResourceIntentReason.Capacity |
                    LocalResourceIntentReason.Mode)) == 0 &&
                (hasCapacityReason == (intent.CapacityPhase != 0)),
            LocalResourcePlanKind.Partition =>
                reasons == LocalResourceIntentReason.Partition && intent.CapacityPhase == 0,
            _ => false,
        };
        if (intent.AbiVersion != LocalResourceManagerProtocol.Version ||
            intent.StructSize != sizeof(NativeLocalResourceIntent) ||
            operationId == 0 || operationGeneration == 0 ||
            intent.OperationId != operationId ||
            intent.OperationGeneration != operationGeneration ||
            snapshotGeneration == 0 ||
            intent.Resource.ManagerInstanceId != _managerInstanceId ||
            intent.Resource.TableSlotGeneration == 0 ||
            intent.Resource.ResourceSlotGeneration == 0 ||
            (intent.Resource.TableId.Low | intent.Resource.TableId.High) == 0 ||
            intent.Resource.TableIncarnation == 0 ||
            (intent.Resource.ResourceUid.Low | intent.Resource.ResourceUid.High) == 0 ||
            intent.Resource.TableSlotIndex >= Configuration.TableCapacity ||
            intent.Resource.ResourceSlotIndex >= Configuration.ResourceCapacity ||
            intent.SnapshotGeneration != snapshotGeneration ||
            intent.TableRevision == 0 || intent.RowRevision == 0 ||
            intent.ActivityRevision == 0 || intent.UseGeneration == 0 ||
            intent.CapabilityId == 0 || intent.CapabilityGeneration == 0 ||
            intent.ActionCode == 0 ||
            intent.CapabilitySlotIndex >= Configuration.CapabilityCapacity ||
            reasons == 0 || (reasons & ~KnownReasons) != 0 ||
            intent.ExpectedEffects == 0 ||
            (((LocalResourceEffects)intent.ExpectedEffects & ~SupportedEffects) != 0) ||
            intent.Destructive > 1 ||
            intent.CapacityPhase > MaximumCapacityPhase ||
            intent.AccessLossImpact > (byte)LocalResourceAccessLossImpact.UnobservableNow ||
            intent.Reserved0[0] != 0 || intent.Reserved0[1] != 0 ||
            intent.Reserved0[2] != 0 || intent.Reserved1 != 0 || !phaseValid ||
            (hasPartitionReason
                ? intent.ReclaimPartitionSlotGeneration == 0 ||
                    intent.ReclaimPartitionSlotIndex >= Configuration.EffectivePartitionCapacity
                : intent.ReclaimPartitionSlotGeneration != 0 ||
                    intent.ReclaimPartitionSlotIndex != uint.MaxValue) ||
            ((((LocalResourceEffects)intent.ExpectedEffects &
                LocalResourceEffects.ReleasesLedgerSlot) != 0) &&
                intent.Destructive != 1) ||
            ((hasCapacityReason || hasPartitionReason) &&
                ((LocalResourceEffects)intent.ExpectedEffects &
                    LocalResourceEffects.ReleasesLedgerSlot) == 0))
        {
            throw new InvalidOperationException(
                "Native local resource manager returned an invalid plan intent.");
        }
    }

    private void ValidateMergedIntents(
        IReadOnlyList<LocalResourceIntent> capacity,
        IReadOnlyList<LocalResourceIntent> mode,
        IReadOnlyList<LocalResourceIntent> merged,
        ulong operationId,
        ulong operationGeneration,
        ulong snapshotGeneration,
        int affectedTableCount)
    {
        var expected = new List<NativeLocalResourceIntent>(
            checked(capacity.Count + mode.Count));
        for (var index = 0; index < capacity.Count; index++)
        {
            var intent = capacity[index].Native;
            ValidateNativePlanIntent(
                intent,
                LocalResourcePlanKind.Capacity,
                operationId,
                operationGeneration,
                snapshotGeneration);
            var duplicate = false;
            for (var previous = 0; previous < index; previous++)
            {
                var prior = capacity[previous].Native;
                if (!SameNativeResource(prior.Resource, intent.Resource)) continue;
                if (!SameNativeIntentExceptReason(prior, intent))
                {
                    throw new InvalidOperationException(
                        "Frozen capacity inputs conflict during merged plan validation.");
                }
                duplicate = true;
            }
            if (!duplicate) expected.Add(intent);
        }

        for (var index = 0; index < mode.Count; index++)
        {
            var intent = mode[index].Native;
            ValidateNativePlanIntent(
                intent,
                LocalResourcePlanKind.Mode,
                operationId,
                operationGeneration,
                snapshotGeneration);
            for (var previous = 0; previous < index; previous++)
            {
                var prior = mode[previous].Native;
                if (SameNativeResource(prior.Resource, intent.Resource) &&
                    !SameNativeIntentExceptReason(prior, intent))
                {
                    throw new InvalidOperationException(
                        "Frozen mode inputs conflict during merged plan validation.");
                }
            }

            var expectedIndex = FindNativeResource(expected, intent.Resource);
            if (expectedIndex < 0)
            {
                expected.Add(intent);
                continue;
            }

            var existing = expected[expectedIndex];
            if ((existing.ReasonFlags & (byte)LocalResourceIntentReason.Capacity) != 0)
            {
                if (!SameNativeResourceSnapshot(existing, intent))
                {
                    throw new InvalidOperationException(
                        "Capacity and mode inputs disagree on their resource snapshot.");
                }
                existing.ReasonFlags |= (byte)LocalResourceIntentReason.Mode;
                expected[expectedIndex] = existing;
            }
        }

        expected.Sort(CompareNativeIntentCanonical);
        if (merged.Count != expected.Count)
        {
            throw new InvalidOperationException(
                "Native merged output does not contain the exact input resource union.");
        }
        for (var index = 0; index < merged.Count; index++)
        {
            var actual = merged[index].Native;
            ValidateNativePlanIntent(
                actual,
                LocalResourcePlanKind.Merged,
                operationId,
                operationGeneration,
                snapshotGeneration);
            if (!SameNativeIntent(actual, expected[index]))
            {
                throw new InvalidOperationException(
                    "Native merged output violates canonical capacity-precedence semantics.");
            }
        }

        var tables = new HashSet<(
            ulong ManagerInstanceId,
            uint TableSlotIndex,
            ulong TableSlotGeneration,
            ulong TableIdLow,
            ulong TableIdHigh,
            ulong TableIncarnation)>();
        foreach (var intent in expected)
        {
            tables.Add((
                intent.Resource.ManagerInstanceId,
                intent.Resource.TableSlotIndex,
                intent.Resource.TableSlotGeneration,
                intent.Resource.TableId.Low,
                intent.Resource.TableId.High,
                intent.Resource.TableIncarnation));
        }
        if (affectedTableCount != tables.Count)
        {
            throw new InvalidOperationException(
                "Native merged output reports an invalid affected-table count.");
        }
    }

    private static int FindNativeResource(
        IReadOnlyList<NativeLocalResourceIntent> values,
        NativeLocalResourceHandle resource)
    {
        for (var index = 0; index < values.Count; index++)
        {
            if (SameNativeResource(values[index].Resource, resource)) return index;
        }
        return -1;
    }

    private static bool SameNativeIntentExceptReason(
        NativeLocalResourceIntent left,
        NativeLocalResourceIntent right)
    {
        left.ReasonFlags = 0;
        right.ReasonFlags = 0;
        return SameNativeIntent(left, right);
    }

    private static bool SameNativeResourceSnapshot(
        NativeLocalResourceIntent left,
        NativeLocalResourceIntent right)
        => left.SnapshotGeneration == right.SnapshotGeneration &&
            left.TableRevision == right.TableRevision &&
            left.RowRevision == right.RowRevision &&
            left.ActivityRevision == right.ActivityRevision &&
            left.UseGeneration == right.UseGeneration &&
            left.SizeBytes == right.SizeBytes &&
            left.SettledActivity == right.SettledActivity &&
            left.RecoveryCostCoefficient == right.RecoveryCostCoefficient &&
            left.AccessLossImpact == right.AccessLossImpact;

    private static bool SameNativeIntent(
        NativeLocalResourceIntent left,
        NativeLocalResourceIntent right)
        => left.AbiVersion == right.AbiVersion &&
            left.StructSize == right.StructSize &&
            SameNativeResource(left.Resource, right.Resource) &&
            left.SnapshotGeneration == right.SnapshotGeneration &&
            left.TableRevision == right.TableRevision &&
            left.RowRevision == right.RowRevision &&
            left.ActivityRevision == right.ActivityRevision &&
            left.UseGeneration == right.UseGeneration &&
            left.CapabilityId == right.CapabilityId &&
            left.CapabilityGeneration == right.CapabilityGeneration &&
            left.SizeBytes == right.SizeBytes &&
            left.SettledActivity == right.SettledActivity &&
            left.RecoveryCostCoefficient == right.RecoveryCostCoefficient &&
            left.ActionCode == right.ActionCode &&
            left.CapabilitySlotIndex == right.CapabilitySlotIndex &&
            left.ReasonFlags == right.ReasonFlags &&
            left.ExpectedEffects == right.ExpectedEffects &&
            left.Destructive == right.Destructive &&
            left.CapacityPhase == right.CapacityPhase &&
            left.AccessLossImpact == right.AccessLossImpact &&
            left.Reserved0[0] == right.Reserved0[0] &&
            left.Reserved0[1] == right.Reserved0[1] &&
            left.Reserved0[2] == right.Reserved0[2] &&
            left.ReclaimPartitionSlotGeneration == right.ReclaimPartitionSlotGeneration &&
            left.ReclaimPartitionSlotIndex == right.ReclaimPartitionSlotIndex &&
            left.Reserved1 == right.Reserved1;

    private static bool SameNativeResource(
        NativeLocalResourceHandle left,
        NativeLocalResourceHandle right)
        => left.ManagerInstanceId == right.ManagerInstanceId &&
            left.TableSlotGeneration == right.TableSlotGeneration &&
            left.ResourceSlotGeneration == right.ResourceSlotGeneration &&
            left.TableId.Low == right.TableId.Low &&
            left.TableId.High == right.TableId.High &&
            left.TableIncarnation == right.TableIncarnation &&
            left.ResourceUid.Low == right.ResourceUid.Low &&
            left.ResourceUid.High == right.ResourceUid.High &&
            left.TableSlotIndex == right.TableSlotIndex &&
            left.ResourceSlotIndex == right.ResourceSlotIndex;

    private static int CompareNativeIntentCanonical(
        NativeLocalResourceIntent left,
        NativeLocalResourceIntent right)
    {
        var leftCapacity =
            (left.ReasonFlags & (byte)LocalResourceIntentReason.Capacity) != 0;
        var rightCapacity =
            (right.ReasonFlags & (byte)LocalResourceIntentReason.Capacity) != 0;
        if (leftCapacity != rightCapacity) return leftCapacity ? -1 : 1;
        return CompareNativeResource(left.Resource, right.Resource);
    }

    private static int CompareNativeResource(
        NativeLocalResourceHandle left,
        NativeLocalResourceHandle right)
    {
        var result = left.ManagerInstanceId.CompareTo(right.ManagerInstanceId);
        if (result != 0) return result;
        result = left.TableSlotIndex.CompareTo(right.TableSlotIndex);
        if (result != 0) return result;
        result = left.TableSlotGeneration.CompareTo(right.TableSlotGeneration);
        if (result != 0) return result;
        result = left.TableId.High.CompareTo(right.TableId.High);
        if (result != 0) return result;
        result = left.TableId.Low.CompareTo(right.TableId.Low);
        if (result != 0) return result;
        result = left.TableIncarnation.CompareTo(right.TableIncarnation);
        if (result != 0) return result;
        result = left.ResourceSlotIndex.CompareTo(right.ResourceSlotIndex);
        if (result != 0) return result;
        result = left.ResourceSlotGeneration.CompareTo(right.ResourceSlotGeneration);
        if (result != 0) return result;
        result = left.ResourceUid.High.CompareTo(right.ResourceUid.High);
        return result != 0 ? result : left.ResourceUid.Low.CompareTo(right.ResourceUid.Low);
    }

    private static NativeLocalResourceIntent[] ToNativeIntents(IReadOnlyList<LocalResourceIntent> values)
    {
        var output = new NativeLocalResourceIntent[values.Count];
        for (var index = 0; index < values.Count; index++) output[index] = values[index].Native;
        return output;
    }

    private static IReadOnlyList<LocalResourceCapacityTablePlan> CopyCapacityTablePlans(
        NativeLocalResourcePlanSummary summary,
        NativeLocalResourceCapacityTablePlanView[] source)
    {
        var count = checked((int)summary.TriggeredTableCount);
        if (count > source.Length)
        {
            throw new InvalidOperationException(
                "Native local resource manager returned too many capacity table results.");
        }

        var output = new LocalResourceCapacityTablePlan[count];
        var affected = 0;
        var stalled = 0;
        var emergency = 0;
        var identities = new HashSet<(ulong ManagerInstanceId, uint SlotIndex, ulong SlotGeneration)>();
        for (var index = 0; index < count; index++)
        {
            var value = source[index];
            var flags = value.Flags;
            if (value.Reserved0 != 0 ||
                (flags & ~NativeLocalResourceCapacityTableFlags.Known) != 0 ||
                !flags.HasFlag(NativeLocalResourceCapacityTableFlags.Triggered) ||
                (flags.HasFlag(NativeLocalResourceCapacityTableFlags.Affected) &&
                    flags.HasFlag(NativeLocalResourceCapacityTableFlags.Stalled)))
            {
                throw new InvalidOperationException(
                    "Native local resource manager returned invalid capacity table flags.");
            }

            var nativeTable = value.Capacity.Table;
            if (!identities.Add((
                nativeTable.ManagerInstanceId,
                nativeTable.SlotIndex,
                nativeTable.SlotGeneration)))
            {
                throw new InvalidOperationException(
                    "Native local resource manager returned a duplicate capacity table result.");
            }
            var isAffected = flags.HasFlag(NativeLocalResourceCapacityTableFlags.Affected);
            var isStalled = flags.HasFlag(NativeLocalResourceCapacityTableFlags.Stalled);
            var isEmergency = flags.HasFlag(NativeLocalResourceCapacityTableFlags.Emergency);
            affected += isAffected ? 1 : 0;
            stalled += isStalled ? 1 : 0;
            emergency += isEmergency ? 1 : 0;
            output[index] = new(
                new LocalResourceTableHandle(nativeTable),
                ToManagedCapacity(value.Capacity),
                isAffected,
                isStalled,
                isEmergency);
        }

        if (affected != summary.AffectedTableCount ||
            stalled != summary.StalledTableCount ||
            emergency != summary.EmergencyTableCount)
        {
            throw new InvalidOperationException(
                "Native local resource manager capacity table results do not match the plan summary.");
        }
        return output;
    }

    private static void ValidateCapacityTableBindings(
        LocalResourcePlan plan,
        IReadOnlyList<LocalResourceCapacityTablePlan> tables)
    {
        static (
            ulong ManagerInstanceId,
            ulong SlotGeneration,
            ulong TableIdLow,
            ulong TableIdHigh,
            ulong TableIncarnation,
            uint SlotIndex) Key(
            LocalResourceTableHandle table)
            => (
                table.Native.ManagerInstanceId,
                table.Native.SlotGeneration,
                table.Native.TableId.Low,
                table.Native.TableId.High,
                table.Native.TableIncarnation,
                table.Native.SlotIndex);

        var intentTables = plan.Intents
            .Select(static intent => Key(intent.TableHandle))
            .ToHashSet();
        var affectedTables = tables
            .Where(static table => table.Affected)
            .Select(static table => Key(table.Table))
            .ToHashSet();
        if (!intentTables.SetEquals(affectedTables))
        {
            throw new InvalidOperationException(
                "Native local resource manager capacity table results do not bind the intent tables.");
        }
    }

    private static LocalResourceCommitResult ToManaged(NativeLocalResourceCommitReceipt receipt)
        => new(
            receipt.SnapshotGeneration,
            receipt.TableRevision,
            receipt.ResourceSlotReleased != 0,
            receipt.EffectUncertain != 0);

    private static void EnsureNativeContract()
    {
        if (_contractValidated) return;
        lock (ContractSync)
        {
            if (_contractValidated) return;
            if (NativeLocalResourceManagerInterop.rm_local_resource_manager_abi_version() != LocalResourceManagerProtocol.Version ||
                NativeLocalResourceManagerInterop.rm_local_resource_manager_config_size() != sizeof(NativeLocalResourceManagerConfig) ||
                NativeLocalResourceManagerInterop.rm_local_resource_manager_table_spec_size() != sizeof(NativeLocalResourceTableSpec) ||
                NativeLocalResourceManagerInterop.rm_local_resource_manager_table_handle_size() != sizeof(NativeLocalResourceTableHandle) ||
                NativeLocalResourceManagerInterop.rm_local_resource_manager_capability_spec_size() != sizeof(NativeLocalResourceCapabilitySpec) ||
                NativeLocalResourceManagerInterop.rm_local_resource_manager_capability_handle_size() != sizeof(NativeLocalResourceCapabilityHandle) ||
                NativeLocalResourceManagerInterop.rm_local_resource_manager_resource_spec_size() != sizeof(NativeLocalResourceSpec) ||
                NativeLocalResourceManagerInterop.rm_local_resource_manager_resource_handle_size() != sizeof(NativeLocalResourceHandle) ||
                NativeLocalResourceManagerInterop.rm_local_resource_manager_partition_handle_size() != sizeof(NativeLocalResourcePartitionHandle) ||
                NativeLocalResourceManagerInterop.rm_local_resource_manager_operation_token_size() != sizeof(NativeLocalResourceOperationToken) ||
                NativeLocalResourceManagerInterop.rm_local_resource_manager_operation_view_size() != sizeof(NativeLocalResourceOperationView) ||
                NativeLocalResourceManagerInterop.rm_local_resource_manager_partition_create_input_size() != sizeof(NativeLocalResourcePartitionCreateInput) ||
                NativeLocalResourceManagerInterop.rm_local_resource_manager_partition_admission_ticket_size() != sizeof(NativeLocalResourcePartitionAdmissionTicket) ||
                NativeLocalResourceManagerInterop.rm_local_resource_manager_partition_admission_summary_size() != sizeof(NativeLocalResourcePartitionAdmissionSummary) ||
                NativeLocalResourceManagerInterop.rm_local_resource_manager_partition_admission_scratch_size() != sizeof(NativeLocalResourcePartitionAdmissionScratch) ||
                NativeLocalResourceManagerInterop.rm_local_resource_manager_partition_view_size() != sizeof(NativeLocalResourcePartitionView) ||
                NativeLocalResourceManagerInterop.rm_local_resource_manager_partition_cell_view_size() != sizeof(NativeLocalResourcePartitionCellView) ||
                NativeLocalResourceManagerInterop.rm_local_resource_manager_partition_resource_admission_size() != sizeof(NativeLocalResourcePartitionResourceAdmission) ||
                NativeLocalResourceManagerInterop.rm_local_resource_manager_use_lease_size() != sizeof(NativeLocalResourceUseLease) ||
                NativeLocalResourceManagerInterop.rm_local_resource_manager_capacity_plan_input_size() != sizeof(NativeLocalResourceCapacityPlanInput) ||
                NativeLocalResourceManagerInterop.rm_local_resource_manager_mode_plan_input_size() != sizeof(NativeLocalResourceModePlanInput) ||
                NativeLocalResourceManagerInterop.rm_local_resource_manager_intent_size() != sizeof(NativeLocalResourceIntent) ||
                NativeLocalResourceManagerInterop.rm_local_resource_manager_plan_stamp_size() != sizeof(NativeLocalResourcePlanStamp) ||
                NativeLocalResourceManagerInterop.rm_local_resource_manager_execution_token_size() != sizeof(NativeLocalResourceExecutionToken) ||
                NativeLocalResourceManagerInterop.rm_local_resource_manager_typed_effect_size() != sizeof(NativeLocalResourceTypedEffect) ||
                NativeLocalResourceManagerInterop.rm_local_resource_manager_plan_summary_size() != sizeof(NativeLocalResourcePlanSummary) ||
                NativeLocalResourceManagerInterop.rm_local_resource_manager_commit_receipt_size() != sizeof(NativeLocalResourceCommitReceipt) ||
                NativeLocalResourceManagerInterop.rm_local_resource_manager_table_capacity_view_size() != sizeof(NativeLocalResourceTableCapacityView) ||
                NativeLocalResourceManagerInterop.rm_local_resource_manager_capacity_table_plan_view_size() != sizeof(NativeLocalResourceCapacityTablePlanView) ||
                NativeLocalResourceManagerInterop.rm_local_resource_manager_resource_view_size() != sizeof(NativeLocalResourceView) ||
                NativeLocalResourceManagerInterop.rm_local_resource_manager_layout_fingerprint() != ManagedLayoutFingerprint())
            {
                throw new InvalidOperationException("Local resource manager native ABI layout mismatch.");
            }
            _contractValidated = true;
        }
    }

    internal static ulong ManagedLayoutFingerprint()
    {
        ReadOnlySpan<ulong> values =
        [
            (ulong)sizeof(NativeLocalResourceManagerConfig),
            OffsetOf<NativeLocalResourceManagerConfig>(nameof(NativeLocalResourceManagerConfig.ConfigurationGeneration)),
            OffsetOf<NativeLocalResourceManagerConfig>(nameof(NativeLocalResourceManagerConfig.UidBucketCapacity)),
            OffsetOf<NativeLocalResourceManagerConfig>(nameof(NativeLocalResourceManagerConfig.PartitionCapacity)),
            OffsetOf<NativeLocalResourceManagerConfig>(nameof(NativeLocalResourceManagerConfig.ConcentratedTriggerFreePercent)),
            (ulong)sizeof(NativeLocalResourceTableSpec),
            OffsetOf<NativeLocalResourceTableSpec>(nameof(NativeLocalResourceTableSpec.Capacity)),
            OffsetOf<NativeLocalResourceTableSpec>(nameof(NativeLocalResourceTableSpec.PartitionReservationStart)),
            OffsetOf<NativeLocalResourceTableSpec>(nameof(NativeLocalResourceTableSpec.PartitionReservationCapacity)),
            (ulong)sizeof(NativeLocalResourceTableHandle),
            OffsetOf<NativeLocalResourceTableHandle>(nameof(NativeLocalResourceTableHandle.TableId)),
            OffsetOf<NativeLocalResourceTableHandle>(nameof(NativeLocalResourceTableHandle.TableIncarnation)),
            (ulong)sizeof(NativeLocalResourceCapabilityBinding),
            (ulong)sizeof(NativeLocalResourceSpec),
            OffsetOf<NativeLocalResourceSpec>(nameof(NativeLocalResourceSpec.SizeBytes)),
            OffsetOf<NativeLocalResourceSpec>(nameof(NativeLocalResourceSpec.RecoveryCostCoefficient)),
            OffsetOf<NativeLocalResourceSpec>(nameof(NativeLocalResourceSpec.Recoverability)),
            (ulong)sizeof(NativeLocalResourceHandle),
            OffsetOf<NativeLocalResourceHandle>(nameof(NativeLocalResourceHandle.ResourceUid)),
            (ulong)sizeof(NativeLocalResourcePartitionHandle),
            OffsetOf<NativeLocalResourcePartitionHandle>(nameof(NativeLocalResourcePartitionHandle.PartitionSlotGeneration)),
            OffsetOf<NativeLocalResourcePartitionHandle>(nameof(NativeLocalResourcePartitionHandle.PartitionSlotIndex)),
            (ulong)sizeof(NativeLocalResourceOperationToken),
            OffsetOf<NativeLocalResourceOperationToken>(nameof(NativeLocalResourceOperationToken.OperationId)),
            OffsetOf<NativeLocalResourceOperationToken>(nameof(NativeLocalResourceOperationToken.OperationGeneration)),
            (ulong)sizeof(NativeLocalResourceOperationView),
            OffsetOf<NativeLocalResourceOperationView>(nameof(NativeLocalResourceOperationView.Phase)),
            (ulong)sizeof(NativeLocalResourceIntent),
            OffsetOf<NativeLocalResourceIntent>(nameof(NativeLocalResourceIntent.OperationId)),
            OffsetOf<NativeLocalResourceIntent>(nameof(NativeLocalResourceIntent.SnapshotGeneration)),
            OffsetOf<NativeLocalResourceIntent>(nameof(NativeLocalResourceIntent.CapabilityGeneration)),
            OffsetOf<NativeLocalResourceIntent>(nameof(NativeLocalResourceIntent.ReasonFlags)),
            OffsetOf<NativeLocalResourceIntent>(nameof(NativeLocalResourceIntent.ReclaimPartitionSlotGeneration)),
            (ulong)sizeof(NativeLocalResourcePlanStamp),
            OffsetOf<NativeLocalResourcePlanStamp>(nameof(NativeLocalResourcePlanStamp.ManagerInstanceId)),
            OffsetOf<NativeLocalResourcePlanStamp>(nameof(NativeLocalResourcePlanStamp.OperationId)),
            OffsetOf<NativeLocalResourcePlanStamp>(nameof(NativeLocalResourcePlanStamp.SnapshotGeneration)),
            OffsetOf<NativeLocalResourcePlanStamp>(nameof(NativeLocalResourcePlanStamp.Kind)),
            (ulong)sizeof(NativeLocalResourceExecutionToken),
            OffsetOf<NativeLocalResourceExecutionToken>(nameof(NativeLocalResourceExecutionToken.AttemptId)),
            (ulong)sizeof(NativeLocalResourceTypedEffect),
            OffsetOf<NativeLocalResourceTypedEffect>(nameof(NativeLocalResourceTypedEffect.Outcome)),
            OffsetOf<NativeLocalResourceTypedEffect>(nameof(NativeLocalResourceTypedEffect.Changes)),
            (ulong)sizeof(NativeLocalResourceCapacityTablePlanView),
            OffsetOf<NativeLocalResourceCapacityTablePlanView>(nameof(NativeLocalResourceCapacityTablePlanView.Flags)),
            (ulong)sizeof(NativeLocalResourceTableCapacityView),
            OffsetOf<NativeLocalResourceTableCapacityView>(nameof(NativeLocalResourceTableCapacityView.DirectCapacity)),
            OffsetOf<NativeLocalResourceTableCapacityView>(nameof(NativeLocalResourceTableCapacityView.PartitionReservationStart)),
            (ulong)sizeof(NativeLocalResourcePartitionCreateInput),
            OffsetOf<NativeLocalResourcePartitionCreateInput>(nameof(NativeLocalResourcePartitionCreateInput.Parent)),
            (ulong)sizeof(NativeLocalResourcePartitionAdmissionTicket),
            OffsetOf<NativeLocalResourcePartitionAdmissionTicket>(nameof(NativeLocalResourcePartitionAdmissionTicket.AdmissionId)),
            OffsetOf<NativeLocalResourcePartitionAdmissionTicket>(nameof(NativeLocalResourcePartitionAdmissionTicket.VictimHash)),
            OffsetOf<NativeLocalResourcePartitionAdmissionTicket>(nameof(NativeLocalResourcePartitionAdmissionTicket.Kind)),
            (ulong)sizeof(NativeLocalResourcePartitionAdmissionScratch),
            OffsetOf<NativeLocalResourcePartitionAdmissionScratch>(nameof(NativeLocalResourcePartitionAdmissionScratch.SelectionRank)),
            (ulong)sizeof(NativeLocalResourcePartitionView),
            (ulong)sizeof(NativeLocalResourcePartitionCellView),
            (ulong)sizeof(NativeLocalResourcePartitionResourceAdmission)
        ];
        var hash = 14695981039346656037UL;
        foreach (var value in values) hash = unchecked((hash ^ value) * 1099511628211UL);
        return hash;
    }

    private static LocalResourceTableCapacity ToManagedCapacity(
        NativeLocalResourceTableCapacityView value)
    {
        if (value.Capacity == 0 || value.OccupiedCount > value.Capacity ||
            value.FreeCount != value.Capacity - value.OccupiedCount ||
            value.PartitionReservationCapacity > value.Capacity ||
            value.PartitionReservationStart >
                value.Capacity - value.PartitionReservationCapacity ||
            (value.PartitionReservationCapacity == 0 && value.PartitionReservationStart != 0) ||
            value.DirectCapacity != value.Capacity - value.PartitionReservationCapacity ||
            value.DirectOccupiedCount > value.DirectCapacity ||
            value.DirectOccupiedCount > value.OccupiedCount ||
            value.DirectFreeCount != value.DirectCapacity - value.DirectOccupiedCount ||
            value.PartitionOccupiedCount != value.OccupiedCount - value.DirectOccupiedCount ||
            value.PartitionOccupiedCount > value.PartitionReservationCapacity ||
            value.PartitionFreeCount !=
                value.PartitionReservationCapacity - value.PartitionOccupiedCount ||
            value.Reserved0 != 0 || value.Revision == 0)
        {
            throw new InvalidOperationException(
                "Native local resource manager returned an invalid table capacity view.");
        }

        return new(
            checked((int)value.Capacity),
            checked((int)value.OccupiedCount),
            checked((int)value.FreeCount),
            checked((int)value.ActivePendingCount),
            checked((int)value.DirectCapacity),
            checked((int)value.DirectOccupiedCount),
            checked((int)value.DirectFreeCount),
            checked((int)value.PartitionReservationStart),
            checked((int)value.PartitionReservationCapacity),
            checked((int)value.PartitionOccupiedCount),
            checked((int)value.PartitionFreeCount),
            value.Revision);
    }

    private static ulong OffsetOf<T>(string field) where T : struct
        => checked((ulong)Marshal.OffsetOf<T>(field).ToInt64());

    internal static LocalResourceOperationToken ToManaged(
        NativeLocalResourceOperationToken token)
        => new(
            token.ManagerInstanceId,
            token.OperationId,
            token.OperationGeneration,
            token.StartSnapshotGeneration,
            (LocalResourceOperationKind)token.Kind);

    internal void CompleteStandaloneOperation(LocalResourceOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        lock (_sync)
        {
            ThrowIfDisposed();
            if (!_operationAdmissionHeld ||
                !ReferenceEquals(_activeStandaloneOperation, operation) ||
                !SameOperationToken(_activeOperationToken, operation.NativeToken))
            {
                throw new NativeLocalResourceManagerException(
                    "complete-standalone-operation",
                    (int)LocalResourceManagerError.StaleOperation);
            }
            CompleteActiveOperationUnderLock();
        }
        _operationGate.Release();
    }

    internal void WaitForStandaloneRecoveryOperation(
        LocalResourceUncertainExecution execution)
    {
        ArgumentNullException.ThrowIfNull(execution);
        var capabilityHandlerStarted = CaptureStandaloneOperationAdmission();
        LocalResourceOperationAdmission.Wait(
            _operationGate,
            capabilityHandlerStarted,
            LocalResourceIntentExecutor.CaptureProcessCapabilityHandlerAdmission(),
            () =>
            {
                lock (_sync)
                {
                    ThrowIfDisposed();
                    RequireStandaloneAccess();
                    AttachRecoveryOperationUnderLock(owner: null, execution);
                }
            });
    }

    internal void CompleteStandaloneRecoveryOperation(
        LocalResourceUncertainExecution execution)
    {
        ArgumentNullException.ThrowIfNull(execution);
        lock (_sync)
        {
            ThrowIfDisposed();
            if (!_operationAdmissionHeld ||
                !ReferenceEquals(_activeRecoveryExecution, execution) ||
                _activeOperationOwner is not null ||
                _activeStandaloneOperation is not null)
            {
                throw new NativeLocalResourceManagerException(
                    "complete-standalone-recovery-operation",
                    (int)LocalResourceManagerError.StaleOperation);
            }
            CompleteActiveOperationUnderLock();
        }
        _operationGate.Release();
    }

    internal void WaitForManagerRecoveryOperation(
        LocalResourceManagerSessionOwner owner,
        LocalResourceUncertainExecution execution)
    {
        ArgumentNullException.ThrowIfNull(execution);
        var capabilityHandlerStarted = CaptureManagerOperationAdmission(owner);
        LocalResourceOperationAdmission.Wait(
            _operationGate,
            capabilityHandlerStarted,
            LocalResourceIntentExecutor.CaptureProcessCapabilityHandlerAdmission(),
            () =>
            {
                lock (_sync)
                {
                    ThrowIfDisposed();
                    RequireNoActiveCapabilityHandler();
                    RequireManagerOwner(owner);
                    AttachRecoveryOperationUnderLock(owner, execution);
                }
            });
    }

    internal void CompleteManagerRecoveryOperation(
        LocalResourceManagerSessionOwner owner,
        LocalResourceUncertainExecution execution)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(execution);
        lock (_sync)
        {
            ThrowIfDisposed();
            RequireManagerOwner(owner);
            if (!_operationAdmissionHeld ||
                !ReferenceEquals(_activeOperationOwner, owner) ||
                !ReferenceEquals(_activeRecoveryExecution, execution))
            {
                throw new NativeLocalResourceManagerException(
                    "complete-manager-recovery-operation",
                    (int)LocalResourceManagerError.StaleOperation);
            }
            CompleteActiveOperationUnderLock();
        }
        _operationGate.Release();
    }

    private LocalResourceOperation BeginStandaloneOperationUnderLock(
        LocalResourceOperationKind kind)
    {
        var token = BeginNativeOperationUnderLock(kind);
        var operation = new LocalResourceOperation(this, token);
        _activeStandaloneOperation = operation;
        return operation;
    }

    private void BeginManagerOperationUnderLock(
        LocalResourceManagerSessionOwner owner,
        LocalResourceOperationKind kind)
    {
        _ = BeginNativeOperationUnderLock(kind);
        _activeOperationOwner = owner;
    }

    private NativeLocalResourceOperationToken BeginNativeOperationUnderLock(
        LocalResourceOperationKind kind)
    {
        if (_operationAdmissionHeld ||
            _activeOperationOwner is not null ||
            _activeStandaloneOperation is not null ||
            _activeRecoveryExecution is not null)
        {
            throw new InvalidOperationException(
                "Managed local resource operation admission is already held.");
        }

        NativeLocalResourceOperationToken token;
        ThrowIfFailed(
            NativeLocalResourceManagerInterop.rm_local_resource_manager_begin_operation(
                _state,
                _stateSize,
                (byte)kind,
                &token),
            "begin-operation");
        _activeOperationToken = token;
        _operationAdmissionHeld = true;
        return token;
    }

    private void AttachRecoveryOperationUnderLock(
        LocalResourceManagerSessionOwner? owner,
        LocalResourceUncertainExecution execution)
    {
        if (!ReferenceEquals(execution.Session, this))
        {
            throw new ArgumentException(
                "The uncertain execution belongs to a different manager session.",
                nameof(execution));
        }
        if (_operationAdmissionHeld ||
            _activeOperationOwner is not null ||
            _activeStandaloneOperation is not null ||
            _activeRecoveryExecution is not null)
        {
            throw new InvalidOperationException(
                "Managed local resource operation admission is already held.");
        }

        var view = ReadNativeOperationUnderLock();
        var executionToken = execution.Token;
        if (view.Phase != (byte)LocalResourceOperationPhase.RecoveryRequired ||
            view.Token.OperationId == 0 ||
            executionToken.OperationId != view.Token.OperationId ||
            executionToken.OperationGeneration != view.Token.OperationGeneration ||
            !execution.CanBeginRecovery)
        {
            throw new NativeLocalResourceManagerException(
                "attach-recovery-operation",
                (int)LocalResourceManagerError.StaleOperation);
        }

        _activeOperationToken = view.Token;
        _activeOperationOwner = owner;
        _activeRecoveryExecution = execution;
        _operationAdmissionHeld = true;
    }

    private void CompleteManagerOperation(LocalResourceManagerSessionOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        lock (_sync)
        {
            ThrowIfDisposed();
            RequireManagerOwner(owner);
            if (!_operationAdmissionHeld ||
                !ReferenceEquals(_activeOperationOwner, owner) ||
                _activeStandaloneOperation is not null ||
                _activeRecoveryExecution is not null)
            {
                throw new NativeLocalResourceManagerException(
                    "complete-manager-operation",
                    (int)LocalResourceManagerError.StaleOperation);
            }
            CompleteActiveOperationUnderLock();
        }
        _operationGate.Release();
    }

    private void CompleteActiveOperationUnderLock()
    {
        var view = ReadNativeOperationUnderLock();
        if (!SameOperationToken(view.Token, _activeOperationToken))
        {
            throw new NativeLocalResourceManagerException(
                "complete-operation-token",
                (int)LocalResourceManagerError.StaleOperation);
        }

        if (view.Phase != (byte)LocalResourceOperationPhase.RecoveryRequired)
        {
            var token = _activeOperationToken;
            ThrowIfFailed(
                NativeLocalResourceManagerInterop.rm_local_resource_manager_finish_operation(
                    _state,
                    _stateSize,
                    &token),
                "finish-operation");
            _activeOperationToken = default;
        }

        _activeOperationOwner = null;
        _activeStandaloneOperation = null;
        _activeRecoveryExecution = null;
        _operationAdmissionHeld = false;
    }

    private NativeLocalResourceOperationView ReadNativeOperationUnderLock()
    {
        NativeLocalResourceOperationView view;
        ThrowIfFailed(
            NativeLocalResourceManagerInterop.rm_local_resource_manager_read_operation(
                _state,
                _stateSize,
                &view),
            "read-operation");
        return view;
    }

    private LocalResourceOperationSnapshot ReadOperationUnderLock()
    {
        var view = ReadNativeOperationUnderLock();
        var phase = (LocalResourceOperationPhase)view.Phase;
        return new(
            phase == LocalResourceOperationPhase.Inactive ? null : ToManaged(view.Token),
            view.CurrentSnapshotGeneration,
            checked((int)view.SettledExecutionCount),
            checked((int)view.ConfirmedEffectCount),
            checked((int)view.PendingCount),
            checked((int)view.ReservedExecutionCount),
            checked((int)view.StartedExecutionCount),
            checked((int)view.UncertainExecutionCount),
            phase);
    }

    private NativeLocalResourceOperationToken RequireCurrentOperationUnderLock(
        LocalResourceManagerSessionOwner? owner,
        LocalResourceOperationKind? expectedKind = null)
    {
        if (!_operationAdmissionHeld || _activeOperationToken.OperationId == 0)
        {
            throw new NativeLocalResourceManagerException(
                "require-operation",
                (int)LocalResourceManagerError.OperationRequired);
        }
        if (owner is null)
        {
            if (_activeStandaloneOperation is null && _activeRecoveryExecution is null)
            {
                throw new NativeLocalResourceManagerException(
                    "require-standalone-operation",
                    (int)LocalResourceManagerError.StaleOperation);
            }
        }
        else if (!ReferenceEquals(_activeOperationOwner, owner))
        {
            throw new NativeLocalResourceManagerException(
                "require-manager-operation",
                (int)LocalResourceManagerError.StaleOperation);
        }
        if (expectedKind.HasValue &&
            _activeOperationToken.Kind != (byte)expectedKind.Value)
        {
            throw new NativeLocalResourceManagerException(
                "require-operation-kind",
                (int)LocalResourceManagerError.InvalidOperationPhase);
        }
        return _activeOperationToken;
    }

    private static bool SameOperationToken(
        NativeLocalResourceOperationToken left,
        NativeLocalResourceOperationToken right)
        => left.ManagerInstanceId == right.ManagerInstanceId &&
            left.OperationId == right.OperationId &&
            left.OperationGeneration == right.OperationGeneration &&
            left.StartSnapshotGeneration == right.StartSnapshotGeneration &&
            left.Kind == right.Kind;

    private static void ValidateOperationKind(LocalResourceOperationKind kind)
    {
        if (kind is < LocalResourceOperationKind.Maintenance or
            > LocalResourceOperationKind.PartitionResourceAdmission)
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    private static void ValidateCommitReceipt(
        NativeLocalResourceOperationToken operation,
        NativeLocalResourceCommitReceipt receipt)
    {
        if (receipt.OperationId != operation.OperationId ||
            receipt.OperationGeneration != operation.OperationGeneration)
        {
            throw new InvalidOperationException(
                "Native local resource manager returned a commit receipt for another operation.");
        }
    }

    private void RequirePendingExecution()
    {
        if (_pendingExecutionCount == 0)
        {
            throw new InvalidOperationException(
                "The local resource session has no pending execution to settle.");
        }
    }

    private void CompleteCloseUnderLock()
    {
        _disposeSealed = true;
        _disposed = true;
        NativeMemory.AlignedFree(_state);
    }

    private IReadOnlyList<LocalResourceUncertainExecution> SnapshotRecoveryRequiredExecutions()
        => _executionAuthorities
            .Where(static execution => execution?.CanBeginRecovery == true)
            .Select(static execution => execution!)
            .ToArray();

    private int RequireExecutionAuthority(LocalResourceUncertainExecution execution)
    {
        ArgumentNullException.ThrowIfNull(execution);
        if (!ReferenceEquals(execution.Session, this))
        {
            throw new ArgumentException(
                "The execution authority belongs to a different manager session.",
                nameof(execution));
        }

        var slotIndex = checked((int)execution.Token.PendingSlotIndex);
        if ((uint)slotIndex >= (uint)_executionAuthorities.Length ||
            !ReferenceEquals(_executionAuthorities[slotIndex], execution))
        {
            throw new InvalidOperationException(
                "The execution authority is not active in its native pending slot.");
        }
        return slotIndex;
    }

    private void CompleteExecutionAuthority(LocalResourceUncertainExecution execution)
    {
        var slotIndex = RequireExecutionAuthority(execution);
        _executionAuthorities[slotIndex] = null;
        _pendingExecutionCount = checked(_pendingExecutionCount - 1);
        execution.CompleteSettlement();
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(_disposed || _disposeSealed, this);

    private void RequireAccess(LocalResourceManagerSessionOwner? owner)
    {
        if (owner is null)
        {
            RequireNoActiveCapabilityHandler();
        }
        RequireExecutionOwnership(owner);
    }

    private void RequirePlanningAccess(LocalResourceManagerSessionOwner? owner)
    {
        RequireAccess(owner);
    }

    private void RequireExecutionOwnership(LocalResourceManagerSessionOwner? owner)
    {
        if (owner is null)
        {
            RequireUnclaimedSession();
            if (!_operationAdmissionHeld ||
                (_activeStandaloneOperation is null && _activeRecoveryExecution is null))
            {
                throw new NativeLocalResourceManagerException(
                    "require-standalone-operation-ownership",
                    (int)LocalResourceManagerError.OperationRequired);
            }
            return;
        }
        RequireManagerOwner(owner);
        if (!_operationAdmissionHeld || !ReferenceEquals(_activeOperationOwner, owner))
        {
            throw new NativeLocalResourceManagerException(
                "require-manager-operation-ownership",
                (int)LocalResourceManagerError.OperationRequired);
        }
    }

    private void RequireStandaloneAccess()
    {
        RequireNoActiveCapabilityHandler();
        RequireUnclaimedSession();
    }

    private Task CaptureManagerOperationAdmission(LocalResourceManagerSessionOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        lock (_sync)
        {
            ThrowIfDisposed();
            RequireNoActiveCapabilityHandler();
            RequireManagerOwner(owner);
            return _capabilityHandlerStarted.Task;
        }
    }

    private Task CaptureStandaloneOperationAdmission()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            RequireStandaloneAccess();
            return _capabilityHandlerStarted.Task;
        }
    }

    private void ValidateStandaloneCloseAdmission()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }
            RequireStandaloneAccess();
        }
    }

    private void RequireNoActiveCapabilityHandler()
    {
        if (_activeCapabilityHandlerCount != 0)
        {
            throw new InvalidOperationException(
                LocalResourceOperationAdmission.ActiveCapabilityHandlerMessage);
        }
    }

    private static TaskCompletionSource CreateHandlerStartedSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void RequireUnclaimedSession()
    {
        if (_managerOwner is not null)
        {
            throw new InvalidOperationException(ClaimedSessionMessage);
        }
    }

    private void RequireManagerOwner(LocalResourceManagerSessionOwner owner)
    {
        if (!ReferenceEquals(owner.Session, this) ||
            !ReferenceEquals(_managerOwner, owner))
        {
            throw new InvalidOperationException(
                "The local resource manager owner is not active for this native session.");
        }
    }
}
