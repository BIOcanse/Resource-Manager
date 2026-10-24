using ResourceManager.App.Infrastructure.Adaptation;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
using System.Runtime.CompilerServices;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Domain.SystemHealth;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace ResourceManager.App.Infrastructure.Optimization.Reports;

internal sealed partial class HostManagerReportCoordinatorOwner
{
    private async Task ApplyPlanCoreAsync(
        CompiledHostManagerPlan hostPlan,
        CancellationToken cancellationToken)
    {
        await SettlePendingAsync(cancellationToken);
        var desired = hostPlan.ReportCoordinator;
        if (appliedPlan?.ConfigurationSha256 == desired.ConfigurationSha256
            && appliedPlan.Build == desired.Build
            && appliedPlan.Recreate == desired.Recreate)
        {
            return;
        }

        if (pendingWorkspaceClose is not null)
            throw new InvalidOperationException("The previous report workspace still has a close obligation.");
        await RestoreRegisteredTargetsAsync(cancellationToken);
        var initial = workspace is null;
        var recreate = !initial
            && (appliedPlan!.Build != desired.Build
                || appliedPlan.Recreate != desired.Recreate);
        var token = initial
            ? deployment.BeginInitialCreate(hostPlan)
            : recreate
                ? deployment.BeginHostRecreateAndHotPublish(hostPlan)
                : deployment.BeginHotPublish(hostPlan);
        var attempt = new ReportCoordinatorDeploymentAttempt(deployment, token);
        NativeReportCoordinatorWorkspace? replacement = null;
        CoordinatorState replacementState = null!;
        HostManagerReportReadModel replacementModel = null!;
        try
        {
            replacement = new NativeReportCoordinatorWorkspace(
                desired,
                runtimeIdentity.InstanceId,
                NextIncarnation(),
                createScratch);
            var loadedRows = await persistenceStore.LoadAsync(cancellationToken);
            replacementState = new CoordinatorState(
                NativeReportCoordinatorPersistenceMigration.RebindLoadedRows(
                    loadedRows,
                    desired));
            ReplaceRules(replacement, desired);
            Import(replacement, desired, replacementState);
            if (coordinatorState is not null)
            {
                replacementState.RestoreCommittedSourceFrames(coordinatorState);
            }
            replacementModel = await SettleAsync(
                replacement,
                desired,
                replacementState,
                Volatile.Read(ref readModel).InterruptSnapshot,
                cancellationToken);
        }
        catch (Exception stagingFailure)
        {
            var failures = new List<Exception> { stagingFailure };
            RetireWorkspace(replacement, failures);
            try
            {
                attempt.CompleteFailed("report-coordinator-apply-failed");
            }
            catch (Exception settlementFailure)
            {
                failures.Add(settlementFailure);
            }
            ThrowFailures(failures);
        }

        try
        {
            attempt.CompleteSucceeded();
        }
        catch (Exception settlementFailure)
        {
            var failures = new List<Exception> { settlementFailure };
            RetireWorkspace(replacement, failures);
            ThrowFailures(failures);
        }

        var previous = workspace;
        workspace = replacement;
        appliedPlan = desired;
        coordinatorState = replacementState;
        PruneRetainedMetadata(replacementState, replacementModel);
        Volatile.Write(ref readModel, replacementModel);
        replacement = null;

        try
        {
            pendingWorkspaceClose = previous;
            previous?.Dispose();
            pendingWorkspaceClose = null;
        }
        catch (Exception retirementFailure)
        {
            logger.LogError(
                retirementFailure,
                "The previous report coordinator workspace could not be retired after the replacement was committed.");
        }
    }

    private static void CaptureDisposalFailure(
        IDisposable? candidate,
        ICollection<Exception> failures)
    {
        try
        {
            candidate?.Dispose();
        }
        catch (Exception disposalFailure)
        {
            failures.Add(disposalFailure);
        }
    }

    [DoesNotReturn]
    private static void ThrowFailures(IReadOnlyList<Exception> failures)
    {
        if (failures.Count == 1)
        {
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        }

        throw new AggregateException(failures);
    }

