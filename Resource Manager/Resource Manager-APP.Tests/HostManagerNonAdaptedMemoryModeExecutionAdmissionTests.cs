using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization;
using ResourceManager.App.Infrastructure.Optimization.Transactions;

namespace ResourceManager.App.Tests;

public sealed class HostManagerNonAdaptedMemoryModeExecutionAdmissionTests
{
    private const ulong SchedulingGeneration = 20;
    private const ulong InventoryGeneration = 30;
    private const ulong CpuSourceGeneration = 31;
    private const ulong ProcessStartKey = 132_537_600_000_000_000;
    private const int ProcessId = 4242;
    private const string SoftwareId = "editor.example";

    [Fact]
    public void ExactReadyPublication_AdmitsOneCurrentOrderedBatch()
    {
        var state = CreateState();

        var admitted = HostManagerNonAdaptedMemoryModeExecutionAdmission.Admit(
            state.Authority,
            state.Projection,
            state.Binding,
            state.ProcessFacts);

        Assert.NotNull(admitted);
        Assert.Equal(SchedulingGeneration, admitted.SchedulingGeneration);
        Assert.Equal(InventoryGeneration, admitted.InventoryGeneration);
        Assert.Equal(CpuSourceGeneration, admitted.CpuSourceGeneration);
        Assert.Single(admitted.Processes);
        Assert.Same(state.Directive, admitted.Processes[0]);
    }

    [Fact]
    public void NonReadyAuthority_DoesNotConsumeAStaleProjection()
    {
        var state = CreateState();
        var unavailable = state.Authority with
        {
            Availability = HostManagerSchedulingAuthorityAvailability.ComputeOnly,
            MemoryModes = null,
            PolicyEvidence = null,
            UnavailableReason = "memory-mode-unavailable"
        };

        Assert.Null(HostManagerNonAdaptedMemoryModeExecutionAdmission.Admit(
            unavailable,
            state.Projection,
            state.Binding,
            state.ProcessFacts));
    }

    [Fact]
    public void NonReadyAuthority_WithCompleteInventory_AdmitsRestoreOnlyBatch()
    {
        var state = CreateState();
        var unavailable = state.Authority with
        {
            Availability = HostManagerSchedulingAuthorityAvailability.ComputeOnly,
            MemoryModes = null,
            PolicyEvidence = null,
            UnavailableReason = "memory-mode-unavailable"
        };
        var processWithoutCpu = state.ProcessFacts.Processes[0] with
        {
            ValidMetricMask = SchedulingProcessMetricMask.None,
            CpuUsagePercent = 0
        };
        var inventoryOnly = state.ProcessFacts with
        {
            CurrentMetricMask = SchedulingProcessMetricMask.None,
            Processes = [processWithoutCpu]
        };

        var admitted = HostManagerNonAdaptedMemoryModeExecutionAdmission
            .AdmitNonReadyOwnerRecovery(
                unavailable,
                state.Binding,
                inventoryOnly);

        Assert.NotNull(admitted);
        Assert.Equal(SchedulingGeneration, admitted.SchedulingGeneration);
        Assert.Equal(InventoryGeneration, admitted.InventoryGeneration);
        Assert.Equal(0UL, admitted.CpuSourceGeneration);
        Assert.Empty(admitted.Processes);
        var owner = CreateOwner(state.Directive, targetMemoryPriority: 3);
        Assert.Single(HostManagerNonAdaptedMemoryModeExecutionAdmission
            .CreateProjectionGapRestores(
                admitted,
                inventoryOnly,
                CreateMemoryOwnershipIndex(owner)));
    }

