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
        var desired = hostPlan.ReportCoordinator;
        if (appliedPlan?.ConfigurationSha256 == desired.ConfigurationSha256
            && appliedPlan.Build == desired.Build
            && appliedPlan.Recreate == desired.Recreate)
        {
            return;
        }

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
                NextIncarnation());
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
            CaptureDisposalFailure(replacement, failures);
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
            CaptureDisposalFailure(replacement, failures);
            ThrowFailures(failures);
        }

        var previous = workspace;
        workspace = replacement;
        appliedPlan = desired;
        coordinatorState = replacementState;
        Volatile.Write(ref readModel, replacementModel);
        replacement = null;

        try
        {
            previous?.Dispose();
        }
        catch (Exception retirementFailure)
        {
            logger.LogError(
                retirementFailure,
                "The previous report coordinator workspace could not be retired after the replacement was committed.");
        }
    }

    private static void CaptureDisposalFailure(
        NativeReportCoordinatorWorkspace? candidate,
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
        Volatile.Write(ref readModel, model);
    }

    private async Task<HostManagerReportReadModel> SettleAsync(
        NativeReportCoordinatorWorkspace target,
        CompiledHostManagerReportCoordinatorPlan plan,
        CoordinatorState state,
        SystemInterruptSnapshot interruptSnapshot,
        CancellationToken cancellationToken)
    {
        var stamp = NextCommandStamp(DateTimeOffset.UtcNow);
        var input = new NativeReportPlanInput
        {
            AbiVersion = NativeReportCoordinatorAbi.Version,
            StructSize = SizeOf<NativeReportPlanInput>(),
            ConfigurationGeneration = plan.ConfigurationGeneration,
            OperationEpoch = stamp.OperationEpoch,
            PlanEpoch = NextEpoch(ref planEpoch, "plan"),
            CommandMonotonicMilliseconds = stamp.MonotonicMilliseconds,
            CommandUtcMilliseconds = stamp.UtcMilliseconds,
            ValidMask = (ulong)NativeReportPlanValidity.Required,
            ReportLimit = checked((uint)target.ReportBuffer.Length),
            PersistenceLimit = checked((uint)target.PersistenceBuffer.Length)
        };
        RequireStatus(
            target.Session.Plan(
                in input,
                target.ReportBuffer,
                target.PersistenceBuffer,
                out var output),
            "planning");
        ValidatePlanOutput(
            in input,
            in output,
            target.ReportBuffer.Length,
            target.PersistenceBuffer.Length);

        var persistenceCount = checked((int)output.PersistenceOperationCount);
        if (persistenceCount != 0)
        {
            var operations = target.PersistenceBuffer
                .AsMemory(0, persistenceCount)
                .ToArray();
            await persistenceStore.PersistAsync(operations, cancellationToken);
            state.ApplyCommitted(operations);
            ApplyPersistenceFeedback(
                target,
                plan,
                operations);
        }

        return BuildReadModel(
            target.ReportBuffer.AsSpan(
                0,
                checked((int)output.ReportCount)),
            plan,
            state,
            output,
            interruptSnapshot);
    }

    private void ApplyPersistenceFeedback(
        NativeReportCoordinatorWorkspace target,
        CompiledHostManagerReportCoordinatorPlan plan,
        IReadOnlyList<NativeReportPersistenceOperation> operations)
    {
        for (var index = 0; index < operations.Count; index++)
        {
            var operation = operations[index];
            target.FeedbackBuffer[index] = new NativeReportPersistenceFeedback
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

        var stamp = NextCommandStamp(DateTimeOffset.UtcNow);
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
                target.FeedbackBuffer.AsSpan(0, operations.Count)),
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
