namespace ResourceManager.App.Infrastructure.Optimization;

internal enum HostManagerCycleEffectKind : byte
{
    SelfLedgerMaintenance = 1,
    NativeTransactionRecovery = 2,
    LegacyPlacementRestore = 3,
    NonAdaptedMemoryTransaction = 4,
    ExitedOwnershipReconciliation = 5,
    NativeActionTransaction = 6,
    AutomaticMemoryCleanup = 7,
    PublicResourceLifecycle = 8,
    NativeWorkspaceLifecycle = 9
}

internal enum HostManagerCycleEffectBudgetLane : byte
{
    NewPointOfNoReturn = 1,
    Recovery = 2
}

internal readonly record struct HostManagerCycleEffectBudgetSnapshot(
    uint NewPointOfNoReturnCapacity,
    uint NewPointOfNoReturnRemaining,
    uint RecoveryCapacity,
    uint RecoveryRemaining);

internal readonly struct HostManagerCycleEffectAdmission
{
    private readonly HostManagerCycleEffectLedgerState? state;
    private readonly ulong allowedEffectMask;

    private HostManagerCycleEffectAdmission(bool scoreOnly, ulong allowedEffectMask)
    {
        state = new HostManagerCycleEffectLedgerState(scoreOnly);
        this.allowedEffectMask = allowedEffectMask;
    }

    internal bool IsScoreOnly => RequireState().IsScoreOnly;

    internal uint NewPointOfNoReturnRemaining
        => RequireState().CaptureBudgetSnapshot().NewPointOfNoReturnRemaining;

    internal uint RecoveryRemaining
        => RequireState().CaptureBudgetSnapshot().RecoveryRemaining;

    internal static HostManagerCycleEffectAdmission Create(bool scoreOnly)
        => new(scoreOnly, ulong.MaxValue);

    internal static HostManagerCycleEffectAdmission CreateForValidation(
        bool scoreOnly,
        in HostManagerProcessEffectValidationCycleSnapshot scope)
        => new(
            scoreOnly,
            HostManagerProcessEffectValidationCyclePolicy.CreateAllowedEffectMask(scope));

    internal void InitializeBudgets(
        uint newPointOfNoReturnCapacity,
        uint recoveryCapacity)
        => RequireState().InitializeBudgets(
            newPointOfNoReturnCapacity,
            recoveryCapacity);

    internal HostManagerCycleEffectBudgetSnapshot CaptureBudgetSnapshot()
        => RequireState().CaptureBudgetSnapshot();

    internal bool TryAcquire(
        HostManagerCycleEffectKind kind,
        out HostManagerCycleEffectPermit permit)
    {
        var current = RequireState();
        HostManagerCycleEffectPermit.RequireKnown(kind);
        if (current.IsScoreOnly
            || (allowedEffectMask & (1UL << ((int)kind - 1))) == 0)
        {
            permit = default;
            return false;
        }

        permit = HostManagerCycleEffectPermit.Create(current, kind);
        return true;
    }

    private HostManagerCycleEffectLedgerState RequireState()
        => state ?? throw new InvalidOperationException(
            "The Host Manager cycle effect admission is not initialized.");
}