    private void ReplaceRules(
        NativeReportCoordinatorWorkspace target,
        CompiledHostManagerReportCoordinatorPlan plan)
    {
        var stamp = NextCommandStamp(DateTimeOffset.UtcNow);
        var input = new NativeReportRuleReplaceInput
        {
            AbiVersion = NativeReportCoordinatorAbi.Version,
            StructSize = SizeOf<NativeReportRuleReplaceInput>(),
            ConfigurationGeneration = plan.ConfigurationGeneration,
            OperationEpoch = stamp.OperationEpoch,
            CommandMonotonicMilliseconds = stamp.MonotonicMilliseconds,
            CommandUtcMilliseconds = stamp.UtcMilliseconds,
            ValidMask = (ulong)NativeReportRuleReplaceValidity.Required,
            RuleCount = checked((uint)target.Rules.Length)
        };
        RequireStatus(
            target.Session.ReplaceRules(in input, target.Rules),
            "rule replacement");
    }

    private void Import(
        NativeReportCoordinatorWorkspace target,
        CompiledHostManagerReportCoordinatorPlan plan,
        CoordinatorState state)
    {
        var stamp = NextCommandStamp(DateTimeOffset.UtcNow);
        var input = new NativeReportImportInput
        {
            AbiVersion = NativeReportCoordinatorAbi.Version,
            StructSize = SizeOf<NativeReportImportInput>(),
            ConfigurationGeneration = plan.ConfigurationGeneration,
            ImportGeneration = NextEpoch(ref importGeneration, "import"),
            OperationEpoch = stamp.OperationEpoch,
            CommandMonotonicMilliseconds = stamp.MonotonicMilliseconds,
            CommandUtcMilliseconds = stamp.UtcMilliseconds,
            ValidMask = (ulong)NativeReportImportValidity.Required,
            RowCount = checked((uint)state.CanonicalRows.Count)
        };
        RequireStatus(
            target.Session.Import(in input, state.OrderedCanonicalRows()),
            "canonical import");
    }

    private void ObserveSource(
        NativeReportCoordinatorWorkspace target,
        CompiledHostManagerReportCoordinatorPlan plan,
        CoordinatorState state,
        HostManagerReportSourceObservation source)
    {
        var sourceFrame = state.PreviewNextSourceFrame(source);
        var facts = new NativeReportFactInput[source.Facts.Count];
        for (var index = 0; index < facts.Length; index++)
        {
            var fact = source.Facts[index];
            var flags = NativeReportFactFlags.CurrentValid;
            if (fact.EventCount is not null)
            {
                flags |= NativeReportFactFlags.EventCountValid;
            }
            facts[index] = new NativeReportFactInput
            {
                StructSize = SizeOf<NativeReportFactInput>(),
                Flags = (uint)flags,
                RuleHandle = fact.Rule.RuleHandle,
                RuleGeneration = fact.Rule.RuleGeneration,
                TargetHandle = fact.TargetHandle,
                EvidencePayloadHandle = 0,
                FactSequence = state.PreviewNextFactSequence(
                    fact.Rule.RuleHandle,
                    fact.TargetHandle),
                CurrentValue = fact.CurrentValue,
                EventCount = fact.EventCount ?? 0,
                SampleDurationMilliseconds = checked(
                    (ulong)source.SampleDurationMilliseconds)
            };
        }

        var stamp = NextCommandStamp(DateTimeOffset.UtcNow);
        var input = new NativeReportSourceSnapshotInput
        {
            AbiVersion = NativeReportCoordinatorAbi.Version,
            StructSize = SizeOf<NativeReportSourceSnapshotInput>(),
            ConfigurationGeneration = plan.ConfigurationGeneration,
            OperationEpoch = stamp.OperationEpoch,
            CommandMonotonicMilliseconds = stamp.MonotonicMilliseconds,
            CommandUtcMilliseconds = stamp.UtcMilliseconds,
            ObservedAtUtcMilliseconds = ToUnixMilliseconds(source.ObservedAt),
            SourceHandle = source.SourceHandle,
            SourceGeneration = sourceFrame.SourceGeneration,
            CoverageScopeHandle = source.CoverageScopeHandle,
            SourceSnapshotEpoch = sourceFrame.SnapshotEpoch,
            ValidMask = (ulong)NativeReportSourceSnapshotValidity.Required,
            FactCount = checked((uint)facts.Length),
            Status = (uint)source.Status
        };
        var status = target.Session.Observe(in input, facts);
        RequireStatus(
            status,
            FormattableString.Invariant(
                $"source observation (source={input.SourceHandle}, coverage={input.CoverageScopeHandle}, providerGeneration={source.ProviderGeneration}, sourceGeneration={input.SourceGeneration}, snapshotEpoch={input.SourceSnapshotEpoch}, status={source.Status}, factCount={facts.Length}, sampleDurationMs={source.SampleDurationMilliseconds}, observedAtUtcMs={input.ObservedAtUtcMilliseconds})"));
        state.CommitSourceObservation(source, sourceFrame, facts);
    }

