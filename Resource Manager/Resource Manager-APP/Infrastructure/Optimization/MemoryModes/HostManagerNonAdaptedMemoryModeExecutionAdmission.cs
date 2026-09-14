using System.Collections.Immutable;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization.Transactions;

namespace ResourceManager.App.Infrastructure.Optimization;

internal sealed record HostManagerNonAdaptedMemoryExecutionBatch(
    ulong SchedulingGeneration,
    ulong InventoryGeneration,
    ulong CpuSourceGeneration,
    ImmutableArray<HostManagerNonAdaptedMemoryProcessDirective> Processes);

internal enum HostManagerNonAdaptedMemoryDirectiveDecisionKind : byte
{
    None = 0,
    FreshApply = 1,
    RestoreToBaseline = 2,
    PagedFrozenClosed = 3,
    AttributionDriftRestoreToBaseline = 4
}

internal readonly record struct HostManagerNonAdaptedMemoryDirectiveDecision(
    HostManagerNonAdaptedMemoryDirectiveDecisionKind Kind,
    HostManagerNonAdaptedMemoryProcessDirective DesiredDirective,
    HostManagerNonAdaptedMemoryProcessDirective TransactionDirective,
    uint CurrentOwnedMemoryPriority);

internal readonly record struct HostManagerNonAdaptedMemoryProjectionGapRestore(
    HostManagerNonAdaptedMemoryDirectiveDecision Decision,
    NativeAppliedOwnershipRecord Ownership);

internal static class HostManagerNonAdaptedMemoryModeExecutionAdmission
{
    internal static HostManagerNonAdaptedMemoryExecutionBatch? Admit(
        HostManagerSchedulingAuthoritySnapshot authority,
        HostManagerNonAdaptedMemoryModeProjectionSnapshot? projection,
        HostManagerSchedulingPlanBinding currentPlanBinding,
        SchedulingProcessFactSnapshot processFacts)
    {
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(currentPlanBinding);
        ArgumentNullException.ThrowIfNull(processFacts);
        if (!currentPlanBinding.IsPublished)
        {
            throw new InvalidDataException(
                "Non-adapted memory-mode execution requires the current published Host plan binding.");
        }
        if (authority.Availability != HostManagerSchedulingAuthorityAvailability.Ready)
        {
            return null;
        }

        var compute = authority.Compute
            ?? throw new InvalidDataException(
                "A ready memory-mode authority publication has no compute result.");
        var cpu = compute.Cpu
            ?? throw new InvalidDataException(
                "A ready memory-mode authority publication has no CPU score domain.");
        var memoryModes = authority.MemoryModes
            ?? throw new InvalidDataException(
                "A ready memory-mode authority publication has no memory-mode result.");
        var policyEvidence = authority.PolicyEvidence
            ?? throw new InvalidDataException(
                "A ready memory-mode authority publication has no policy evidence.");
        var currentProjection = projection
            ?? throw new InvalidDataException(
                "A ready memory-mode authority publication has no non-adapted process projection.");
        if (authority.AttemptGeneration == 0
            || authority.ObservedAt == default
            || authority.PlanBinding != currentPlanBinding
            || compute.SchedulingGeneration != authority.AttemptGeneration
            || cpu.SchedulingGeneration != authority.AttemptGeneration
            || cpu.SourceGeneration == 0
            || cpu.SourceIdentity.InventoryGeneration != processFacts.Generation
            || memoryModes.ConfigurationGeneration
                != currentPlanBinding.MemoryModeConfigurationGeneration
             || memoryModes.SchedulingGeneration != authority.AttemptGeneration
             || memoryModes.SnapshotGeneration != authority.AttemptGeneration
             || !memoryModes.MemorySource.IsValid
             || currentProjection.SchedulingGeneration != authority.AttemptGeneration
            || currentProjection.InventoryGeneration != processFacts.Generation
            || currentProjection.CpuSourceGeneration != cpu.SourceGeneration
            || !processFacts.IsInventoryCurrentComplete()
            || !policyEvidence.IsBoundTo(
                currentPlanBinding,
                memoryModes.UnrestrictedAllowed))
        {
            throw new InvalidDataException(
                "The non-adapted memory-mode execution projection is not bound to the current scheduling, process, and Host-plan generation.");
        }

        var facts = processFacts.Processes.ToDictionary(
            static fact => (fact.ProcessId, fact.ProcessStartKey));
        var processScores = cpu.Scores
            .Where(static score =>
                score.Kind == NativeComputeScoringOutputKind.ProcessCpu)
            .ToDictionary(static score => (score.ProcessId, score.ProcessStartKey));
        var desiredModes = memoryModes.Software.ToDictionary(
            static desired => desired.SoftwareKey);
        var targets = new HashSet<ulong>();
        HostManagerNonAdaptedMemoryProcessDirective? previous = null;
        foreach (var directive in currentProjection.Processes)
        {
            var identity = (directive.ProcessId, directive.ProcessStartKey);
            if (directive.SchedulingGeneration != authority.AttemptGeneration
                || directive.ProcessId <= 0
                || directive.ProcessStartKey == 0
                || directive.SoftwareKey == 0
                || string.IsNullOrWhiteSpace(directive.SoftwareId)
                || NativeStableIdentity.CreateCaseInsensitiveKey(directive.SoftwareId)
                    != directive.SoftwareKey
                || !facts.TryGetValue(identity, out var fact)
                || !fact.SoftwareId.Equals(
                    directive.SoftwareId,
                    StringComparison.OrdinalIgnoreCase)
                || !processScores.TryGetValue(identity, out var processScore)
                || processScore.SchedulingGeneration != authority.AttemptGeneration
                || processScore.SoftwareKey != directive.SoftwareKey
                || processScore.MemberCount != 1
                || !desiredModes.TryGetValue(
                    directive.SoftwareKey,
                    out var desired)
                || desired.SchedulingGeneration != authority.AttemptGeneration
                || desired.SnapshotGeneration != authority.AttemptGeneration
                || desired.Rank != directive.SoftwareRank
                || desired.Mode != directive.Mode
                || directive.MemoryPolicyTargetKey !=
                    CreateMemoryPolicyTargetKey(directive)
                || !targets.Add(directive.MemoryPolicyTargetKey)
                || !IsCanonicalDirective(directive))
            {
                throw new InvalidDataException(
                    "A non-adapted memory-mode directive is not an exact member of the current authority publication.");
            }
            if (previous is not null && Compare(previous, directive) > 0)
            {
                throw new InvalidDataException(
                    "Non-adapted memory-mode directives are not in canonical software-rank and process order.");
            }
            previous = directive;
        }

        return new(
            currentProjection.SchedulingGeneration,
            currentProjection.InventoryGeneration,
            currentProjection.CpuSourceGeneration,
            currentProjection.Processes);
    }

