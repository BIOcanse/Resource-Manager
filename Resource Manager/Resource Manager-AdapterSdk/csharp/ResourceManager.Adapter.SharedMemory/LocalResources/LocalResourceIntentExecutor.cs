namespace ResourceManager.Adapter.LocalResources;

internal delegate LocalResourceUncertainExecution LocalResourceIntentBegin(
    LocalResourceIntent intent,
    LocalResourceExecutionContext context);

internal readonly record struct LocalResourceIntentExecutionOutcome(
    LocalResourceExecutionResult Result,
    bool CapabilityInvoked);

public sealed class LocalResourceUncertainExecution
{
    private const int Active = 0;
    private const int RecoveryRequired = 1;
    private const int Resolving = 2;
    private const int Resolved = 3;

    private NativeLocalResourceExecutionToken _token;
    private int _tokenBound;
    private int _resolutionState = Active;

    internal LocalResourceUncertainExecution(
        NativeLocalResourceManagerSession session,
        LocalResourceExecutionContext context)
    {
        Session = session;
        Context = context;
    }

    public LocalResourceExecutionContext Context { get; }
    public bool IsRecoveryRequired => Volatile.Read(ref _resolutionState) is RecoveryRequired or Resolving;
    public bool IsResolved => Volatile.Read(ref _resolutionState) == Resolved;
    internal NativeLocalResourceManagerSession Session { get; }
    internal bool CanBeginRecovery => Volatile.Read(ref _resolutionState) == RecoveryRequired;
    internal NativeLocalResourceExecutionToken Token
    {
        get
        {
            if (Volatile.Read(ref _tokenBound) == 0)
            {
                throw new InvalidOperationException("The local resource execution token is not bound.");
            }
            return _token;
        }
    }

    internal void BindToken(NativeLocalResourceExecutionToken token)
    {
        if (Volatile.Read(ref _tokenBound) != 0)
        {
            throw new InvalidOperationException("The local resource execution token is already bound.");
        }
        _token = token;
        Volatile.Write(ref _tokenBound, 1);
    }

    internal void MarkRecoveryRequired()
    {
        var state = Volatile.Read(ref _resolutionState);
        while (state == Active)
        {
            var observed = Interlocked.CompareExchange(
                ref _resolutionState,
                RecoveryRequired,
                Active);
            if (observed == Active) return;
            state = observed;
        }
    }

    internal void CompleteSettlement() => Volatile.Write(ref _resolutionState, Resolved);

    internal bool TryBeginResolution() =>
        Interlocked.CompareExchange(ref _resolutionState, Resolving, RecoveryRequired) ==
            RecoveryRequired;

    internal void CompleteResolution() => Volatile.Write(ref _resolutionState, Resolved);
    internal void ResetResolution() => Interlocked.CompareExchange(
        ref _resolutionState,
        RecoveryRequired,
        Resolving);
}

public sealed class LocalResourceEffectUncertainException : Exception
{
    internal LocalResourceEffectUncertainException(
        LocalResourceUncertainExecution execution,
        Exception innerException)
        : base("The resource action was invoked, but its final effect is unknown.", innerException)
        => Execution = execution;

    public LocalResourceUncertainExecution Execution { get; }
}

public sealed class LocalResourceIntentExecutor
{
    private static readonly AsyncLocal<NativeLocalResourceManagerSession?> CurrentHandlerSession = new();
    private static readonly object ProcessHandlerGate = new();
    private static readonly Dictionary<NativeLocalResourceManagerSession, int>
        ActiveProcessHandlerSessions = [];
    private static int _activeProcessHandlerCount;
    private static TaskCompletionSource _processHandlerStarted = CreateHandlerStartedSignal();
    private readonly NativeLocalResourceManagerSession _session;
    private readonly LocalResourceManagerSessionOwner? _owner;
    private readonly LocalResourceOperation? _operation;

    public LocalResourceIntentExecutor(NativeLocalResourceManagerSession session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
    }

    internal LocalResourceIntentExecutor(LocalResourceOperation operation)
    {
        _operation = operation ?? throw new ArgumentNullException(nameof(operation));
        _session = operation.Session;
    }

