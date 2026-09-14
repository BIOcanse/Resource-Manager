using System.Diagnostics;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Domain.Optimization.MemoryCleanup;

namespace ResourceManager.App.Infrastructure.Optimization;

internal readonly record struct HostManagerMemoryCleanupValidationActionOutcome(
    bool ResultReturned,
    bool Succeeded,
    uint? Win32Error);

internal readonly record struct HostManagerMemoryCleanupValidationFactContext(
    ulong PlannerConfigurationGeneration,
    double GuardedFreeRatio,
    double PhysicalEmergencyFreeRatio,
    double VirtualEmergencyFreeRatio,
    ulong CapacityWorkspaceIdentity,
    ulong CapacityConfigurationGeneration,
    ulong CapacityCatalogGeneration,
    ulong BaselineCapacityCommittedGeneration,
    ulong FinalCapacityCommittedGeneration,
    long BaselineCapacityCapturedAtUtcTicks,
    long FinalCapacityCapturedAtUtcTicks,
    bool OrdinaryMemoryFreeRatioCurrent,
    bool PhysicalMemoryFreeRatioCurrent,
    bool VirtualMemoryFreeRatioCurrent);

internal sealed class HostManagerMemoryCleanupValidationEvidenceReservation : IDisposable
{
    internal static HostManagerMemoryCleanupValidationEvidenceReservation ProductionUnscoped
        { get; } = new();

    private readonly HostManagerMemoryCleanupValidationEvidenceLedger? owner;
    private readonly object sync = new();
    private bool settled;

    private HostManagerMemoryCleanupValidationEvidenceReservation()
    {
    }

    internal HostManagerMemoryCleanupValidationEvidenceReservation(
        HostManagerMemoryCleanupValidationEvidenceLedger owner,
        Guid reservationId)
    {
        this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
        ReservationId = reservationId;
    }

    internal Guid ReservationId { get; }

    internal bool IsProductionUnscoped => owner is null;

    internal void MarkWriterStarted(
        DateTimeOffset writerStartedAt,
        long writerStartedAtQpcTicks)
    {
        if (IsProductionUnscoped)
        {
            return;
        }
        lock (sync)
        {
            if (settled)
            {
                throw new InvalidOperationException(
                    "The memory-cleanup validation evidence reservation was already settled.");
            }
            owner!.MarkWriterStarted(
                ReservationId,
                writerStartedAt,
                writerStartedAtQpcTicks);
        }
    }

    internal void Complete(
        IReadOnlyList<HostManagerMemoryCleanupValidationActionOutcome> outcomes,
        bool resultShapeExact,
        bool journalSettled,
        DateTimeOffset completedAt,
        long completedAtQpcTicks)
    {
        ArgumentNullException.ThrowIfNull(outcomes);
        if (IsProductionUnscoped)
        {
            return;
        }
        lock (sync)
        {
            if (settled)
            {
                throw new InvalidOperationException(
                    "The memory-cleanup validation evidence reservation was already settled.");
            }
            owner!.Complete(
                ReservationId,
                outcomes,
                resultShapeExact,
                journalSettled,
                completedAt,
                completedAtQpcTicks);
            settled = true;
        }
    }

    internal void CompleteUnknown(
        DateTimeOffset completedAt,
        long completedAtQpcTicks)
    {
        if (IsProductionUnscoped)
        {
            return;
        }
        lock (sync)
        {
            if (settled)
            {
                throw new InvalidOperationException(
                    "The memory-cleanup validation evidence reservation was already settled.");
            }
            owner!.CompleteUnknown(
                ReservationId,
                completedAt,
                completedAtQpcTicks);
            settled = true;
        }
    }

    public void Dispose()
    {
        if (IsProductionUnscoped)
        {
            return;
        }
        lock (sync)
        {
            if (settled)
            {
                return;
            }
            owner!.Abandon(ReservationId);
            settled = true;
        }
    }
}

internal sealed class HostManagerMemoryCleanupValidationEvidenceLedger
{
    internal const int SchemaVersion = 3;
    internal const string Contract =
        "host-manager-memory-cleanup-validation-evidence-v3";
    internal const int MaximumBatchCount = 256;
    internal const int MaximumActionCount = 4096;

