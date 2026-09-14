namespace ResourceManager.Adapter.LocalResources;

internal interface ILocalResourceModeAdmission
{
    void Validate(LocalResourceModeRequest request);
    LocalResourceUncertainExecution Begin(
        LocalResourceModeRequest request,
        LocalResourceIntent intent,
        LocalResourceExecutionContext context);
}

internal sealed class LocalResourceModeSupersededException : Exception
{
}

public sealed class LocalResourceManagerPump : IDisposable
{
    private static readonly IReadOnlySet<IntentIdentity> EmptyIntentIdentities =
        System.Collections.Frozen.FrozenSet<IntentIdentity>.Empty;
    private readonly NativeLocalResourceManagerSession _session;
    private readonly LocalResourceManagerSessionOwner _owner;
    private readonly LocalResourceIntentExecutor _executor;
    private readonly ILocalResourceModeAdmission _publicModeAdmission;
    private readonly object _closeGate = new();
    private readonly object _publicModeStateGate = new();
    private readonly Dictionary<TableHandleIdentity, LocalResourceModeRequest> _publicModeStates = [];
    private volatile bool _disposed;

    public LocalResourceManagerPump(NativeLocalResourceManagerSession session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        ThrowIfCapabilityHandlerReentry();
        _publicModeAdmission = new PublicModeAdmission(this);
        _owner = session.AcquireManagerOwner();
        try
        {
            _executor = new(session, _owner);
        }
        catch
        {
            session.ReleaseManagerOwner(_owner);
            throw;
        }
    }

    public bool IsBackgroundWorkerRunning => false;
    public LocalResourcePlan? LastCapacityPlan { get; private set; }
    public int PendingExecutionCount => _session.PendingExecutionCount;
    internal LocalResourceManagerSessionOwner Owner => _owner;

    public async Task<LocalResourceManagerTickResult> TickAsync(
        LocalResourceCapacityStrategy capacityStrategy,
        IReadOnlyList<LocalResourceModeRequest> modeRequests,
        CancellationToken cancellationToken = default)
    {
        LocalResourceIntentExecutor.ThrowIfCapabilityHandlerModePublication();
        ArgumentNullException.ThrowIfNull(modeRequests);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (capacityStrategy is not LocalResourceCapacityStrategy.Concentrated
            and not LocalResourceCapacityStrategy.Smooth)
        {
            throw new ArgumentOutOfRangeException(nameof(capacityStrategy));
        }

        var normalized = NormalizeModeRequests(modeRequests.ToArray());
        var requestedTables = AcceptPublicModeRequests(normalized);
        return await TickCoreAsync(
            capacityStrategy,
            () => SnapshotPublicModeRequests(requestedTables),
            _publicModeAdmission,
            cancellationToken).ConfigureAwait(false);
    }

    internal Task<LocalResourceManagerTickResult> TickAsync(
        LocalResourceCapacityStrategy capacityStrategy,
        Func<IReadOnlyList<LocalResourceModeRequest>> modeRequestSource,
        ILocalResourceModeAdmission modeAdmission,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(modeRequestSource);
        ArgumentNullException.ThrowIfNull(modeAdmission);
        return TickCoreAsync(
            capacityStrategy,
            modeRequestSource,
            modeAdmission,
            cancellationToken);
    }