    private async Task SettlePendingAsync(CancellationToken cancellationToken)
    {
        if (workspace is not { PendingPlan: not null } current) return;
        await SettleAndPublishAsync(current, RequireAppliedPlan(), RequireCoordinatorState(), cancellationToken);
    }

    private async Task SettleAndPublishAsync(
        NativeReportCoordinatorWorkspace target,
        CompiledHostManagerReportCoordinatorPlan plan,
        CoordinatorState state,
        CancellationToken cancellationToken,
        SystemInterruptSnapshot? interruptSnapshot = null)
    {
        var committedInterrupts = interruptSnapshot
            ?? Volatile.Read(ref readModel).InterruptSnapshot;
        var model = await SettleAsync(
            target,
            plan,
            state,
            committedInterrupts,
            cancellationToken);
        PruneRetainedMetadata(state, model);
        Volatile.Write(ref readModel, model);
    }

    private async Task<HostManagerReportReadModel> SettleAsync(
        NativeReportCoordinatorWorkspace target,
        CompiledHostManagerReportCoordinatorPlan plan,
        CoordinatorState state,
        SystemInterruptSnapshot interruptSnapshot,
        CancellationToken cancellationToken)
    {
        using var lease = target.AcquireScratch();
        var settledInterrupts = target.PendingPlan?.InterruptSnapshot ?? interruptSnapshot;
        var commandUtc = DateTimeOffset.UtcNow;
        // Full batches have one feedback-derived layer (Trust -> Report); fixed UTC
        // prevents persistence latency from creating more metadata checkpoints.
        const int maximumSuccessfulBatches = 2;
        for (var successfulBatches = 0; ; successfulBatches++)
        {
            var planned = PlanReports(target, lease, plan, settledInterrupts, commandUtc);
            if (planned.Operations.Length == 0)
            {
                var model = ReadPlannedReports(target, lease, plan, state, planned.Output, settledInterrupts);
                lease.Complete();
                return model;
            }
            if (successfulBatches == maximumSuccessfulBatches)
                throw new InvalidOperationException(
                    "Report persistence did not stabilize after two successful full batches.");

            // Independent persistence values, not a Memory/Span borrowed from the region.
            await persistenceStore.PersistAsync(planned.Operations, cancellationToken, targetCatalog);
            state.ApplyCommitted(planned.Operations);
            ApplyPersistenceFeedback(target, lease, plan, planned.Operations, commandUtc);
            target.PendingPlan = null;
        }
    }