    private readonly object sync = new();
    private readonly Dictionary<Guid, PendingBatch> pending = [];
    private readonly List<HostManagerMemoryCleanupValidationBatchEvidence> completed = [];
    private Guid scopeId;
    private long scopeGeneration;
    private long nextSequence;
    private int reservedActionCount;
    private HostManagerMemoryCleanupValidationEvidenceSnapshot? sealedSnapshot;

    internal HostManagerMemoryCleanupValidationEvidenceLedger(TimeProvider timeProvider)
    {
        this.timeProvider = timeProvider
            ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    private readonly TimeProvider timeProvider;

    internal void Reset(Guid newScopeId, long newScopeGeneration)
    {
        if (newScopeId == Guid.Empty || newScopeGeneration <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(newScopeId),
                "A memory-cleanup validation evidence scope identity is invalid.");
        }
        lock (sync)
        {
            if (pending.Count != 0)
            {
                throw new InvalidOperationException(
                    "Memory-cleanup validation evidence still has an active reservation.");
            }
            scopeId = newScopeId;
            scopeGeneration = newScopeGeneration;
            nextSequence = 0;
            reservedActionCount = 0;
            completed.Clear();
            sealedSnapshot = null;
        }
    }

    internal bool TryReserve(
        HostManagerProcessEffectValidationCycleSnapshot scope,
        ulong cycleSequence,
        Guid batchId,
        ulong attemptGeneration,
        ulong plannerStateRevision,
        HostManagerMemoryCleanupValidationFactContext factContext,
        AutomaticMemoryCleanupPlanRequest request,
        IReadOnlyList<AutomaticMemoryCleanupDecision> decisions,
        out HostManagerMemoryCleanupValidationEvidenceReservation? reservation)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(decisions);
        if (scope.IsProductionUnscoped)
        {
            reservation =
                HostManagerMemoryCleanupValidationEvidenceReservation.ProductionUnscoped;
            return true;
        }
        if (scope.State != HostManagerProcessEffectValidationScopeState.Active
            || scope.ScopeId == Guid.Empty
            || scope.Generation <= 0
            || cycleSequence == 0
            || batchId == Guid.Empty
            || attemptGeneration == 0
            || plannerStateRevision == 0
            || !IsValidRequestFacts(factContext, request)
            || request.Candidates.Count is < 1 or >
                HostManagerProcessEffectValidationScopeAuthority
                    .MaximumAllowedProcessCount
            || decisions.Count is < 1 or > HostManagerProcessEffectValidationScopeAuthority
                .MaximumAllowedProcessCount)
        {
            reservation = null;
            return false;
        }

        var actions = new PreparedAction[decisions.Count];
        var selectedMode = decisions[0].Mode;
        if (!IsValidSelectedMode(factContext, request, selectedMode))
        {
            reservation = null;
            return false;
        }
        var preparedProcessIdentities = new HashSet<HostManagerComputeProcessIdentity>();
        var preparedReservationIdentities = new HashSet<(uint Slot, uint Generation)>();
        var preparedSourceInputIndexes = new HashSet<int>();
        for (var index = 0; index < decisions.Count; index++)
        {
            var decision = decisions[index];
            long processStartTimeFileTimeUtc;
            try
            {
                processStartTimeFileTimeUtc = decision.Candidate.ProcessStartedAt.ToFileTime();
            }
            catch (ArgumentOutOfRangeException)
            {
                reservation = null;
                return false;
            }
            if (decision.SourceInputIndex < 0
                || decision.SourceInputIndex >= request.Candidates.Count
                || !preparedSourceInputIndexes.Add(decision.SourceInputIndex)
                || decision.Candidate != request.Candidates[decision.SourceInputIndex]
                || decision.Mode != selectedMode
                || decision.Candidate.ProcessId <= 0
                || processStartTimeFileTimeUtc <= 0
                || decision.Reservation.StateGeneration == 0)
            {
                reservation = null;
                return false;
            }
            var identity = new HostManagerComputeProcessIdentity(
                decision.Candidate.ProcessId,
                checked((ulong)processStartTimeFileTimeUtc));
            if (!scope.Allows(identity)
                || !preparedProcessIdentities.Add(identity)
                || !preparedReservationIdentities.Add((
                    decision.Reservation.StateSlot,
                    decision.Reservation.StateGeneration)))
            {
                reservation = null;
                return false;
            }
            actions[index] = new PreparedAction(
                index,
                decision.SourceInputIndex,
                decision.Candidate.ProcessId,
                processStartTimeFileTimeUtc,
                decision.Reservation.StateSlot,
                decision.Reservation.StateGeneration);
        }