    internal static HostManagerNonAdaptedMemoryExecutionBatch?
        AdmitNonReadyOwnerRecovery(
            HostManagerSchedulingAuthoritySnapshot authority,
            HostManagerSchedulingPlanBinding currentPlanBinding,
            SchedulingProcessFactSnapshot processFacts)
    {
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(currentPlanBinding);
        ArgumentNullException.ThrowIfNull(processFacts);
        if (!currentPlanBinding.IsPublished)
        {
            throw new InvalidDataException(
                "Owned process-memory recovery requires the current published Host plan binding.");
        }
        if (authority.Availability == HostManagerSchedulingAuthorityAvailability.Ready)
        {
            throw new InvalidOperationException(
                "Ready memory-mode authority must use the full execution admission.");
        }
        if (authority.Availability is not (
                HostManagerSchedulingAuthorityAvailability.ComputeOnly or
                HostManagerSchedulingAuthorityAvailability.Unavailable))
        {
            throw new InvalidDataException(
                "Owned process-memory recovery received an unknown authority availability.");
        }
        if (authority.AttemptGeneration == 0
            || authority.ObservedAt == default
            || authority.PlanBinding is null)
        {
            return null;
        }
        if (authority.PlanBinding != currentPlanBinding)
        {
            throw new InvalidDataException(
                "Owned process-memory recovery is not bound to the current Host plan publication.");
        }
        if (!processFacts.IsInventoryCurrentComplete())
        {
            return null;
        }

        return new(
            authority.AttemptGeneration,
            processFacts.Generation,
            0,
            []);
    }

