using System.Diagnostics;
using System.Text.Json;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Domain.Optimization.MemoryCleanup;
using ResourceManager.App.Infrastructure.Optimization;

namespace Resource_Manager_APP.Tests;

public sealed class HostManagerMemoryCleanupValidationEvidenceLedgerTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 13, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ProductionUnscopedUsesNoEvidenceReservationOrState()
    {
        var ledger = new HostManagerMemoryCleanupValidationEvidenceLedger(
            new FixedTimeProvider(Now));

        Assert.True(ledger.TryReserve(
            HostManagerProcessEffectValidationCycleSnapshot.ProductionUnscoped,
            cycleSequence: 0,
            batchId: Guid.Empty,
            attemptGeneration: 0,
            plannerStateRevision: 0,
            factContext: default,
            request: new AutomaticMemoryCleanupPlanRequest(
                AutomaticMemoryCleanupRequestKind.Normal,
                OrdinaryMemoryFreeRatio: 1,
                PhysicalMemoryFreeRatio: 1,
                VirtualMemoryFreeRatio: 1,
                Candidates: []),
            decisions: [],
            out var reservation));
        Assert.NotNull(reservation);
        Assert.True(reservation.IsProductionUnscoped);
        reservation.Complete(
            [],
            resultShapeExact: false,
            journalSettled: false,
            completedAt: default,
            completedAtQpcTicks: 0);

        var snapshot = ledger.Capture();
        Assert.Null(snapshot.ScopeId);
        Assert.Equal(0, snapshot.ScopeGeneration);
        Assert.Equal(0, snapshot.CaptureSequence);
        Assert.False(snapshot.Sealed);
        Assert.Null(snapshot.Failure);
        Assert.Empty(snapshot.Batches);
    }

    [Fact]
    public void ScopedCompletionPublishesExactBoundedCausalRecord()
    {
        var fixture = CreateScopedLedger();
        var batchId = Guid.NewGuid();

        Assert.True(TryReserve(
            fixture,
            cycleSequence: 11,
            batchId,
            attemptGeneration: 13,
            plannerStateRevision: 17,
            [fixture.Decision],
            out var reservation));
        Assert.NotNull(reservation);
        Assert.False(reservation.IsProductionUnscoped);
        var writerStartedAtQpcTicks = Stopwatch.GetTimestamp();
        reservation.MarkWriterStarted(Now, writerStartedAtQpcTicks);
        var completedAtQpcTicks = Stopwatch.GetTimestamp();
        reservation.Complete(
            [new(
                ResultReturned: true,
                Succeeded: false,
                Win32Error: 5)],
            resultShapeExact: true,
            journalSettled: true,
            completedAt: Now.AddSeconds(1),
            completedAtQpcTicks);

        var snapshot = fixture.Ledger.SealAndCapture();
        Assert.Equal(HostManagerMemoryCleanupValidationEvidenceLedger.SchemaVersion,
            snapshot.SchemaVersion);
        Assert.Equal(HostManagerMemoryCleanupValidationEvidenceLedger.Contract,
            snapshot.Contract);
        Assert.Equal(fixture.Scope.ScopeId, snapshot.ScopeId);
        Assert.Equal(fixture.Scope.Generation, snapshot.ScopeGeneration);
        Assert.Equal(1, snapshot.CaptureSequence);
        Assert.True(snapshot.Sealed);
        Assert.Null(snapshot.Failure);
        var batch = Assert.Single(snapshot.Batches);
        Assert.Equal(1, batch.Sequence);
        Assert.Equal(11UL, batch.CycleSequence);
        Assert.Equal(batchId, batch.BatchId);
        Assert.Equal(13UL, batch.AttemptGeneration);
        Assert.Equal(17UL, batch.PlannerStateRevision);
        Assert.Equal(fixture.FactContext.PlannerConfigurationGeneration,
            batch.PlannerConfigurationGeneration);
        Assert.Equal(fixture.FactContext.GuardedFreeRatio, batch.GuardedFreeRatio);
        Assert.Equal(fixture.FactContext.PhysicalEmergencyFreeRatio,
            batch.PhysicalEmergencyFreeRatio);
        Assert.Equal(fixture.FactContext.VirtualEmergencyFreeRatio,
            batch.VirtualEmergencyFreeRatio);
        Assert.Equal(fixture.FactContext.CapacityWorkspaceIdentity,
            batch.CapacityWorkspaceIdentity);
        Assert.Equal(fixture.FactContext.CapacityConfigurationGeneration,
            batch.CapacityConfigurationGeneration);
        Assert.Equal(fixture.FactContext.CapacityCatalogGeneration,
            batch.CapacityCatalogGeneration);
        Assert.Equal(fixture.FactContext.BaselineCapacityCommittedGeneration,
            batch.BaselineCapacityCommittedGeneration);
        Assert.Equal(fixture.FactContext.FinalCapacityCommittedGeneration,
            batch.FinalCapacityCommittedGeneration);
        Assert.Equal(fixture.FactContext.BaselineCapacityCapturedAtUtcTicks,
            batch.BaselineCapacityCapturedAtUtcTicks);
        Assert.Equal(fixture.FactContext.FinalCapacityCapturedAtUtcTicks,
            batch.FinalCapacityCapturedAtUtcTicks);
        Assert.Equal(fixture.Request.Kind, batch.RequestKind);
        Assert.Equal(fixture.Request.OrdinaryMemoryFreeRatio,
            batch.OrdinaryMemoryFreeRatio);
        Assert.Equal(fixture.FactContext.OrdinaryMemoryFreeRatioCurrent,
            batch.OrdinaryMemoryFreeRatioCurrent);
        Assert.Equal(fixture.Request.PhysicalMemoryFreeRatio,
            batch.PhysicalMemoryFreeRatio);
        Assert.Equal(fixture.FactContext.PhysicalMemoryFreeRatioCurrent,
            batch.PhysicalMemoryFreeRatioCurrent);
        Assert.Equal(fixture.Request.VirtualMemoryFreeRatio,
            batch.VirtualMemoryFreeRatio);
        Assert.Equal(fixture.FactContext.VirtualMemoryFreeRatioCurrent,
            batch.VirtualMemoryFreeRatioCurrent);
        Assert.Equal(fixture.Decision.Mode, batch.SelectedMode);
        Assert.Equal(fixture.Request.Candidates.Count, batch.CandidateCount);
        Assert.Equal(1, batch.RequestedCount);
        Assert.True(batch.ResultShapeExact);
        Assert.True(batch.OutcomeKnown);
        Assert.True(batch.JournalSettled);
        Assert.Equal(Now, batch.WriterStartedAt);
        Assert.Equal(writerStartedAtQpcTicks, batch.WriterStartedAtQpcTicks);
        Assert.Equal(completedAtQpcTicks, batch.CompletedAtQpcTicks);
        var action = Assert.Single(batch.Actions);
        Assert.Equal(0, action.ActionIndex);
        Assert.Equal(fixture.Decision.SourceInputIndex, action.SourceInputIndex);
        Assert.Equal(fixture.Decision.Candidate.ProcessId, action.ProcessId);
        Assert.Equal(
            fixture.Decision.Candidate.ProcessStartedAt.ToFileTime(),
            action.ProcessStartTimeFileTimeUtc);
        Assert.Equal(fixture.Decision.Reservation.StateSlot,
            action.ReservationStateSlot);
        Assert.Equal(fixture.Decision.Reservation.StateGeneration,
            action.ReservationStateGeneration);
        Assert.True(action.ResultReturned);
        Assert.False(action.Succeeded);
        Assert.Equal(5U, action.Win32Error);
    }

    [Fact]
    public void EmergencyCompletionPublishesCurrentEmergencyFactsAndMode()
    {
        var fixture = CreateScopedLedger();
        fixture = fixture with
        {
            Request = fixture.Request with
            {
                Kind = AutomaticMemoryCleanupRequestKind.Normal
                    | AutomaticMemoryCleanupRequestKind.EvaluateEmergency,
                PhysicalMemoryFreeRatio = 0.04
            },
            FactContext = fixture.FactContext with
            {
                PhysicalMemoryFreeRatioCurrent = true
            }
        };
        var decision = fixture.Decision with
        {
            Mode = AutomaticMemoryCleanupMode.Emergency
        };

        Assert.True(TryReserve(
            fixture,
            cycleSequence: 18,
            Guid.NewGuid(),
            attemptGeneration: 19,
            plannerStateRevision: 20,
            [decision],
            out var reservation));
        Assert.NotNull(reservation);
        var writerStartedAtQpcTicks = Stopwatch.GetTimestamp();
        reservation.MarkWriterStarted(Now, writerStartedAtQpcTicks);
        reservation.Complete(
            [new(true, true, 0)],
            resultShapeExact: true,
            journalSettled: true,
            completedAt: Now.AddSeconds(1),
            completedAtQpcTicks: writerStartedAtQpcTicks);

        var batch = Assert.Single(fixture.Ledger.SealAndCapture().Batches);
        Assert.Equal(fixture.Request.Kind, batch.RequestKind);
        Assert.Equal(0.04, batch.PhysicalMemoryFreeRatio);
        Assert.True(batch.PhysicalMemoryFreeRatioCurrent);
        Assert.Equal(AutomaticMemoryCleanupMode.Emergency, batch.SelectedMode);
    }

    [Fact]
    public void ForcedEmergencyDoesNotRequireCurrentEmergencyRatiosAndRejectsNormal()
    {
        var fixture = CreateScopedLedger();
        fixture = fixture with
        {
            Request = fixture.Request with
            {
                Kind = AutomaticMemoryCleanupRequestKind.ForceEmergency
            }
        };
        var emergency = fixture.Decision with
        {
            Mode = AutomaticMemoryCleanupMode.Emergency
        };

        Assert.True(TryReserve(
            fixture,
            cycleSequence: 201,
            Guid.NewGuid(),
            attemptGeneration: 202,
            plannerStateRevision: 203,
            [emergency],
            out var reservation));
        reservation!.Dispose();
        Assert.False(TryReserve(
            fixture,
            cycleSequence: 204,
            Guid.NewGuid(),
            attemptGeneration: 205,
            plannerStateRevision: 206,
            [fixture.Decision],
            out _));
    }

    [Theory]
    [InlineData("zero-configuration-generation")]
    [InlineData("guarded-threshold-not-a-number")]
    [InlineData("physical-threshold-out-of-range")]
    [InlineData("virtual-threshold-positive-infinity")]
    [InlineData("capacity-workspace-zero")]
    [InlineData("capacity-configuration-zero")]
    [InlineData("capacity-catalog-zero")]
    [InlineData("baseline-capacity-generation-zero")]
    [InlineData("final-capacity-generation-not-new")]
    [InlineData("baseline-capacity-ticks-zero")]
    [InlineData("final-capacity-ticks-not-new")]
    [InlineData("none-request-kind")]
    [InlineData("unknown-request-kind")]
    [InlineData("ordinary-not-a-number")]
    [InlineData("physical-negative")]
    [InlineData("virtual-positive-infinity")]
    [InlineData("ordinary-stale-non-sentinel")]
    [InlineData("physical-stale-non-sentinel")]
    [InlineData("virtual-stale-non-sentinel")]
    [InlineData("invalid-mode")]
    [InlineData("normal-mode-without-normal-request")]
    [InlineData("emergency-mode-without-emergency-request")]
    [InlineData("source-index-out-of-range")]
    [InlineData("candidate-does-not-match-source")]
    [InlineData("empty-candidates")]
    public void InvalidV3FactsAreRejectedAtomically(string invalidCase)
    {
        var fixture = CreateScopedLedger();
        var request = fixture.Request;
        var factContext = fixture.FactContext;
        IReadOnlyList<AutomaticMemoryCleanupDecision> decisions = [fixture.Decision];

        switch (invalidCase)
        {
            case "zero-configuration-generation":
                factContext = factContext with
                {
                    PlannerConfigurationGeneration = 0
                };
                break;
            case "guarded-threshold-not-a-number":
                factContext = factContext with { GuardedFreeRatio = double.NaN };
                break;
            case "physical-threshold-out-of-range":
                factContext = factContext with { PhysicalEmergencyFreeRatio = 1.01 };
                break;
            case "virtual-threshold-positive-infinity":
                factContext = factContext with
                {
                    VirtualEmergencyFreeRatio = double.PositiveInfinity
                };
                break;
            case "capacity-workspace-zero":
                factContext = factContext with { CapacityWorkspaceIdentity = 0 };
                break;
            case "capacity-configuration-zero":
                factContext = factContext with { CapacityConfigurationGeneration = 0 };
                break;
            case "capacity-catalog-zero":
                factContext = factContext with { CapacityCatalogGeneration = 0 };
                break;
            case "baseline-capacity-generation-zero":
                factContext = factContext with
                {
                    BaselineCapacityCommittedGeneration = 0
                };
                break;
            case "final-capacity-generation-not-new":
                factContext = factContext with
                {
                    FinalCapacityCommittedGeneration =
                        factContext.BaselineCapacityCommittedGeneration
                };
                break;
            case "baseline-capacity-ticks-zero":
                factContext = factContext with { BaselineCapacityCapturedAtUtcTicks = 0 };
                break;
            case "final-capacity-ticks-not-new":
                factContext = factContext with
                {
                    FinalCapacityCapturedAtUtcTicks =
                        factContext.BaselineCapacityCapturedAtUtcTicks
                };
                break;
            case "none-request-kind":
                request = request with { Kind = AutomaticMemoryCleanupRequestKind.None };
                break;
            case "unknown-request-kind":
                request = request with
                {
                    Kind = unchecked((AutomaticMemoryCleanupRequestKind)byte.MaxValue)
                };
                break;
            case "ordinary-not-a-number":
                request = request with { OrdinaryMemoryFreeRatio = double.NaN };
                break;
            case "physical-negative":
                request = request with { PhysicalMemoryFreeRatio = -double.Epsilon };
                factContext = factContext with { PhysicalMemoryFreeRatioCurrent = true };
                break;
            case "virtual-positive-infinity":
                request = request with { VirtualMemoryFreeRatio = double.PositiveInfinity };
                factContext = factContext with { VirtualMemoryFreeRatioCurrent = true };
                break;
            case "ordinary-stale-non-sentinel":
                factContext = factContext with { OrdinaryMemoryFreeRatioCurrent = false };
                break;
            case "physical-stale-non-sentinel":
                request = request with { PhysicalMemoryFreeRatio = 0.5 };
                break;
            case "virtual-stale-non-sentinel":
                request = request with { VirtualMemoryFreeRatio = 0.5 };
                break;
            case "invalid-mode":
                decisions = [fixture.Decision with
                {
                    Mode = unchecked((AutomaticMemoryCleanupMode)byte.MaxValue)
                }];
                break;
            case "normal-mode-without-normal-request":
                request = request with
                {
                    Kind = AutomaticMemoryCleanupRequestKind.EvaluateEmergency,
                    PhysicalMemoryFreeRatio = 0.05
                };
                factContext = factContext with { PhysicalMemoryFreeRatioCurrent = true };
                break;
            case "emergency-mode-without-emergency-request":
                decisions = [fixture.Decision with
                {
                    Mode = AutomaticMemoryCleanupMode.Emergency
                }];
                break;
            case "source-index-out-of-range":
                decisions = [fixture.Decision with { SourceInputIndex = 1 }];
                break;
            case "candidate-does-not-match-source":
                decisions = [fixture.Decision with
                {
                    Candidate = fixture.Decision.Candidate with
                    {
                        DisplayName = "not-the-request-candidate"
                    }
                }];
                break;
            case "empty-candidates":
                request = request with { Candidates = [] };
                break;
            default:
                throw new InvalidOperationException($"Unknown invalid case '{invalidCase}'.");
        }

        var before = fixture.Ledger.Capture();
        Assert.False(fixture.Ledger.TryReserve(
            fixture.Scope,
            cycleSequence: 21,
            Guid.NewGuid(),
            attemptGeneration: 22,
            plannerStateRevision: 23,
            factContext,
            request,
            decisions,
            out var rejected));
        Assert.Null(rejected);
        var after = fixture.Ledger.Capture();
        Assert.Equal(before.CaptureSequence, after.CaptureSequence);
        Assert.Equal(before.Sealed, after.Sealed);
        Assert.Equal(before.Failure, after.Failure);
        Assert.Empty(after.Batches);

        Assert.True(TryReserve(
            fixture,
            cycleSequence: 24,
            Guid.NewGuid(),
            attemptGeneration: 25,
            plannerStateRevision: 26,
            [fixture.Decision],
            out var valid));
        valid!.Dispose();
        Assert.Empty(fixture.Ledger.Capture().Batches);
    }

    [Fact]
    public void MixedModesAreRejectedAtomically()
    {
        var fixture = CreateScopedLedger(candidateCount: 2);
        fixture = fixture with
        {
            Request = fixture.Request with
            {
                Kind = AutomaticMemoryCleanupRequestKind.Normal
                    | AutomaticMemoryCleanupRequestKind.EvaluateEmergency,
                PhysicalMemoryFreeRatio = 0.5,
                VirtualMemoryFreeRatio = 0.5
            },
            FactContext = fixture.FactContext with
            {
                PhysicalMemoryFreeRatioCurrent = true,
                VirtualMemoryFreeRatioCurrent = true
            }
        };
        var decisions = fixture.Decisions.ToArray();
        decisions[1] = decisions[1] with
        {
            Mode = AutomaticMemoryCleanupMode.Emergency
        };

        Assert.False(TryReserve(
            fixture,
            cycleSequence: 27,
            Guid.NewGuid(),
            attemptGeneration: 28,
            plannerStateRevision: 29,
            decisions,
            out var reservation));
        Assert.Null(reservation);
        Assert.Empty(fixture.Ledger.Capture().Batches);
    }

    [Fact]
    public void V3SnapshotSerializationUsesExactWebContractAndRoundTrips()
    {
        var fixture = CreateScopedLedger();
        Assert.True(TryReserve(
            fixture,
            cycleSequence: 30,
            Guid.NewGuid(),
            attemptGeneration: 31,
            plannerStateRevision: 32,
            [fixture.Decision],
            out var reservation));
        var qpc = Stopwatch.GetTimestamp();
        reservation!.MarkWriterStarted(Now, qpc);
        reservation.Complete(
            [new(true, false, 5)],
            resultShapeExact: true,
            journalSettled: true,
            completedAt: Now.AddSeconds(1),
            completedAtQpcTicks: qpc);
        var snapshot = fixture.Ledger.SealAndCapture();

        var json = JsonSerializer.Serialize(snapshot, JsonSerializerOptions.Web);
        using var document = JsonDocument.Parse(json);
        AssertExactProperties(
            document.RootElement,
            "schemaVersion",
            "contract",
            "scopeId",
            "scopeGeneration",
            "captureSequence",
            "capturedAt",
            "capturedAtQpcTicks",
            "qpcFrequency",
            "sealed",
            "failure",
            "batches");
        var batch = Assert.Single(document.RootElement.GetProperty("batches")
            .EnumerateArray());
        AssertExactProperties(
            batch,
            "sequence",
            "cycleSequence",
            "batchId",
            "attemptGeneration",
            "plannerStateRevision",
            "plannerConfigurationGeneration",
            "guardedFreeRatio",
            "physicalEmergencyFreeRatio",
            "virtualEmergencyFreeRatio",
            "capacityWorkspaceIdentity",
            "capacityConfigurationGeneration",
            "capacityCatalogGeneration",
            "baselineCapacityCommittedGeneration",
            "finalCapacityCommittedGeneration",
            "baselineCapacityCapturedAtUtcTicks",
            "finalCapacityCapturedAtUtcTicks",
            "requestKind",
            "ordinaryMemoryFreeRatio",
            "ordinaryMemoryFreeRatioCurrent",
            "physicalMemoryFreeRatio",
            "physicalMemoryFreeRatioCurrent",
            "virtualMemoryFreeRatio",
            "virtualMemoryFreeRatioCurrent",
            "selectedMode",
            "candidateCount",
            "requestedCount",
            "resultShapeExact",
            "outcomeKnown",
            "journalSettled",
            "writerStartedAt",
            "writerStartedAtQpcTicks",
            "completedAt",
            "completedAtQpcTicks",
            "actions");
        Assert.Equal((byte)fixture.Request.Kind, batch.GetProperty("requestKind").GetByte());
        Assert.Equal((byte)fixture.Decision.Mode, batch.GetProperty("selectedMode").GetByte());
        var action = Assert.Single(batch.GetProperty("actions").EnumerateArray());
        AssertExactProperties(
            action,
            "actionIndex",
            "sourceInputIndex",
            "processId",
            "processStartTimeFileTimeUtc",
            "reservationStateSlot",
            "reservationStateGeneration",
            "resultReturned",
            "succeeded",
            "win32Error");
        Assert.DoesNotContain("evidence-v2", json, StringComparison.Ordinal);

        var roundTrip = JsonSerializer.Deserialize<
            HostManagerMemoryCleanupValidationEvidenceSnapshot>(
                json,
                JsonSerializerOptions.Web);
        Assert.NotNull(roundTrip);
        Assert.Equal(
            json,
            JsonSerializer.Serialize(roundTrip, JsonSerializerOptions.Web));
    }

    [Fact]
    public void BatchAndActionCapacityBoundariesAreExact()
    {
        var batchFixture = CreateScopedLedger();
        for (var index = 0;
             index < HostManagerMemoryCleanupValidationEvidenceLedger.MaximumBatchCount;
             index++)
        {
            CompleteKnownReservation(
                batchFixture,
                checked((ulong)(index + 1)),
                [batchFixture.Decision with
                {
                    Reservation = new(
                        checked((uint)(index + 1)),
                        StateGeneration: 1)
                }]);
        }
        Assert.False(TryReserve(
            batchFixture,
            cycleSequence: 1_000,
            Guid.NewGuid(),
            attemptGeneration: 1_001,
            plannerStateRevision: 1_002,
            [batchFixture.Decision with
            {
                Reservation = new(StateSlot: 1_001, StateGeneration: 1)
            }],
            out _));
        Assert.Equal(
            HostManagerMemoryCleanupValidationEvidenceLedger.MaximumBatchCount,
            batchFixture.Ledger.Capture().Batches.Count);

        var actionFixture = CreateScopedLedger(
            HostManagerProcessEffectValidationScopeAuthority.MaximumAllowedProcessCount);
        var batchSize = actionFixture.Decisions.Count;
        var batchCount = HostManagerMemoryCleanupValidationEvidenceLedger.MaximumActionCount
            / batchSize;
        Assert.Equal(
            HostManagerMemoryCleanupValidationEvidenceLedger.MaximumActionCount,
            batchCount * batchSize);
        for (var batchIndex = 0; batchIndex < batchCount; batchIndex++)
        {
            var firstStateSlot = checked((uint)(batchIndex * batchSize + 1));
            var decisions = actionFixture.Decisions
                .Select((decision, decisionIndex) => decision with
                {
                    Reservation = new(
                        checked(firstStateSlot + (uint)decisionIndex),
                        StateGeneration: 1)
                })
                .ToArray();
            CompleteKnownReservation(
                actionFixture,
                checked((ulong)(batchIndex + 1)),
                decisions);
        }
        Assert.False(TryReserve(
            actionFixture,
            cycleSequence: 2_000,
            Guid.NewGuid(),
            attemptGeneration: 2_001,
            plannerStateRevision: 2_002,
            [actionFixture.Decision with
            {
                Reservation = new(StateSlot: 5_000, StateGeneration: 1)
            }],
            out _));
        Assert.Equal(
            HostManagerMemoryCleanupValidationEvidenceLedger.MaximumActionCount,
            actionFixture.Ledger.Capture().Batches.Sum(static batch => batch.RequestedCount));
    }

    [Fact]
    public void UnknownCompletionIsExplicitAndNeverPassesAsKnown()
    {
        var fixture = CreateScopedLedger();

        Assert.True(TryReserve(
            fixture,
            cycleSequence: 19,
            Guid.NewGuid(),
            attemptGeneration: 23,
            plannerStateRevision: 29,
            [fixture.Decision],
            out var reservation));
        reservation!.MarkWriterStarted(Now, Stopwatch.GetTimestamp());
        reservation!.CompleteUnknown(Now.AddSeconds(1), Stopwatch.GetTimestamp());

        var batch = Assert.Single(fixture.Ledger.Capture().Batches);
        Assert.Equal(fixture.Request.Kind, batch.RequestKind);
        Assert.Equal(fixture.Decision.Mode, batch.SelectedMode);
        Assert.Equal(fixture.FactContext.PlannerConfigurationGeneration,
            batch.PlannerConfigurationGeneration);
        Assert.False(batch.ResultShapeExact);
        Assert.False(batch.OutcomeKnown);
        Assert.False(batch.JournalSettled);
        var action = Assert.Single(batch.Actions);
        Assert.False(action.ResultReturned);
        Assert.False(action.Succeeded);
        Assert.Null(action.Win32Error);
    }

    [Fact]
    public void InvalidCompletionRemainsCancelableWithoutLeakingCapacity()
    {
        var fixture = CreateScopedLedger();

        Assert.True(TryReserve(
            fixture,
            cycleSequence: 31,
            Guid.NewGuid(),
            attemptGeneration: 37,
            plannerStateRevision: 41,
            [fixture.Decision],
            out var reservation));
        Assert.Throws<InvalidDataException>(() => reservation!.Complete(
            [],
            resultShapeExact: true,
            journalSettled: true,
            completedAt: Now,
            completedAtQpcTicks: Stopwatch.GetTimestamp()));
        Assert.NotNull(fixture.Ledger.Capture().Failure);

        reservation!.Dispose();

        var snapshot = fixture.Ledger.Capture();
        Assert.Null(snapshot.Failure);
        Assert.Empty(snapshot.Batches);
        fixture.Ledger.Reset(Guid.NewGuid(), 2);
    }

    [Theory]
    [InlineData(false, true, null)]
    [InlineData(false, false, 5U)]
    [InlineData(true, true, null)]
    [InlineData(true, true, 5U)]
    [InlineData(true, false, null)]
    [InlineData(true, false, 0U)]
    public void ContradictoryCompletionTupleBecomesPermanentUnknownOnDispose(
        bool resultReturned,
        bool succeeded,
        uint? win32Error)
    {
        var fixture = CreateScopedLedger();
        Assert.True(TryReserve(
            fixture,
            cycleSequence: 211,
            Guid.NewGuid(),
            attemptGeneration: 212,
            plannerStateRevision: 213,
            [fixture.Decision],
            out var reservation));
        var qpc = Stopwatch.GetTimestamp();
        reservation!.MarkWriterStarted(Now, qpc);

        Assert.Throws<InvalidDataException>(() => reservation.Complete(
            [new(resultReturned, succeeded, win32Error)],
            resultShapeExact: true,
            journalSettled: true,
            completedAt: Now.AddSeconds(1),
            completedAtQpcTicks: qpc));
        reservation.Dispose();

        var batch = Assert.Single(fixture.Ledger.Capture().Batches);
        Assert.False(batch.ResultShapeExact);
        Assert.False(batch.OutcomeKnown);
        Assert.False(batch.JournalSettled);
        var action = Assert.Single(batch.Actions);
        Assert.False(action.ResultReturned);
        Assert.False(action.Succeeded);
        Assert.Null(action.Win32Error);
    }

    [Fact]
    public void PostWriterCompletionFailureDisposesToPermanentUnknownEvidence()
    {
        var fixture = CreateScopedLedger();

        Assert.True(TryReserve(
            fixture,
            cycleSequence: 43,
            Guid.NewGuid(),
            attemptGeneration: 47,
            plannerStateRevision: 53,
            [fixture.Decision],
            out var reservation));
        reservation!.MarkWriterStarted(Now, Stopwatch.GetTimestamp());
        Assert.Throws<InvalidDataException>(() => reservation.Complete(
            [],
            resultShapeExact: true,
            journalSettled: true,
            completedAt: Now.AddSeconds(1),
            completedAtQpcTicks: Stopwatch.GetTimestamp()));

        reservation.Dispose();

        var batch = Assert.Single(fixture.Ledger.Capture().Batches);
        Assert.False(batch.ResultShapeExact);
        Assert.False(batch.OutcomeKnown);
        Assert.False(batch.JournalSettled);
        Assert.True(batch.WriterStartedAtQpcTicks > 0);
        Assert.True(batch.CompletedAtQpcTicks >= batch.WriterStartedAtQpcTicks);
        Assert.False(Assert.Single(batch.Actions).ResultReturned);
    }

    [Fact]
    public void SuccessfulSealIsIdempotentAndRejectsLateReservations()
    {
        var fixture = CreateScopedLedger();
        Assert.True(TryReserve(
            fixture,
            cycleSequence: 59,
            Guid.NewGuid(),
            attemptGeneration: 61,
            plannerStateRevision: 67,
            [fixture.Decision],
            out var reservation));
        reservation!.MarkWriterStarted(Now, Stopwatch.GetTimestamp());
        reservation.Complete(
            [new(true, true, 0)],
            resultShapeExact: true,
            journalSettled: true,
            completedAt: Now.AddSeconds(1),
            completedAtQpcTicks: Stopwatch.GetTimestamp());

        var first = fixture.Ledger.SealAndCapture();
        var second = fixture.Ledger.SealAndCapture();

        Assert.True(first.Sealed);
        Assert.Same(first, second);
        Assert.False(TryReserve(
            fixture,
            cycleSequence: 71,
            Guid.NewGuid(),
            attemptGeneration: 73,
            plannerStateRevision: 79,
            [fixture.Decision],
            out var lateReservation));
        Assert.Null(lateReservation);
    }

    [Fact]
    public void PendingReservationPreventsSealUntilPreWriterAbandonment()
    {
        var fixture = CreateScopedLedger();
        Assert.True(TryReserve(
            fixture,
            cycleSequence: 83,
            Guid.NewGuid(),
            attemptGeneration: 89,
            plannerStateRevision: 97,
            [fixture.Decision],
            out var reservation));

        var unsettled = fixture.Ledger.SealAndCapture();

        Assert.False(unsettled.Sealed);
        Assert.NotNull(unsettled.Failure);
        reservation!.Dispose();

        var sealedSnapshot = fixture.Ledger.SealAndCapture();
        Assert.True(sealedSnapshot.Sealed);
        Assert.Null(sealedSnapshot.Failure);
        Assert.Empty(sealedSnapshot.Batches);
    }

    [Fact]
    public void DuplicateCycleAttemptAndPlannerReservationIdentitiesAreRejected()
    {
        var fixture = CreateScopedLedger();
        Assert.True(TryReserve(
            fixture,
            cycleSequence: 101,
            Guid.NewGuid(),
            attemptGeneration: 103,
            plannerStateRevision: 107,
            [fixture.Decision],
            out var reservation));

        var distinctReservation = fixture.Decision with
        {
            Reservation = new AutomaticMemoryCleanupReservation(
                StateSlot: 11,
                StateGeneration: 13)
        };
        Assert.False(TryReserve(
            fixture,
            cycleSequence: 101,
            Guid.NewGuid(),
            attemptGeneration: 109,
            plannerStateRevision: 113,
            [distinctReservation],
            out _));
        Assert.False(TryReserve(
            fixture,
            cycleSequence: 127,
            Guid.NewGuid(),
            attemptGeneration: 103,
            plannerStateRevision: 131,
            [distinctReservation],
            out _));
        Assert.False(TryReserve(
            fixture,
            cycleSequence: 137,
            Guid.NewGuid(),
            attemptGeneration: 139,
            plannerStateRevision: 149,
            [fixture.Decision],
            out _));
        Assert.False(TryReserve(
            fixture,
            cycleSequence: 151,
            Guid.NewGuid(),
            attemptGeneration: 157,
            plannerStateRevision: 163,
            [fixture.Decision, fixture.Decision],
            out _));

        reservation!.Dispose();
        Assert.True(TryReserve(
            fixture,
            cycleSequence: 101,
            Guid.NewGuid(),
            attemptGeneration: 103,
            plannerStateRevision: 107,
            [fixture.Decision],
            out var retry));
        retry!.Dispose();
    }

    [Fact]
    public void CompletedPlannerReservationIdentityCannotBeReused()
    {
        var fixture = CreateScopedLedger();
        Assert.True(TryReserve(
            fixture,
            cycleSequence: 167,
            Guid.NewGuid(),
            attemptGeneration: 173,
            plannerStateRevision: 179,
            [fixture.Decision],
            out var reservation));
        reservation!.MarkWriterStarted(Now, Stopwatch.GetTimestamp());
        reservation.Complete(
            [new(true, true, 0)],
            resultShapeExact: true,
            journalSettled: true,
            completedAt: Now.AddSeconds(1),
            completedAtQpcTicks: Stopwatch.GetTimestamp());

        Assert.False(TryReserve(
            fixture,
            cycleSequence: 181,
            Guid.NewGuid(),
            attemptGeneration: 191,
            plannerStateRevision: 193,
            [fixture.Decision],
            out _));
    }

    private static bool TryReserve(
        ScopedFixture fixture,
        ulong cycleSequence,
        Guid batchId,
        ulong attemptGeneration,
        ulong plannerStateRevision,
        IReadOnlyList<AutomaticMemoryCleanupDecision> decisions,
        out HostManagerMemoryCleanupValidationEvidenceReservation? reservation)
        => fixture.Ledger.TryReserve(
            fixture.Scope,
            cycleSequence,
            batchId,
            attemptGeneration,
            plannerStateRevision,
            fixture.FactContext,
            fixture.Request,
            decisions,
            out reservation);

    private static void CompleteKnownReservation(
        ScopedFixture fixture,
        ulong identity,
        IReadOnlyList<AutomaticMemoryCleanupDecision> decisions)
    {
        Assert.True(TryReserve(
            fixture,
            cycleSequence: identity,
            Guid.NewGuid(),
            attemptGeneration: identity,
            plannerStateRevision: identity,
            decisions,
            out var reservation));
        var qpc = Stopwatch.GetTimestamp();
        reservation!.MarkWriterStarted(Now, qpc);
        reservation.Complete(
            decisions.Select(static _ =>
                    new HostManagerMemoryCleanupValidationActionOutcome(
                        ResultReturned: true,
                        Succeeded: true,
                        Win32Error: 0))
                .ToArray(),
            resultShapeExact: true,
            journalSettled: true,
            completedAt: Now.AddSeconds(1),
            completedAtQpcTicks: qpc);
    }

    private static void AssertExactProperties(
        JsonElement element,
        params string[] expected)
        => Assert.Equal(
            expected.Order(StringComparer.Ordinal),
            element.EnumerateObject()
                .Select(static property => property.Name)
                .Order(StringComparer.Ordinal));

    private static ScopedFixture CreateScopedLedger(int candidateCount = 1)
    {
        var time = new FixedTimeProvider(Now);
        if (candidateCount is < 1 or >
            HostManagerProcessEffectValidationScopeAuthority.MaximumAllowedProcessCount)
        {
            throw new ArgumentOutOfRangeException(nameof(candidateCount));
        }
        var decisions = Enumerable.Range(0, candidateCount)
            .Select(index => new AutomaticMemoryCleanupDecision(
                SourceInputIndex: index,
                new AutomaticMemoryCleanupCandidate(
                    TargetId: $"target-{index}",
                    DisplayName: $"Target {index}",
                    ProcessId: 1234 + index,
                    ProcessStartedAt: Now.AddMinutes(-5).AddSeconds(index),
                    RuntimeState: "background",
                    BaseScore: 0.5 + index,
                    CpuScore: 0.25,
                    MemoryUsedPercent: 12.5,
                    CanApply: true),
                AutomaticMemoryCleanupMode.Normal,
                new AutomaticMemoryCleanupReservation(
                    StateSlot: checked((uint)(index + 1)),
                    StateGeneration: 9)))
            .ToArray();
        var scope = new HostManagerProcessEffectValidationCycleSnapshot(
            HostManagerProcessEffectValidationScopeState.Active,
            Guid.NewGuid(),
            generation: 1,
            jobName: "validation-job",
            expiresAt: Now.AddHours(1),
            allowedProcesses: decisions
                .Select(static decision => new HostManagerComputeProcessIdentity(
                    decision.Candidate.ProcessId,
                    checked((ulong)decision.Candidate.ProcessStartedAt.ToFileTime())))
                .ToHashSet(),
            time);
        var ledger = new HostManagerMemoryCleanupValidationEvidenceLedger(time);
        ledger.Reset(scope.ScopeId, scope.Generation);
        var request = new AutomaticMemoryCleanupPlanRequest(
            AutomaticMemoryCleanupRequestKind.Normal,
            OrdinaryMemoryFreeRatio: 0.25,
            PhysicalMemoryFreeRatio: 1,
            VirtualMemoryFreeRatio: 1,
            Candidates: decisions.Select(static decision => decision.Candidate).ToArray());
        var factContext = new HostManagerMemoryCleanupValidationFactContext(
            PlannerConfigurationGeneration: 43,
            GuardedFreeRatio: 0.30,
            PhysicalEmergencyFreeRatio: 0.06,
            VirtualEmergencyFreeRatio: 0.08,
            CapacityWorkspaceIdentity: 47,
            CapacityConfigurationGeneration: 53,
            CapacityCatalogGeneration: 59,
            BaselineCapacityCommittedGeneration: 61,
            FinalCapacityCommittedGeneration: 67,
            BaselineCapacityCapturedAtUtcTicks: Now.AddSeconds(-1).UtcTicks,
            FinalCapacityCapturedAtUtcTicks: Now.UtcTicks,
            OrdinaryMemoryFreeRatioCurrent: true,
            PhysicalMemoryFreeRatioCurrent: false,
            VirtualMemoryFreeRatioCurrent: false);
        return new(ledger, scope, decisions, request, factContext);
    }

    private sealed record ScopedFixture(
        HostManagerMemoryCleanupValidationEvidenceLedger Ledger,
        HostManagerProcessEffectValidationCycleSnapshot Scope,
        IReadOnlyList<AutomaticMemoryCleanupDecision> Decisions,
        AutomaticMemoryCleanupPlanRequest Request,
        HostManagerMemoryCleanupValidationFactContext FactContext)
    {
        internal AutomaticMemoryCleanupDecision Decision => Decisions[0];
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