        lock (sync)
        {
            if (scopeId != scope.ScopeId
                || scopeGeneration != scope.Generation
                || sealedSnapshot is not null
                || pending.Count + completed.Count >= MaximumBatchCount
                || actions.Length > MaximumActionCount - reservedActionCount
                || pending.Values.Any(item => item.BatchId == batchId)
                || completed.Any(item => item.BatchId == batchId)
                || pending.Values.Any(item => item.CycleSequence == cycleSequence)
                || completed.Any(item => item.CycleSequence == cycleSequence)
                || pending.Values.Any(item =>
                    item.AttemptGeneration == attemptGeneration)
                || completed.Any(item =>
                    item.AttemptGeneration == attemptGeneration)
                || pending.Values
                    .SelectMany(static item => item.Actions)
                    .Any(action => preparedReservationIdentities.Contains((
                        action.ReservationStateSlot,
                        action.ReservationStateGeneration)))
                || completed
                    .SelectMany(static item => item.Actions)
                    .Any(action => preparedReservationIdentities.Contains((
                        action.ReservationStateSlot,
                        action.ReservationStateGeneration))))
            {
                reservation = null;
                return false;
            }
            var reservationId = Guid.NewGuid();
            pending.Add(
                reservationId,
                new PendingBatch(
                    batchId,
                    cycleSequence,
                    attemptGeneration,
                    plannerStateRevision,
                    factContext,
                    request.Kind,
                    request.OrdinaryMemoryFreeRatio,
                    request.PhysicalMemoryFreeRatio,
                    request.VirtualMemoryFreeRatio,
                    selectedMode,
                    request.Candidates.Count,
                    actions,
                    WriterStartedAt: default,
                    WriterStartedAtQpcTicks: 0));
            reservedActionCount = checked(reservedActionCount + actions.Length);
            reservation = new(
                this,
                reservationId);
            return true;
        }
    }

    internal HostManagerMemoryCleanupValidationEvidenceSnapshot Capture()
    {
        lock (sync)
        {
            return sealedSnapshot ?? CreateSnapshotLocked(isSealed: false);
        }
    }

    internal HostManagerMemoryCleanupValidationEvidenceSnapshot SealAndCapture()
    {
        lock (sync)
        {
            if (sealedSnapshot is not null)
            {
                return sealedSnapshot;
            }
            var snapshot = CreateSnapshotLocked(
                isSealed: scopeId != Guid.Empty && pending.Count == 0);
            if (snapshot.Sealed)
            {
                sealedSnapshot = snapshot;
            }
            return snapshot;
        }
    }

    internal void MarkWriterStarted(
        Guid reservationId,
        DateTimeOffset writerStartedAt,
        long writerStartedAtQpcTicks)
    {
        lock (sync)
        {
            var batch = GetPending(reservationId);
            if (writerStartedAt.UtcTicks <= 0
                || writerStartedAtQpcTicks <= 0
                || batch.WriterStartedAtQpcTicks != 0)
            {
                throw new InvalidDataException(
                    "Memory-cleanup validation writer-start evidence is invalid.");
            }
            pending[reservationId] = batch with
            {
                WriterStartedAt = writerStartedAt,
                WriterStartedAtQpcTicks = writerStartedAtQpcTicks
            };
        }
    }

    internal void Complete(
        Guid reservationId,
        IReadOnlyList<HostManagerMemoryCleanupValidationActionOutcome> outcomes,
        bool resultShapeExact,
        bool journalSettled,
        DateTimeOffset completedAt,
        long completedAtQpcTicks)
    {
        lock (sync)
        {
            var batch = GetPending(reservationId);
            if (outcomes.Count != batch.Actions.Length
                || outcomes.Any(static outcome => !IsCanonicalOutcome(outcome))
                || completedAt.UtcTicks <= 0
                || completedAtQpcTicks <= 0
                || !HasValidWriterInterval(batch, completedAt, completedAtQpcTicks))
            {
                throw new InvalidDataException(
                    "Memory-cleanup validation evidence completion is invalid.");
            }
            var actionEvidence = new HostManagerMemoryCleanupValidationActionEvidence[
                batch.Actions.Length];
            var outcomeKnown = resultShapeExact;
            for (var index = 0; index < actionEvidence.Length; index++)
            {
                var prepared = batch.Actions[index];
                var outcome = outcomes[index];
                if (!outcome.ResultReturned)
                {
                    outcomeKnown = false;
                }
                else if (outcome.Succeeded)
                {
                    outcomeKnown &= outcome.Win32Error == 0;
                }
                else
                {
                    outcomeKnown &= outcome.Win32Error is > 0;
                }
                actionEvidence[index] = new(
                    prepared.ActionIndex,
                    prepared.SourceInputIndex,
                    prepared.ProcessId,
                    prepared.ProcessStartTimeFileTimeUtc,
                    prepared.ReservationStateSlot,
                    prepared.ReservationStateGeneration,
                    outcome.ResultReturned,
                    outcome.Succeeded,
                    outcome.Win32Error);
            }
            _ = RemovePending(reservationId, releaseCapacity: false);
            var sequence = checked(++nextSequence);
            completed.Add(new(
                sequence,
                batch.CycleSequence,
                batch.BatchId,
                batch.AttemptGeneration,
                batch.PlannerStateRevision,
                batch.FactContext.PlannerConfigurationGeneration,
                batch.FactContext.GuardedFreeRatio,
                batch.FactContext.PhysicalEmergencyFreeRatio,
                batch.FactContext.VirtualEmergencyFreeRatio,
                batch.FactContext.CapacityWorkspaceIdentity,
                batch.FactContext.CapacityConfigurationGeneration,
                batch.FactContext.CapacityCatalogGeneration,
                batch.FactContext.BaselineCapacityCommittedGeneration,
                batch.FactContext.FinalCapacityCommittedGeneration,
                batch.FactContext.BaselineCapacityCapturedAtUtcTicks,
                batch.FactContext.FinalCapacityCapturedAtUtcTicks,
                batch.RequestKind,
                batch.OrdinaryMemoryFreeRatio,
                batch.FactContext.OrdinaryMemoryFreeRatioCurrent,
                batch.PhysicalMemoryFreeRatio,
                batch.FactContext.PhysicalMemoryFreeRatioCurrent,
                batch.VirtualMemoryFreeRatio,
                batch.FactContext.VirtualMemoryFreeRatioCurrent,
                batch.SelectedMode,
                batch.CandidateCount,
                batch.Actions.Length,
                resultShapeExact,
                outcomeKnown,
                journalSettled,
                batch.WriterStartedAt,
                batch.WriterStartedAtQpcTicks,
                completedAt,
                completedAtQpcTicks,
                actionEvidence));
        }
    }

    internal void CompleteUnknown(
        Guid reservationId,
        DateTimeOffset completedAt,
        long completedAtQpcTicks)
    {
        lock (sync)
        {
            var batch = GetPending(reservationId);
            if (completedAt.UtcTicks <= 0
                || completedAtQpcTicks <= 0
                || !HasValidWriterInterval(batch, completedAt, completedAtQpcTicks))
            {
                throw new InvalidDataException(
                    "Memory-cleanup validation unknown completion is invalid.");
            }
            AppendUnknownLocked(
                reservationId,
                batch,
                completedAt,
                completedAtQpcTicks);
        }
    }

    internal void Abandon(Guid reservationId)
    {
        lock (sync)
        {
            var batch = GetPending(reservationId);
            if (batch.WriterStartedAtQpcTicks == 0)
            {
                _ = RemovePending(reservationId, releaseCapacity: true);
                return;
            }
            var completedAt = timeProvider.GetUtcNow();
            if (completedAt < batch.WriterStartedAt)
            {
                completedAt = batch.WriterStartedAt;
            }
            var completedAtQpcTicks = Math.Max(
                Stopwatch.GetTimestamp(),
                batch.WriterStartedAtQpcTicks);
            AppendUnknownLocked(
                reservationId,
                batch,
                completedAt,
                completedAtQpcTicks);
        }
    }

    private HostManagerMemoryCleanupValidationEvidenceSnapshot CreateSnapshotLocked(
        bool isSealed)
        => new(
            SchemaVersion,
            Contract,
            scopeId == Guid.Empty ? null : scopeId,
            scopeGeneration,
            nextSequence,
            timeProvider.GetUtcNow(),
            Stopwatch.GetTimestamp(),
            Stopwatch.Frequency,
            isSealed,
            pending.Count == 0
                ? null
                : "Memory-cleanup validation evidence has an unsettled reservation.",
            completed.ToArray());

    private void AppendUnknownLocked(
        Guid reservationId,
        PendingBatch batch,
        DateTimeOffset completedAt,
        long completedAtQpcTicks)
    {
        var actions = batch.Actions
            .Select(static action =>
                new HostManagerMemoryCleanupValidationActionEvidence(
                    action.ActionIndex,
                    action.SourceInputIndex,
                    action.ProcessId,
                    action.ProcessStartTimeFileTimeUtc,
                    action.ReservationStateSlot,
                    action.ReservationStateGeneration,
                    ResultReturned: false,
                    Succeeded: false,
                    Win32Error: null))
            .ToArray();
        _ = RemovePending(reservationId, releaseCapacity: false);
        var sequence = checked(++nextSequence);
        completed.Add(new(
            sequence,
            batch.CycleSequence,
            batch.BatchId,
            batch.AttemptGeneration,
            batch.PlannerStateRevision,
            batch.FactContext.PlannerConfigurationGeneration,
            batch.FactContext.GuardedFreeRatio,
            batch.FactContext.PhysicalEmergencyFreeRatio,
            batch.FactContext.VirtualEmergencyFreeRatio,
            batch.FactContext.CapacityWorkspaceIdentity,
            batch.FactContext.CapacityConfigurationGeneration,
            batch.FactContext.CapacityCatalogGeneration,
            batch.FactContext.BaselineCapacityCommittedGeneration,
            batch.FactContext.FinalCapacityCommittedGeneration,
            batch.FactContext.BaselineCapacityCapturedAtUtcTicks,
            batch.FactContext.FinalCapacityCapturedAtUtcTicks,
            batch.RequestKind,
            batch.OrdinaryMemoryFreeRatio,
            batch.FactContext.OrdinaryMemoryFreeRatioCurrent,
            batch.PhysicalMemoryFreeRatio,
            batch.FactContext.PhysicalMemoryFreeRatioCurrent,
            batch.VirtualMemoryFreeRatio,
            batch.FactContext.VirtualMemoryFreeRatioCurrent,
            batch.SelectedMode,
            batch.CandidateCount,
            batch.Actions.Length,
            ResultShapeExact: false,
            OutcomeKnown: false,
            JournalSettled: false,
            batch.WriterStartedAt,
            batch.WriterStartedAtQpcTicks,
            completedAt,
            completedAtQpcTicks,
            actions));
    }

    private static bool HasValidWriterInterval(
        PendingBatch batch,
        DateTimeOffset completedAt,
        long completedAtQpcTicks)
        => batch.WriterStartedAt.UtcTicks > 0
            && batch.WriterStartedAtQpcTicks > 0
            && completedAt >= batch.WriterStartedAt
            && completedAtQpcTicks >= batch.WriterStartedAtQpcTicks;

    private static bool IsValidRequestFacts(
        HostManagerMemoryCleanupValidationFactContext factContext,
        AutomaticMemoryCleanupPlanRequest request)
    {
        const AutomaticMemoryCleanupRequestKind allKinds =
            AutomaticMemoryCleanupRequestKind.Normal
            | AutomaticMemoryCleanupRequestKind.EvaluateEmergency
            | AutomaticMemoryCleanupRequestKind.ForceEmergency;
        return factContext.PlannerConfigurationGeneration != 0
            && IsRatio(factContext.GuardedFreeRatio)
            && IsRatio(factContext.PhysicalEmergencyFreeRatio)
            && IsRatio(factContext.VirtualEmergencyFreeRatio)
            && factContext.CapacityWorkspaceIdentity != 0
            && factContext.CapacityConfigurationGeneration != 0
            && factContext.CapacityCatalogGeneration != 0
            && factContext.BaselineCapacityCommittedGeneration != 0
            && factContext.FinalCapacityCommittedGeneration
                > factContext.BaselineCapacityCommittedGeneration
            && factContext.BaselineCapacityCapturedAtUtcTicks > 0
            && factContext.FinalCapacityCapturedAtUtcTicks
                > factContext.BaselineCapacityCapturedAtUtcTicks
            && request.Candidates is not null
            && request.Kind != AutomaticMemoryCleanupRequestKind.None
            && (request.Kind & ~allKinds) == 0
            && IsRatio(request.OrdinaryMemoryFreeRatio)
            && IsRatio(request.PhysicalMemoryFreeRatio)
            && IsRatio(request.VirtualMemoryFreeRatio)
            && (factContext.OrdinaryMemoryFreeRatioCurrent
                || request.OrdinaryMemoryFreeRatio == 1)
            && (factContext.PhysicalMemoryFreeRatioCurrent
                || request.PhysicalMemoryFreeRatio == 1)
            && (factContext.VirtualMemoryFreeRatioCurrent
                || request.VirtualMemoryFreeRatio == 1);
    }

    private static bool IsValidSelectedMode(
        HostManagerMemoryCleanupValidationFactContext factContext,
        AutomaticMemoryCleanupPlanRequest request,
        AutomaticMemoryCleanupMode selectedMode)
    {
        var requestKind = request.Kind;
        return selectedMode switch
        {
            AutomaticMemoryCleanupMode.Normal =>
                (requestKind & AutomaticMemoryCleanupRequestKind.Normal) != 0
                && (requestKind & AutomaticMemoryCleanupRequestKind.ForceEmergency) == 0
                && factContext.OrdinaryMemoryFreeRatioCurrent
                && (requestKind switch
                    {
                        var kind when (kind
                                & AutomaticMemoryCleanupRequestKind.EvaluateEmergency) == 0 =>
                            true,
                        _ => factContext.PhysicalMemoryFreeRatioCurrent
                            && request.PhysicalMemoryFreeRatio
                                > factContext.PhysicalEmergencyFreeRatio
                            && (!factContext.VirtualMemoryFreeRatioCurrent
                                || request.VirtualMemoryFreeRatio
                                    > factContext.VirtualEmergencyFreeRatio)
                    })
                && request.OrdinaryMemoryFreeRatio < factContext.GuardedFreeRatio,
            AutomaticMemoryCleanupMode.Emergency =>
                (requestKind & AutomaticMemoryCleanupRequestKind.ForceEmergency) != 0
                || ((requestKind
                        & AutomaticMemoryCleanupRequestKind.EvaluateEmergency) != 0
                    && ((factContext.PhysicalMemoryFreeRatioCurrent
                            && request.PhysicalMemoryFreeRatio
                                <= factContext.PhysicalEmergencyFreeRatio)
                        || (factContext.VirtualMemoryFreeRatioCurrent
                            && request.VirtualMemoryFreeRatio
                                <= factContext.VirtualEmergencyFreeRatio))),
            _ => false
        };
    }

    private static bool IsRatio(double value)
        => double.IsFinite(value) && value is >= 0 and <= 1;

    private static bool IsCanonicalOutcome(
        HostManagerMemoryCleanupValidationActionOutcome outcome)
        => outcome.ResultReturned
            ? outcome.Succeeded
                ? outcome.Win32Error == 0
                : outcome.Win32Error is > 0
            : !outcome.Succeeded && outcome.Win32Error is null;

    private PendingBatch RemovePending(Guid reservationId, bool releaseCapacity)
    {
        if (reservationId == Guid.Empty
            || !pending.Remove(reservationId, out var batch))
        {
            throw new InvalidOperationException(
                "Memory-cleanup validation evidence reservation is missing.");
        }
        if (releaseCapacity)
        {
            reservedActionCount = checked(reservedActionCount - batch.Actions.Length);
        }
        return batch;
    }

    private PendingBatch GetPending(Guid reservationId)
    {
        if (reservationId == Guid.Empty
            || !pending.TryGetValue(reservationId, out var batch))
        {
            throw new InvalidOperationException(
                "Memory-cleanup validation evidence reservation is missing.");
        }
        return batch;
    }

    private readonly record struct PreparedAction(
        int ActionIndex,
        int SourceInputIndex,
        int ProcessId,
        long ProcessStartTimeFileTimeUtc,
        uint ReservationStateSlot,
        uint ReservationStateGeneration);

    private sealed record PendingBatch(
        Guid BatchId,
        ulong CycleSequence,
        ulong AttemptGeneration,
        ulong PlannerStateRevision,
        HostManagerMemoryCleanupValidationFactContext FactContext,
        AutomaticMemoryCleanupRequestKind RequestKind,
        double OrdinaryMemoryFreeRatio,
        double PhysicalMemoryFreeRatio,
        double VirtualMemoryFreeRatio,
        AutomaticMemoryCleanupMode SelectedMode,
        int CandidateCount,
        PreparedAction[] Actions,
        DateTimeOffset WriterStartedAt,
        long WriterStartedAtQpcTicks);
}