internal readonly struct HostManagerCycleEffectPermit
{
    private readonly HostManagerCycleEffectLedgerState? state;
    private readonly HostManagerCycleEffectKind kind;

    private HostManagerCycleEffectPermit(
        HostManagerCycleEffectLedgerState state,
        HostManagerCycleEffectKind kind)
    {
        this.state = state;
        this.kind = kind;
    }

    internal static HostManagerCycleEffectPermit Create(
        HostManagerCycleEffectLedgerState state,
        HostManagerCycleEffectKind kind)
    {
        ArgumentNullException.ThrowIfNull(state);
        RequireKnown(kind);
        return new HostManagerCycleEffectPermit(state, kind);
    }

    internal void Require(HostManagerCycleEffectKind expectedKind)
    {
        RequireKnown(expectedKind);
        if (state is null || kind != expectedKind)
        {
            throw new InvalidOperationException(
                $"The Host Manager cycle effect permit does not authorize {expectedKind}.");
        }
    }

    internal bool TryReserveNewPointOfNoReturn(
        uint maximumCount,
        out HostManagerCycleEffectReservation? reservation)
    {
        RequireNewPointOfNoReturnKind();
        return state!.TryReserve(
            HostManagerCycleEffectBudgetLane.NewPointOfNoReturn,
            maximumCount,
            out reservation);
    }

    internal bool TryReserveExactNewPointOfNoReturn(
        uint count,
        out HostManagerCycleEffectReservation? reservation)
    {
        RequireNewPointOfNoReturnKind();
        return state!.TryReserveExact(
            HostManagerCycleEffectBudgetLane.NewPointOfNoReturn,
            count,
            out reservation);
    }

    internal uint NewPointOfNoReturnRemaining
    {
        get
        {
            RequireNewPointOfNoReturnKind();
            return state!.CaptureBudgetSnapshot().NewPointOfNoReturnRemaining;
        }
    }

    internal void RequireSingleNewActionReservation(HostManagerCycleEffectReservation reservation)
    {
        Require(HostManagerCycleEffectKind.NativeActionTransaction);
        ArgumentNullException.ThrowIfNull(reservation);
        reservation.RequireSingleNewAction(state!);
    }

    internal void EnterSingleNewAction(HostManagerCycleEffectReservation reservation)
    {
        Require(HostManagerCycleEffectKind.NativeActionTransaction);
        ArgumentNullException.ThrowIfNull(reservation);
        reservation.EnterSingleNewAction(state!);
    }

    internal bool TryReserveOwnedMemoryRestore(
        uint maximumCount,
        out HostManagerCycleEffectReservation? reservation)
    {
        RequireOwnedMemoryRestoreKind();
        return state!.TryReserve(
            HostManagerCycleEffectBudgetLane.Recovery,
            maximumCount,
            out reservation);
    }

    internal bool TryReserveOwnedNativeRestore(
        out HostManagerCycleEffectReservation? reservation)
    {
        Require(HostManagerCycleEffectKind.NativeActionTransaction);
        return state!.TryReserveExact(
            HostManagerCycleEffectBudgetLane.Recovery,
            1,
            out reservation);
    }

    internal uint OwnedMemoryRestoreRemaining
    {
        get
        {
            RequireOwnedMemoryRestoreKind();
            return state!.CaptureBudgetSnapshot().RecoveryRemaining;
        }
    }

    internal bool TryReserveRecovery(
        uint maximumCount,
        out HostManagerCycleEffectReservation? reservation)
    {
        RequireRecoveryKind();
        return state!.TryReserve(
            HostManagerCycleEffectBudgetLane.Recovery,
            maximumCount,
            out reservation);
    }

    internal uint RecoveryRemaining
    {
        get
        {
            RequireRecoveryKind();
            return state!.CaptureBudgetSnapshot().RecoveryRemaining;
        }
    }

    private void RequireNewPointOfNoReturnKind()
    {
        if (state is null || kind is not (
            HostManagerCycleEffectKind.NonAdaptedMemoryTransaction or
            HostManagerCycleEffectKind.NativeActionTransaction or
            HostManagerCycleEffectKind.AutomaticMemoryCleanup or
            HostManagerCycleEffectKind.PublicResourceLifecycle))
        {
            throw new InvalidOperationException(
                $"The Host Manager cycle effect permit does not authorize new PONR reservation for {kind}.");
        }
    }

    private void RequireRecoveryKind()
    {
        if (state is null || kind is not (
            HostManagerCycleEffectKind.NativeTransactionRecovery or
            HostManagerCycleEffectKind.LegacyPlacementRestore or
            HostManagerCycleEffectKind.ExitedOwnershipReconciliation or
            HostManagerCycleEffectKind.PublicResourceLifecycle))
        {
            throw new InvalidOperationException(
                $"The Host Manager cycle effect permit does not authorize recovery reservation for {kind}.");
        }
    }

    private void RequireOwnedMemoryRestoreKind()
    {
        if (state is null
            || kind != HostManagerCycleEffectKind.NonAdaptedMemoryTransaction)
        {
            throw new InvalidOperationException(
                $"The Host Manager cycle effect permit does not authorize owned memory restoration for {kind}.");
        }
    }

    internal static void RequireKnown(HostManagerCycleEffectKind kind)
    {
        if (kind is < HostManagerCycleEffectKind.SelfLedgerMaintenance
            or > HostManagerCycleEffectKind.NativeWorkspaceLifecycle)
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }
    }
}