    private (NativeReportPlanOutput Output, NativeReportPersistenceOperation[] Operations) PlanReports(
        NativeReportCoordinatorWorkspace target,
        ComputeScratchLease lease,
        CompiledHostManagerReportCoordinatorPlan plan,
        SystemInterruptSnapshot interruptSnapshot,
        DateTimeOffset commandUtc)
    {
        using var view = new ComputeScratchView(lease);
        var reports = view.Column<NativeReportOutput>(target.Layout.Reports, target.Layout.ReportCapacity);
        var persistence = view.Column<NativeReportPersistenceOperation>(target.Layout.Persistence,
            target.Layout.PersistenceCapacity);
        NativeReportPlanInput input;
        if (target.PendingPlan is { } pending)
        {
            input = pending.Input;
        }
        else
        {
            var stamp = NextCommandStamp(commandUtc);
            input = new NativeReportPlanInput
            {
                AbiVersion = NativeReportCoordinatorAbi.Version,
                StructSize = SizeOf<NativeReportPlanInput>(),
                ConfigurationGeneration = plan.ConfigurationGeneration,
                OperationEpoch = stamp.OperationEpoch,
                PlanEpoch = NextEpoch(ref planEpoch, "plan"),
                CommandMonotonicMilliseconds = stamp.MonotonicMilliseconds,
                CommandUtcMilliseconds = stamp.UtcMilliseconds,
                ValidMask = (ulong)NativeReportPlanValidity.Required,
                ReportLimit = checked((uint)reports.Length),
                PersistenceLimit = checked((uint)persistence.Length)
            };
        }
        RequireStatus(target.Session.Plan(in input, reports, persistence, out var output), "planning");
        if (output.PersistenceOperationCount != 0)
            target.PendingPlan ??= new PendingReportPlan(input, interruptSnapshot);
        ValidatePlanOutput(in input, in output, reports.Length, persistence.Length);
        return (output, persistence[..checked((int)output.PersistenceOperationCount)].ToArray());
    }

    private HostManagerReportReadModel ReadPlannedReports(
        NativeReportCoordinatorWorkspace target,
        ComputeScratchLease lease,
        CompiledHostManagerReportCoordinatorPlan plan,
        CoordinatorState state,
        in NativeReportPlanOutput output,
        SystemInterruptSnapshot interruptSnapshot)
    {
        using var view = new ComputeScratchView(lease);
        return BuildReadModel(view.Column<NativeReportOutput>(target.Layout.Reports,
            target.Layout.ReportCapacity)[..checked((int)output.ReportCount)],
            plan, state, output, interruptSnapshot);
    }

    private void ApplyPersistenceFeedback(
        NativeReportCoordinatorWorkspace target,
        ComputeScratchLease lease,
        CompiledHostManagerReportCoordinatorPlan plan,
        IReadOnlyList<NativeReportPersistenceOperation> operations,
        DateTimeOffset commandUtc)
    {
        using var view = new ComputeScratchView(lease);
        var feedback = view.Column<NativeReportPersistenceFeedback>(target.Layout.Feedback,
            target.Layout.PersistenceCapacity);
        for (var index = 0; index < operations.Count; index++)
        {
            var operation = operations[index];
            feedback[index] = new NativeReportPersistenceFeedback
            {
                StructSize = SizeOf<NativeReportPersistenceFeedback>(),
                Status = (uint)NativeReportPersistenceFeedbackStatus.Persisted,
                SessionInstanceLow = operation.SessionInstanceLow,
                SessionInstanceHigh = operation.SessionInstanceHigh,
                PlanEpoch = operation.PlanEpoch,
                MutationVersion = operation.MutationVersion,
                IdentityHandle = operation.IdentityHandle,
                SlotGeneration = operation.SlotGeneration,
                OperationKind = operation.OperationKind,
                SlotIndex = operation.SlotIndex
            };
        }

        var stamp = NextCommandStamp(commandUtc);
        var input = new NativeReportPersistenceFeedbackInput
        {
            AbiVersion = NativeReportCoordinatorAbi.Version,
            StructSize = SizeOf<NativeReportPersistenceFeedbackInput>(),
            ConfigurationGeneration = plan.ConfigurationGeneration,
            OperationEpoch = stamp.OperationEpoch,
            FeedbackEpoch = NextEpoch(ref feedbackEpoch, "feedback"),
            CommandMonotonicMilliseconds = stamp.MonotonicMilliseconds,
            CommandUtcMilliseconds = stamp.UtcMilliseconds,
            ValidMask = (ulong)NativeReportPersistenceFeedbackValidity.Required,
            FeedbackCount = checked((uint)operations.Count)
        };
        RequireStatus(
            target.Session.ApplyFeedback(
                in input,
                feedback[..operations.Count]),
            "persistence feedback");
    }