    private async Task<LocalResourceManagerTickResult> TickCoreAsync(
        LocalResourceCapacityStrategy capacityStrategy,
        Func<IReadOnlyList<LocalResourceModeRequest>> modeRequestSource,
        ILocalResourceModeAdmission? modeAdmission,
        CancellationToken cancellationToken)
    {
        LocalResourceIntentExecutor.ThrowIfCurrentCapabilityHandlerReentry();
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _session.WaitForManagerOperationAsync(
                _owner,
                cancellationToken,
                LocalResourceOperationKind.Cleanup)
            .ConfigureAwait(false);
        var advanceActivityEpoch = false;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _session.ValidateManagerOwner(_owner);
            var recoveryBaseline = new HashSet<LocalResourceUncertainExecution>(
                _session.GetRecoveryRequiredExecutionsOwned(_owner),
                ReferenceEqualityComparer.Instance);
            advanceActivityEpoch = true;
            var capacityPlanning = _session.PlanCapacityDetailed(_owner, capacityStrategy);
            var capacity = capacityPlanning.Plan;
            LastCapacityPlan = capacity;
            var capacityTablePlans = capacityPlanning.TriggeredTables.ToDictionary(
                static table => (table.Table.TableId, table.Table.TableIncarnation));

            var capacityTasks = capacity.Intents
                .GroupBy(static intent => (intent.TableId, intent.TableIncarnation))
                .Select(group =>
                {
                    var first = group.First();
                    return ObserveTableRunAsync(
                        (first.TableId, first.TableIncarnation),
                        LocalResourceManagerTickPhase.Capacity,
                        ExecuteCapacityTableBoundedAsync(
                            first.TableHandle,
                            capacityStrategy,
                            capacityTablePlans[(first.TableId, first.TableIncarnation)].Emergency,
                            group.ToArray(),
                            cancellationToken));
                })
                .ToArray();
            var capacityOutcomes = await Task.WhenAll(capacityTasks).ConfigureAwait(false);
            var capacityResults = capacityOutcomes
                .Where(static outcome => outcome.Result is not null)
                .Select(static outcome => outcome.Result!)
                .Concat(capacityPlanning.TriggeredTables
                    .Where(static table => !table.Affected)
                    .Select(TableRunResult.FromUnexecutedCapacity))
                .ToArray();
            var capacityFailures = CollectFailures(capacityOutcomes);
            var capacityFailedTables = capacityFailures
                .Select(static failure => (failure.TableId, failure.TableIncarnation))
                .ToHashSet();

            var normalizedModes = NormalizeModeRequests(modeRequestSource())
                .Where(static request => request.MaximumIntents > 0)
                .Where(request => !capacityFailedTables.Contains(
                    (request.Table.TableId, request.Table.TableIncarnation)))
                .ToArray();
            var modeExcludedCapacityResources = capacityResults
                .SelectMany(static result => result.ModeExcludedResources)
                .ToHashSet();
            var modeExcludedCapacityActions = capacityResults
                .SelectMany(static result => result.ModeExcludedActions)
                .ToHashSet();
            TableRunOutcome[] modeOutcomes;
            if (OperationRequiresRecovery())
            {
                modeOutcomes = normalizedModes
                    .Select(request =>
                    {
                        var stoppedResult = CreateOperationRecoveryStop(request.Table);
                        return new TableRunOutcome(
                            stoppedResult.Identity,
                            LocalResourceManagerTickPhase.Mode,
                            stoppedResult,
                            CapacityActionCount: 0,
                            ModeActionCount: 0,
                            Stopped: true,
                            Array.Empty<LocalResourceUncertainExecution>(),
                            Failure: null);
                    })
                    .ToArray();
            }
            else
            {
                var modeTasks = normalizedModes.Select(request => ObserveTableRunAsync(
                    (request.Table.TableId, request.Table.TableIncarnation),
                    LocalResourceManagerTickPhase.Mode,
                    ExecuteModeTableBoundedAsync(
                        request,
                        modeExcludedCapacityResources,
                        modeExcludedCapacityActions,
                        modeAdmission,
                        cancellationToken))).ToArray();
                modeOutcomes = await Task.WhenAll(modeTasks).ConfigureAwait(false);
            }
            var modeResults = modeOutcomes
                .Where(static outcome => outcome.Result is not null)
                .Select(static outcome => outcome.Result!)
                .ToArray();
            var allOutcomes = capacityOutcomes.Concat(modeOutcomes).ToArray();
            var failures = CollectFailures(allOutcomes);
            var result = BuildTickResult(
                capacity,
                capacityResults,
                modeResults,
                allOutcomes,
                failures,
                CaptureNewRecoveryExecutions(recoveryBaseline),
                cancellationToken.IsCancellationRequested);
            if (failures.Count != 0)
            {
                throw new LocalResourceManagerTickFailedException(result, failures);
            }
            return result;
        }
        finally
        {
            try
            {
                if (advanceActivityEpoch &&
                    _session.ReadOperation().Phase !=
                        LocalResourceOperationPhase.RecoveryRequired)
                {
                    _session.AdvanceActivityEpochOwned(_owner);
                }
            }
            finally
            {
                _session.ReleaseOperation(_owner);
            }
        }
    }

    public LocalResourceExecutionResult Reconcile(
        LocalResourceUncertainExecution execution,
        LocalResourceEffect confirmedEffect)
    {
        ThrowIfCapabilityHandlerReentry();
        _session.WaitForManagerRecoveryOperation(_owner, execution);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _session.ValidateManagerOwner(_owner);
            return _executor.ReconcileOwned(execution, confirmedEffect);
        }
        finally
        {
            _session.CompleteManagerRecoveryOperation(_owner, execution);
        }
    }

    public IReadOnlyList<LocalResourceUncertainExecution> GetRecoveryRequiredExecutions()
    {
        ThrowIfCapabilityHandlerReentry();
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _session.GetRecoveryRequiredExecutionsOwned(_owner);
    }

    public LocalResourceManagerCloseResult TryClose()
        => TryClose(sealOwnedSession: false, out _);

    internal LocalResourceManagerCloseResult TryClose(
        bool sealOwnedSession,
        out int pendingExecutionCount)
    {
        ThrowIfCapabilityHandlerReentry();
        return TryCloseCore(
            sealOwnedSession,
            out pendingExecutionCount);
    }

    internal LocalResourceManagerCloseResult TryCloseOwnedAfterFailedInitialization(
        out int pendingExecutionCount)
        => TryCloseCore(
            sealOwnedSession: true,
            out pendingExecutionCount);

    private LocalResourceManagerCloseResult TryCloseCore(
        bool sealOwnedSession,
        out int pendingExecutionCount)
    {
        lock (_closeGate)
        {
            if (_disposed)
            {
                pendingExecutionCount = 0;
                return LocalResourceManagerCloseResult.Closed;
            }
            _session.WaitForOwnedCloseAdmission(_owner);
            try
            {
                if (_disposed)
                {
                    pendingExecutionCount = 0;
                    return LocalResourceManagerCloseResult.Closed;
                }
                _session.ValidateManagerOwner(_owner);
                LocalResourceManagerCloseResult closeResult;
                if (sealOwnedSession)
                {
                    closeResult = _session.TryCloseOwned(
                        _owner,
                        out pendingExecutionCount);
                }
                else
                {
                    pendingExecutionCount = _session.PendingExecutionCount;
                    closeResult = pendingExecutionCount == 0
                        ? LocalResourceManagerCloseResult.Closed
                        : LocalResourceManagerCloseResult.RecoveryRequired;
                }
                if (closeResult == LocalResourceManagerCloseResult.RecoveryRequired)
                {
                    return closeResult;
                }
                lock (_publicModeStateGate)
                {
                    _disposed = true;
                    _publicModeStates.Clear();
                }
                if (!sealOwnedSession)
                {
                    _session.ReleaseManagerOwner(_owner);
                }
                return LocalResourceManagerCloseResult.Closed;
            }
            finally
            {
                _session.ReleaseOwnedCloseAdmission();
            }
        }
    }

    public void Dispose()
    {
        if (TryClose(sealOwnedSession: false, out var pendingExecutionCount) ==
            LocalResourceManagerCloseResult.RecoveryRequired)
        {
            throw new LocalResourceRecoveryRequiredException(pendingExecutionCount);
        }
    }

    private async Task<TableRunResult> ExecuteCapacityTableBoundedAsync(
        LocalResourceTableHandle table,
        LocalResourceCapacityStrategy strategy,
        bool smoothEmergency,
        IReadOnlyList<LocalResourceIntent> triggerSnapshot,
        CancellationToken cancellationToken)
    {
        await _session.WaitForTableExecutionAsync().ConfigureAwait(false);
        try
        {
            return await ExecuteCapacityTableAsync(
                table,
                strategy,
                smoothEmergency,
                triggerSnapshot,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _session.ReleaseTableExecution();
        }
    }

    private async Task<TableRunResult> ExecuteCapacityTableAsync(
        LocalResourceTableHandle table,
        LocalResourceCapacityStrategy strategy,
        bool smoothEmergency,
        IReadOnlyList<LocalResourceIntent> triggerSnapshot,
        CancellationToken cancellationToken)
    {
        var before = default(LocalResourceTableCapacity);
        var beforeRead = false;
        var attempted = new HashSet<ResourceIdentity>();
        var modeExcluded = new HashSet<ResourceIdentity>();
        var modeExcludedActions = new HashSet<IntentIdentity>();
        var uncertain = new List<LocalResourceUncertainExecution>();
        var actions = 0;
        var released = 0;
        var stopped = false;
        var stopReasons = LocalResourceTableStopReason.None;

        try
        {
            before = _session.ReadCapacity(table);
            beforeRead = true;
            if (strategy == LocalResourceCapacityStrategy.Smooth)
            {
                foreach (var intent in triggerSnapshot)
                {
                    if (OperationRequiresRecovery())
                    {
                        stopped = true;
                        stopReasons |=
                            LocalResourceTableStopReason.OperationRecoveryRequired;
                        break;
                    }
                    if (cancellationToken.IsCancellationRequested)
                    {
                        stopped = true;
                        stopReasons |= LocalResourceTableStopReason.CancellationRequested;
                        break;
                    }
                    var identity = ResourceIdentity.From(intent);
                    if (!attempted.Add(identity)) continue;
                    var attempt = await TryExecuteAsync(intent, cancellationToken).ConfigureAwait(false);
                    if (attempt.Invoked) modeExcludedActions.Add(IntentIdentity.From(intent));
                    if (ShouldBlockResourceFromMode(attempt)) modeExcluded.Add(identity);
                    if (attempt.Invoked) actions++;
                    if (attempt.ReleasedSlot) released++;
                    if (attempt.UncertainExecution is not null) uncertain.Add(attempt.UncertainExecution);
                    if (attempt.Failure is not null)
                    {
                        stopReasons |= attempt.StopReason;
                        throw attempt.Failure;
                    }
                    if (attempt.Disposition == ExecutionDisposition.Stop)
                    {
                        stopped = true;
                        stopReasons |= attempt.StopReason;
                        break;
                    }
                }
            }
            else
            {
                foreach (var next in triggerSnapshot)
                {
                    if (OperationRequiresRecovery())
                    {
                        stopped = true;
                        stopReasons |=
                            LocalResourceTableStopReason.OperationRecoveryRequired;
                        break;
                    }
                    if (cancellationToken.IsCancellationRequested)
                    {
                        stopped = true;
                        stopReasons |= LocalResourceTableStopReason.CancellationRequested;
                        break;
                    }
                    var current = _session.ReadCapacity(table);
                    if (HasReachedConcentratedTarget(current)) break;
                    var identity = ResourceIdentity.From(next);
                    if (!attempted.Add(identity)) continue;
                    var attempt = await TryExecuteAsync(next, cancellationToken).ConfigureAwait(false);
                    if (attempt.Invoked) modeExcludedActions.Add(IntentIdentity.From(next));
                    if (ShouldBlockResourceFromMode(attempt)) modeExcluded.Add(identity);
                    if (attempt.Invoked) actions++;
                    if (attempt.ReleasedSlot) released++;
                    if (attempt.UncertainExecution is not null) uncertain.Add(attempt.UncertainExecution);
                    if (attempt.Failure is not null)
                    {
                        stopReasons |= attempt.StopReason;
                        throw attempt.Failure;
                    }
                    if (attempt.Disposition == ExecutionDisposition.Stop)
                    {
                        stopped = true;
                        stopReasons |= attempt.StopReason;
                        break;
                    }
                }
            }

            var after = _session.ReadCapacity(table);
            var capacityGoalReached = strategy == LocalResourceCapacityStrategy.Concentrated
                ? HasReachedConcentratedTarget(after)
                : !smoothEmergency || HasExitedSmoothEmergency(after);
            if (!capacityGoalReached)
            {
                stopped = true;
                stopReasons |= LocalResourceTableStopReason.CapacityGoalNotReached;
            }
            return new(
                (table.TableId, table.TableIncarnation),
                before,
                after,
                actions,
                0,
                released,
                capacityGoalReached,
                stopped,
                modeExcluded,
                modeExcludedActions,
                uncertain,
                stopReasons);
        }
        catch (NativeLocalResourceManagerException exception)
            when (exception.Error is LocalResourceManagerError.StaleTable or
                LocalResourceManagerError.StaleResource or
                LocalResourceManagerError.StaleCapability or
                LocalResourceManagerError.StaleIntent)
        {
            return new(
                (table.TableId, table.TableIncarnation),
                before,
                before,
                actions,
                0,
                released,
                false,
                true,
                modeExcluded,
                modeExcludedActions,
                uncertain,
                stopReasons | LocalResourceTableStopReason.Stale);
        }
        catch (Exception exception)
        {
            throw CreatePartialFailure(
                table,
                beforeRead,
                before,
                actions,
                modeActionCount: 0,
                released,
                capacityGoalReached: false,
                modeExcluded,
                modeExcludedActions,
                uncertain,
                stopReasons,
                exception);
        }
    }

    private async Task<TableRunResult> ExecuteModeTableBoundedAsync(
        LocalResourceModeRequest request,
        IReadOnlySet<ResourceIdentity> capacityBlockedResources,
        IReadOnlySet<IntentIdentity> capacityAttemptedActions,
        ILocalResourceModeAdmission? modeAdmission,
        CancellationToken cancellationToken)
    {
        await _session.WaitForTableExecutionAsync().ConfigureAwait(false);
        try
        {
            var table = request.Table;
            var before = default(LocalResourceTableCapacity);
            var beforeRead = false;
            var attempted = new HashSet<ResourceIdentity>();
            var uncertain = new List<LocalResourceUncertainExecution>();
            var actions = 0;
            var released = 0;
            var stopped = false;
            var stopReasons = LocalResourceTableStopReason.None;
            try
            {
                before = _session.ReadCapacity(table);
                beforeRead = true;
                LocalResourceIntentBegin? beginIntent = modeAdmission is null
                    ? null
                    : (intent, context) => modeAdmission.Begin(request, intent, context);
                while (attempted.Count < request.MaximumIntents)
                {
                    if (OperationRequiresRecovery())
                    {
                        stopped = true;
                        stopReasons |=
                            LocalResourceTableStopReason.OperationRecoveryRequired;
                        break;
                    }
                    modeAdmission?.Validate(request);
                    var plan = _session.PlanModeOwned(_owner, request with
                    {
                        MaximumIntents = _session.Configuration.ResourceCapacity
                    });
                    var releasedSlot = false;
                    var foundCandidate = false;
                    foreach (var next in plan.Intents)
                    {
                        if (attempted.Count >= request.MaximumIntents) break;
                        if (cancellationToken.IsCancellationRequested)
                        {
                            stopped = true;
                            stopReasons |= LocalResourceTableStopReason.CancellationRequested;
                            break;
                        }
                        var identity = ResourceIdentity.From(next);
                        if (capacityBlockedResources.Contains(identity) ||
                            capacityAttemptedActions.Contains(IntentIdentity.From(next)) ||
                            !attempted.Add(identity))
                        {
                            continue;
                        }
                        foundCandidate = true;
                        var attempt = await TryExecuteAsync(
                            next,
                            cancellationToken,
                            beginIntent).ConfigureAwait(false);
                        if (attempt.Invoked) actions++;
                        if (attempt.ReleasedSlot)
                        {
                            released++;
                            releasedSlot = true;
                        }
                        if (attempt.UncertainExecution is not null) uncertain.Add(attempt.UncertainExecution);
                        if (attempt.Failure is not null)
                        {
                            stopReasons |= attempt.StopReason;
                            throw attempt.Failure;
                        }
                        if (attempt.Disposition == ExecutionDisposition.Stop)
                        {
                            stopped = true;
                            stopReasons |= attempt.StopReason;
                            break;
                        }
                        if (releasedSlot)
                        {
                            break;
                        }
                    }
                    if (stopped || !foundCandidate || !releasedSlot)
                    {
                        break;
                    }
                }
                var after = _session.ReadCapacity(table);
                return new(
                    (table.TableId, table.TableIncarnation),
                    before,
                    after,
                    0,
                    actions,
                    released,
                    true,
                    stopped,
                    attempted,
                    EmptyIntentIdentities,
                    uncertain,
                    stopReasons);
            }
            catch (LocalResourceModeSupersededException)
            {
                var after = _session.ReadCapacity(table);
                return new(
                    (request.Table.TableId, request.Table.TableIncarnation),
                    before,
                    after,
                    0,
                    actions,
                    released,
                    true,
                    true,
                    attempted,
                    EmptyIntentIdentities,
                    uncertain,
                    stopReasons | LocalResourceTableStopReason.ModeSuperseded);
            }
            catch (NativeLocalResourceManagerException exception)
                when (exception.Error is LocalResourceManagerError.StaleTable or
                    LocalResourceManagerError.StaleResource or
                    LocalResourceManagerError.StaleIntent)
            {
                return new(
                    (request.Table.TableId, request.Table.TableIncarnation),
                    before,
                    before,
                    0,
                    actions,
                    released,
                    true,
                    true,
                    attempted,
                    EmptyIntentIdentities,
                    uncertain,
                    stopReasons | LocalResourceTableStopReason.Stale);
            }
            catch (NativeLocalResourceManagerException exception)
                when (exception.Error == LocalResourceManagerError.RecoveryRequired)
            {
                var after = _session.ReadCapacity(table);
                return new(
                    (request.Table.TableId, request.Table.TableIncarnation),
                    beforeRead ? before : after,
                    after,
                    0,
                    actions,
                    released,
                    true,
                    true,
                    attempted,
                    EmptyIntentIdentities,
                    uncertain,
                    stopReasons |
                        LocalResourceTableStopReason.OperationRecoveryRequired);
            }
            catch (Exception exception)
            {
                throw CreatePartialFailure(
                    table,
                    beforeRead,
                    before,
                    capacityActionCount: 0,
                    actions,
                    released,
                    capacityGoalReached: true,
                    attempted,
                    EmptyIntentIdentities,
                    uncertain,
                    stopReasons,
                    exception);
            }
        }
        finally
        {
            _session.ReleaseTableExecution();
        }
    }

    private TableRunPartialException CreatePartialFailure(
        LocalResourceTableHandle table,
        bool beforeRead,
        LocalResourceTableCapacity before,
        int capacityActionCount,
        int modeActionCount,
        int releasedSlotCount,
        bool capacityGoalReached,
        IReadOnlySet<ResourceIdentity> modeExcludedResources,
        IReadOnlySet<IntentIdentity> modeExcludedActions,
        IReadOnlyList<LocalResourceUncertainExecution> uncertainExecutions,
        LocalResourceTableStopReason stopReasons,
        Exception failure)
    {
        if (!beforeRead)
        {
            return new TableRunPartialException(null, failure);
        }

        try
        {
            return new TableRunPartialException(
                new TableRunResult(
                    (table.TableId, table.TableIncarnation),
                    before,
                    _session.ReadCapacity(table),
                    capacityActionCount,
                    modeActionCount,
                    releasedSlotCount,
                    capacityGoalReached,
                    Stopped: true,
                    modeExcludedResources,
                    modeExcludedActions,
                    uncertainExecutions,
                    stopReasons),
                failure);
        }
        catch (Exception capacityFailure)
        {
            return new TableRunPartialException(
                null,
                new AggregateException(
                    "A local resource table failed and its current capacity could not be read.",
                    failure,
                    capacityFailure));
        }
    }

    private static bool ShouldBlockResourceFromMode(ExecutionAttempt attempt)
        => attempt.ReleasedSlot ||
            attempt.UncertainExecution is not null ||
            attempt.Disposition == ExecutionDisposition.Stop;

    private async ValueTask<ExecutionAttempt> TryExecuteAsync(
        LocalResourceIntent intent,
        CancellationToken cancellationToken,
        LocalResourceIntentBegin? beginIntent = null)
    {
        try
        {
            var outcome = await _executor.ExecuteOwnedAsync(
                intent,
                cancellationToken,
                beginIntent).ConfigureAwait(false);
            var result = outcome.Result;
            return result.UncertainExecution is null
                ? new(
                    ExecutionDisposition.Continue,
                    outcome.CapabilityInvoked,
                    result.ResourceSlotReleased,
                    null,
                    LocalResourceTableStopReason.None,
                    null)
                : new(
                    ExecutionDisposition.Stop,
                    outcome.CapabilityInvoked,
                    false,
                    result.UncertainExecution,
                    LocalResourceTableStopReason.EffectUncertain,
                    null);
        }
        catch (LocalResourceEffectUncertainException exception)
            when (cancellationToken.IsCancellationRequested &&
                exception.InnerException is OperationCanceledException)
        {
            return new(
                ExecutionDisposition.Stop,
                true,
                false,
                exception.Execution,
                LocalResourceTableStopReason.CancellationRequested |
                    LocalResourceTableStopReason.EffectUncertain,
                null);
        }
        catch (LocalResourceModeSupersededException)
        {
            return new(
                ExecutionDisposition.Stop,
                false,
                false,
                null,
                LocalResourceTableStopReason.ModeSuperseded,
                null);
        }
        catch (LocalResourceEffectUncertainException exception)
        {
            return new(
                ExecutionDisposition.Stop,
                true,
                false,
                exception.Execution,
                LocalResourceTableStopReason.EffectUncertain,
                exception);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(
                ExecutionDisposition.Stop,
                false,
                false,
                null,
                LocalResourceTableStopReason.CancellationRequested,
                null);
        }
        catch (NativeLocalResourceManagerException exception)
            when (exception.Operation == "begin-intent" &&
                (exception.Error is LocalResourceManagerError.StaleTable or
                    LocalResourceManagerError.StaleResource or
                    LocalResourceManagerError.StaleCapability or
                    LocalResourceManagerError.StaleIntent))
        {
            return new(
                ExecutionDisposition.Skip,
                false,
                false,
                null,
                LocalResourceTableStopReason.None,
                null);
        }
        catch (NativeLocalResourceManagerException exception)
            when (exception.Operation == "begin-intent" &&
                exception.Error == LocalResourceManagerError.ResourceBusy)
        {
            return new(
                ExecutionDisposition.Stop,
                false,
                false,
                null,
                LocalResourceTableStopReason.ResourceBusy,
                null);
        }
        catch (NativeLocalResourceManagerException exception)
            when (exception.Operation == "begin-intent" &&
                exception.Error == LocalResourceManagerError.PendingFull)
        {
            return new(
                ExecutionDisposition.Stop,
                false,
                false,
                null,
                LocalResourceTableStopReason.PendingCapacityExhausted,
                null);
        }
        catch (NativeLocalResourceManagerException exception)
            when (exception.Operation == "begin-intent" &&
                exception.Error == LocalResourceManagerError.RecoveryRequired)
        {
            return new(
                ExecutionDisposition.Stop,
                false,
                false,
                null,
                LocalResourceTableStopReason.OperationRecoveryRequired,
                null);
        }
    }

    private bool OperationRequiresRecovery()
        => _session.ReadOperation().Phase ==
            LocalResourceOperationPhase.RecoveryRequired;

    private TableRunResult CreateOperationRecoveryStop(LocalResourceTableHandle table)
    {
        var capacity = _session.ReadCapacity(table);
        return new(
            (table.TableId, table.TableIncarnation),
            capacity,
            capacity,
            CapacityActionCount: 0,
            ModeActionCount: 0,
            ReleasedSlotCount: 0,
            CapacityGoalReached: true,
            Stopped: true,
            new HashSet<ResourceIdentity>(),
            EmptyIntentIdentities,
            Array.Empty<LocalResourceUncertainExecution>(),
            LocalResourceTableStopReason.OperationRecoveryRequired);
    }

    private bool HasReachedConcentratedTarget(LocalResourceTableCapacity capacity)
        => (long)GuardFree(capacity) * 100 >=
            (long)GuardCapacity(capacity) *
            _session.Configuration.EffectiveCapacityPolicy.ConcentratedTargetFreePercent;

    private bool HasExitedSmoothEmergency(LocalResourceTableCapacity capacity)
        => (long)GuardFree(capacity) * 100 >
            (long)GuardCapacity(capacity) *
            _session.Configuration.EffectiveCapacityPolicy.SmoothEmergencyFreePercent;

    private static int GuardCapacity(LocalResourceTableCapacity capacity)
        => capacity.DirectCapacity == 0 ? capacity.Capacity : capacity.DirectCapacity;

    private static int GuardFree(LocalResourceTableCapacity capacity)
        => capacity.DirectCapacity == 0 ? capacity.FreeCount : capacity.DirectFreeCount;

    private void ThrowIfCapabilityHandlerReentry()
        => LocalResourceIntentExecutor.ThrowIfCapabilityHandlerLifecycleEntry();

    private IReadOnlyList<TableHandleIdentity> AcceptPublicModeRequests(
        IReadOnlyList<LocalResourceModeRequest> requests)
    {
        var accepted = new (TableHandleIdentity Identity, LocalResourceModeRequest Request)[
            requests.Count];
        lock (_publicModeStateGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            for (var index = 0; index < requests.Count; index++)
            {
                _session.ValidateTableHandleOwned(_owner, requests[index].Table);
            }

            var staged = new Dictionary<TableHandleIdentity, LocalResourceModeRequest>(
                _publicModeStates);
            for (var index = 0; index < requests.Count; index++)
            {
                var request = requests[index];
                var identity = TableHandleIdentity.From(request.Table);
                foreach (var staleIdentity in staged.Keys
                    .Where(candidate => candidate.SlotIndex == identity.SlotIndex &&
                        candidate != identity)
                    .ToArray())
                {
                    staged.Remove(staleIdentity);
                }
                staged.TryGetValue(identity, out var current);
                var hasCurrent = current.Generation != 0;
                LocalResourceModeRequest next;
                if (request.Generation == 0)
                {
                    if (hasCurrent && current.Generation == ulong.MaxValue)
                    {
                        throw new InvalidOperationException(
                            "The automatic local resource mode generation is exhausted.");
                    }
                    next = request with
                    {
                        Generation = hasCurrent ? current.Generation + 1 : 1
                    };
                }
                else if (!hasCurrent || request.Generation > current.Generation)
                {
                    next = request;
                }
                else if (request.Generation < current.Generation)
                {
                    throw new InvalidOperationException(
                        "A local resource mode state cannot move to an older generation.");
                }
                else if (!HasSameModeState(request, current))
                {
                    throw new InvalidOperationException(
                        "A local resource mode generation cannot be rebound to different state.");
                }
                else
                {
                    next = current;
                }
                accepted[index] = (identity, next);
                staged[identity] = next;
            }

            if (staged.Count > _session.Configuration.TableCapacity)
            {
                throw new InvalidOperationException(
                    "The public local resource mode authority exceeded the native table capacity.");
            }
            _publicModeStates.Clear();
            foreach (var value in staged)
            {
                _publicModeStates.Add(value.Key, value.Value);
            }
        }
        return accepted.Select(static value => value.Identity).ToArray();
    }

    private IReadOnlyList<LocalResourceModeRequest> SnapshotPublicModeRequests(
        IReadOnlyList<TableHandleIdentity> requestedTables)
    {
        lock (_publicModeStateGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return requestedTables
                .Select(identity => _publicModeStates[identity])
                .OrderBy(static request => request.Table.TableId.High)
                .ThenBy(static request => request.Table.TableId.Low)
                .ThenBy(static request => request.Table.TableIncarnation)
                .ToArray();
        }
    }

    private void ValidatePublicMode(LocalResourceModeRequest request)
    {
        lock (_publicModeStateGate)
        {
            RequireCurrentPublicMode(request);
        }
    }

    private LocalResourceUncertainExecution BeginPublicMode(
        LocalResourceModeRequest request,
        LocalResourceIntent intent,
        LocalResourceExecutionContext context)
    {
        lock (_publicModeStateGate)
        {
            RequireCurrentPublicMode(request);
            return _session.BeginIntentCore(_owner, intent, context);
        }
    }

    private void RequireCurrentPublicMode(LocalResourceModeRequest request)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_publicModeStates.TryGetValue(
                TableHandleIdentity.From(request.Table),
                out var current)
            || current.Generation != request.Generation
            || !HasSameModeState(current, request))
        {
            throw new LocalResourceModeSupersededException();
        }
    }

    private static bool HasSameModeState(
        LocalResourceModeRequest first,
        LocalResourceModeRequest second)
        => first.Mode == second.Mode &&
            first.MaximumIntents == second.MaximumIntents;

    private sealed class PublicModeAdmission(LocalResourceManagerPump pump)
        : ILocalResourceModeAdmission
    {
        public void Validate(LocalResourceModeRequest request)
            => pump.ValidatePublicMode(request);

        public LocalResourceUncertainExecution Begin(
            LocalResourceModeRequest request,
            LocalResourceIntent intent,
            LocalResourceExecutionContext context)
            => pump.BeginPublicMode(request, intent, context);
    }

    private static IReadOnlyList<LocalResourceModeRequest> NormalizeModeRequests(
        IReadOnlyList<LocalResourceModeRequest> requests)
    {
        foreach (var request in requests)
        {
            if (!Enum.IsDefined(request.Mode) ||
                request.MaximumIntents < 0 ||
                (request.MaximumIntents == 0 &&
                    request.Mode is LocalResourceCleanupMode.Optimize or
                        LocalResourceCleanupMode.ReleaseAll))
            {
                throw new ArgumentOutOfRangeException(nameof(requests));
            }
        }

        var normalized = new List<LocalResourceModeRequest>();
        foreach (var group in requests
            .GroupBy(static request => TableHandleIdentity.From(request.Table)))
        {
            var first = group.First();
            if (group.Any(request => request.Mode != first.Mode ||
                request.MaximumIntents != first.MaximumIntents ||
                request.Generation != first.Generation))
            {
                throw new ArgumentException(
                    "A table cannot receive conflicting local resource mode requests in one Tick.",
                    nameof(requests));
            }
            normalized.Add(first);
        }
        return normalized;
    }

    private static IReadOnlyList<TableRunResult> MergeTableResults(
        IReadOnlyList<TableRunResult> capacity,
        IReadOnlyList<TableRunResult> mode)
    {
        var results = capacity.ToDictionary(static result => result.Identity);
        foreach (var modeResult in mode)
        {
            if (!results.TryGetValue(modeResult.Identity, out var capacityResult))
            {
                results.Add(modeResult.Identity, modeResult);
                continue;
            }
            results[modeResult.Identity] = capacityResult.MergeMode(modeResult);
        }
        return results.Values
            .OrderBy(static result => result.Identity.TableId.High)
            .ThenBy(static result => result.Identity.TableId.Low)
            .ThenBy(static result => result.Identity.TableIncarnation)
            .ToArray();
    }

    private static async Task<TableRunOutcome> ObserveTableRunAsync(
        (LocalResourceId TableId, ulong TableIncarnation) identity,
        LocalResourceManagerTickPhase phase,
        Task<TableRunResult> task)
    {
        try
        {
            var result = await task.ConfigureAwait(false);
            return new(
                identity,
                phase,
                result,
                result.CapacityActionCount,
                result.ModeActionCount,
                result.Stopped,
                result.UncertainExecutions,
                null);
        }
        catch (TableRunPartialException exception)
        {
            return new(
                identity,
                phase,
                exception.PartialResult,
                exception.CapacityActionCount,
                exception.ModeActionCount,
                true,
                exception.UncertainExecutions,
                exception.InnerException!);
        }
        catch (Exception exception)
        {
            return new(
                identity,
                phase,
                null,
                0,
                0,
                true,
                Array.Empty<LocalResourceUncertainExecution>(),
                exception);
        }
    }

    private static IReadOnlyList<LocalResourceTableTickFailure> CollectFailures(
        IReadOnlyList<TableRunOutcome> outcomes)
        => outcomes
            .Where(static outcome => outcome.Failure is not null)
            .Select(static outcome => new LocalResourceTableTickFailure(
                outcome.Identity.TableId,
                outcome.Identity.TableIncarnation,
                outcome.Phase,
                outcome.Failure!))
            .OrderBy(static failure => failure.Phase)
            .ThenBy(static failure => failure.TableId.High)
            .ThenBy(static failure => failure.TableId.Low)
            .ThenBy(static failure => failure.TableIncarnation)
            .ToArray();

    private IReadOnlyList<LocalResourceUncertainExecution> CaptureNewRecoveryExecutions(
        IReadOnlySet<LocalResourceUncertainExecution> recoveryBaseline)
        => _session.GetRecoveryRequiredExecutionsOwned(_owner)
            .Where(execution => !recoveryBaseline.Contains(execution))
            .ToArray();

    private static LocalResourceManagerTickResult BuildTickResult(
        LocalResourcePlan capacityPlan,
        IReadOnlyList<TableRunResult> capacityResults,
        IReadOnlyList<TableRunResult> modeResults,
        IReadOnlyList<TableRunOutcome> outcomes,
        IReadOnlyList<LocalResourceTableTickFailure> failures,
        IReadOnlyList<LocalResourceUncertainExecution> recoveryExecutions,
        bool cancellationObserved)
    {
        var failedIdentities = failures
            .Select(static failure => (failure.TableId, failure.TableIncarnation))
            .ToHashSet();
        var allTables = MergeTableResults(capacityResults, modeResults)
            .Select(result => failedIdentities.Contains(result.Identity)
                ? result with { Stopped = true }
                : result)
            .ToArray();
        var uncertain = outcomes
            .SelectMany(static outcome => outcome.UncertainExecutions)
            .Concat(recoveryExecutions)
            .Distinct<LocalResourceUncertainExecution>(ReferenceEqualityComparer.Instance)
            .ToArray();
        var stopped = allTables
            .Where(static result => result.Stopped)
            .Select(static result => result.Identity)
            .Concat(outcomes
                .Where(static outcome => outcome.Stopped)
                .Select(static outcome => outcome.Identity))
            .Concat(failedIdentities)
            .Distinct()
            .OrderBy(static table => table.TableId.High)
            .ThenBy(static table => table.TableId.Low)
            .ThenBy(static table => table.TableIncarnation)
            .ToArray();
        return new(
            capacityPlan,
            outcomes.Sum(static outcome => outcome.CapacityActionCount),
            outcomes.Sum(static outcome => outcome.ModeActionCount),
            stopped,
            uncertain,
            allTables.Select(static result => result.ToPublic()).ToArray(),
            cancellationObserved);
    }

    private enum ExecutionDisposition
    {
        Continue,
        Skip,
        Stop
    }

    private readonly record struct ExecutionAttempt(
        ExecutionDisposition Disposition,
        bool Invoked,
        bool ReleasedSlot,
        LocalResourceUncertainExecution? UncertainExecution,
        LocalResourceTableStopReason StopReason,
        Exception? Failure);

    private sealed record TableRunOutcome(
        (LocalResourceId TableId, ulong TableIncarnation) Identity,
        LocalResourceManagerTickPhase Phase,
        TableRunResult? Result,
        int CapacityActionCount,
        int ModeActionCount,
        bool Stopped,
        IReadOnlyList<LocalResourceUncertainExecution> UncertainExecutions,
        Exception? Failure);

    private sealed class TableRunPartialException : Exception
    {
        internal TableRunPartialException(
            TableRunResult? partialResult,
            Exception innerException)
            : base("A local resource table failed after partial execution.", innerException)
        {
            PartialResult = partialResult;
            CapacityActionCount = partialResult?.CapacityActionCount ?? 0;
            ModeActionCount = partialResult?.ModeActionCount ?? 0;
            UncertainExecutions = partialResult?.UncertainExecutions.ToArray() ?? [];
        }

        internal TableRunResult? PartialResult { get; }
        internal int CapacityActionCount { get; }
        internal int ModeActionCount { get; }
        internal IReadOnlyList<LocalResourceUncertainExecution> UncertainExecutions { get; }
    }

    private readonly record struct ResourceIdentity(
        LocalResourceId TableId,
        ulong TableIncarnation,
        LocalResourceId ResourceUid)
    {
        internal static ResourceIdentity From(LocalResourceIntent intent)
            => new(intent.TableId, intent.TableIncarnation, intent.ResourceUid);
    }

    private readonly record struct IntentIdentity(
        ResourceIdentity Resource,
        ulong CapabilityId,
        ulong CapabilityGeneration,
        uint ActionCode)
    {
        internal static IntentIdentity From(LocalResourceIntent intent)
            => new(
                ResourceIdentity.From(intent),
                intent.CapabilityId,
                intent.CapabilityGeneration,
                intent.ActionCode);
    }

    private readonly record struct TableHandleIdentity(
        ulong ManagerInstanceId,
        ulong SlotGeneration,
        ulong TableIdLow,
        ulong TableIdHigh,
        ulong TableIncarnation,
        uint SlotIndex)
    {
        internal static TableHandleIdentity From(LocalResourceTableHandle table)
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

    private sealed record TableRunResult(
        (LocalResourceId TableId, ulong TableIncarnation) Identity,
        LocalResourceTableCapacity Before,
        LocalResourceTableCapacity After,
        int CapacityActionCount,
        int ModeActionCount,
        int ReleasedSlotCount,
        bool CapacityGoalReached,
        bool Stopped,
        IReadOnlySet<ResourceIdentity> ModeExcludedResources,
        IReadOnlySet<IntentIdentity> ModeExcludedActions,
        IReadOnlyList<LocalResourceUncertainExecution> UncertainExecutions,
        LocalResourceTableStopReason StopReasons)
    {
        internal static TableRunResult FromUnexecutedCapacity(
            LocalResourceCapacityTablePlan table)
            => new(
                (table.Table.TableId, table.Table.TableIncarnation),
                table.Capacity,
                table.Capacity,
                CapacityActionCount: 0,
                ModeActionCount: 0,
                ReleasedSlotCount: 0,
                CapacityGoalReached: false,
                Stopped: table.Stalled,
                new HashSet<ResourceIdentity>(),
                EmptyIntentIdentities,
                Array.Empty<LocalResourceUncertainExecution>(),
                table.Stalled
                    ? LocalResourceTableStopReason.NoEligibleCandidate |
                        LocalResourceTableStopReason.CapacityGoalNotReached
                    : LocalResourceTableStopReason.None);

        internal TableRunResult MergeMode(TableRunResult mode)
            => this with
            {
                After = mode.After,
                ModeActionCount = mode.ModeActionCount,
                ReleasedSlotCount = ReleasedSlotCount + mode.ReleasedSlotCount,
                Stopped = Stopped || mode.Stopped,
                StopReasons = StopReasons | mode.StopReasons,
                ModeExcludedResources = ModeExcludedResources
                    .Concat(mode.ModeExcludedResources)
                    .ToHashSet(),
                ModeExcludedActions = ModeExcludedActions
                    .Concat(mode.ModeExcludedActions)
                    .ToHashSet(),
                UncertainExecutions = UncertainExecutions.Concat(mode.UncertainExecutions).ToArray()
            };

        internal LocalResourceTableTickResult ToPublic()
            => new LocalResourceTableTickResult(
                Identity.TableId,
                Identity.TableIncarnation,
                Before,
                After,
                CapacityActionCount,
                ModeActionCount,
                ReleasedSlotCount,
                CapacityGoalReached,
                Stopped,
                UncertainExecutions.Count != 0)
            {
                StopReasons = StopReasons
            };
    }
}