    internal static ImmutableArray<HostManagerNonAdaptedMemoryProjectionGapRestore>
        CreateProjectionGapRestores(
            HostManagerNonAdaptedMemoryExecutionBatch batch,
            SchedulingProcessFactSnapshot processFacts,
            NativeAppliedOwnershipFactIndex ownershipIndex)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(processFacts);
        ArgumentNullException.ThrowIfNull(ownershipIndex);
        if (batch.SchedulingGeneration == 0
            || batch.InventoryGeneration == 0
            || batch.InventoryGeneration != processFacts.Generation
            || !processFacts.IsInventoryCurrentComplete())
        {
            throw new InvalidDataException(
                "Owner-driven memory restores require one complete admitted process inventory generation.");
        }

        var liveFacts = new Dictionary<
            NativeAppliedOwnershipProcessIncarnation,
            SchedulingProcessFact>(processFacts.Processes.Count);
        foreach (var fact in processFacts.Processes)
        {
            var incarnation = new NativeAppliedOwnershipProcessIncarnation(
                checked((uint)fact.ProcessId),
                fact.ProcessStartKey);
            if (!liveFacts.TryAdd(incarnation, fact)
                || NativeStableIdentity.CreateCaseInsensitiveKey(fact.SoftwareId) == 0)
            {
                throw new InvalidDataException(
                    "Projection-gap memory restores received an invalid current process identity.");
            }
        }

        var represented = new HashSet<NativeAppliedOwnershipProcessIncarnation>();
        foreach (var directive in batch.Processes)
        {
            var incarnation = new NativeAppliedOwnershipProcessIncarnation(
                checked((uint)directive.ProcessId),
                directive.ProcessStartKey);
            if (directive.SchedulingGeneration != batch.SchedulingGeneration
                || !liveFacts.TryGetValue(incarnation, out var fact)
                || directive.SoftwareId is null
                || !fact.SoftwareId.Equals(
                    directive.SoftwareId,
                    StringComparison.OrdinalIgnoreCase)
                || !represented.Add(incarnation))
            {
                throw new InvalidDataException(
                    "Projection-gap memory restores received a directive outside the admitted process generation.");
            }
            _ = CreatePrimary(directive);
        }

        var restores = ImmutableArray.CreateBuilder<
            HostManagerNonAdaptedMemoryProjectionGapRestore>();
        foreach (var pair in ownershipIndex.MemoryProcessRecords
                     .OrderBy(static pair => pair.Key.ProcessId)
                     .ThenBy(static pair => pair.Key.ProcessStartKey)
                     .ThenBy(static pair => pair.Key.SoftwareKey)
                     .ThenBy(static pair => pair.Key.TargetKey))
        {
            var incarnation = new NativeAppliedOwnershipProcessIncarnation(
                pair.Key.ProcessId,
                pair.Key.ProcessStartKey);
            if (represented.Contains(incarnation))
            {
                continue;
            }

            var ownership = pair.Value;
            var hasLiveFact = liveFacts.TryGetValue(incarnation, out var fact);
            var softwareKey = hasLiveFact
                ? NativeStableIdentity.CreateCaseInsensitiveKey(fact!.SoftwareId)
                : pair.Key.SoftwareKey;
            var softwareId = hasLiveFact ? fact!.SoftwareId : null;
            var restoreDirective = new HostManagerNonAdaptedMemoryProcessDirective(
                batch.SchedulingGeneration,
                softwareKey,
                softwareId,
                checked((int)pair.Key.ProcessId),
                pair.Key.ProcessStartKey,
                NativeStableIdentity.CreateCaseInsensitiveKey(
                    HostManagerTargetIdentity.CreateProcessMemoryPolicyTargetId(
                        checked((int)pair.Key.ProcessId),
                        pair.Key.ProcessStartKey)),
                SoftwareRank: 0,
                NativeMemoryMode.Normal,
                HostManagerNonAdaptedMemoryProcessAction.RestoreOwnedConstraints,
                TargetMemoryPriority: null);
            var decision = !hasLiveFact || pair.Key.SoftwareKey == softwareKey
                ? Decide(
                    restoreDirective,
                    ownership,
                    allowFreshApply: false)
                : CreateAttributionDriftRestore(
                    restoreDirective,
                    in ownership);
            if (decision.Kind is not (
                    HostManagerNonAdaptedMemoryDirectiveDecisionKind.RestoreToBaseline
                    or HostManagerNonAdaptedMemoryDirectiveDecisionKind
                        .AttributionDriftRestoreToBaseline))
            {
                throw new InvalidDataException(
                    "A live process-memory projection gap did not produce an owned restore.");
            }
            restores.Add(new(decision, ownership));
        }