    private HostManagerReportReadModel BuildReadModel(
        ReadOnlySpan<NativeReportOutput> outputs,
        CompiledHostManagerReportCoordinatorPlan plan,
        CoordinatorState state,
        in NativeReportPlanOutput native,
        SystemInterruptSnapshot interruptSnapshot)
    {
        var reportBuilder = ImmutableArray.CreateBuilder<OptimizationReportItem>(
            outputs.Length);
        var reportById = ImmutableDictionary.CreateBuilder<
            string,
            OptimizationReportItem>(
                StringComparer.OrdinalIgnoreCase);
        var nativeReportById = ImmutableDictionary.CreateBuilder<
            string,
            NativeReportOutput>(
                StringComparer.OrdinalIgnoreCase);
        foreach (ref readonly var output in outputs)
        {
            ValidateReportOutput(in output, plan);
            var report = HostManagerReportPresentation.CreateReport(
                in output,
                plan,
                targetCatalog);
            reportBuilder.Add(report);
            reportById.Add(report.Id, report);
            nativeReportById.Add(report.Id, output);
        }

        var trustBuilder = ImmutableArray.CreateBuilder<
            TrustedOptimizationTarget>();
        var nativeTrustById = ImmutableDictionary.CreateBuilder<
            string,
            NativeReportPersistenceOperation>(
                StringComparer.OrdinalIgnoreCase);
        foreach (var row in state.CanonicalRows.Values
            .Where(static row =>
                row.OperationKind == (uint)NativeReportPersistenceKind.Trust)
            .OrderBy(static row => row.FirstObservedAtUtcMilliseconds))
        {
            var trust = HostManagerReportPresentation.CreateTrust(
                in row,
                plan,
                targetCatalog);
            trustBuilder.Add(trust);
            nativeTrustById.Add(trust.Id, row);
        }

        var capturedAt = FromUnixMilliseconds(native.LogicalUtcMilliseconds);
        var reports = reportBuilder.ToImmutable();
        var trusted = trustBuilder.ToImmutable();
        var softwareIssueSignals = HostManagerSoftwareIssueSignalProjection.Project(
            reports,
            outputs,
            plan,
            interruptSnapshot);
        return new HostManagerReportReadModel(
            capturedAt,
            reports,
            trusted,
            new OptimizationRecorderStatus(
                capturedAt,
                state.LastObservationAt,
                plan.Recreate.Rules.Length,
                state.AvailableRuleCount(plan),
                checked((int)native.ActiveReportCount),
                trusted.Length,
                protectionService.ProtectedTargetCount,
                SampleIntervalSeconds),
            softwareIssueSignals,
            interruptSnapshot,
            reportById.ToImmutable(),
            nativeReportById.ToImmutable(),
            nativeTrustById.ToImmutable());
    }

    private void PruneRetainedMetadata(CoordinatorState state, HostManagerReportReadModel model)
    {
        var retainedTargets = state.CanonicalRows.Values.Select(static row => row.TargetHandle)
            .Concat(model.NativeReportById.Values.Select(static row => row.TargetHandle)).ToHashSet();
        foreach (var target in targetCatalog.Keys.ToArray())
            if (!retainedTargets.Contains(target)) targetCatalog.Remove(target);
        state.PruneFactSequences();
    }

    private CommandStamp NextCommandStamp(DateTimeOffset now)
    {
        var tick = Environment.TickCount64;
        var candidate = tick > 0 ? checked((ulong)tick) : 1;
        lastCommandMonotonicMilliseconds = Math.Max(
            candidate,
            NextEpochValue(lastCommandMonotonicMilliseconds, "command monotonic"));
        lastCommandUtcMilliseconds = Math.Max(
            lastCommandUtcMilliseconds,
            ToUnixMilliseconds(now));
        return new CommandStamp(
            NextEpoch(ref operationEpoch, "operation"),
            lastCommandMonotonicMilliseconds,
            lastCommandUtcMilliseconds);
    }

    private ulong NextIncarnation()
        => NextEpoch(ref incarnation, "workspace incarnation");

