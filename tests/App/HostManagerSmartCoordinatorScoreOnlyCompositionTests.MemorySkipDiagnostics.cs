using System.Collections;
using System.Reflection;
using Microsoft.Extensions.Logging;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization;
using ResourceManager.App.Infrastructure.Optimization.Transactions;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerSmartCoordinatorScoreOnlyCompositionTests
{
    private const int MemorySkipPid = 43_200;
    private const string MemorySkipSoftware = "software:memory-skip-diagnostics";
    private static readonly DateTimeOffset MemorySkipStartedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
    private static ulong MemorySkipStart => checked((ulong)MemorySkipStartedAt.ToFileTime());

    [Theory]
    [InlineData("capture_unavailable")]
    [InlineData("capture_identity_mismatch")]
    [InlineData("baseline_at_target")]
    public async Task MemorySkipDiagnosticsExposeTheActualCaptureFailure(string expectedReason)
    {
        await using var fixture = await CreateMemorySkipFixtureAsync(scoreOnly: false, run: false);
        fixture.ProcessPolicyWriter.MemoryReadHandler = pid => expectedReason == "capture_unavailable"
            ? RecoveryReadResult<ProcessMemoryPriorityRecoverySnapshot>.Unavailable(5, "denied")
            : RecoveryReadResult<ProcessMemoryPriorityRecoverySnapshot>.Found(new(pid,
                expectedReason == "capture_identity_mismatch" ? MemorySkipStartedAt.AddSeconds(1) : MemorySkipStartedAt,
                expectedReason == "baseline_at_target" ? 3U : 5U));

        await fixture.RunRealtimeCycleAsync();

        await AssertMemorySkipAsync(fixture, expectedReason);
        Assert.Equal(0, fixture.ProcessPolicyWriter.BatchCalls);
        Assert.Empty((await fixture.ReadJournalSnapshotAsync()).Records);
        Assert.Empty((await fixture.NativeTransactions.AppliedOwnership.ReadSnapshotAsync(CancellationToken.None)).Records);
    }

    [Fact]
    public async Task MemorySkipDiagnosticsCountEveryDirectiveAndBoundExamplesToFiveExactTargets()
    {
        await using var fixture = await CreateMemorySkipFixtureAsync(count: 8);

        var diagnostics = await fixture.Coordinator.GetDecisionDiagnosticsAsync(CancellationToken.None);

        Assert.Equal(8, diagnostics.NonAdaptedMemoryDecisions.Count);
        Assert.All(diagnostics.NonAdaptedMemoryDecisions, row =>
        {
            Assert.False(row.TransactionPrepared);
            Assert.Equal("memory_effect_not_admitted", row.SkipReason);
        });
        var summary = Assert.Single(diagnostics.NonAdaptedMemorySkipCounts);
        Assert.Equal("memory_effect_not_admitted", summary.Reason);
        Assert.Equal(8, summary.Count);
        var expectedTargets = fixture.Coordinator.NonAdaptedMemoryModeProjection!.Processes.Take(5)
            .Select(row => HostManagerTargetIdentity.CreateProcessMemoryPolicyTargetId(row.ProcessId, row.ProcessStartKey));
        Assert.Equal(expectedTargets, summary.ExampleTargetIds);
        Assert.Equal(5, summary.ExampleTargetIds.Distinct().Count());
    }

    [Fact]
    public async Task MemorySkipDiagnosticsReportAnUnreachedStageWithoutInventingAPreparedTransaction()
    {
        await using var fixture = await CreateMemorySkipFixtureAsync();
        ResetMemorySkipOutcomes(fixture.Coordinator);
        PublishMemorySkipTestSegment(fixture.Coordinator);

        await AssertMemorySkipAsync(fixture, "memory_stage_not_reached");
    }

    [Theory]
    [InlineData(0U, 1U, true, "transaction_limit")]
    [InlineData(1U, 0U, true, "new_ponr_budget_exhausted")]
    [InlineData(1U, 1U, false, "fresh_apply_disabled")]
    public async Task MemorySkipDiagnosticsExplainBatchLimitsAndDisabledFreshApply(
        uint maximumTransactions, uint freshBudget, bool allowFresh, string expectedReason)
    {
        await using var fixture = await CreateMemorySkipFixtureAsync();

        await RunMemorySkipBatchAsync(fixture, maximumTransactions, freshBudget, allowFresh: allowFresh);

        await AssertMemorySkipAsync(fixture, expectedReason);
        Assert.Equal(0, fixture.ProcessPolicyWriter.BatchCalls);
    }

    [Fact]
    public async Task MemorySkipDiagnosticsExplainUnavailableSchedulingAuthority()
    {
        await using var fixture = await CreateMemorySkipFixtureAsync();
        var authority = fixture.Coordinator.SchedulingAuthority;
        ReadPrivateField<HostManagerSchedulingAuthority>(fixture.Coordinator, "schedulingAuthorityOwner")!
            .PublishUnavailable(authority.RoundNumber, authority.PlanBinding!, authority.ObservedAt, "fixture unavailable");

        await RunMemorySkipBatchAsync(fixture);

        await AssertMemorySkipAsync(fixture, "authority_not_ready");
    }

    [Fact]
    public async Task MemorySkipDiagnosticsExplainANormalDirectiveWithoutOwnedConstraints()
    {
        await using var fixture = await CreateMemorySkipFixtureAsync(baseScore: 100);
        Assert.Equal(HostManagerNonAdaptedMemoryProcessAction.RestoreOwnedConstraints,
            Assert.Single(fixture.Coordinator.NonAdaptedMemoryModeProjection!.Processes).Action);

        await RunMemorySkipBatchAsync(fixture);

        await AssertMemorySkipAsync(fixture, "no_owned_constraints");
    }

    [Fact]
    public async Task MemorySkipDiagnosticsExplainClosedPagedFrozenExecution()
    {
        await using var fixture = await CreateMemorySkipFixtureAsync(baseScore: 20);
        Assert.Equal(HostManagerNonAdaptedMemoryProcessAction.PagedFrozen,
            Assert.Single(fixture.Coordinator.NonAdaptedMemoryModeProjection!.Processes).Action);

        await RunMemorySkipBatchAsync(fixture);

        await AssertMemorySkipAsync(fixture, "paged_frozen_closed");
        Assert.Equal(0, fixture.ProcessPolicyWriter.BatchCalls);
    }

    [Theory]
    [InlineData(false, "already_owned")]
    [InlineData(true, "owned_memory_unavailable")]
    public async Task MemorySkipDiagnosticsDistinguishOwnedMemoryFromAnUnreadableOwner(bool unavailable, string expectedReason)
    {
        await using var fixture = await CreateMemorySkipFixtureAsync();
        await fixture.SeedProcessOwnershipAsync(MemorySkipPid, MemorySkipStart, MemorySkipSoftware, true, 43_200);
        fixture.ProcessPolicyWriter.MemoryReadHandler = pid => unavailable
            ? RecoveryReadResult<ProcessMemoryPriorityRecoverySnapshot>.Unavailable(5, "denied")
            : RecoveryReadResult<ProcessMemoryPriorityRecoverySnapshot>.Found(new(pid, MemorySkipStartedAt, 3));

        await RunMemorySkipBatchAsync(fixture);

        await AssertMemorySkipAsync(fixture, expectedReason);
        Assert.Single((await fixture.NativeTransactions.AppliedOwnership.ReadSnapshotAsync(CancellationToken.None)).Records);
        Assert.Equal(0, fixture.ProcessPolicyWriter.BatchCalls);
    }

    [Fact]
    public async Task MemorySkipDiagnosticsExplainChangedAttributionWithoutAcquiringNewMemoryOwnership()
    {
        await using var fixture = await CreateMemorySkipFixtureAsync();
        await fixture.SeedProcessOwnershipAsync(MemorySkipPid, MemorySkipStart, "software:previous-memory-owner", false, 43_201);

        await RunMemorySkipBatchAsync(fixture);

        await AssertMemorySkipAsync(fixture, "attribution_changed");
        Assert.Equal(0, fixture.ProcessPolicyWriter.BatchCalls);
    }

    [Fact]
    public async Task MemorySkipDiagnosticsExplainOwnedRestoreBudgetExhaustion()
    {
        await using var fixture = await CreateMemorySkipFixtureAsync();
        await fixture.SeedProcessOwnershipAsync(MemorySkipPid, MemorySkipStart, MemorySkipSoftware, true, 43_202);

        await RunMemorySkipBatchAsync(fixture, recoveryBudget: 0, allowFresh: false);

        await AssertMemorySkipAsync(fixture, "restore_budget_exhausted");
        Assert.Equal(0, fixture.ProcessPolicyWriter.BatchCalls);
    }

    [Fact]
    public async Task MemorySkipDiagnosticsExplainAProcessOutsideTheCurrentValidationScope()
    {
        await using var fixture = await CreateMemorySkipFixtureAsync();
        var scope = new HostManagerProcessEffectValidationCycleSnapshot(
            HostManagerProcessEffectValidationScopeState.Active, Guid.NewGuid(), 1, "fixture-scope",
            DateTimeOffset.MaxValue, new HashSet<HostManagerComputeProcessIdentity>(), TimeProvider.System,
            nonAdaptedMemoryTransactionAllowed: true);

        await RunMemorySkipBatchAsync(fixture, scope: scope);

        await AssertMemorySkipAsync(fixture, "process_scope_excluded");
    }

    [Fact]
    public async Task MemorySkipDiagnosticsExplainFreshAdmissionRefusedAfterScopeChanged()
    {
        using var validation = CoordinatorValidationScopeFixture.Create(new(MemorySkipPid, MemorySkipStart));
        await using var fixture = await CreateMemorySkipFixtureAsync(scopeAuthority: validation.Authority);

        // A formerly unscoped cycle cannot enter the newly opened scoped authority.
        await RunMemorySkipBatchAsync(fixture, scope: HostManagerProcessEffectValidationCycleSnapshot.ProductionUnscoped);

        await AssertMemorySkipAsync(fixture, "validation_admission_refused");
        Assert.Equal(0, fixture.ProcessPolicyWriter.BatchCalls);
        Assert.Empty((await fixture.ReadJournalSnapshotAsync()).Records);
    }

    [Fact]
    public async Task MemorySkipDiagnosticsExplainRecoveryWithoutAnAuthorizedScopedHandoff()
    {
        using var validation = CoordinatorValidationScopeFixture.Create(new(MemorySkipPid, MemorySkipStart));
        await using var fixture = await CreateMemorySkipFixtureAsync(scopeAuthority: validation.Authority);
        await fixture.SeedProcessOwnershipAsync(MemorySkipPid, MemorySkipStart, MemorySkipSoftware, true, 43_203);

        await RunMemorySkipBatchAsync(fixture, allowFresh: false, scope: validation.Authority.Capture());

        await AssertMemorySkipAsync(fixture, "recovery_admission_refused");
        Assert.Equal(0, fixture.ProcessPolicyWriter.BatchCalls);
        Assert.Single((await fixture.NativeTransactions.AppliedOwnership.ReadSnapshotAsync(CancellationToken.None)).Records);
    }

    [Fact]
    public async Task MemorySkipDiagnosticsExplainPendingWorkForTheExactMemoryTarget()
    {
        await using var fixture = await CreateMemorySkipFixtureAsync();
        var directive = Assert.Single(fixture.Coordinator.NonAdaptedMemoryModeProjection!.Processes);
        Assert.True(fixture.NativeTransactions.TryAcquireExecutionAdmission(out var admission));
        await using (admission!)
        {
            await ReconcileMemorySkipPayloadsAsync(fixture.Coordinator, admission!);
            var journal = await admission!.ReadSnapshotAsync(CancellationToken.None);
            var action = HostManagerProcessMemoryTransactionProjection.CreateApply(directive,
                fixture.NativeTransactions.CurrentJournalPlan.HotPublish.ConfigurationGeneration, 43_204, 1);
            var capture = new HostManagerProcessPolicyTransaction(fixture.ProcessPolicyWriter)
                .CaptureMemoryPriority(directive.ProcessId, directive.TargetMemoryPriority!.Value);
            Assert.Equal(HostManagerProcessPolicyCaptureStatus.Captured, capture.Status);
            var now = checked((ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var binding = HostManagerProcessMemoryTransactionProjection.CreatePayloadBinding(action, capture.Payload,
                journal.Header.JournalInstanceLow, journal.Header.JournalInstanceHigh, now, 3, now + 60_000);
            var reference = await admission.PersistPayloadAsync(binding, capture.Payload, CancellationToken.None);
            var provenance = NativeTransactionJournalPayloadProvenance.Create(binding);
            var prepare = HostManagerProcessMemoryTransactionProjection.CreatePrepare(action, capture.Payload,
                binding, binding, reference, provenance, journal.Header.JournalRevision, now);
            Assert.Equal(NativeTransactionJournalStatus.Ok, await admission.PrepareAsync(prepare, CancellationToken.None));
        }

        await RunMemorySkipBatchAsync(fixture);

        await AssertMemorySkipAsync(fixture, "pending_journal");
        Assert.Equal(0, fixture.ProcessPolicyWriter.BatchCalls);
    }

    [Fact]
    public async Task MemorySkipDiagnosticsPreservePreparedOutcomeWhenItsUncertainWriteBlocksRemainingTargets()
    {
        await using var fixture = await CreateMemorySkipFixtureAsync(count: 3);
        fixture.ProcessPolicyWriter.BatchHandler = _ => throw new InvalidOperationException("write result unknown");

        await RunMemorySkipBatchAsync(fixture, maximumTransactions: 3, freshBudget: 3);

        var diagnostics = await fixture.Coordinator.GetDecisionDiagnosticsAsync(CancellationToken.None);
        var prepared = Assert.Single(diagnostics.NonAdaptedMemoryDecisions, row => row.TransactionPrepared);
        Assert.Null(prepared.SkipReason);
        Assert.Equal(2, diagnostics.NonAdaptedMemoryDecisions.Count(row => row.SkipReason == "memory_transaction_blocked"));
        var summary = Assert.Single(diagnostics.NonAdaptedMemorySkipCounts);
        Assert.Equal("memory_transaction_blocked", summary.Reason);
        Assert.Equal(2, summary.Count);
        Assert.DoesNotContain(HostManagerTargetIdentity.CreateProcessMemoryPolicyTargetId(prepared.ProcessId,
            ulong.Parse(prepared.ProcessStartKey, System.Globalization.CultureInfo.InvariantCulture)), summary.ExampleTargetIds);
        Assert.Equal(1, fixture.ProcessPolicyWriter.BatchCalls);
    }

    [Fact]
    public async Task MemorySkipDiagnosticsResetPerCycleAndLogOnlyWhenAReasonBecomesActive()
    {
        var logs = new MemorySkipLogger();
        await using var fixture = await CreateMemorySkipFixtureAsync(scoreOnly: false, run: false, logger: logs);
        var unavailable = true;
        fixture.ProcessPolicyWriter.MemoryReadHandler = pid => unavailable
            ? RecoveryReadResult<ProcessMemoryPriorityRecoverySnapshot>.Unavailable(5, "denied")
            : RecoveryReadResult<ProcessMemoryPriorityRecoverySnapshot>.Found(new(pid, MemorySkipStartedAt, 3));

        await fixture.RunRealtimeCycleAsync();
        await fixture.RunRealtimeCycleAsync();
        await AssertMemorySkipAsync(fixture, "capture_unavailable");
        Assert.Equal(1, logs.Reasons.Count(reason => reason == "capture_unavailable"));
        unavailable = false;
        await fixture.RunRealtimeCycleAsync();
        await AssertMemorySkipAsync(fixture, "baseline_at_target");
        Assert.Equal(1, logs.Reasons.Count(reason => reason == "baseline_at_target"));
        unavailable = true;
        await fixture.RunRealtimeCycleAsync();
        await AssertMemorySkipAsync(fixture, "capture_unavailable");
        Assert.Equal(2, logs.Reasons.Count(reason => reason == "capture_unavailable"));
    }

    [Theory]
    [InlineData("capture_invalid_request")]
    [InlineData("capture_payload_mismatch")]
    public async Task MemoryCaptureDiagnosticsClassifyDefensiveBoundaryFailures(string expectedReason)
    {
        await using var fixture = await CreateMemorySkipFixtureAsync();
        ResetMemorySkipOutcomes(fixture.Coordinator);
        var directive = Assert.Single(fixture.Coordinator.NonAdaptedMemoryModeProjection!.Processes);
        var capture = expectedReason == "capture_invalid_request"
            ? HostManagerProcessPolicyCaptureResult.Empty(HostManagerProcessPolicyCaptureStatus.InvalidRequest, MemorySkipPid, 0)
            : new HostManagerProcessPolicyCaptureResult(HostManagerProcessPolicyCaptureStatus.Captured,
                HostManagerProcessPolicyTransactionFields.MemoryPriority, MemorySkipPid, checked((long)MemorySkipStart), [0]);

        Assert.True(fixture.Coordinator.TryRecordMemoryCaptureSkip(directive, capture, 3));
        PublishMemorySkipTestSegment(fixture.Coordinator);
        await AssertMemorySkipAsync(fixture, expectedReason);
    }

    [Theory]
    [InlineData("pid")]
    [InlineData("start")]
    [InlineData("fields")]
    [InlineData("target")]
    public void MemoryCaptureDiagnosticsRejectCanonicalPayloadForADifferentDecision(string mismatch)
    {
        var directive = CreateMemorySkipDirective();
        var payload = new HostManagerProcessPolicyRollbackPayload(
            mismatch == "fields" ? HostManagerProcessPolicyTransactionFields.All : HostManagerProcessPolicyTransactionFields.MemoryPriority,
            mismatch == "pid" ? MemorySkipPid + 1 : MemorySkipPid,
            checked((long)MemorySkipStart) + (mismatch == "start" ? 1 : 0),
            mismatch == "fields" ? 32U : 0U, 5, 0, 0,
            mismatch == "fields" ? 0U : mismatch == "target" ? 2U : 3U);
        var capture = new HostManagerProcessPolicyCaptureResult(HostManagerProcessPolicyCaptureStatus.Captured,
            HostManagerProcessPolicyTransactionFields.MemoryPriority, MemorySkipPid, checked((long)MemorySkipStart),
            HostManagerProcessPolicyRollbackPayloadCodec.Encode(payload));

        Assert.Equal("capture_payload_mismatch", HostManagerSmartCoordinator.GetMemoryCaptureSkipReason(directive, capture, 3));
    }

    private static HostManagerNonAdaptedMemoryProcessDirective CreateMemorySkipDirective()
        => new(1, NativeStableIdentity.CreateCaseInsensitiveKey(MemorySkipSoftware), MemorySkipSoftware,
            MemorySkipPid, MemorySkipStart, NativeStableIdentity.CreateCaseInsensitiveKey(
                HostManagerTargetIdentity.CreateProcessMemoryPolicyTargetId(MemorySkipPid, MemorySkipStart)),
            1, NativeMemoryMode.Optimize, HostManagerNonAdaptedMemoryProcessAction.Optimize, 3);

    private static async Task<ScoreOnlyCoordinatorFixture> CreateMemorySkipFixtureAsync(
        bool scoreOnly = true, bool run = true, int count = 1, double baseScore = 35,
        ILogger<HostManagerSmartCoordinator>? logger = null,
        HostManagerProcessEffectValidationScopeAuthority? scopeAuthority = null)
    {
        var facts = CreateCompleteProcessFacts(Enumerable.Range(0, count).Select(index =>
            (MemorySkipPid + index, MemorySkipStart, index == 0 ? MemorySkipSoftware : $"{MemorySkipSoftware}:{index}",
                baseScore, 1D, 25D)).ToArray());
        var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true, scoreOnlyEnabled: scoreOnly,
            processFactsSnapshot: facts, memoryModePolicyEnabled: true, automaticMemoryCleanupEnabled: false,
            optimizationMode: AppOptimizationModes.MemoryOnly, cpuUsagePercent: 1,
            processRecoveryRead: pid => RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(new(pid, MemorySkipStartedAt)),
            coordinatorLogger: logger, processEffectValidationScopeAuthority: scopeAuthority);
        fixture.MetricSampler.SetSnapshot(CreateHardwareSnapshot(memoryUsagePercent: 100, cpuUsagePercent: 1));
        fixture.ProcessPolicyWriter.MemoryReadHandler = pid =>
            RecoveryReadResult<ProcessMemoryPriorityRecoverySnapshot>.Found(new(pid, MemorySkipStartedAt, 5));
        if (run) await fixture.RunRealtimeCycleAsync();
        return fixture;
    }

    private static async Task RunMemorySkipBatchAsync(ScoreOnlyCoordinatorFixture fixture,
        uint maximumTransactions = 1, uint freshBudget = 1, uint recoveryBudget = 1, bool allowFresh = true,
        HostManagerProcessEffectValidationCycleSnapshot? scope = null)
    {
        ResetMemorySkipOutcomes(fixture.Coordinator);
        var admission = HostManagerCycleEffectAdmission.Create(scoreOnly: false);
        admission.InitializeBudgets(Math.Max(1U, freshBudget), Math.Max(1U, recoveryBudget));
        Assert.True(admission.TryAcquire(HostManagerCycleEffectKind.NonAdaptedMemoryTransaction, out var permit));
        if (freshBudget == 0)
        {
            Assert.True(permit.TryReserveNewPointOfNoReturn(1, out var priorFresh));
            using (priorFresh!) priorFresh!.EnterPointOfNoReturn();
        }
        if (recoveryBudget == 0)
        {
            Assert.True(permit.TryReserveOwnedMemoryRestore(1, out var priorRestore));
            using (priorRestore!) priorRestore!.EnterPointOfNoReturn();
        }
        var capture = (Task)typeof(HostManagerSmartCoordinator).GetMethod("CaptureHostManagerSampleAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(fixture.Coordinator, [fixture.RuntimePlan, CancellationToken.None])!;
        await capture;
        var captured = capture.GetType().GetProperty("Result")!.GetValue(capture)!;
        var sample = captured.GetType().GetProperty("Sample")!.GetValue(captured)!;
        var ownership = HostManagerSmartCoordinator.CreateAppliedOwnershipFactIndex(
            await fixture.NativeTransactions.AppliedOwnership.ReadSnapshotAsync(CancellationToken.None));
        Assert.True(fixture.NativeTransactions.TryAcquireExecutionAdmission(out var journal));
        await using (journal!)
        {
            await ReconcileMemorySkipPayloadsAsync(fixture.Coordinator, journal!);
            var work = (Task)typeof(HostManagerSmartCoordinator).GetMethod("ApplyNonAdaptedMemoryModeTransactionsAsync",
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(fixture.Coordinator,
                [permit, scope ?? HostManagerProcessEffectValidationCycleSnapshot.ProductionUnscoped,
                    fixture.Coordinator.SchedulingAuthority.PlanBinding!, sample, ownership, journal,
                    maximumTransactions, allowFresh, CancellationToken.None])!;
            await work;
        }
        PublishMemorySkipTestSegment(fixture.Coordinator);
    }

    // These tests execute an individual private effect stage instead of a round.
    // Complete its observation publication explicitly, just as the real segment does.
    private static void PublishMemorySkipTestSegment(HostManagerSmartCoordinator coordinator)
        => typeof(HostManagerSmartCoordinator)
            .GetMethod("PublishObservation", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(coordinator, null);

    private static async Task ReconcileMemorySkipPayloadsAsync(HostManagerSmartCoordinator coordinator,
        HostManagerTransactionJournalAdmission admission)
    {
        if (!admission.PayloadReconciliationRequired) return;
        var snapshot = await admission.ReadSnapshotAsync(CancellationToken.None);
        await (Task)typeof(HostManagerSmartCoordinator)
            .GetMethod("ReconcileNativePayloadsIfRequiredAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(coordinator, [admission, snapshot, CancellationToken.None])!;
        Assert.False(admission.PayloadReconciliationRequired);
    }

    private static void ResetMemorySkipOutcomes(HostManagerSmartCoordinator coordinator)
        => ((IDictionary)ReadPrivateField<object>(coordinator, "memoryDirectiveOutcomes")!).Clear();

    private static async Task AssertMemorySkipAsync(ScoreOnlyCoordinatorFixture fixture, string expectedReason)
    {
        var diagnostics = await fixture.Coordinator.GetDecisionDiagnosticsAsync(CancellationToken.None);
        var row = Assert.Single(diagnostics.NonAdaptedMemoryDecisions);
        Assert.False(row.TransactionPrepared);
        Assert.Equal(expectedReason, row.SkipReason);
        var count = Assert.Single(diagnostics.NonAdaptedMemorySkipCounts);
        Assert.Equal(expectedReason, count.Reason);
        Assert.Equal(1, count.Count);
        var targetId = HostManagerTargetIdentity.CreateProcessMemoryPolicyTargetId(MemorySkipPid, MemorySkipStart);
        Assert.Equal([targetId], count.ExampleTargetIds);
        var wire = System.Text.Json.JsonSerializer.SerializeToElement(diagnostics,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        var wireDecision = Assert.Single(wire.GetProperty("nonAdaptedMemoryDecisions").EnumerateArray());
        Assert.Equal(expectedReason, wireDecision.GetProperty("skipReason").GetString());
        Assert.False(wireDecision.GetProperty("transactionPrepared").GetBoolean());
        var wireCount = Assert.Single(wire.GetProperty("nonAdaptedMemorySkipCounts").EnumerateArray());
        Assert.Equal(expectedReason, wireCount.GetProperty("reason").GetString());
        Assert.Equal(1, wireCount.GetProperty("count").GetInt32());
        Assert.Equal(targetId, Assert.Single(wireCount.GetProperty("exampleTargetIds").EnumerateArray()).GetString());
    }

    private sealed class MemorySkipLogger : ILogger<HostManagerSmartCoordinator>
    {
        internal List<string> Reasons { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (state is IEnumerable<KeyValuePair<string, object?>> values
                && values.Any(pair => pair.Key == "{OriginalFormat}"
                    && pair.Value is string format && format.StartsWith("Host Manager memory directives skipped:", StringComparison.Ordinal)))
                Reasons.Add((string)values.Single(pair => pair.Key == "Reason").Value!);
        }
    }
}