    [Fact]
    public void NonReadyAuthority_WithIncompleteInventory_DoesNotAdmitBlindRestore()
    {
        var state = CreateState();
        var unavailable = state.Authority with
        {
            Availability = HostManagerSchedulingAuthorityAvailability.Unavailable,
            Compute = null,
            MemoryModes = null,
            PolicyEvidence = null,
            UnavailableReason = "compute-sample-incomplete"
        };
        var incomplete = state.ProcessFacts with
        {
            InventoryStatus = SamplingObservationStatus.RetainedLastGood
        };

        Assert.Null(HostManagerNonAdaptedMemoryModeExecutionAdmission
            .AdmitNonReadyOwnerRecovery(
                unavailable,
                state.Binding,
                incomplete));
        Assert.Throws<InvalidDataException>(() =>
            HostManagerNonAdaptedMemoryModeExecutionAdmission
                .AdmitNonReadyOwnerRecovery(
                    unavailable,
                    state.Binding with
                    {
                        HostPlanEpoch = state.Binding.HostPlanEpoch + 1
                    },
                    state.ProcessFacts));
    }

    [Fact]
    public void PlanOrProcessGenerationDrift_FailsClosed()
    {
        var state = CreateState();

        Assert.Throws<InvalidDataException>(() =>
            HostManagerNonAdaptedMemoryModeExecutionAdmission.Admit(
                state.Authority,
                state.Projection,
                state.Binding with { HostPlanEpoch = state.Binding.HostPlanEpoch + 1 },
                state.ProcessFacts));
        Assert.Throws<InvalidDataException>(() =>
            HostManagerNonAdaptedMemoryModeExecutionAdmission.Admit(
                state.Authority,
                state.Projection with
                {
                    InventoryGeneration = InventoryGeneration + 1
                },
                state.Binding,
                state.ProcessFacts));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ScoreInventoryOrPhysicalWindowDrift_FailsClosed(bool changePhysicalWindow)
    {
        var state = CreateState();
        var compute = state.Authority.Compute!;
        var cpu = compute.Cpu!;
        var changed = cpu with
        {
            SourceIdentity = changePhysicalWindow
                ? cpu.SourceIdentity with { MetricGeneration = CpuSourceGeneration + 1 }
                : cpu.SourceIdentity with { InventoryGeneration = InventoryGeneration + 1 }
        };

        Assert.Throws<InvalidDataException>(() => HostManagerNonAdaptedMemoryModeExecutionAdmission.Admit(
            state.Authority with { Compute = compute with { Cpu = changed } },
            state.Projection, state.Binding, state.ProcessFacts));
    }

    [Fact]
    public void DirectiveMembershipOrTargetDrift_FailsClosed()
    {
        var state = CreateState();
        var drifted = state.Directive with
        {
            MemoryPolicyTargetKey = state.Directive.MemoryPolicyTargetKey + 1
        };

        Assert.Throws<InvalidDataException>(() =>
            HostManagerNonAdaptedMemoryModeExecutionAdmission.Admit(
                state.Authority,
                state.Projection with { Processes = [drifted] },
                state.Binding,
                state.ProcessFacts));
    }

    [Fact]
    public void OptimizeWithoutOwner_RequiresOneFreshApply()
    {
        var state = CreateState();

        var decision = HostManagerNonAdaptedMemoryModeExecutionAdmission.Decide(
            state.Directive,
            ownership: null,
            allowFreshApply: true);

        Assert.Equal(
            HostManagerNonAdaptedMemoryDirectiveDecisionKind.FreshApply,
            decision.Kind);
        Assert.Same(state.Directive, decision.TransactionDirective);
        Assert.Equal(0U, decision.CurrentOwnedMemoryPriority);
    }

    [Fact]
    public void OptimizeWithoutOwner_WhenFreshApplyIsDisabled_DoesNothing()
    {
        var state = CreateState();

        var decision = HostManagerNonAdaptedMemoryModeExecutionAdmission.Decide(
            state.Directive,
            ownership: null,
            allowFreshApply: false);

        Assert.Equal(
            HostManagerNonAdaptedMemoryDirectiveDecisionKind.None,
            decision.Kind);
        Assert.Equal(0U, decision.CurrentOwnedMemoryPriority);
        Assert.Same(state.Directive, decision.TransactionDirective);
    }

    [Fact]
    public void OptimizeWithOwner_WhenFreshApplyIsDisabled_RestoresBaseline()
    {
        var state = CreateState();
        var owner = CreateOwner(state.Directive, targetMemoryPriority: 3);

        var decision = HostManagerNonAdaptedMemoryModeExecutionAdmission.Decide(
            state.Directive,
            owner,
            allowFreshApply: false);

        Assert.Equal(
            HostManagerNonAdaptedMemoryDirectiveDecisionKind.RestoreToBaseline,
            decision.Kind);
        Assert.Equal(3U, decision.CurrentOwnedMemoryPriority);
        Assert.Equal(NativeMemoryMode.Normal, decision.TransactionDirective.Mode);
        Assert.Equal(
            HostManagerNonAdaptedMemoryProcessAction.RestoreOwnedConstraints,
            decision.TransactionDirective.Action);
        Assert.Null(decision.TransactionDirective.TargetMemoryPriority);
    }

    [Fact]
    public void OptimizeWithSameOwner_IsAlreadySatisfied()
    {
        var state = CreateState();
        var owner = CreateOwner(state.Directive, targetMemoryPriority: 3);

        var decision = HostManagerNonAdaptedMemoryModeExecutionAdmission.Decide(
            state.Directive,
            owner,
            allowFreshApply: true);

        Assert.Equal(
            HostManagerNonAdaptedMemoryDirectiveDecisionKind.None,
            decision.Kind);
        Assert.Equal(3U, decision.CurrentOwnedMemoryPriority);
    }

    [Fact]
    public void OptimizeTargetChange_RestoresBaselineBeforeAnyFutureFreshApply()
    {
        var state = CreateState(targetMemoryPriority: 1);
        var owner = CreateOwner(
            state.Directive with { TargetMemoryPriority = 3 },
            targetMemoryPriority: 3);

        var decision = HostManagerNonAdaptedMemoryModeExecutionAdmission.Decide(
            state.Directive,
            owner,
            allowFreshApply: true);

        Assert.Equal(
            HostManagerNonAdaptedMemoryDirectiveDecisionKind.RestoreToBaseline,
            decision.Kind);
        Assert.Equal(3U, decision.CurrentOwnedMemoryPriority);
        Assert.Equal(NativeMemoryMode.Normal, decision.TransactionDirective.Mode);
        Assert.Equal(
            HostManagerNonAdaptedMemoryProcessAction.RestoreOwnedConstraints,
            decision.TransactionDirective.Action);
        Assert.Null(decision.TransactionDirective.TargetMemoryPriority);
        Assert.Equal(
            state.Directive.SchedulingGeneration,
            decision.TransactionDirective.SchedulingGeneration);
        Assert.Equal(
            state.Directive.MemoryPolicyTargetKey,
            decision.TransactionDirective.MemoryPolicyTargetKey);
    }

    [Fact]
    public void PagedFrozen_RemainsClosedAndDoesNotDisturbExistingMemoryOwnership()
    {
        var state = CreateState(
            NativeMemoryMode.PagedFrozen,
            HostManagerNonAdaptedMemoryProcessAction.PagedFrozen,
            targetMemoryPriority: 1);
        var ownerDirective = state.Directive with
        {
            Mode = NativeMemoryMode.Optimize,
            Action = HostManagerNonAdaptedMemoryProcessAction.Optimize,
            TargetMemoryPriority = 3
        };
        var owner = CreateOwner(ownerDirective, targetMemoryPriority: 3);

        var decision = HostManagerNonAdaptedMemoryModeExecutionAdmission.Decide(
            state.Directive,
            owner,
            allowFreshApply: true);

        Assert.Equal(
            HostManagerNonAdaptedMemoryDirectiveDecisionKind.PagedFrozenClosed,
            decision.Kind);
        Assert.Equal(3U, decision.CurrentOwnedMemoryPriority);
        Assert.Same(state.Directive, decision.TransactionDirective);
    }

    [Fact]
    public void AttributionDrift_RestoresTheExactOwnedSoftwareIdentityBeforeReapply()
    {
        var state = CreateState();
        const string ownedSoftwareId = "legacy.editor.example";
        var ownedDirective = state.Directive with
        {
            SoftwareId = ownedSoftwareId,
            SoftwareKey = NativeStableIdentity.CreateCaseInsensitiveKey(ownedSoftwareId)
        };
        var owner = CreateOwner(ownedDirective, targetMemoryPriority: 3);

        var decision = HostManagerNonAdaptedMemoryModeExecutionAdmission
            .CreateAttributionDriftRestore(state.Directive, in owner);

        Assert.Equal(
            HostManagerNonAdaptedMemoryDirectiveDecisionKind
                .AttributionDriftRestoreToBaseline,
            decision.Kind);
        Assert.Equal(3U, decision.CurrentOwnedMemoryPriority);
        Assert.Equal(NativeMemoryMode.Normal, decision.TransactionDirective.Mode);
        Assert.Equal(
            HostManagerNonAdaptedMemoryProcessAction.RestoreOwnedConstraints,
            decision.TransactionDirective.Action);
        Assert.Null(decision.TransactionDirective.TargetMemoryPriority);
        Assert.Equal(state.Directive.SoftwareKey, decision.DesiredDirective.SoftwareKey);

        var action = HostManagerProcessMemoryTransactionProjection
            .CreateAttributionDriftRestore(
                decision.TransactionDirective,
                in owner,
                configurationGeneration: 100,
                actionId: 103,
                hostSessionIncarnation: 104,
                decision.CurrentOwnedMemoryPriority);
        Assert.Equal(owner.Primary.SoftwareId, action.SoftwareKey);
        Assert.Null(action.SoftwareId);
        Assert.Equal(owner.Primary.TargetId, action.MemoryPolicyTargetKey);
        Assert.Equal(ProcessId, action.ProcessId);
        Assert.Equal(ProcessStartKey, action.ProcessStartKey);
        Assert.Equal(NativeTransactionJournalDisposition.Restore, action.Disposition);
    }

    [Fact]
    public void LiveOwnedProcessOmittedFromProjection_RestoresToBaseline()
    {
        var state = CreateState();
        var owner = CreateOwner(state.Directive, targetMemoryPriority: 3);
        var batch = new HostManagerNonAdaptedMemoryExecutionBatch(
            SchedulingGeneration,
            InventoryGeneration,
            0,
            []);

        var restore = Assert.Single(
            HostManagerNonAdaptedMemoryModeExecutionAdmission
                .CreateProjectionGapRestores(
                    batch,
                    state.ProcessFacts,
                    CreateMemoryOwnershipIndex(owner)));

        Assert.Equal(
            HostManagerNonAdaptedMemoryDirectiveDecisionKind.RestoreToBaseline,
            restore.Decision.Kind);
        Assert.Equal(ProcessId, restore.Decision.TransactionDirective.ProcessId);
        Assert.Equal(
            ProcessStartKey,
            restore.Decision.TransactionDirective.ProcessStartKey);
        Assert.Equal(
            state.Directive.MemoryPolicyTargetKey,
            restore.Decision.TransactionDirective.MemoryPolicyTargetKey);
        Assert.Equal(3U, restore.Decision.CurrentOwnedMemoryPriority);
        Assert.Equal(owner.Primary, restore.Ownership.Primary);
    }

    [Fact]
    public void LiveOwnedProcessOmittedAfterAttributionChange_RestoresOwnedIdentity()
    {
        var state = CreateState();
        var owner = CreateOwner(state.Directive, targetMemoryPriority: 3);
        const string currentSoftwareId = "adapted.editor.example";
        var currentFact = state.ProcessFacts.Processes[0] with
        {
            SoftwareId = currentSoftwareId
        };
        var currentFacts = state.ProcessFacts with { Processes = [currentFact] };
        var batch = new HostManagerNonAdaptedMemoryExecutionBatch(
            SchedulingGeneration,
            InventoryGeneration,
            0,
            []);

        var restore = Assert.Single(
            HostManagerNonAdaptedMemoryModeExecutionAdmission
                .CreateProjectionGapRestores(
                    batch,
                    currentFacts,
                    CreateMemoryOwnershipIndex(owner)));

        Assert.Equal(
            HostManagerNonAdaptedMemoryDirectiveDecisionKind
                .AttributionDriftRestoreToBaseline,
            restore.Decision.Kind);
        Assert.Equal(
            NativeStableIdentity.CreateCaseInsensitiveKey(currentSoftwareId),
            restore.Decision.DesiredDirective.SoftwareKey);
        Assert.Equal(owner.Primary.SoftwareId, restore.Ownership.Primary.SoftwareId);
    }

    [Fact]
    public void LiveOwnedProcessAlreadyRepresented_DoesNotCreateGapRestore()
    {
        var state = CreateState();
        var owner = CreateOwner(state.Directive, targetMemoryPriority: 3);
        var batch = new HostManagerNonAdaptedMemoryExecutionBatch(
            SchedulingGeneration,
            InventoryGeneration,
            CpuSourceGeneration,
            [state.Directive]);

        var restores = HostManagerNonAdaptedMemoryModeExecutionAdmission
            .CreateProjectionGapRestores(
                batch,
                state.ProcessFacts,
                CreateMemoryOwnershipIndex(owner));

        Assert.Empty(restores);
    }

    [Fact]
    public void OwnedProcessMissingFromCurrentSnapshot_RestoresFromDurableOwnership()
    {
        var state = CreateState();
        var owner = CreateOwner(state.Directive, targetMemoryPriority: 3);
        var emptyFacts = state.ProcessFacts with
        {
            EnumeratedCount = 0,
            EmittedCount = 0,
            Processes = []
        };
        var batch = new HostManagerNonAdaptedMemoryExecutionBatch(
            SchedulingGeneration,
            InventoryGeneration,
            0,
            []);

        var restore = Assert.Single(
            HostManagerNonAdaptedMemoryModeExecutionAdmission
                .CreateProjectionGapRestores(
                    batch,
                    emptyFacts,
                    CreateMemoryOwnershipIndex(owner)));

        Assert.Equal(
            HostManagerNonAdaptedMemoryDirectiveDecisionKind.RestoreToBaseline,
            restore.Decision.Kind);
        Assert.Equal(owner.Primary.SoftwareId, restore.Decision.TransactionDirective.SoftwareKey);
        Assert.Null(restore.Decision.TransactionDirective.SoftwareId);
        Assert.Equal(ProcessId, restore.Decision.TransactionDirective.ProcessId);
        Assert.Equal(ProcessStartKey, restore.Decision.TransactionDirective.ProcessStartKey);
        Assert.Equal(3U, restore.Decision.CurrentOwnedMemoryPriority);
    }

    [Fact]
    public void ProjectionGapRestore_ProcessGenerationDriftFailsClosed()
    {
        var state = CreateState();
        var owner = CreateOwner(state.Directive, targetMemoryPriority: 3);
        var batch = new HostManagerNonAdaptedMemoryExecutionBatch(
            SchedulingGeneration,
            InventoryGeneration + 1,
            0,
            []);

        Assert.Throws<InvalidDataException>(() =>
            HostManagerNonAdaptedMemoryModeExecutionAdmission
                .CreateProjectionGapRestores(
                    batch,
                    state.ProcessFacts,
                    CreateMemoryOwnershipIndex(owner)));
    }

    private static TestState CreateState(
        NativeMemoryMode mode = NativeMemoryMode.Optimize,
        HostManagerNonAdaptedMemoryProcessAction action =
            HostManagerNonAdaptedMemoryProcessAction.Optimize,
        uint? targetMemoryPriority = 3)
    {
        var binding = CreateBinding();
        var softwareKey = NativeStableIdentity.CreateCaseInsensitiveKey(SoftwareId);
        var memoryTarget = NativeStableIdentity.CreateCaseInsensitiveKey(
            HostManagerTargetIdentity.CreateProcessMemoryPolicyTargetId(
                ProcessId,
                ProcessStartKey));
        var processTarget = NativeStableIdentity.CreateCaseInsensitiveKey(
            HostManagerTargetIdentity.CreateProcessTargetId(
                ProcessId,
                ProcessStartKey));
        var directive = new HostManagerNonAdaptedMemoryProcessDirective(
            SchedulingGeneration,
            softwareKey,
            SoftwareId,
            ProcessId,
            ProcessStartKey,
            memoryTarget,
            SoftwareRank: 1,
            mode,
            action,
            targetMemoryPriority);
        var fact = new SchedulingProcessFact(
            ProcessId,
            ProcessStartKey,
            "editor",
            "C:\\editor.exe",
            SoftwareId,
            "Editor",
            "desktop",
            "Desktop",
            BaseScore: 1,
            SchedulingProcessMetricMask.CpuUsage,
            CpuUsagePercent: 2,
            MemoryUsagePercent: 0,
            InventoryGeneration,
            []);
        var processFacts = new SchedulingProcessFactSnapshot(
            SamplingObservationStatus.Current,
            InventoryGeneration,
            ObservedAtUtcTicks: 100,
            EnumeratedCount: 1,
            EmittedCount: 1,
            SkippedCount: 0,
            OverflowCount: 0,
            SchedulingProcessMetricMask.CpuUsage,
            SchedulingProcessMetricMask.CpuUsage,
            [fact])
        {
            DatasetObservations = new Dictionary<
                SchedulingProcessMetricMask,
                SchedulingProcessDatasetObservation>
            {
                [SchedulingProcessMetricMask.CpuUsage] =
                    SchedulingProcessDatasetObservation.CreateCurrent(
                        SchedulingProcessMetricMask.CpuUsage,
                        CpuSourceGeneration + 10,
                        100,
                        InventoryGeneration,
                        100)
            }
        };
        var processScore = new HostManagerComputeScore(
            NativeComputeScoringOutputKind.ProcessCpu,
            SchedulingGeneration,
            processTarget,
            softwareKey,
            ProcessId,
            ProcessStartKey,
            AdapterKey: 0,
            Score: 0.2,
            MemberCount: 1,
            NativeComputeScoringRuntimeState.BackgroundProcess);
        var softwareScore = new HostManagerComputeScore(
            NativeComputeScoringOutputKind.SoftwareCpu,
            SchedulingGeneration,
            softwareKey,
            softwareKey,
            ProcessId: 0,
            ProcessStartKey: 0,
            AdapterKey: 0,
            Score: 0.2,
            MemberCount: 1,
            NativeComputeScoringRuntimeState.BackgroundProcess);
        var cpu = new HostManagerComputeScoreDomainSnapshot(
                SchedulingGeneration,
                CpuSourceGeneration,
                TopologyGeneration: 0,
                TopologyFingerprint: 0,
                [processScore, softwareScore]);
        var compute = new HostManagerComputeScoringCycleResult(
            SchedulingGeneration,
            cpu with { SourceIdentity = cpu.SourceIdentity with { InventoryGeneration = InventoryGeneration } },
            Gpu: null);
        var desired = new HostManagerDesiredMemoryMode(
            softwareKey,
            SchedulingGeneration,
            SchedulingGeneration,
            CpuScore: 0.2,
            Rank: 1,
            mode,
            NativeMemoryGradeSet.Known);
        var memoryModes = new HostManagerMemoryModeDesiredSnapshot(
            binding.MemoryModeConfigurationGeneration,
            SchedulingGeneration,
            MemorySource: new HostManagerMemorySourceStamp(30, 40),
            SnapshotGeneration: SchedulingGeneration,
            UnrestrictedAllowed: false,
            OptimizeCount: mode == NativeMemoryMode.Optimize ? 1U : 0U,
            StrongestCount: mode == NativeMemoryMode.PagedFrozen ? 1U : 0U,
            [desired]);
        var authority = new HostManagerSchedulingAuthoritySnapshot(
            SchedulingGeneration,
            HostManagerSchedulingAuthorityAvailability.Ready,
            DateTimeOffset.UnixEpoch.AddSeconds(1),
            binding,
            compute,
            memoryModes,
            HostManagerMemoryModePolicyEvidence.Create(
                binding,
                binding.MemoryModePolicySourceKind,
                binding.MemoryModeConfigurationSha256,
                allowUnrestricted: false),
            UnavailableReason: null);
        var projection = new HostManagerNonAdaptedMemoryModeProjectionSnapshot(
            SchedulingGeneration,
            InventoryGeneration,
            CpuSourceGeneration,
            [directive]);
        return new(binding, authority, projection, processFacts, directive);
    }

    private static HostManagerSchedulingPlanBinding CreateBinding()
        => new(
            HostPublicationSequence: 1,
            HostPlanEpoch: 10,
            HostPlanSha256: new string('A', 64),
            SmartConfigurationGeneration: 11,
            SmartConfigurationSha256: new string('B', 64),
            MemoryModePolicyEnabled: true,
            MemoryModePolicySourceKind:
                HostManagerMemoryModePolicySourceKinds.ProductBaseline,
            MemoryModeConfigurationGeneration: 12,
            MemoryModeConfigurationSha256: new string('C', 64));

    private static NativeAppliedOwnershipRecord CreateOwner(
        HostManagerNonAdaptedMemoryProcessDirective directive,
        uint targetMemoryPriority)
    {
        var action = HostManagerProcessMemoryTransactionProjection.CreateApply(
            directive,
            configurationGeneration: 100,
            actionId: 101,
            hostSessionIncarnation: 102);
        var payload = HostManagerProcessPolicyRollbackPayloadCodec.Encode(
            new HostManagerProcessPolicyRollbackPayload(
                HostManagerProcessPolicyTransactionFields.MemoryPriority,
                ProcessId,
                checked((long)ProcessStartKey),
                BaselinePriorityClass: 0,
                BaselineMemoryPriority: 5,
                BaselinePowerControlMask: 0,
                BaselinePowerStateMask: 0,
                targetMemoryPriority));
        var binding = HostManagerProcessMemoryTransactionProjection.CreatePayloadBinding(
            action,
            payload,
            journalInstanceLow: 201,
            journalInstanceHigh: 202,
            preparedAtUtcMilliseconds: 1_000,
            maximumRecoveryAttempts: 5,
            recoveryDeadlineUtcMilliseconds: 2_000);
        var reference = CreatePayloadReference(payload);
        var prepare = HostManagerProcessMemoryTransactionProjection.CreatePrepare(
            action,
            payload,
            binding,
            binding,
            reference,
            NativeTransactionJournalPayloadProvenance.Create(binding),
            expectedJournalRevision: 7,
            preparedAtUtcMilliseconds: 1_000);
        var record = CreateRecord(prepare);
        var snapshot = new NativeTransactionJournalSnapshotHeader
        {
            AbiVersion = NativeTransactionJournalAbi.Version,
            StructSize = NativeTransactionJournalSession
                .SizeOf<NativeTransactionJournalSnapshotHeader>(),
            JournalRevision = 8,
            JournalInstanceLow = 201,
            JournalInstanceHigh = 202
        };
        var promote = HostManagerProcessMemoryTransactionProjection.CreatePromote(
            action,
            payload,
            snapshot,
            record,
            reference,
            binding,
            expectedLedgerRevision: 9,
            promotedAtUtcMilliseconds: 1_002);
        return new NativeAppliedOwnershipRecord
        {
            Primary = promote.Primary,
            OriginalBinding = promote.OriginalBinding,
            Payload = promote.Payload,
            CurrentGrades = promote.CurrentGrades,
            RecordRevision = 1,
            PromotedAtUtcMilliseconds = promote.PromotedAtUtcMilliseconds,
            UpdatedAtUtcMilliseconds = promote.PromotedAtUtcMilliseconds
        };
    }

    private static NativeAppliedOwnershipFactIndex CreateMemoryOwnershipIndex(
        NativeAppliedOwnershipRecord ownership)
    {
        var identity = new NativeAppliedOwnershipProcessIdentity(
            ownership.Primary.TargetId,
            ownership.Primary.SoftwareId,
            ownership.Primary.ProcessId,
            ownership.Primary.ProcessStartKey);
        var incarnation = new NativeAppliedOwnershipProcessIncarnation(
            ownership.Primary.ProcessId,
            ownership.Primary.ProcessStartKey);
        return new(
            LedgerRevision: 9,
            new Dictionary<
                NativeAppliedOwnershipProcessIdentity,
                NativeAppliedOwnershipRecord>(),
            new Dictionary<
                NativeAppliedOwnershipProcessIdentity,
                NativeAppliedOwnershipRecord>
            {
                [identity] = ownership
            },
            new Dictionary<ulong, NativeAppliedOwnershipRecord>(),
            new Dictionary<
                NativeAppliedOwnershipProcessIncarnation,
                NativeAppliedOwnershipProcessAnchor>
            {
                [incarnation] = new(
                    ownership.Primary.SoftwareId,
                    HasProcessOwnership: false,
                    HasMemoryOwnership: true)
            });
    }

    private static NativeTransactionJournalPayloadReference CreatePayloadReference(
        byte[] payload)
    {
        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(payload, digest);
        return new(
            Slot: 1,
            Generation: 1,
            Length: checked((ulong)payload.Length),
            DigestLow: BinaryPrimitives.ReadUInt64LittleEndian(digest[..8]),
            DigestHigh: BinaryPrimitives.ReadUInt64LittleEndian(digest.Slice(8, 8)));
    }

    private static NativeTransactionJournalRecord CreateRecord(
        in NativeTransactionJournalPrepareInput prepare)
        => new()
        {
            Identity = prepare.Identity,
            Scope = prepare.Scope,
            Disposition = prepare.Disposition,
            DomainMask = prepare.DomainMask,
            GradeValidMask = prepare.GradeValidMask,
            ProcessFromGrade = prepare.ProcessFromGrade,
            ProcessToGrade = prepare.ProcessToGrade,
            CpuFromGrade = prepare.CpuFromGrade,
            CpuToGrade = prepare.CpuToGrade,
            GpuFromGrade = prepare.GpuFromGrade,
            GpuToGrade = prepare.GpuToGrade,
            StableSystemStatus = prepare.StableSystemStatus,
            StableSystemError = prepare.StableSystemError,
            PayloadKind = prepare.PayloadKind,
            PayloadSlot = prepare.PayloadSlot,
            PayloadGeneration = prepare.PayloadGeneration,
            PayloadLength = prepare.PayloadLength,
            PayloadDigestLow = prepare.PayloadDigestLow,
            PayloadDigestHigh = prepare.PayloadDigestHigh,
            PayloadProvenanceDigestLow = prepare.PayloadProvenanceDigestLow,
            PayloadProvenanceDigestHigh = prepare.PayloadProvenanceDigestHigh,
            PreparedAtUtcMilliseconds = prepare.NowUtcMilliseconds,
            UpdatedAtUtcMilliseconds = prepare.NowUtcMilliseconds + 1,
            Phase = (uint)NativeTransactionJournalPhase.EffectObserved,
            EntryRevision = 1,
            MaximumRecoveryAttempts = prepare.MaximumRecoveryAttempts,
            RecoveryDeadlineUtcMilliseconds = prepare.RecoveryDeadlineUtcMilliseconds
        };

    private sealed record TestState(
        HostManagerSchedulingPlanBinding Binding,
        HostManagerSchedulingAuthoritySnapshot Authority,
        HostManagerNonAdaptedMemoryModeProjectionSnapshot Projection,
        SchedulingProcessFactSnapshot ProcessFacts,
        HostManagerNonAdaptedMemoryProcessDirective Directive);
}