    private static ulong NextEpoch(ref ulong value, string name)
    {
        value = NextEpochValue(value, name);
        return value;
    }

    private static ulong NextEpochValue(ulong value, string name)
        => value < ulong.MaxValue
            ? value + 1
            : throw new InvalidOperationException(
                $"The report coordinator {name} is exhausted.");

    private static long ToUnixMilliseconds(DateTimeOffset value)
    {
        var result = value.ToUnixTimeMilliseconds();
        return result >= 0
            ? result
            : throw new InvalidOperationException(
                "A report coordinator timestamp precedes the Unix epoch.");
    }

    private static DateTimeOffset FromUnixMilliseconds(long value)
        => value >= 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(value)
            : throw new InvalidDataException(
                "A report coordinator timestamp precedes the Unix epoch.");

    private static uint SizeOf<T>() where T : unmanaged
        => checked((uint)Unsafe.SizeOf<T>());

    private static void RequireStatus(
        NativeReportCoordinatorStatus status,
        string operation)
    {
        if (status != NativeReportCoordinatorStatus.Ok)
        {
            throw new InvalidOperationException(
                $"Native report coordinator {operation} failed with {status}.");
        }
    }

    private static unsafe void ValidatePlanOutput(
        in NativeReportPlanInput input,
        in NativeReportPlanOutput output,
        int reportCapacity,
        int persistenceCapacity)
    {
        var flags = (NativeReportPlanFlags)output.Flags;
        if (output.StructSize != SizeOf<NativeReportPlanOutput>()
            || output.ReportCount > checked((uint)reportCapacity)
            || output.PersistenceOperationCount > checked((uint)persistenceCapacity)
            || output.LastPlanEpoch != input.PlanEpoch
            || output.LastOperationEpoch != input.OperationEpoch
            || output.SessionInstanceLow == 0
            || output.SessionInstanceHigh == 0
            || (flags & ~NativeReportPlanFlags.Known) != 0
            || flags.HasFlag(NativeReportPlanFlags.HasMoreReports)
            || flags.HasFlag(NativeReportPlanFlags.HasMorePersistence)
            || output.ReservedU32 != 0
            || output.ResidentByteCount == 0
            || output.Reserved[0] != 0
            || output.Reserved[1] != 0)
        {
            throw new InvalidOperationException(
                "The native report plan output violates the fixed v5 contract.");
        }
    }

    private static unsafe void ValidateReportOutput(
        in NativeReportOutput output,
        CompiledHostManagerReportCoordinatorPlan plan)
    {
        var ruleHandle = output.RuleHandle;
        var familyHandle = output.FamilyHandle;
        if (output.StructSize != SizeOf<NativeReportOutput>()
            || (output.Flags & ~(uint)NativeReportOutputFlags.Known) != 0
            || output.ReportHandle == 0
            || output.SlotGeneration == 0
            || output.TargetHandle == 0
            || output.FamilyHandle == 0
            || output.RuleHandle == 0
            || !HasRule(plan, ruleHandle, familyHandle)
            || output.ReportTypeHandle == 0
            || output.ResourceKindHandle == 0
            || output.PayloadHandle == 0
            || output.CreatedAtUtcMilliseconds < 0
            || output.UpdatedAtUtcMilliseconds < output.CreatedAtUtcMilliseconds
            || output.LastObservedAtUtcMilliseconds < output.CreatedAtUtcMilliseconds
            || !double.IsFinite(output.CurrentValue)
            || !double.IsFinite(output.AverageValue)
            || !double.IsFinite(output.PeakValue)
            || output.SlotIndex
                >= checked((uint)plan.Recreate.Capacity.MaximumReportCount)
            || output.ReservedU32 != 0
            || output.Reserved[0] != 0
            || output.Reserved[1] != 0)
        {
            throw new InvalidDataException(
                "A native report output violates the fixed v5 contract.");
        }
    }

    private static bool HasRule(
        CompiledHostManagerReportCoordinatorPlan plan,
        ulong ruleHandle,
        ulong familyHandle)
    {
        foreach (var rule in plan.Recreate.Rules)
        {
            if (rule.RuleHandle == ruleHandle
                && rule.FamilyHandle == familyHandle)
            {
                return true;
            }
        }
        return false;
    }