        return restores.ToImmutable();
    }

    internal static NativeAppliedOwnershipPrimaryIdentity CreatePrimary(
        HostManagerNonAdaptedMemoryProcessDirective directive)
    {
        ArgumentNullException.ThrowIfNull(directive);
        if (!IsCanonicalDirective(directive)
            || directive.MemoryPolicyTargetKey !=
                CreateMemoryPolicyTargetKey(directive))
        {
            throw new InvalidDataException(
                "The non-adapted memory directive has no canonical independent memory-policy identity.");
        }
        return new NativeAppliedOwnershipPrimaryIdentity
        {
            Scope = (uint)NativeAppliedOwnershipScope.Process,
            TargetId = directive.MemoryPolicyTargetKey,
            SoftwareId = directive.SoftwareKey,
            ProcessStartKey = directive.ProcessStartKey,
            ProcessId = checked((uint)directive.ProcessId)
        };
    }

    internal static HostManagerNonAdaptedMemoryDirectiveDecision Decide(
        HostManagerNonAdaptedMemoryProcessDirective directive,
        NativeAppliedOwnershipRecord? ownership,
        bool allowFreshApply)
    {
        ArgumentNullException.ThrowIfNull(directive);
        _ = CreatePrimary(directive);
        var ownedPriority = ownership is null
            ? 0U
            : RequireExactMemoryOwner(directive, ownership.Value);
        if (!allowFreshApply && ownedPriority != 0)
        {
            return CreateRestoreDecision(directive, ownedPriority);
        }
        if (directive.Action == HostManagerNonAdaptedMemoryProcessAction.PagedFrozen)
        {
            return new(
                HostManagerNonAdaptedMemoryDirectiveDecisionKind.PagedFrozenClosed,
                directive,
                directive,
                ownedPriority);
        }
        if (directive.Action ==
            HostManagerNonAdaptedMemoryProcessAction.RestoreOwnedConstraints)
        {
            return ownedPriority == 0
                ? new(
                    HostManagerNonAdaptedMemoryDirectiveDecisionKind.None,
                    directive,
                    directive,
                    0)
                : new(
                    HostManagerNonAdaptedMemoryDirectiveDecisionKind.RestoreToBaseline,
                    directive,
                    directive,
                    ownedPriority);
        }

        var target = directive.TargetMemoryPriority
            ?? throw new InvalidDataException(
                "An optimizing memory directive has no explicit memory-priority target.");
        if (ownedPriority == 0)
        {
            return allowFreshApply
                ? new(
                    HostManagerNonAdaptedMemoryDirectiveDecisionKind.FreshApply,
                    directive,
                    directive,
                    0)
                : new(
                    HostManagerNonAdaptedMemoryDirectiveDecisionKind.None,
                    directive,
                    directive,
                    0);
        }
        if (ownedPriority == target)
        {
            return new(
                HostManagerNonAdaptedMemoryDirectiveDecisionKind.None,
                directive,
                directive,
                ownedPriority);
        }

        return CreateRestoreDecision(directive, ownedPriority);
    }

    private static HostManagerNonAdaptedMemoryDirectiveDecision CreateRestoreDecision(
        HostManagerNonAdaptedMemoryProcessDirective directive,
        uint ownedPriority)
    {
        var restore = directive with
        {
            Mode = NativeMemoryMode.Normal,
            Action = HostManagerNonAdaptedMemoryProcessAction.RestoreOwnedConstraints,
            TargetMemoryPriority = null
        };
        return new(
            HostManagerNonAdaptedMemoryDirectiveDecisionKind.RestoreToBaseline,
            directive,
            restore,
            ownedPriority);
    }

    internal static HostManagerNonAdaptedMemoryDirectiveDecision
        CreateAttributionDriftRestore(
            HostManagerNonAdaptedMemoryProcessDirective directive,
            in NativeAppliedOwnershipRecord ownership)
    {
        ArgumentNullException.ThrowIfNull(directive);
        var currentPrimary = CreatePrimary(directive);
        var ownedPriority = RequireMemoryOwner(
            directive,
            ownership,
            requireSoftwareIdentity: false);
        if (ownership.Primary.SoftwareId == currentPrimary.SoftwareId)
        {
            throw new InvalidDataException(
                "A process-memory attribution-drift restore requires different current and owned software keys.");
        }

        return new(
            HostManagerNonAdaptedMemoryDirectiveDecisionKind
                .AttributionDriftRestoreToBaseline,
            directive,
            directive with
            {
                Mode = NativeMemoryMode.Normal,
                Action = HostManagerNonAdaptedMemoryProcessAction
                    .RestoreOwnedConstraints,
                TargetMemoryPriority = null
            },
            ownedPriority);
    }

    private static uint RequireExactMemoryOwner(
        HostManagerNonAdaptedMemoryProcessDirective directive,
        in NativeAppliedOwnershipRecord ownership)
        => RequireMemoryOwner(
            directive,
            ownership,
            requireSoftwareIdentity: true);

    private static uint RequireMemoryOwner(
        HostManagerNonAdaptedMemoryProcessDirective directive,
        in NativeAppliedOwnershipRecord ownership,
        bool requireSoftwareIdentity)
    {
        var primary = CreatePrimary(directive);
        var binding = HostManagerAppliedOwnershipProjection.CreatePayloadBinding(
            in ownership);
        var priority = ownership.CurrentGrades.ProcessGrade;
        if (ownership.Primary.Scope != primary.Scope
            || ownership.Primary.TargetId != primary.TargetId
            || requireSoftwareIdentity
                && ownership.Primary.SoftwareId != primary.SoftwareId
            || ownership.Primary.ProcessStartKey != primary.ProcessStartKey
            || ownership.Primary.ProcessId != primary.ProcessId
            || ownership.CurrentGrades.ValidMask !=
                (uint)NativeAppliedOwnershipGradeValidity.Memory
            || priority is < 1 or > 5
            || ownership.CurrentGrades.CpuGrade != 0
            || ownership.CurrentGrades.GpuGrade != 0
            || binding.Scope != NativeTransactionJournalScope.Process
            || binding.Disposition != NativeTransactionJournalDisposition.Apply
            || binding.Domain != NativeTransactionJournalDomain.PhysicalMemory
            || binding.GradeValidMask != NativeTransactionJournalGradeValidity.Memory
            || binding.ProcessFromGrade != 0
            || binding.ProcessToGrade != priority
            || binding.CpuFromGrade != 0
            || binding.CpuToGrade != 0
            || binding.GpuFromGrade != 0
            || binding.GpuToGrade != 0)
        {
            throw new InvalidDataException(
                "The independent process-memory ownership record is not canonical for its directive.");
        }
        return checked((uint)priority);
    }

    private static bool IsCanonicalDirective(
        HostManagerNonAdaptedMemoryProcessDirective directive)
        => directive.Mode switch
        {
            NativeMemoryMode.Unrestricted or NativeMemoryMode.Normal =>
                directive.Action ==
                    HostManagerNonAdaptedMemoryProcessAction.RestoreOwnedConstraints
                && directive.TargetMemoryPriority is null,
            NativeMemoryMode.Optimize =>
                directive.Action == HostManagerNonAdaptedMemoryProcessAction.Optimize
                && directive.TargetMemoryPriority is >= 1 and <= 5,
            NativeMemoryMode.PagedFrozen =>
                directive.Action == HostManagerNonAdaptedMemoryProcessAction.PagedFrozen
                && directive.TargetMemoryPriority is >= 1 and <= 5,
            _ => false
        };

    private static ulong CreateMemoryPolicyTargetKey(
        HostManagerNonAdaptedMemoryProcessDirective directive)
        => NativeStableIdentity.CreateCaseInsensitiveKey(
            HostManagerTargetIdentity.CreateProcessMemoryPolicyTargetId(
                directive.ProcessId,
                directive.ProcessStartKey));

    private static int Compare(
        HostManagerNonAdaptedMemoryProcessDirective left,
        HostManagerNonAdaptedMemoryProcessDirective right)
    {
        var rank = left.SoftwareRank.CompareTo(right.SoftwareRank);
        if (rank != 0)
        {
            return rank;
        }
        var process = left.ProcessId.CompareTo(right.ProcessId);
        return process != 0
            ? process
            : left.ProcessStartKey.CompareTo(right.ProcessStartKey);
    }
}