internal sealed class HostManagerCycleEffectReservation : IDisposable
{
    private readonly HostManagerCycleEffectLedgerState state;
    private readonly HostManagerCycleEffectBudgetLane lane;
    private readonly object sync = new();
    private uint enteredPointOfNoReturnCount;
    private bool settled;

    internal HostManagerCycleEffectReservation(
        HostManagerCycleEffectLedgerState state,
        HostManagerCycleEffectBudgetLane lane,
        uint count)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (count == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        this.state = state;
        this.lane = lane;
        Count = count;
    }

    internal uint Count { get; }

    internal HostManagerCycleEffectBudgetLane Lane => lane;

    internal void RequireSingleNewAction(HostManagerCycleEffectLedgerState owner)
    {
        lock (sync) RequireSingleNewActionCore(owner);
    }

    internal void EnterSingleNewAction(HostManagerCycleEffectLedgerState owner)
    {
        lock (sync)
        {
            RequireSingleNewActionCore(owner);
            // Observation and placement are steps of one reserved action, not new budget units.
            enteredPointOfNoReturnCount = 1;
        }
    }

    private void RequireSingleNewActionCore(HostManagerCycleEffectLedgerState owner)
    {
        if (!ReferenceEquals(state, owner)
            || lane != HostManagerCycleEffectBudgetLane.NewPointOfNoReturn
            || Count != 1
            || settled)
        {
            throw new InvalidOperationException(
                "The placement requires an active single new-action reservation from this cycle.");
        }
    }

    internal void EnterPointOfNoReturn(uint count = 1)
    {
        if (count == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        lock (sync)
        {
            if (settled)
            {
                throw new InvalidOperationException(
                    "The Host Manager effect reservation was already settled.");
            }
            if (count > Count - enteredPointOfNoReturnCount)
            {
                throw new InvalidDataException(
                    "The Host Manager effect reservation entered more points of no return than it reserved.");
            }
            enteredPointOfNoReturnCount = checked(enteredPointOfNoReturnCount + count);
        }
    }

    internal void ReleaseUnusedBeforePointOfNoReturn(uint consumedCount)
    {
        if (consumedCount > Count)
        {
            throw new InvalidDataException(
                "The Host Manager effect reservation consumed more actions than it reserved.");
        }
        uint releaseCount;
        lock (sync)
        {
            if (settled)
            {
                throw new InvalidOperationException(
                    "The Host Manager effect reservation was already settled.");
            }
            settled = true;
            releaseCount = checked(Count - consumedCount);
        }

        state.Release(lane, releaseCount);
    }

    public void Dispose()
    {
        uint releaseCount;
        lock (sync)
        {
            if (settled)
            {
                return;
            }
            settled = true;
            releaseCount = checked(Count - enteredPointOfNoReturnCount);
        }

        state.Release(lane, releaseCount);
    }
}

internal sealed class HostManagerCycleEffectLedgerState(bool scoreOnly)
{
    private readonly object sync = new();
    private bool budgetsInitialized;
    private uint newPointOfNoReturnCapacity;
    private uint newPointOfNoReturnRemaining;
    private uint recoveryCapacity;
    private uint recoveryRemaining;

    internal bool IsScoreOnly { get; } = scoreOnly;

    internal void InitializeBudgets(
        uint requestedNewPointOfNoReturnCapacity,
        uint requestedRecoveryCapacity)
    {
        lock (sync)
        {
            if (budgetsInitialized)
            {
                throw new InvalidOperationException(
                    "The Host Manager cycle effect budgets were already initialized.");
            }
            if (!IsScoreOnly
                && (requestedNewPointOfNoReturnCapacity == 0
                    || requestedRecoveryCapacity == 0))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(requestedNewPointOfNoReturnCapacity),
                    "Executing Host Manager cycles require nonzero new-PONR and recovery capacities.");
            }