    private readonly record struct CommandStamp(
        ulong OperationEpoch,
        ulong MonotonicMilliseconds,
        long UtcMilliseconds);
}

internal sealed class ReportCoordinatorDeploymentAttempt(
    HostManagerReportCoordinatorRuntime deployment,
    HostManagerDeploymentAttemptToken token)
{
    internal bool SettlementAttempted { get; private set; }

    internal HostManagerDeploymentAttemptSettlement CompleteSucceeded()
    {
        BeginSettlement();
        return deployment.CompleteSucceeded(token);
    }

    internal HostManagerDeploymentAttemptSettlement CompleteFailed(string failureCode)
    {
        BeginSettlement();
        return deployment.CompleteFailed(token, failureCode);
    }

    private void BeginSettlement()
    {
        if (SettlementAttempted)
        {
            throw new InvalidOperationException(
                "A report coordinator deployment attempt can be settled only once.");
        }
        SettlementAttempted = true;
    }
}

internal sealed class CoordinatorState
{
    private readonly Dictionary<(ulong Source, ulong Coverage), ulong> sourceHighWater = [];
    private readonly Dictionary<(ulong Source, ulong Coverage), SourceFrameState> sourceFrames = [];
    private readonly Dictionary<(ulong Rule, ulong Target), ulong> factSequences = [];

    internal CoordinatorState(IEnumerable<NativeReportPersistenceOperation> rows)
    {
        foreach (var row in rows)
        {
            CanonicalRows.Add((row.OperationKind, row.IdentityHandle), row);
            if (row.OperationKind == (uint)NativeReportPersistenceKind.Source)
            {
                var key = (row.SourceHandle, row.CoverageScopeHandle);
                sourceHighWater[key] = Math.Max(
                    sourceHighWater.GetValueOrDefault(key),
                    row.SourceGeneration);
            }
            if (row.OperationKind == (uint)NativeReportPersistenceKind.Observation)
            {
                factSequences[(row.RuleHandle, row.TargetHandle)] = Math.Max(
                    factSequences.GetValueOrDefault(
                        (row.RuleHandle, row.TargetHandle)),
                    row.FactSequence);
            }
        }
    }

    internal Dictionary<(uint Kind, ulong Identity), NativeReportPersistenceOperation>
        CanonicalRows
    { get; } = [];

    internal DateTimeOffset? LastObservationAt { get; private set; }

    internal NativeReportPersistenceOperation[] OrderedCanonicalRows()
        => CanonicalRows.Values
            .OrderBy(static row => row.OperationKind)
            .ThenBy(static row => row.IdentityHandle)
            .ToArray();

    internal void RestoreCommittedSourceFrames(CoordinatorState previous)
    {
        foreach (var row in CanonicalRows.Values)
        {
            if (row.OperationKind != (uint)NativeReportPersistenceKind.Source)
            {
                continue;
            }
            var key = (row.SourceHandle, row.CoverageScopeHandle);
            // A live observation may be newer than the imported checkpoint.
            if (!previous.sourceFrames.TryGetValue(key, out var frame)
                || frame.SourceGeneration != row.SourceGeneration
                || frame.SnapshotEpoch != row.SourceSnapshotEpoch
                || (uint)frame.Status != row.SourceStatus)
            {
                continue;
            }
            sourceFrames.Add(key, frame);
            var observedAt = DateTimeOffset.FromUnixTimeMilliseconds(
                frame.ObservedAtUtcMilliseconds);
            if (LastObservationAt is null || observedAt > LastObservationAt.Value)
            {
                LastObservationAt = observedAt;
            }
        }
    }

    internal bool ShouldObserveSource(
        HostManagerReportSourceObservation observation)
    {
        var key = (observation.SourceHandle, observation.CoverageScopeHandle);
        return !sourceFrames.TryGetValue(key, out var committed)
            || committed.Status != observation.Status
            || committed.ProviderGeneration != observation.ProviderGeneration
            || committed.ObservedAtUtcMilliseconds
                != observation.ObservedAt.ToUnixTimeMilliseconds();
    }

