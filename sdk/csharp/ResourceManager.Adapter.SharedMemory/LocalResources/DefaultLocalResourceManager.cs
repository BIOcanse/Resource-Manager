namespace ResourceManager.Adapter.LocalResources;

public sealed class DefaultLocalResourceManager : IDisposable, ILocalResourceModeAdmission
{
    private readonly LocalResourceManagerPump _pump;
    private readonly LocalResourceIntentExecutor _partitionExecutor;
    private readonly bool _ownsSession;
    private readonly object _closeGate = new();
    private readonly object _modeStateGate = new();
    private readonly Dictionary<TableIdentity, LocalResourceModeState> _modeStates = [];
    private readonly Dictionary<TableIdentity, ManagedTable> _managedTables = [];
    private LocalResourceSoftwareMemoryModeState? _desiredMemoryMode;
    private bool _closeInProgress;
    private bool _disposed;

    public DefaultLocalResourceManager(
        NativeLocalResourceManagerSession session,
        LocalResourceCapacityStrategy capacityStrategy = LocalResourceCapacityStrategy.Concentrated)
        : this(session, capacityStrategy, ownsSession: false)
    {
    }

    private DefaultLocalResourceManager(
        NativeLocalResourceManagerSession session,
        LocalResourceCapacityStrategy capacityStrategy,
        bool ownsSession)
    {
        if (capacityStrategy is not LocalResourceCapacityStrategy.Concentrated
            and not LocalResourceCapacityStrategy.Smooth)
        {
            throw new ArgumentOutOfRangeException(nameof(capacityStrategy));
        }
        Session = session ?? throw new ArgumentNullException(nameof(session));
        CapacityStrategy = capacityStrategy;
        _ownsSession = ownsSession;
        _pump = new(session);
        try
        {
            _partitionExecutor = new(session, _pump.Owner);
            foreach (var table in session.SnapshotRegisteredTablesOwned(_pump.Owner))
            {
                var identity = TableIdentity.From(table.Handle);
                if (!_managedTables.TryAdd(identity, new(table.Handle, table.Domain)))
                {
                    throw new InvalidOperationException(
                        "The native local resource manager returned a duplicate live table identity.");
                }
            }
        }
        catch (Exception constructionFailure)
        {
            try
            {
                _pump.Dispose();
            }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException(
                    "Default local resource manager construction and owner cleanup both failed.",
                    constructionFailure,
                    cleanupFailure);
            }
            throw;
        }
    }

    public static DefaultLocalResourceManager Create(
        LocalResourceManagerConfiguration configuration,
        LocalResourceCapacityStrategy capacityStrategy = LocalResourceCapacityStrategy.Concentrated)
        => CreateCore(configuration, capacityStrategy, initialize: null);

    internal static DefaultLocalResourceManager CreateConfigured(
        LocalResourceManagerConfiguration configuration,
        LocalResourceCapacityStrategy capacityStrategy,
        Action<DefaultLocalResourceManager> initialize)
    {
        ArgumentNullException.ThrowIfNull(initialize);
        return CreateCore(configuration, capacityStrategy, initialize);
    }

    private static DefaultLocalResourceManager CreateCore(
        LocalResourceManagerConfiguration configuration,
        LocalResourceCapacityStrategy capacityStrategy,
        Action<DefaultLocalResourceManager>? initialize)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var session = new NativeLocalResourceManagerSession(configuration);
        DefaultLocalResourceManager manager;
        try
        {
            manager = new(session, capacityStrategy, ownsSession: true);
        }
        catch (Exception constructionFailure)
        {
            try
            {
                session.CloseAfterFailedManagerConstruction();
            }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException(
                    "Local resource manager construction and its private session cleanup both failed.",
                    constructionFailure,
                    cleanupFailure);
            }
            throw;
        }

        if (initialize is null)
        {
            return manager;
        }

        try
        {
            initialize(manager);
            return manager;
        }
        catch (Exception initializationFailure)
        {
            try
            {
                manager.CloseAfterFailedInitialization();
            }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException(
                    "Local resource manager initialization and owned-session cleanup both failed.",
                    initializationFailure,
                    cleanupFailure);
            }
            throw;
        }
    }

    public NativeLocalResourceManagerSession Session { get; }
    public LocalResourceCapacityStrategy CapacityStrategy { get; }
    public bool IsBackgroundWorkerRunning => false;
    public LocalResourcePlan? LastCapacityPlan => _pump.LastCapacityPlan;
    public int PendingExecutionCount => Session.PendingExecutionCount;
    public LocalResourceSoftwareMemoryModeState? DesiredMemoryMode
    {
        get
        {
            ThrowIfCapabilityHandlerReentry();
            lock (_modeStateGate)
            {
                ThrowIfUnavailable();
                return _desiredMemoryMode;
            }
        }
    }

    public LocalResourceTableHandle RegisterTable(LocalResourceTableDefinition definition)
    {
        ThrowIfCapabilityHandlerReentry();
        EnterOwnedOperation();
        try
        {
            lock (_modeStateGate)
            {
                ThrowIfUnavailable();
                var table = Session.RegisterTableOwned(_pump.Owner, definition);
                var identity = TableIdentity.From(table);
                try
                {
                    if (!_managedTables.TryAdd(identity, new(table, definition.Domain)))
                    {
                        throw new InvalidOperationException(
                            "The default local resource manager received a duplicate table identity.");
                    }
                    if (definition.Domain == LocalResourceDomain.Memory
                        && _desiredMemoryMode is { } memoryMode)
                    {
                        _modeStates.Add(identity, ProjectMemoryMode(table, memoryMode));
                    }
                    return table;
                }
                catch
                {
                    _managedTables.Remove(identity);
                    Session.UnregisterTableOwned(_pump.Owner, table);
                    throw;
                }
            }
        }
        finally
        {
            Session.ReleaseOperation(_pump.Owner);
        }
    }

    public void UnregisterTable(LocalResourceTableHandle table)
    {
        ThrowIfCapabilityHandlerReentry();
        EnterOwnedOperation();
        try
        {
            lock (_modeStateGate)
            {
                ThrowIfUnavailable();
                var identity = TableIdentity.From(table);
                if (!_managedTables.ContainsKey(identity))
                {
                    throw new InvalidOperationException(
                        "Only a table registered through this default manager can be unregistered through it.");
                }
                Session.UnregisterTableOwned(_pump.Owner, table);
                _managedTables.Remove(identity);
                _modeStates.Remove(identity);
            }
        }
        finally
        {
            Session.ReleaseOperation(_pump.Owner);
        }
    }

    public LocalResourceCapabilityHandle RegisterCapability(
        LocalResourceTableHandle table,
        LocalResourceCapabilityDefinition definition,
        LocalResourceCapabilityHandler handler)
    {
        ThrowIfCapabilityHandlerReentry();
        EnterOwnedOperation();
        try
        {
            EnsureAvailable();
            return Session.Capabilities.RegisterOwned(_pump.Owner, table, definition, handler);
        }
        finally
        {
            Session.ReleaseOperation(_pump.Owner);
        }
    }

    public LocalResourceCapabilityHandle ReplaceCapability(
        LocalResourceCapabilityHandle handle,
        LocalResourceCapabilityDefinition definition,
        LocalResourceCapabilityHandler handler)
    {
        ThrowIfCapabilityHandlerReentry();
        EnterOwnedOperation();
        try
        {
            EnsureAvailable();
            return Session.Capabilities.ReplaceOwned(_pump.Owner, handle, definition, handler);
        }
        finally
        {
            Session.ReleaseOperation(_pump.Owner);
        }
    }

    public void UnregisterCapability(LocalResourceCapabilityHandle handle)
    {
        ThrowIfCapabilityHandlerReentry();
        EnterOwnedOperation();
        try
        {
            EnsureAvailable();
            Session.Capabilities.UnregisterOwned(_pump.Owner, handle);
        }
        finally
        {
            Session.ReleaseOperation(_pump.Owner);
        }
    }

    public LocalResourceHandle RegisterResource(
        LocalResourceTableHandle table,
        LocalResourceDefinition definition)
    {
        ThrowIfCapabilityHandlerReentry();
        EnterOwnedOperation();
        try
        {
            EnsureAvailable();
            return Session.RegisterResourceOwned(_pump.Owner, table, definition);
        }
        finally
        {
            Session.ReleaseOperation(_pump.Owner);
        }
    }

    public LocalResourceHandle RegisterResource(
        LocalResourcePartitionHandle partition,
        int localOrdinal,
        LocalResourceDefinition definition)
    {
        ThrowIfCapabilityHandlerReentry();
        EnterOwnedOperation();
        try
        {
            EnsureAvailable();
            return Session.RegisterPartitionResourceOwned(
                _pump.Owner,
                partition,
                localOrdinal,
                definition);
        }
        finally
        {
            Session.ReleaseOperation(_pump.Owner);
        }
    }

    public LocalResourcePartitionSnapshot ReadPartition(
        LocalResourcePartitionHandle partition)
    {
        ThrowIfCapabilityHandlerReentry();
        EnterOwnedOperation();
        try
        {
            EnsureAvailable();
            return Session.ReadPartition(partition);
        }
        finally
        {
            Session.ReleaseOperation(_pump.Owner);
        }
    }

    public LocalResourcePartitionCell ReadPartitionCell(
        LocalResourcePartitionHandle partition,
        int localOrdinal)
    {
        ThrowIfCapabilityHandlerReentry();
        EnterOwnedOperation();
        try
        {
            EnsureAvailable();
            return Session.ReadPartitionCell(partition, localOrdinal);
        }
        finally
        {
            Session.ReleaseOperation(_pump.Owner);
        }
    }

    public async Task<LocalResourcePartitionCloseResult> ClosePartitionAsync(
        LocalResourcePartitionHandle partition,
        CancellationToken cancellationToken = default)
    {
        ThrowIfCapabilityHandlerReentry();
        await Session.WaitForManagerOperationAsync(
                _pump.Owner,
                cancellationToken,
                LocalResourceOperationKind.PartitionAdmission)
            .ConfigureAwait(false);
        try
        {
            EnsureAvailable();
            await Session.WaitForTableExecutionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                LocalResourcePartitionClosePlan plan;
                try
                {
                    plan = Session.PlanPartitionCloseOwned(_pump.Owner, partition);
                }
                catch (NativeLocalResourceManagerException exception)
                    when (exception.Error == LocalResourceManagerError.PartitionBusy)
                {
                    return new(
                        LocalResourcePartitionCloseStatus.Blocked,
                        InvokedActionCount: 0,
                        ReleasedResourceCount: 0,
                        Array.Empty<LocalResourceUncertainExecution>());
                }
                if (!Session.Capabilities.CanResolveAll(plan.Intents))
                {
                    return new(
                        LocalResourcePartitionCloseStatus.Blocked,
                        InvokedActionCount: 0,
                        ReleasedResourceCount: 0,
                        Array.Empty<LocalResourceUncertainExecution>());
                }

                var invokedActionCount = 0;
                var releasedResourceCount = 0;
                foreach (var intent in plan.Intents)
                {
                    LocalResourceIntentExecutionOutcome outcome;
                    try
                    {
                        outcome = await _partitionExecutor.ExecuteOwnedAsync(
                                intent,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (LocalResourceEffectUncertainException exception)
                    {
                        return new(
                            LocalResourcePartitionCloseStatus.EffectUncertain,
                            InvokedActionCount: invokedActionCount + 1,
                            ReleasedResourceCount: releasedResourceCount,
                            UncertainExecutions: [exception.Execution]);
                    }
                    catch (OperationCanceledException)
                        when (releasedResourceCount > 0 && cancellationToken.IsCancellationRequested)
                    {
                        return new(
                            LocalResourcePartitionCloseStatus.Canceled,
                            invokedActionCount,
                            releasedResourceCount,
                            Array.Empty<LocalResourceUncertainExecution>());
                    }
                    if (outcome.CapabilityInvoked) invokedActionCount++;
                    if (outcome.Result.EffectUncertain)
                    {
                        return new(
                            LocalResourcePartitionCloseStatus.EffectUncertain,
                            invokedActionCount,
                            releasedResourceCount,
                            outcome.Result.UncertainExecution is { } uncertain
                                ? [uncertain]
                                : Array.Empty<LocalResourceUncertainExecution>());
                    }
                    if (!outcome.Result.ResourceSlotReleased)
                    {
                        return new(
                            LocalResourcePartitionCloseStatus.Blocked,
                            invokedActionCount,
                            releasedResourceCount,
                            Array.Empty<LocalResourceUncertainExecution>());
                    }
                    releasedResourceCount++;
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    if (releasedResourceCount == 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                    return new(
                        LocalResourcePartitionCloseStatus.Canceled,
                        invokedActionCount,
                        releasedResourceCount,
                        Array.Empty<LocalResourceUncertainExecution>());
                }

                try
                {
                    Session.CommitPartitionCloseOwned(_pump.Owner, plan);
                }
                catch (NativeLocalResourceManagerException exception)
                {
                    return new(
                        LocalResourcePartitionCloseStatus.CommitRejected,
                        invokedActionCount,
                        releasedResourceCount,
                        Array.Empty<LocalResourceUncertainExecution>())
                    {
                        FailureError = exception.Error
                    };
                }
                return new(
                    LocalResourcePartitionCloseStatus.Closed,
                    invokedActionCount,
                    releasedResourceCount,
                    Array.Empty<LocalResourceUncertainExecution>());
            }
            finally
            {
                Session.ReleaseTableExecution();
            }
        }
        finally
        {
            Session.ReleaseOperation(_pump.Owner);
        }
    }

    public Task<LocalResourcePartitionAdmissionResult> AdmitPartitionAsync(
        LocalResourceTableHandle table,
        int capacity,
        CancellationToken cancellationToken = default)
        => AdmitPartitionCoreAsync(table, parent: null, capacity, cancellationToken);

    public Task<LocalResourcePartitionAdmissionResult> AdmitChildPartitionAsync(
        LocalResourcePartitionHandle parent,
        int capacity,
        CancellationToken cancellationToken = default)
        => AdmitPartitionCoreAsync(parent.TableHandle, parent, capacity, cancellationToken);

    private async Task<LocalResourcePartitionAdmissionResult> AdmitPartitionCoreAsync(
        LocalResourceTableHandle table,
        LocalResourcePartitionHandle? parent,
        int capacity,
        CancellationToken cancellationToken)
    {
        ThrowIfCapabilityHandlerReentry();
        await Session.WaitForManagerOperationAsync(
                _pump.Owner,
                cancellationToken,
                LocalResourceOperationKind.PartitionAdmission)
            .ConfigureAwait(false);
        try
        {
            EnsureAvailable();
            await Session.WaitForTableExecutionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                LocalResourcePartitionAdmissionPlan plan;
                try
                {
                    plan = Session.PlanPartitionAdmissionOwned(
                        _pump.Owner,
                        table,
                        capacity,
                        parent);
                }
                catch (NativeLocalResourceManagerException exception)
                    when (exception.Error == LocalResourceManagerError.PartitionFull)
                {
                    return BlockedPartitionAdmission();
                }
                if (!Session.Capabilities.CanResolveAll(plan.Intents))
                {
                    return BlockedPartitionAdmission();
                }

                var invokedActionCount = 0;
                var releasedResourceCount = 0;
                foreach (var intent in plan.Intents)
                {
                    LocalResourceIntentExecutionOutcome outcome;
                    try
                    {
                        outcome = await _partitionExecutor.ExecuteOwnedAsync(
                                intent,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (LocalResourceEffectUncertainException exception)
                    {
                        return new(
                            LocalResourcePartitionAdmissionStatus.EffectUncertain,
                            Partition: null,
                            InvokedActionCount: invokedActionCount + 1,
                            ReleasedResourceCount: releasedResourceCount,
                            UncertainExecutions: [exception.Execution]);
                    }
                    catch (OperationCanceledException)
                        when (releasedResourceCount > 0 && cancellationToken.IsCancellationRequested)
                    {
                        return new(
                            LocalResourcePartitionAdmissionStatus.Canceled,
                            Partition: null,
                            invokedActionCount,
                            releasedResourceCount,
                            Array.Empty<LocalResourceUncertainExecution>());
                    }
                    if (outcome.CapabilityInvoked) invokedActionCount++;
                    if (outcome.Result.EffectUncertain)
                    {
                        return new(
                            LocalResourcePartitionAdmissionStatus.EffectUncertain,
                            Partition: null,
                            invokedActionCount,
                            releasedResourceCount,
                            outcome.Result.UncertainExecution is { } uncertain
                                ? [uncertain]
                                : Array.Empty<LocalResourceUncertainExecution>());
                    }
                    if (!outcome.Result.ResourceSlotReleased)
                    {
                        return new(
                            LocalResourcePartitionAdmissionStatus.Blocked,
                            Partition: null,
                            invokedActionCount,
                            releasedResourceCount,
                            Array.Empty<LocalResourceUncertainExecution>());
                    }
                    releasedResourceCount++;
                }

                LocalResourcePartitionHandle admitted;
                try
                {
                    admitted = Session.CommitPartitionAdmissionOwned(_pump.Owner, plan);
                }
                catch (NativeLocalResourceManagerException exception)
                {
                    return new(
                        LocalResourcePartitionAdmissionStatus.CommitRejected,
                        Partition: null,
                        invokedActionCount,
                        releasedResourceCount,
                        Array.Empty<LocalResourceUncertainExecution>())
                    {
                        FailureError = exception.Error
                    };
                }
                return new(
                    LocalResourcePartitionAdmissionStatus.Admitted,
                    admitted,
                    invokedActionCount,
                    releasedResourceCount,
                    Array.Empty<LocalResourceUncertainExecution>());
            }
            finally
            {
                Session.ReleaseTableExecution();
            }
        }
        finally
        {
            Session.ReleaseOperation(_pump.Owner);
        }
    }

    public async Task<LocalResourcePartitionResourceAdmissionResult>
        AdmitPartitionResourceAsync(
            LocalResourcePartitionHandle partition,
            LocalResourceDefinition definition,
            CancellationToken cancellationToken = default)
    {
        ThrowIfCapabilityHandlerReentry();
        await Session.WaitForManagerOperationAsync(
                _pump.Owner,
                cancellationToken,
                LocalResourceOperationKind.PartitionResourceAdmission)
            .ConfigureAwait(false);
        try
        {
            EnsureAvailable();
            await Session.WaitForTableExecutionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                LocalResourcePartitionResourceAdmissionPlan plan;
                try
                {
                    plan = Session.PlanPartitionResourceAdmissionOwned(
                        _pump.Owner,
                        partition,
                        definition);
                }
                catch (NativeLocalResourceManagerException exception)
                    when (exception.Error == LocalResourceManagerError.PartitionFull)
                {
                    return BlockedPartitionResourceAdmission();
                }

                var invokedActionCount = 0;
                var releasedResourceCount = 0;
                if (plan.ReleaseIntent is { } intent)
                {
                    LocalResourceIntentExecutionOutcome outcome;
                    try
                    {
                        outcome = await _partitionExecutor.ExecuteOwnedAsync(
                                intent,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (LocalResourceEffectUncertainException exception)
                    {
                        return new(
                            LocalResourcePartitionAdmissionStatus.EffectUncertain,
                            Placement: null,
                            InvokedActionCount: 1,
                            ReleasedResourceCount: 0,
                            UncertainExecutions: [exception.Execution]);
                    }
                    if (outcome.CapabilityInvoked) invokedActionCount++;
                    if (outcome.Result.EffectUncertain)
                    {
                        return new(
                            LocalResourcePartitionAdmissionStatus.EffectUncertain,
                            Placement: null,
                            invokedActionCount,
                            releasedResourceCount,
                            outcome.Result.UncertainExecution is { } uncertain
                                ? [uncertain]
                                : Array.Empty<LocalResourceUncertainExecution>());
                    }
                    if (!outcome.Result.ResourceSlotReleased)
                    {
                        return new(
                            LocalResourcePartitionAdmissionStatus.Blocked,
                            Placement: null,
                            invokedActionCount,
                            releasedResourceCount,
                            Array.Empty<LocalResourceUncertainExecution>());
                    }
                    releasedResourceCount = 1;
                }

                LocalResourceHandle resource;
                try
                {
                    resource = Session.CommitPartitionResourceAdmissionOwned(
                        _pump.Owner,
                        plan);
                }
                catch (NativeLocalResourceManagerException exception)
                {
                    return new(
                        LocalResourcePartitionAdmissionStatus.CommitRejected,
                        Placement: null,
                        invokedActionCount,
                        releasedResourceCount,
                        Array.Empty<LocalResourceUncertainExecution>())
                    {
                        FailureError = exception.Error
                    };
                }
                return new(
                    LocalResourcePartitionAdmissionStatus.Admitted,
                    new LocalResourcePartitionResourcePlacement(
                        partition,
                        plan.LocalOrdinal,
                        resource),
                    invokedActionCount,
                    releasedResourceCount,
                    Array.Empty<LocalResourceUncertainExecution>());
            }
            finally
            {
                Session.ReleaseTableExecution();
            }
        }
        finally
        {
            Session.ReleaseOperation(_pump.Owner);
        }
    }

    public void UpdateResource(
        LocalResourceHandle handle,
        LocalResourceDefinition definition)
    {
        ThrowIfCapabilityHandlerReentry();
        EnterOwnedOperation();
        try
        {
            EnsureAvailable();
            Session.UpdateResourceOwned(_pump.Owner, handle, definition);
        }
        finally
        {
            Session.ReleaseOperation(_pump.Owner);
        }
    }

    public void UnregisterResource(LocalResourceHandle handle)
    {
        ThrowIfCapabilityHandlerReentry();
        EnterOwnedOperation();
        try
        {
            EnsureAvailable();
            Session.UnregisterResourceOwned(_pump.Owner, handle);
        }
        finally
        {
            Session.ReleaseOperation(_pump.Owner);
        }
    }

    public void Touch(LocalResourceHandle handle, uint weight = 1)
    {
        ThrowIfCapabilityHandlerReentry();
        EnterOwnedOperation();
        try
        {
            EnsureAvailable();
            Session.TouchOwned(_pump.Owner, handle, weight);
        }
        finally
        {
            Session.ReleaseOperation(_pump.Owner);
        }
    }

    public LocalResourceUseLease BeginUse(LocalResourceHandle handle)
    {
        ThrowIfCapabilityHandlerReentry();
        EnterOwnedOperation();
        try
        {
            EnsureAvailable();
            return Session.BeginUseOwned(_pump.Owner, handle);
        }
        finally
        {
            Session.ReleaseOperation(_pump.Owner);
        }
    }

    public void EndUse(LocalResourceUseLease lease)
    {
        ThrowIfCapabilityHandlerReentry();
        EnterOwnedOperation();
        try
        {
            EnsureAvailable();
            Session.EndUseOwned(_pump.Owner, lease);
        }
        finally
        {
            Session.ReleaseOperation(_pump.Owner);
        }
    }

    public void SetDesiredMemoryMode(LocalResourceSoftwareMemoryModeState state)
    {
        LocalResourceIntentExecutor.ThrowIfCapabilityHandlerModePublication();
        ValidateMemoryModeState(state);
        lock (_modeStateGate)
        {
            ThrowIfUnavailable();
            if (_desiredMemoryMode is { } current)
            {
                if (state.Generation < current.Generation)
                {
                    throw new InvalidOperationException(
                        "A software memory mode cannot move to an older generation.");
                }
                if (state.Generation == current.Generation)
                {
                    if (state != current)
                    {
                        throw new InvalidOperationException(
                            "A software memory mode generation cannot be rebound to different state.");
                    }
                    return;
                }
            }

            var projected = _managedTables.Values
                .Where(static table => table.Domain == LocalResourceDomain.Memory)
                .Select(table => ProjectMemoryMode(table.Handle, state))
                .ToArray();
            for (var index = 0; index < projected.Length; index++)
            {
                ValidateModeTransition(projected[index]);
            }
            for (var index = 0; index < projected.Length; index++)
            {
                var mode = projected[index];
                _modeStates[TableIdentity.From(mode.Table)] = mode;
            }
            _desiredMemoryMode = state;
        }
    }

    public void SetDesiredMode(LocalResourceModeState state)
    {
        LocalResourceIntentExecutor.ThrowIfCapabilityHandlerModePublication();
        ValidateModeState(state);
        lock (_modeStateGate)
        {
            ThrowIfUnavailable();
            if (!Session.OwnsManagerInstance(state.Table))
            {
                throw new InvalidOperationException(
                    "A desired local resource mode table must belong to this manager session.");
            }
            Session.ValidateTableHandleOwned(_pump.Owner, state.Table);
            ValidateModeTransition(state);
            _modeStates[TableIdentity.From(state.Table)] = state;
        }
    }

    public Task<LocalResourceManagerTickResult> TickAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfCapabilityHandlerReentry();
        lock (_modeStateGate)
        {
            ThrowIfUnavailable();
        }
        return _pump.TickAsync(CapacityStrategy, SnapshotModeRequests, this, cancellationToken);
    }

    public LocalResourceExecutionResult Reconcile(
        LocalResourceUncertainExecution execution,
        LocalResourceEffect confirmedEffect)
    {
        ThrowIfCapabilityHandlerReentry();
        lock (_modeStateGate)
        {
            ThrowIfUnavailable();
        }
        return _pump.Reconcile(execution, confirmedEffect);
    }

    public IReadOnlyList<LocalResourceUncertainExecution> GetRecoveryRequiredExecutions()
    {
        ThrowIfCapabilityHandlerReentry();
        lock (_modeStateGate)
        {
            ThrowIfUnavailable();
        }
        return _pump.GetRecoveryRequiredExecutions();
    }

    public LocalResourceManagerCloseResult TryClose()
        => TryClose(out _);

    private LocalResourceManagerCloseResult TryClose(out int pendingExecutionCount)
    {
        ThrowIfCapabilityHandlerReentry();
        return TryCloseCore(initializationCleanup: false, out pendingExecutionCount);
    }

    private void CloseAfterFailedInitialization()
    {
        if (!_ownsSession)
        {
            throw new InvalidOperationException(
                "Only a factory-owned local resource manager can close failed initialization.");
        }
        if (TryCloseCore(initializationCleanup: true, out var pendingExecutionCount) ==
            LocalResourceManagerCloseResult.RecoveryRequired)
        {
            throw new LocalResourceRecoveryRequiredException(pendingExecutionCount);
        }
    }

    private LocalResourceManagerCloseResult TryCloseCore(
        bool initializationCleanup,
        out int pendingExecutionCount)
    {
        lock (_closeGate)
        {
            lock (_modeStateGate)
            {
                if (_disposed)
                {
                    pendingExecutionCount = 0;
                    return LocalResourceManagerCloseResult.Closed;
                }
                _closeInProgress = true;
            }

            try
            {
                var closeResult = initializationCleanup
                    ? _pump.TryCloseOwnedAfterFailedInitialization(
                        out pendingExecutionCount)
                    : _pump.TryClose(
                        _ownsSession,
                        out pendingExecutionCount);
                if (closeResult == LocalResourceManagerCloseResult.RecoveryRequired)
                {
                    return closeResult;
                }
                lock (_modeStateGate)
                {
                    _disposed = true;
                    _modeStates.Clear();
                    _managedTables.Clear();
                    _desiredMemoryMode = null;
                }
                pendingExecutionCount = 0;
                return LocalResourceManagerCloseResult.Closed;
            }
            finally
            {
                lock (_modeStateGate)
                {
                    _closeInProgress = false;
                }
            }
        }
    }

    public void Dispose()
    {
        if (TryClose(out var pendingExecutionCount) ==
            LocalResourceManagerCloseResult.RecoveryRequired)
        {
            throw new LocalResourceRecoveryRequiredException(pendingExecutionCount);
        }
    }

    private static void ValidateModeState(LocalResourceModeState state)
    {
        if (state.Generation == 0 ||
            !Enum.IsDefined(state.Mode) ||
            state.MaximumIntentsPerTick < 0 ||
            (state.MaximumIntentsPerTick == 0 &&
                state.Mode is LocalResourceCleanupMode.Optimize or
                    LocalResourceCleanupMode.ReleaseAll))
        {
            throw new ArgumentOutOfRangeException(nameof(state));
        }
    }

    private static void ValidateMemoryModeState(LocalResourceSoftwareMemoryModeState state)
    {
        if (state.Generation == 0
            || !Enum.IsDefined(state.Mode)
            || state.MaximumIntentsPerTick < 0
            || (state.MaximumIntentsPerTick == 0
                && state.Mode is LocalResourceSoftwareMemoryMode.Optimize
                    or LocalResourceSoftwareMemoryMode.PagedFrozen))
        {
            throw new ArgumentOutOfRangeException(nameof(state));
        }
    }

    private void ValidateModeTransition(LocalResourceModeState state)
    {
        var identity = TableIdentity.From(state.Table);
        if (!_modeStates.TryGetValue(identity, out var current))
        {
            return;
        }
        if (state.Generation < current.Generation)
        {
            throw new InvalidOperationException(
                "A local resource mode state cannot move to an older generation.");
        }
        if (state.Generation == current.Generation
            && (state.Mode != current.Mode
                || state.MaximumIntentsPerTick != current.MaximumIntentsPerTick))
        {
            throw new InvalidOperationException(
                "A local resource mode generation cannot be rebound to different state.");
        }
    }

    private static LocalResourceModeState ProjectMemoryMode(
        LocalResourceTableHandle table,
        LocalResourceSoftwareMemoryModeState state)
        => new(
            table,
            state.Mode switch
            {
                LocalResourceSoftwareMemoryMode.Unrestricted =>
                    LocalResourceCleanupMode.Unrestricted,
                LocalResourceSoftwareMemoryMode.Normal =>
                    LocalResourceCleanupMode.Normal,
                LocalResourceSoftwareMemoryMode.Optimize =>
                    LocalResourceCleanupMode.Optimize,
                LocalResourceSoftwareMemoryMode.PagedFrozen =>
                    LocalResourceCleanupMode.ReleaseAll,
                _ => throw new ArgumentOutOfRangeException(nameof(state))
            },
            state.MaximumIntentsPerTick,
            state.Generation);

    private void ThrowIfCapabilityHandlerReentry()
        => LocalResourceIntentExecutor.ThrowIfCapabilityHandlerLifecycleEntry();

    private static LocalResourcePartitionAdmissionResult BlockedPartitionAdmission()
        => new(
            LocalResourcePartitionAdmissionStatus.Blocked,
            Partition: null,
            InvokedActionCount: 0,
            ReleasedResourceCount: 0,
            Array.Empty<LocalResourceUncertainExecution>());

    private static LocalResourcePartitionResourceAdmissionResult
        BlockedPartitionResourceAdmission()
        => new(
            LocalResourcePartitionAdmissionStatus.Blocked,
            Placement: null,
            InvokedActionCount: 0,
            ReleasedResourceCount: 0,
            Array.Empty<LocalResourceUncertainExecution>());

    private void EnterOwnedOperation()
    {
        Session.WaitForManagerOperation(_pump.Owner);
    }

    private void EnsureAvailable()
    {
        lock (_modeStateGate)
        {
            ThrowIfUnavailable();
        }
    }

    private IReadOnlyList<LocalResourceModeRequest> SnapshotModeRequests()
    {
        lock (_modeStateGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _modeStates.Values
                .OrderBy(static state => state.Table.TableId.High)
                .ThenBy(static state => state.Table.TableId.Low)
                .ThenBy(static state => state.Table.TableIncarnation)
                .Select(static state => new LocalResourceModeRequest(
                    state.Table,
                    state.Mode,
                    state.MaximumIntentsPerTick,
                    state.Generation))
                .ToArray();
        }
    }

    void ILocalResourceModeAdmission.Validate(LocalResourceModeRequest request)
    {
        lock (_modeStateGate)
        {
            RequireCurrentMode(request);
        }
    }

    LocalResourceUncertainExecution ILocalResourceModeAdmission.Begin(
        LocalResourceModeRequest request,
        LocalResourceIntent intent,
        LocalResourceExecutionContext context)
    {
        lock (_modeStateGate)
        {
            RequireCurrentMode(request);
            return Session.BeginIntentCore(_pump.Owner, intent, context);
        }
    }

    private void RequireCurrentMode(LocalResourceModeRequest request)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_modeStates.TryGetValue(TableIdentity.From(request.Table), out var current)
            || current.Generation != request.Generation
            || current.Mode != request.Mode
            || current.MaximumIntentsPerTick != request.MaximumIntents)
        {
            throw new LocalResourceModeSupersededException();
        }
    }

    private void ThrowIfUnavailable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_closeInProgress)
        {
            throw new InvalidOperationException(
                "The local resource manager is closing and cannot start a new operation.");
        }
    }

    private readonly record struct TableIdentity(
        ulong ManagerInstanceId,
        ulong SlotGeneration,
        ulong TableIdLow,
        ulong TableIdHigh,
        ulong TableIncarnation,
        uint SlotIndex)
    {
        internal static TableIdentity From(LocalResourceTableHandle table)
        {
            var native = table.Native;
            return new(
                native.ManagerInstanceId,
                native.SlotGeneration,
                native.TableId.Low,
                native.TableId.High,
                native.TableIncarnation,
                native.SlotIndex);
        }
    }

    private readonly record struct ManagedTable(
        LocalResourceTableHandle Handle,
        LocalResourceDomain Domain);
}