            newPointOfNoReturnCapacity = IsScoreOnly
                ? 0
                : requestedNewPointOfNoReturnCapacity;
            newPointOfNoReturnRemaining = newPointOfNoReturnCapacity;
            recoveryCapacity = IsScoreOnly ? 0 : requestedRecoveryCapacity;
            recoveryRemaining = recoveryCapacity;
            budgetsInitialized = true;
        }
    }

    internal HostManagerCycleEffectBudgetSnapshot CaptureBudgetSnapshot()
    {
        lock (sync)
        {
            RequireBudgetsInitialized();
            return new HostManagerCycleEffectBudgetSnapshot(
                newPointOfNoReturnCapacity,
                newPointOfNoReturnRemaining,
                recoveryCapacity,
                recoveryRemaining);
        }
    }

    internal bool TryReserve(
        HostManagerCycleEffectBudgetLane lane,
        uint maximumCount,
        out HostManagerCycleEffectReservation? reservation)
        => TryReserveCore(
            lane,
            maximumCount,
            requireExactCount: false,
            out reservation);

    internal bool TryReserveExact(
        HostManagerCycleEffectBudgetLane lane,
        uint count,
        out HostManagerCycleEffectReservation? reservation)
        => TryReserveCore(
            lane,
            count,
            requireExactCount: true,
            out reservation);

    private bool TryReserveCore(
        HostManagerCycleEffectBudgetLane lane,
        uint requestedCount,
        bool requireExactCount,
        out HostManagerCycleEffectReservation? reservation)
    {
        lock (sync)
        {
            RequireBudgetsInitialized();
            if (requestedCount == 0)
            {
                reservation = null;
                return false;
            }
            var remaining = lane switch
            {
                HostManagerCycleEffectBudgetLane.NewPointOfNoReturn
                    => newPointOfNoReturnRemaining,
                HostManagerCycleEffectBudgetLane.Recovery => recoveryRemaining,
                _ => throw new ArgumentOutOfRangeException(nameof(lane), lane, null)
            };
            var count = requireExactCount && requestedCount > remaining
                ? 0
                : Math.Min(requestedCount, remaining);
            if (count == 0)
            {
                reservation = null;
                return false;
            }

            if (lane == HostManagerCycleEffectBudgetLane.NewPointOfNoReturn)
            {
                newPointOfNoReturnRemaining = checked(
                    newPointOfNoReturnRemaining - count);
            }
            else
            {
                recoveryRemaining = checked(recoveryRemaining - count);
            }
            reservation = new HostManagerCycleEffectReservation(this, lane, count);
            return true;
        }
    }

    internal void Release(HostManagerCycleEffectBudgetLane lane, uint count)
    {
        if (count == 0)
        {
            return;
        }

        lock (sync)
        {
            RequireBudgetsInitialized();
            switch (lane)
            {
                case HostManagerCycleEffectBudgetLane.NewPointOfNoReturn:
                    newPointOfNoReturnRemaining = checked(
                        newPointOfNoReturnRemaining + count);
                    if (newPointOfNoReturnRemaining > newPointOfNoReturnCapacity)
                    {
                        throw new InvalidOperationException(
                            "The Host Manager new-PONR budget was over-released.");
                    }
                    break;
                case HostManagerCycleEffectBudgetLane.Recovery:
                    recoveryRemaining = checked(recoveryRemaining + count);
                    if (recoveryRemaining > recoveryCapacity)
                    {
                        throw new InvalidOperationException(
                            "The Host Manager recovery budget was over-released.");
                    }
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(lane), lane, null);
            }
        }
    }

    private void RequireBudgetsInitialized()
    {
        if (!budgetsInitialized)
        {
            throw new InvalidOperationException(
                "The Host Manager cycle effect budgets are not initialized.");
        }
    }
}