    internal int AvailableRuleCount(
        CompiledHostManagerReportCoordinatorPlan plan)
    {
        var count = 0;
        foreach (var group in plan.Recreate.Rules.GroupBy(static rule =>
            (rule.SourceHandle, rule.CoverageScopeHandle)))
        {
            if (sourceFrames.TryGetValue(group.Key, out var source)
                && source.Status == NativeReportSourceStatus.Complete)
            {
                count += group.Count();
            }
        }
        return count;
    }

    internal SourceFrame PreviewNextSourceFrame(
        HostManagerReportSourceObservation observation)
    {
        var key = (observation.SourceHandle, observation.CoverageScopeHandle);
        if (!sourceFrames.TryGetValue(key, out var state))
        {
            state = new SourceFrameState(
                observation.ProviderGeneration,
                Next(
                    sourceHighWater.GetValueOrDefault(key),
                    "source generation"),
                0,
                0,
                observation.Status);
        }
        else if (observation.ProviderGeneration > 0
            && observation.ProviderGeneration != state.ProviderGeneration)
        {
            state = new SourceFrameState(
                observation.ProviderGeneration,
                Next(state.SourceGeneration, "source generation"),
                0,
                0,
                observation.Status);
        }

        state = state with
        {
            SnapshotEpoch = Next(
                state.SnapshotEpoch,
                "source snapshot epoch")
        };
        return new SourceFrame(state.SourceGeneration, state.SnapshotEpoch);
    }

    internal ulong PreviewNextFactSequence(ulong ruleHandle, ulong targetHandle)
    {
        var key = (ruleHandle, targetHandle);
        return Next(
            factSequences.GetValueOrDefault(key),
            "fact sequence");
    }

    internal void CommitSourceObservation(
        HostManagerReportSourceObservation observation,
        SourceFrame frame,
        ReadOnlySpan<NativeReportFactInput> facts)
    {
        var sourceKey = (
            observation.SourceHandle,
            observation.CoverageScopeHandle);
        sourceFrames[sourceKey] = new SourceFrameState(
            observation.ProviderGeneration,
            frame.SourceGeneration,
            frame.SnapshotEpoch,
            observation.ObservedAt.ToUnixTimeMilliseconds(),
            observation.Status);
        sourceHighWater[sourceKey] = frame.SourceGeneration;
        if (LastObservationAt is null
            || observation.ObservedAt > LastObservationAt.Value)
        {
            LastObservationAt = observation.ObservedAt;
        }
        foreach (ref readonly var fact in facts)
        {
            factSequences[(fact.RuleHandle, fact.TargetHandle)] =
                fact.FactSequence;
        }
    }

    internal void ApplyCommitted(
        IReadOnlyList<NativeReportPersistenceOperation> operations)
    {
        foreach (var row in operations)
        {
            var key = (row.OperationKind, row.IdentityHandle);
            if ((((NativeReportPersistenceFlags)row.Flags)
                    & NativeReportPersistenceFlags.Delete) != 0)
            {
                CanonicalRows.Remove(key);
            }
            else
            {
                CanonicalRows[key] = row;
            }
        }
    }

    internal void PruneFactSequences()
    {
        var retained = CanonicalRows.Values
            .Where(static row => row.OperationKind == (uint)NativeReportPersistenceKind.Observation)
            .Select(static row => (row.RuleHandle, row.TargetHandle)).ToHashSet();
        foreach (var key in factSequences.Keys.ToArray())
            if (!retained.Contains(key)) factSequences.Remove(key);
    }

    private static ulong Next(ulong value, string name)
        => value < ulong.MaxValue
            ? value + 1
            : throw new InvalidOperationException(
                $"The report coordinator {name} is exhausted.");

    private sealed record SourceFrameState(
        long ProviderGeneration,
        ulong SourceGeneration,
        ulong SnapshotEpoch,
        long ObservedAtUtcMilliseconds,
        NativeReportSourceStatus Status);

    internal readonly record struct SourceFrame(
        ulong SourceGeneration,
        ulong SnapshotEpoch);
}