    internal LocalResourceIntentExecutor(
        NativeLocalResourceManagerSession session,
        LocalResourceManagerSessionOwner owner)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
    }

    public async ValueTask<LocalResourceExecutionResult> ExecuteAsync(
        LocalResourceIntent intent,
        CancellationToken cancellationToken = default)
    {
        ThrowIfCapabilityHandlerLifecycleEntry();
        if (_operation is null)
        {
            throw new InvalidOperationException(
                "Intent execution requires the LocalResourceOperation that produced the intent.");
        }
        _operation.RequireActive();
        _session.ValidateStandaloneOperation(_operation);
        cancellationToken.ThrowIfCancellationRequested();
        await _session.WaitForTableExecutionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var outcome = await ExecuteCoreAsync(intent, cancellationToken).ConfigureAwait(false);
            return outcome.Result;
        }
        finally
        {
            _session.ReleaseTableExecution();
        }
    }

    internal ValueTask<LocalResourceIntentExecutionOutcome> ExecuteOwnedAsync(
        LocalResourceIntent intent,
        CancellationToken cancellationToken,
        LocalResourceIntentBegin? beginIntent = null)
    {
        ThrowIfCurrentCapabilityHandlerReentry();
        if (_owner is null)
        {
            throw new InvalidOperationException(
                "An owner-bound local resource executor is required inside a manager Tick.");
        }
        return ExecuteCoreAsync(intent, cancellationToken, beginIntent);
    }

    private async ValueTask<LocalResourceIntentExecutionOutcome> ExecuteCoreAsync(
        LocalResourceIntent intent,
        CancellationToken cancellationToken,
        LocalResourceIntentBegin? beginIntent = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var context = CreateContext(intent);
        var execution = beginIntent is null
            ? _session.BeginIntentCore(_owner, intent, context)
            : beginIntent(intent, context);
        LocalResourceCapabilityHandler handler;
        bool capabilityResolved;
        try
        {
            capabilityResolved = _session.Capabilities.TryResolve(
                intent.CapabilityId,
                intent.CapabilityGeneration,
                out handler);
        }
        catch (Exception exception)
        {
            try
            {
                _session.AbortIntentCore(_owner, execution);
            }
            catch (Exception abortException)
            {
                throw new AggregateException(
                    "Capability resolution failed and aborting its begun intent also failed.",
                    exception,
                    abortException);
            }
            throw;
        }
        if (!capabilityResolved)
        {
            var abort = _session.AbortIntentCore(_owner, execution);
            return new(
                new(abort, abort.EffectUncertain ? execution : null),
                CapabilityInvoked: false);
        }

        try
        {
            _session.EnterCapabilityHandler(_owner);
        }
        catch (Exception exception)
        {
            try
            {
                _session.AbortIntentCore(_owner, execution);
            }
            catch (Exception abortException)
            {
                throw new AggregateException(
                    "Capability handler admission failed and aborting its begun intent also failed.",
                    exception,
                    abortException);
            }
            throw;
        }

        var previousHandlerSession = CurrentHandlerSession.Value;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            _session.MarkEffectStartedCore(_owner, execution);
        }
        catch (Exception exception)
        {
            _session.ExitCapabilityHandler();
            try
            {
                _session.AbortIntentCore(_owner, execution);
            }
            catch (Exception abortException)
            {
                throw new AggregateException(
                    "The action did not start, but aborting its reserved intent also failed.",
                    exception,
                    abortException);
            }
            throw;
        }

        try
        {
            LocalResourceEffect effect;
            try
            {
                CurrentHandlerSession.Value = _session;
                effect = await handler(context, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                CurrentHandlerSession.Value = previousHandlerSession;
                _session.ExitCapabilityHandler();
            }
            var commit = _session.CommitEffectCore(_owner, execution, effect);
            var uncertain = commit.EffectUncertain ? execution : null;
            return new(
                new(commit, uncertain),
                CapabilityInvoked: true);
        }
        catch (Exception exception)
        {
            try
            {
                var commit = _session.MarkEffectUncertainCore(_owner, execution);
                if (!commit.EffectUncertain)
                {
                    throw new InvalidOperationException(
                        "Native manager did not preserve an invoked action as uncertain.");
                }
            }
            catch (Exception settlementException)
            {
                throw new LocalResourceEffectUncertainException(
                    execution,
                    new AggregateException(
                        "The invoked action failed and preserving its uncertain state also failed.",
                        exception,
                        settlementException));
            }
            throw new LocalResourceEffectUncertainException(execution, exception);
        }
    }

    public LocalResourceExecutionResult Reconcile(
        LocalResourceUncertainExecution execution,
        LocalResourceEffect confirmedEffect)
    {
        ThrowIfCapabilityHandlerLifecycleEntry();
        _session.WaitForStandaloneRecoveryOperation(execution);
        try
        {
            return ReconcileCore(execution, confirmedEffect);
        }
        finally
        {
            _session.CompleteStandaloneRecoveryOperation(execution);
        }
    }

    internal LocalResourceExecutionResult ReconcileOwned(
        LocalResourceUncertainExecution execution,
        LocalResourceEffect confirmedEffect)
    {
        ThrowIfCurrentCapabilityHandlerReentry();
        if (_owner is null)
        {
            throw new InvalidOperationException(
                "An owner-bound local resource executor is required for manager reconciliation.");
        }
        return ReconcileCore(execution, confirmedEffect);
    }

    private LocalResourceExecutionResult ReconcileCore(
        LocalResourceUncertainExecution execution,
        LocalResourceEffect confirmedEffect)
    {
        ArgumentNullException.ThrowIfNull(execution);
        if (!ReferenceEquals(execution.Session, _session))
        {
            throw new ArgumentException(
                "The uncertain execution belongs to a different manager session.",
                nameof(execution));
        }
        if (confirmedEffect.Outcome == LocalResourceEffectOutcome.EffectUnknown)
        {
            throw new ArgumentOutOfRangeException(
                nameof(confirmedEffect),
                "Reconciliation requires a known final outcome.");
        }
        if (!execution.TryBeginResolution())
        {
            throw new InvalidOperationException("The uncertain execution is already resolving or resolved.");
        }

        try
        {
            var commit = _session.CommitEffectCore(_owner, execution, confirmedEffect);
            if (commit.EffectUncertain)
            {
                throw new InvalidOperationException("Reconciliation did not produce a known final effect.");
            }
            execution.CompleteResolution();
            return new(commit, null);
        }
        catch
        {
            execution.ResetResolution();
            throw;
        }
    }

    private static LocalResourceExecutionContext CreateContext(LocalResourceIntent intent)
        => new(
            intent.TableId,
            intent.TableIncarnation,
            intent.ResourceUid,
            intent.CapabilityId,
            intent.CapabilityGeneration,
            intent.ActionCode,
            intent.ExpectedEffects,
            intent.Reason,
            intent.SizeBytes);

    internal static Task CaptureProcessCapabilityHandlerAdmission()
    {
        lock (ProcessHandlerGate)
        {
            if (_activeProcessHandlerCount != 0)
            {
                throw new InvalidOperationException(
                    LocalResourceOperationAdmission.ActiveCapabilityHandlerMessage);
            }
            return _processHandlerStarted.Task;
        }
    }

    internal static void EnterProcessCapabilityHandler(
        NativeLocalResourceManagerSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        TaskCompletionSource? handlerStarted = null;
        lock (ProcessHandlerGate)
        {
            if (_activeProcessHandlerCount == 0)
            {
                handlerStarted = _processHandlerStarted;
            }
            _activeProcessHandlerCount = checked(_activeProcessHandlerCount + 1);
            ActiveProcessHandlerSessions.TryGetValue(session, out var sessionCount);
            ActiveProcessHandlerSessions[session] = checked(sessionCount + 1);
        }
        handlerStarted?.TrySetResult();
    }

    internal static void ExitProcessCapabilityHandler(
        NativeLocalResourceManagerSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        lock (ProcessHandlerGate)
        {
            if (_activeProcessHandlerCount <= 0 ||
                !ActiveProcessHandlerSessions.TryGetValue(session, out var sessionCount) ||
                sessionCount <= 0)
            {
                throw new InvalidOperationException(
                    "The process-wide local resource capability handler admission is unbalanced.");
            }
            if (sessionCount == 1)
            {
                ActiveProcessHandlerSessions.Remove(session);
            }
            else
            {
                ActiveProcessHandlerSessions[session] = sessionCount - 1;
            }
            _activeProcessHandlerCount--;
            if (_activeProcessHandlerCount == 0)
            {
                ActiveProcessHandlerSessions.Clear();
                _processHandlerStarted = CreateHandlerStartedSignal();
            }
        }
    }

    internal static void ThrowIfCapabilityHandlerLifecycleEntry()
    {
        if (CurrentHandlerSession.Value is not null)
        {
            throw new InvalidOperationException(
                "A local resource capability handler cannot enter any local resource manager lifecycle.");
        }
        lock (ProcessHandlerGate)
        {
            if (_activeProcessHandlerCount != 0)
            {
                throw new InvalidOperationException(
                    "A local resource capability handler cannot enter any local resource manager lifecycle.");
            }
        }
    }

    internal static void ThrowIfCapabilityHandlerModePublication()
    {
        if (CurrentHandlerSession.Value is not null)
        {
            throw new InvalidOperationException(
                "A local resource capability handler cannot publish local resource manager mode state.");
        }
    }

    internal static void ThrowIfCurrentCapabilityHandlerReentry()
    {
        if (CurrentHandlerSession.Value is not null)
        {
            throw new InvalidOperationException(
                "A local resource capability handler cannot enter any local resource manager lifecycle.");
        }
    }

    private static TaskCompletionSource CreateHandlerStartedSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
