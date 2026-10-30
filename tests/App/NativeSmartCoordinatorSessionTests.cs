using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization;

namespace Resource_Manager_APP.Tests;

public sealed class NativeSmartCoordinatorSessionTests
{
    [Fact]
    public void CapacityQueryReturnsExactCallerSuppliedShape()
    {
        var configuration = CreateConfiguration();

        var result = NativeSmartCoordinatorSession.CapacityForConfiguration(
            in configuration,
            out var capacity);

        Assert.Equal(NativeSmartCoordinatorStatus.Ok, result);
        Assert.Equal(NativeSmartCoordinatorAbi.Version, capacity.AbiVersion);
        Assert.Equal(configuration.Generation, capacity.ConfigurationGeneration);
        Assert.Equal(configuration.MaximumInputRows, capacity.InputRowCapacity);
        Assert.Equal(configuration.MaximumActions, capacity.ActionCapacity);
        Assert.Equal(configuration.MaximumReservations, capacity.FeedbackCapacity);
        Assert.Equal(
            configuration.MaximumProcesses + configuration.MaximumSoftwareGroups,
            capacity.SnapshotRowCapacity);
        Assert.Equal(configuration.MaximumProcesses, capacity.ProcessCapacity);
        Assert.Equal(configuration.MaximumSoftwareGroups, capacity.SoftwareCapacity);
        Assert.Equal(configuration.MaximumGpuStates, capacity.GpuStateCapacity);
        Assert.Equal(configuration.MaximumAtomicGroups, capacity.AtomicGroupCapacity);
    }

    [Fact]
    public void IncompleteConfigurationIsRejectedWithoutCreatingFallbackState()
    {
        var configuration = CreateConfiguration();
        configuration.FieldMask &= ~NativeSmartCoordinatorConfigFields.Features;

        var result = NativeSmartCoordinatorSession.CapacityForConfiguration(
            in configuration,
            out var capacity);

        Assert.Equal(NativeSmartCoordinatorStatus.InvalidArgument, result);
        Assert.Equal(0u, capacity.AbiVersion);
    }



    [Fact]
    public void PreviousProtectionSemanticsAbiIsRejected()
    {
        var configuration = CreateConfiguration();
        configuration.AbiVersion = 0x000c_0001;

        var result = NativeSmartCoordinatorSession.CapacityForConfiguration(in configuration, out var capacity);

        Assert.Equal(NativeSmartCoordinatorStatus.AbiMismatch, result);
        Assert.Equal(0u, capacity.AbiVersion);
    }

    [Fact]
    public void RetiredStaticScoreOnlyFeatureBitIsRejected()
    {
        var configuration = CreateConfiguration();
        configuration.FeatureFlags |= (NativeSmartCoordinatorFeatures)(1UL << 4);

        var result = NativeSmartCoordinatorSession.CapacityForConfiguration(
            in configuration,
            out var capacity);

        Assert.Equal(NativeSmartCoordinatorStatus.InvalidArgument, result);
        Assert.Equal(0u, capacity.AbiVersion);
    }

    [Fact]
    public void SessionOwnsCapacityResetAndSnapshotState()
    {
        var configuration = CreateConfiguration();
        using var session = new NativeSmartCoordinatorSession(in configuration);

        var capacity = session.Capacity;
        Assert.Equal(configuration.Generation, capacity.ConfigurationGeneration);

        var rows = new NativeSmartCoordinatorSnapshotRow[capacity.SnapshotRowCapacity];
        var snapshot = default(NativeSmartCoordinatorSnapshot);
        Assert.Equal(
            NativeSmartCoordinatorStatus.Ok,
            session.GetSnapshot(ref snapshot, rows));
        Assert.Equal(configuration.Generation, snapshot.ConfigurationGeneration);
        Assert.Equal(0u, snapshot.ProcessCount);
        Assert.Equal(0u, snapshot.SoftwareCount);
        Assert.Equal(0u, snapshot.SnapshotRowCount);

        Assert.Equal(NativeSmartCoordinatorStatus.Ok, session.Reset());
        snapshot = default;
        Assert.Equal(
            NativeSmartCoordinatorStatus.Ok,
            session.GetSnapshot(ref snapshot, rows));
        Assert.Equal(0u, snapshot.ActionCount);
        Assert.Equal(0u, snapshot.InflightCount);
    }

    [Fact]
    public void ReconfigureAcceptsSameGenerationAndRollbackWithStableCapacityShape()
    {
        var configuration = CreateConfiguration();
        using var session = new NativeSmartCoordinatorSession(in configuration);

        Assert.Equal(
            NativeSmartCoordinatorStatus.Ok,
            session.Reconfigure(in configuration));

        var resized = configuration;
        resized.Generation = 2;
        resized.MaximumProcesses++;
        resized.MaximumActions++;
        resized.MaximumReservations++;
        resized.MaximumAtomicGroups++;
        Assert.Equal(
            NativeSmartCoordinatorStatus.RecreateRequired,
            session.Reconfigure(in resized));

        var replacement = configuration;
        replacement.Generation = 2;
        replacement.NormalIntervalMilliseconds = 750;
        Assert.Equal(
            NativeSmartCoordinatorStatus.Ok,
            session.Reconfigure(in replacement));
        Assert.Equal(2UL, session.Capacity.ConfigurationGeneration);
        Assert.Equal(NativeSmartCoordinatorStatus.Ok, session.Reconfigure(in configuration));
        Assert.Equal(configuration.Generation, session.Capacity.ConfigurationGeneration);
        var snapshot = default(NativeSmartCoordinatorSnapshot);
        var rows = new NativeSmartCoordinatorSnapshotRow[session.Capacity.SnapshotRowCapacity];
        Assert.Equal(NativeSmartCoordinatorStatus.Ok, session.GetSnapshot(ref snapshot, rows));
        Assert.Equal(configuration.Generation, snapshot.ConfigurationGeneration);
    }

    [Fact]
    public void ScoreOnlyAndActionLimitAreRequiredExplicitCycleInputs()
    {
        var configuration = CreateConfiguration();
        using var session = new NativeSmartCoordinatorSession(in configuration);
        var actions = new NativeSmartCoordinatorAction[session.Capacity.ActionCapacity];
        var snapshot = default(NativeSmartCoordinatorSnapshot);

        var missingMode = CreateEmptyCycleInput(
            configuration.Generation,
            session.Capacity.ActionCapacity,
            cycleSequence: 1,
            authoritative: true);
        missingMode.ValidMask &= ~NativeSmartCoordinatorCycleValidity.ScoreOnlyMode;
        Assert.Equal(
            NativeSmartCoordinatorStatus.InvalidArgument,
            session.PlanCurrent(
                in missingMode,
                ReadOnlySpan<NativeSmartCoordinatorInputRow>.Empty,
                actions,
                ref snapshot));

        var scoreOnly = missingMode;
        scoreOnly.ValidMask |= NativeSmartCoordinatorCycleValidity.ScoreOnlyMode;
        scoreOnly.ValidMask &= ~NativeSmartCoordinatorCycleValidity.MaximumActionsThisCycle;
        Assert.Equal(
            NativeSmartCoordinatorStatus.InvalidArgument,
            session.PlanCurrent(
                in scoreOnly,
                ReadOnlySpan<NativeSmartCoordinatorInputRow>.Empty,
                actions,
                ref snapshot));

        scoreOnly.ValidMask |= NativeSmartCoordinatorCycleValidity.MaximumActionsThisCycle;
        scoreOnly.MaximumActionsThisCycle = session.Capacity.ActionCapacity + 1;
        Assert.Equal(
            NativeSmartCoordinatorStatus.InvalidArgument,
            session.PlanCurrent(
                in scoreOnly,
                ReadOnlySpan<NativeSmartCoordinatorInputRow>.Empty,
                actions,
                ref snapshot));

        scoreOnly.MaximumActionsThisCycle = session.Capacity.ActionCapacity;
        scoreOnly.Flags |= NativeSmartCoordinatorCycleFlags.ScoreOnly;
        Assert.Equal(
            NativeSmartCoordinatorStatus.Ok,
            session.PlanCurrent(
                in scoreOnly,
                ReadOnlySpan<NativeSmartCoordinatorInputRow>.Empty,
                actions,
                ref snapshot));
        Assert.True(snapshot.Flags.HasFlag(NativeSmartCoordinatorSnapshotFlags.ScoreOnly));

        var normal = CreateEmptyCycleInput(
            configuration.Generation,
            session.Capacity.ActionCapacity,
            cycleSequence: 2,
            authoritative: false);
        Assert.Equal(
            NativeSmartCoordinatorStatus.Ok,
            session.PlanCurrent(
                in normal,
                ReadOnlySpan<NativeSmartCoordinatorInputRow>.Empty,
                actions,
                ref snapshot));
        Assert.False(snapshot.Flags.HasFlag(NativeSmartCoordinatorSnapshotFlags.ScoreOnly));
    }

    [Fact]
    public void FirstPlanWithoutAuthoritativeAppliedFactsIsExplicitlyRejected()
    {
        var configuration = CreateConfiguration();
        using var session = new NativeSmartCoordinatorSession(in configuration);
        var input = new NativeSmartCoordinatorCycleInput
        {
            AbiVersion = NativeSmartCoordinatorAbi.Version,
            StructSize = NativeSmartCoordinatorSession.SizeOf<NativeSmartCoordinatorCycleInput>(),
            InputRowStructSize = NativeSmartCoordinatorSession.SizeOf<NativeSmartCoordinatorInputRow>(),
            ActionStructSize = NativeSmartCoordinatorSession.SizeOf<NativeSmartCoordinatorAction>(),
            ConfigurationGeneration = configuration.Generation,
            CycleSequence = 1,
            ObservedAtMilliseconds = 1_000,
            ValidMask = NativeSmartCoordinatorCycleValidity.Known,
            Flags = NativeSmartCoordinatorCycleFlags.FullProcessSnapshot,
            InputCount = 0,
            ActionCapacity = session.Capacity.ActionCapacity,
            RoundNumber = 1,
            MaximumActionsThisCycle = session.Capacity.ActionCapacity
        };
        var actions = new NativeSmartCoordinatorAction[session.Capacity.ActionCapacity];
        var snapshot = default(NativeSmartCoordinatorSnapshot);

        var result = session.PlanCurrent(
            in input,
            ReadOnlySpan<NativeSmartCoordinatorInputRow>.Empty,
            actions,
            ref snapshot);

        Assert.Equal(NativeSmartCoordinatorStatus.InvalidArgument, result);
    }

    [Fact]
    public void SnapshotHotPathDoesNotAllocateManagedMemory()
    {
        var configuration = CreateConfiguration();
        using var session = new NativeSmartCoordinatorSession(in configuration);
        var rows = new NativeSmartCoordinatorSnapshotRow[session.Capacity.SnapshotRowCapacity];
        var snapshot = default(NativeSmartCoordinatorSnapshot);
        for (var index = 0; index < 32; index++)
        {
            if (session.GetSnapshot(ref snapshot, rows) != NativeSmartCoordinatorStatus.Ok)
            {
                throw new InvalidOperationException("Native smart coordinator snapshot warmup failed.");
            }
        }

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var result = NativeSmartCoordinatorStatus.Ok;
        for (var index = 0; index < 1_000; index++)
        {
            result = session.GetSnapshot(ref snapshot, rows);
            if (result != NativeSmartCoordinatorStatus.Ok)
            {
                break;
            }
        }
        var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Assert.Equal(NativeSmartCoordinatorStatus.Ok, result);
        Assert.Equal(0, allocatedBytes);
    }

    [Fact]
    public void WorkspaceUsesOnlyNativeCapacityAndExplicitCounts()
    {
        var configuration = CreateConfiguration();
        using var workspace = new NativeSmartCoordinatorWorkspace(in configuration, OperationScratchFixture.Create);
        workspace.EnsureRows(Math.Min(8U, workspace.Capacity.InputRowCapacity));
        using var operation = workspace.BeginOperation();
        using var buffers = workspace.CreateScratchView();
        Assert.Equal(configuration.MaximumInputRows, (uint)buffers.Inputs.Length);
        // Feedback opens only for a plan's issued actions; its ceiling stays the native capacity.
        Assert.Null(workspace.FeedbackLayout);
        Assert.Equal(configuration.MaximumReservations, workspace.Capacity.FeedbackCapacity);

        var input = CreateEmptyCycleInput(
            configuration.Generation,
            workspace.Capacity.ActionCapacity,
            cycleSequence: 1,
            authoritative: true);
        Assert.Equal(NativeSmartCoordinatorStatus.Ok, workspace.PlanCurrent(in input));
        Assert.Empty(workspace.PlannedActions());
        Assert.Equal(0u, workspace.Snapshot.ActionCount);

        Assert.Equal(NativeSmartCoordinatorStatus.Ok, workspace.RefreshSnapshot());
        Assert.Empty(workspace.CurrentSnapshotRows.ToArray());
        HostManagerSmartCoordinator.ValidateNativeSnapshotRows(
            workspace.CurrentSnapshotRows,
            workspace.Snapshot);
        Assert.Equal(NativeSmartCoordinatorStatus.NoData, workspace.ApplyFeedback(0));

        input.InputCount = workspace.Capacity.InputRowCapacity + 1;
        Assert.Equal(NativeSmartCoordinatorStatus.BufferTooSmall, workspace.PlanCurrent(in input));
        Assert.Equal(0u, workspace.Snapshot.ActionCount);
    }

    [Fact]
    public void WorkspacePlanHotPathDoesNotAllocateManagedMemory()
    {
        var configuration = CreateConfiguration();
        using var workspace = new NativeSmartCoordinatorWorkspace(in configuration, OperationScratchFixture.Create);
        workspace.EnsureRows(Math.Min(8U, workspace.Capacity.InputRowCapacity));
        using var operation = workspace.BeginOperation();
        using var buffers = workspace.CreateScratchView();
        var input = CreateEmptyCycleInput(
            configuration.Generation,
            workspace.Capacity.ActionCapacity,
            cycleSequence: 1,
            authoritative: true);
        Assert.Equal(NativeSmartCoordinatorStatus.Ok, workspace.PlanCurrent(in input));

        input.Flags &= ~NativeSmartCoordinatorCycleFlags.AuthoritativeAppliedFacts;
        for (var index = 0; index < 32; index++)
        {
            input.CycleSequence++;
            input.ObservedAtMilliseconds++;
            if (workspace.PlanCurrent(in input) != NativeSmartCoordinatorStatus.Ok)
            {
                throw new InvalidOperationException("Native smart coordinator workspace warmup failed.");
            }
        }

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var result = NativeSmartCoordinatorStatus.Ok;
        for (var index = 0; index < 1_000; index++)
        {
            input.CycleSequence++;
            input.ObservedAtMilliseconds++;
            result = workspace.PlanCurrent(in input);
            if (result != NativeSmartCoordinatorStatus.Ok)
            {
                break;
            }
        }
        var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Assert.Equal(NativeSmartCoordinatorStatus.Ok, result);
        Assert.Equal(0, allocatedBytes);
    }

    [Fact]
    public void ConfigurationWriterRejectsPartialStateVectors()
    {
        var exception = Assert.Throws<ArgumentException>(static () =>
        {
            var configuration = CreateConfiguration();
            NativeSmartCoordinatorConfigurationWriter.SetProcessStateMultipliers(
                ref configuration,
                new double[NativeSmartCoordinatorAbi.RuntimeStateCount - 1]);
        });

        Assert.Equal("values", exception.ParamName);
    }

    [Fact]
    public void ProcessFactsPlanFeedbackAndSnapshotRoundTripThroughNativeState()
    {
        var configuration = CreateProcessPolicyConfiguration();
        using var workspace = new NativeSmartCoordinatorWorkspace(in configuration, OperationScratchFixture.Create);
        workspace.EnsureRows(Math.Min(8U, workspace.Capacity.InputRowCapacity));
        using var operation = workspace.BeginOperation();
        using var buffers = workspace.CreateScratchView();
        var process = CreateProcessFact();
        buffers.Inputs[0] = process;
        buffers.Inputs[1] = CreateMetricFact(
            in process,
            sourceIndex: 1,
            NativeSmartCoordinatorMetricKind.CpuUsagePercent,
            value: 50);
        var appliedAction = default(NativeSmartCoordinatorAction);
        for (ulong sequence = 1; sequence <= 3; sequence++)
        {
            var input = CreateEmptyCycleInput(
                configuration.Generation,
                workspace.Capacity.ActionCapacity,
                sequence,
                authoritative: sequence == 1);
            input.InputCount = 2;
            input.ObservedAtMilliseconds = checked((long)sequence * 1_000);
            Assert.Equal(NativeSmartCoordinatorStatus.Ok, workspace.PlanCurrent(in input));
            _ = HostManagerSmartCoordinator.ValidateNativeActionBatch(workspace);
            Assert.True(TryFindProcessAction(workspace.PlannedActions(), process.TargetKey, out var action));
            if (sequence < 3)
            {
                Assert.Equal(
                    NativeSmartCoordinatorActionFlags.None,
                    action.Flags & NativeSmartCoordinatorActionFlags.RequiresFeedback);
                continue;
            }

            appliedAction = action;
        }

        Assert.Equal(NativeSmartCoordinatorActionDisposition.Apply, appliedAction.Disposition);
        Assert.Equal(NativeSmartCoordinatorProcessGrade.Level4, appliedAction.ToProcessGrade);
        Assert.NotEqual(
            NativeSmartCoordinatorActionFlags.None,
            appliedAction.Flags & NativeSmartCoordinatorActionFlags.RequiresFeedback);
        Assert.Equal(
            NativeSmartCoordinatorActionFlags.None,
            appliedAction.Flags & NativeSmartCoordinatorActionFlags.Atomic);
        Assert.Equal(0UL, appliedAction.AtomicGroupId);
        Assert.Equal(0U, appliedAction.GroupMemberIndex);
        Assert.Equal(0U, appliedAction.GroupMemberCount);

        workspace.SetFeedback(0, CreateSucceededFeedback(in appliedAction, completedAtMilliseconds: 3_100));
        Assert.Equal(NativeSmartCoordinatorStatus.Ok, workspace.ApplyFeedback(1));
        Assert.Equal(NativeSmartCoordinatorStatus.Ok, workspace.RefreshSnapshot());
        HostManagerSmartCoordinator.ValidateNativeSnapshotRows(
            workspace.CurrentSnapshotRows,
            workspace.Snapshot);
        Assert.True(TryFindProcessSnapshot(
            workspace.CurrentSnapshotRows,
            process.TargetKey,
            out var snapshotRow));
        Assert.Equal(NativeSmartCoordinatorProcessGrade.Level4, snapshotRow.AppliedProcessGrade);
        Assert.NotEqual(
            NativeSmartCoordinatorSnapshotRowFlags.None,
            snapshotRow.Flags & NativeSmartCoordinatorSnapshotRowFlags.ProcessOwned);
        Assert.Equal(0u, workspace.Snapshot.InflightCount);
    }





    [Theory]
    [InlineData(2)]
    [InlineData(255)]
    public void RetiredOrUnknownProtectionCannotBecomeNativePlanningFacts(byte protectionLevel)
    {
        var configuration = CreateProcessPolicyConfiguration();
        using var workspace = new NativeSmartCoordinatorWorkspace(in configuration, OperationScratchFixture.Create);
        workspace.EnsureRows(2);
        using var operation = workspace.BeginOperation();
        using var buffers = workspace.CreateScratchView();
        var process = CreateProcessFact();
        process.ProtectionLevel = protectionLevel;
        buffers.Inputs[0] = process;
        buffers.Inputs[1] = CreateMetricFact(in process, 1, NativeSmartCoordinatorMetricKind.CpuUsagePercent, 50);
        var input = CreateEmptyCycleInput(configuration.Generation, workspace.Capacity.ActionCapacity, 1, authoritative: true);
        input.InputCount = 2;

        Assert.Equal(NativeSmartCoordinatorStatus.Ok, workspace.PlanCurrent(in input));
        Assert.True(workspace.Snapshot.InvalidFactCount >= 2);
        Assert.Equal(1u, workspace.PlannedActionCount);
        Assert.Equal(NativeSmartCoordinatorActionFlags.None,
            workspace.GetPlannedAction(0).Flags & NativeSmartCoordinatorActionFlags.RequiresFeedback);
        Assert.Equal(NativeSmartCoordinatorStatus.Ok, workspace.RefreshSnapshot());
        Assert.True(TryFindProcessSnapshot(workspace.CurrentSnapshotRows, process.TargetKey, out var row));
        Assert.Equal(0, row.ProtectionLevel);
        Assert.Equal(NativeSmartCoordinatorInputValidity.None, row.ValidMask & NativeSmartCoordinatorInputValidity.Protection);
    }

    [Theory]
    [InlineData(0, -4)]
    [InlineData(1, -3)]
    public void NoFreezeProtectionKeepsLowScoreOptimizationEligible(byte protectionLevel, sbyte expectedGrade)
    {
        var configuration = CreateProcessPolicyConfiguration();
        configuration.RequiredConsecutiveDecisions = 1;
        using var workspace = new NativeSmartCoordinatorWorkspace(in configuration, OperationScratchFixture.Create);
        workspace.EnsureRows(2);
        using var operation = workspace.BeginOperation();
        using var buffers = workspace.CreateScratchView();
        var process = CreateProcessFact();
        process.ProtectionLevel = protectionLevel;
        buffers.Inputs[0] = process;
        buffers.Inputs[1] = CreateMetricFact(in process, 1, NativeSmartCoordinatorMetricKind.CpuUsagePercent, 50);
        var input = CreateEmptyCycleInput(configuration.Generation, workspace.Capacity.ActionCapacity, 1, authoritative: true);
        input.InputCount = 2;

        Assert.Equal(NativeSmartCoordinatorStatus.Ok, workspace.PlanCurrent(in input));
        Assert.Equal(1u, HostManagerSmartCoordinator.ValidateNativeActionBatch(workspace));
        Assert.True(TryFindProcessAction(workspace.PlannedActions(), process.TargetKey, out var action));
        Assert.Equal((NativeSmartCoordinatorProcessGrade)expectedGrade, action.ToProcessGrade);
        Assert.Equal(protectionLevel == 1,
            action.ReasonMask.HasFlag(NativeSmartCoordinatorReason.ProtectionLevel1ClampsFreeze));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ZeroActionBudgetDoesNotBecomeScoreOnly(bool explicitScoreOnly)
    {
        var configuration = CreateProcessPolicyConfiguration();
        using var workspace = new NativeSmartCoordinatorWorkspace(in configuration, OperationScratchFixture.Create);
        workspace.EnsureRows(Math.Min(8U, workspace.Capacity.InputRowCapacity));
        using var operation = workspace.BeginOperation();
        using var buffers = workspace.CreateScratchView();
        var process = CreateProcessFact();
        process.BaseScore = 40;
        buffers.Inputs[0] = process;
        buffers.Inputs[1] = CreateMetricFact(
            in process, 1, NativeSmartCoordinatorMetricKind.CpuUsagePercent, 1);

        for (ulong sequence = 1; sequence <= 4; sequence++)
        {
            var cycle = CreateEmptyCycleInput(configuration.Generation,
                workspace.Capacity.ActionCapacity, sequence, authoritative: sequence == 1);
            cycle.InputCount = 2;
            cycle.ObservedAtMilliseconds = checked((long)sequence * 1_000);
            cycle.MaximumActionsThisCycle = sequence <= 3 ? 0u : 1u;
            if (explicitScoreOnly && sequence <= 3)
            {
                cycle.Flags |= NativeSmartCoordinatorCycleFlags.ScoreOnly;
            }
            Assert.Equal(NativeSmartCoordinatorStatus.Ok, workspace.PlanCurrent(in cycle));
            var feedbackCount = HostManagerSmartCoordinator.ValidateNativeActionBatch(workspace);
            Assert.Equal(NativeSmartCoordinatorStatus.Ok, workspace.RefreshSnapshot());
            Assert.True(TryFindProcessSnapshot(workspace.CurrentSnapshotRows,
                process.TargetKey, out var row));
            if (sequence <= 3)
            {
                Assert.Empty(workspace.PlannedActions());
                Assert.Equal(0u, workspace.Snapshot.InflightCount);
                Assert.Equal(explicitScoreOnly, workspace.Snapshot.Flags.HasFlag(
                    NativeSmartCoordinatorSnapshotFlags.ScoreOnly));
                Assert.Equal(explicitScoreOnly ? 0u : (uint)sequence,
                    row.ProcessPendingCount);
            }
            else
            {
                Assert.Equal(explicitScoreOnly ? 0u : 1u,
                    feedbackCount);
            }
        }
    }

    private static NativeSmartCoordinatorInputRow CreateProcessFact()
        => new()
        {
            StructSize = NativeSmartCoordinatorSession.SizeOf<NativeSmartCoordinatorInputRow>(),
            MetricKind = NativeSmartCoordinatorMetricKind.None,
            SoftwareKind = NativeSmartCoordinatorSoftwareKind.GeneralApplication,
            ProtectionLevel = 0,
            ValidMask = NativeSmartCoordinatorInputValidity.ProcessIdentity |
                NativeSmartCoordinatorInputValidity.SoftwareIdentity |
                NativeSmartCoordinatorInputValidity.SoftwareKind |
                NativeSmartCoordinatorInputValidity.BaseScore |
                NativeSmartCoordinatorInputValidity.SurfaceFacts |
                NativeSmartCoordinatorInputValidity.Eligibility |
                NativeSmartCoordinatorInputValidity.Protection |
                NativeSmartCoordinatorInputValidity.AppliedProcessGrade,
            Flags = NativeSmartCoordinatorInputFlags.Running |
                NativeSmartCoordinatorInputFlags.ProcessCpuMetricsComplete |
                NativeSmartCoordinatorInputFlags.ProcessGpuMetricsComplete |
                NativeSmartCoordinatorInputFlags.CanApplyProcessPolicy |
                NativeSmartCoordinatorInputFlags.HardwareSchedulingEligible,
            TargetKey = 101,
            SoftwareKey = 1_001,
            ProcessStartKey = 111,
            BaseScore = 20,
            SourceIndex = 0,
            ProcessId = 11,
            AppliedProcessGrade = NativeSmartCoordinatorProcessGrade.Normal,
        };

    private static NativeSmartCoordinatorInputRow CreateMetricFact(
        in NativeSmartCoordinatorInputRow process,
        uint sourceIndex,
        NativeSmartCoordinatorMetricKind metricKind,
        double value)
    {
        var row = process;
        row.SourceIndex = sourceIndex;
        row.MetricKind = metricKind;
        row.MetricValue = value;
        row.ValidMask |= NativeSmartCoordinatorInputValidity.Metric |
            NativeSmartCoordinatorInputValidity.ProcessScore |
            NativeSmartCoordinatorInputValidity.SoftwareScore |
            NativeSmartCoordinatorInputValidity.ScoreMemberCount;
        row.ScoreMemberCount = 1;
        row.ProcessScore = process.BaseScore * 0.45 * (value / 100);
        row.SoftwareScore = row.ProcessScore;
        return row;
    }

    private static NativeSmartCoordinatorFeedback CreateSucceededFeedback(
        in NativeSmartCoordinatorAction action,
        long completedAtMilliseconds)
        => new()
        {
            StructSize = NativeSmartCoordinatorSession.SizeOf<NativeSmartCoordinatorFeedback>(),
            Flags = NativeSmartCoordinatorFeedbackFlags.ProcessOwned |
                NativeSmartCoordinatorFeedbackFlags.RollbackPayloadPersisted,
            ValidMask = NativeSmartCoordinatorFeedbackValidity.CompletedAt |
                NativeSmartCoordinatorFeedbackValidity.ActualProcessGrade,
            ActionId = action.ActionId,
            PlanEpoch = action.PlanEpoch,
            ConfigurationGeneration = action.ConfigurationGeneration,
            TargetKey = action.TargetKey,
            SoftwareKey = action.SoftwareKey,
            ProcessStartKey = action.ProcessStartKey,
            CompletedAtMilliseconds = completedAtMilliseconds,
            ProcessId = action.ProcessId,
            SystemErrorCode = 0,
            Status = NativeSmartCoordinatorFeedbackStatus.Succeeded,
            Scope = action.Scope,
            ActualProcessGrade = action.ToProcessGrade
        };

    private static bool TryFindProcessAction(
        ReadOnlySpan<NativeSmartCoordinatorAction> actions,
        ulong targetKey,
        out NativeSmartCoordinatorAction result)
    {
        for (var index = 0; index < actions.Length; index++)
        {
            var action = actions[index];
            if (action.Scope == NativeSmartCoordinatorActionScope.ProcessPolicy
                && action.TargetKey == targetKey)
            {
                result = action;
                return true;
            }
        }

        result = default;
        return false;
    }

    private static bool TryFindProcessSnapshot(
        ReadOnlySpan<NativeSmartCoordinatorSnapshotRow> rows,
        ulong targetKey,
        out NativeSmartCoordinatorSnapshotRow result)
    {
        for (var index = 0; index < rows.Length; index++)
        {
            var row = rows[index];
            if (row.RowKind == NativeSmartCoordinatorSnapshotRowKind.Process
                && row.TargetKey == targetKey)
            {
                result = row;
                return true;
            }
        }

        result = default;
        return false;
    }

    private static NativeSmartCoordinatorCycleInput CreateEmptyCycleInput(
        ulong configurationGeneration,
        uint actionCapacity,
        ulong cycleSequence,
        bool authoritative)
        => new()
        {
            AbiVersion = NativeSmartCoordinatorAbi.Version,
            StructSize = NativeSmartCoordinatorSession.SizeOf<NativeSmartCoordinatorCycleInput>(),
            InputRowStructSize = NativeSmartCoordinatorSession.SizeOf<NativeSmartCoordinatorInputRow>(),
            ActionStructSize = NativeSmartCoordinatorSession.SizeOf<NativeSmartCoordinatorAction>(),
            ConfigurationGeneration = configurationGeneration,
            CycleSequence = cycleSequence,
            ObservedAtMilliseconds = 1_000,
            ValidMask = NativeSmartCoordinatorCycleValidity.Known,
            Flags = NativeSmartCoordinatorCycleFlags.FullProcessSnapshot |
                (authoritative
                    ? NativeSmartCoordinatorCycleFlags.AuthoritativeAppliedFacts
                    : NativeSmartCoordinatorCycleFlags.None),
            InputCount = 0,
            ActionCapacity = actionCapacity,
            RoundNumber = cycleSequence,
            MaximumActionsThisCycle = actionCapacity
        };

    [Fact]
    public void SmartScratchLayoutUsesCheckedTypedExtentsWithoutAByteCountCap()
    {
        Assert.Equal(104, System.Runtime.InteropServices.Marshal.SizeOf<NativeSmartCoordinatorInputRow>());
        Assert.Equal(120, System.Runtime.InteropServices.Marshal.SizeOf<NativeSmartCoordinatorAction>());
        Assert.Equal(96, System.Runtime.InteropServices.Marshal.SizeOf<NativeSmartCoordinatorFeedback>());
        // Facts, plan and feedback are three regions; each keeps its own typed columns.
        var facts = new NativeSmartFactsLayout(64, 5);
        Assert.Equal(0UL, facts.Inputs);
        Assert.Equal(64UL * 104, facts.MeasureTable);
        Assert.Equal(64UL * 104 + 24, facts.Length); // Five u32 slots end on the next 8-byte boundary.
        var plan = new NativeSmartPlanLayout(20_000_000, 4096, 64);
        Assert.Equal(0UL, plan.Actions);
        Assert.Equal(20_000_000UL * 120, plan.NativeScratch);
        Assert.Equal(NativeSmartColumns.Round(20_000_000UL * 120 + 63 + 4096), plan.Length);
        Assert.True(plan.Length > int.MaxValue); // Layout proof only: no large allocation.
        Assert.Equal(6UL * 96, new NativeSmartFeedbackLayout(6).Length);
        Assert.Throws<OverflowException>(() => new NativeSmartFactsLayout(uint.MaxValue, 1));
        Assert.Throws<OverflowException>(() => new NativeSmartPlanLayout(uint.MaxValue, 1, 1));
        Assert.Throws<OverflowException>(() => new NativeSmartFeedbackLayout(uint.MaxValue));
    }

    [Fact]
    public void RealSmartScratchReceivesNativeActionsAndKeepsPublishedSnapshotSeparate()
    {
        using var owner = new ResourceManager.App.Infrastructure.Adaptation.ResourceManagerLedgerOwner();
        var configuration = CreateProcessPolicyConfiguration();
        configuration.MaximumInputRows = 64;
        using var workspace = new NativeSmartCoordinatorWorkspace(in configuration, owner.CreateScratch);
        Assert.Equal(0UL, owner.ReadTable().RegisteredBytes);
        var process = CreateProcessFact();
        workspace.EnsureRows(64);
        using (var operation = workspace.BeginOperation())
        {
            using var buffers = workspace.CreateScratchView();
            buffers.Inputs[0] = process;
            buffers.Inputs[1] = CreateMetricFact(in process, 1,
                NativeSmartCoordinatorMetricKind.CpuUsagePercent, 50);
            NativeSmartCoordinatorAction action = default;
            for (ulong sequence = 1; sequence <= 3; sequence++)
            {
                var input = CreateEmptyCycleInput(configuration.Generation,
                    workspace.Capacity.ActionCapacity, sequence, sequence == 1);
                input.InputCount = 2;
                input.ObservedAtMilliseconds = checked((long)sequence * 1_000);
                Assert.Equal(NativeSmartCoordinatorStatus.Ok, workspace.Plan(in input));
                Assert.True(TryFindProcessAction(workspace.PlannedActions(), process.TargetKey, out action));
            }
            Assert.True(action.Flags.HasFlag(NativeSmartCoordinatorActionFlags.RequiresFeedback));
            // Native writes actions into the plan region, which this plan opened.
            var actualLease = SmartLease(workspace, "planLease");
            var layout = workspace.PlanLayout!;
            var nativeOutput = System.Runtime.InteropServices.MemoryMarshal.Read<NativeSmartCoordinatorAction>(
                actualLease.GetMemoryChunk(layout.Actions, 120).Span);
            Assert.Equal(action.ActionId, nativeOutput.ActionId);
            // 64 fact rows exceed a ledger page; the plan's few action rows and staging stay below one, so they are managed memory.
            Assert.True(SmartActiveLease(workspace).AllocationNumber != 0);
            Assert.True(layout.Length < 4096);
            Assert.Equal(0UL, actualLease.AllocationNumber);
            workspace.SetFeedback(0, CreateSucceededFeedback(in action, 3_100));
            Assert.Equal(NativeSmartCoordinatorStatus.Ok, workspace.ApplyFeedback(1));
            operation.Complete();
        }
        // The round returned all three regions, so idle reclaim finds nothing left; only the native session's own
        // state (its retained process) stays registered, outside the ledger.
        var native = Assert.Single(owner.ReadWorkspaceCapacities());
        Assert.Equal("smart-coordinator-native", native.WorkArea);
        Assert.False(native.InLedger);
        owner.SetMode(ResourceManager.Ledger.LedgerTableMode.Optimize);
        Assert.Equal(0UL, owner.Tick().Released);
        Assert.Equal(0UL, owner.ReadTable().RegisteredBytes);
        Assert.Equal(NativeSmartCoordinatorStatus.Ok, workspace.RefreshSnapshot());
        Assert.Contains(workspace.CurrentSnapshotRows.ToArray(), row => row.ProcessId == process.ProcessId);
        Assert.Equal(0UL, owner.ReadTable().RegisteredBytes); // Snapshot reads do not open a scratch region.
    }

    [Fact]
    public async Task SmartScratchLeaseSurvivesAwaitAndBlocksNormalClose()
    {
        using var owner = new ResourceManager.App.Infrastructure.Adaptation.ResourceManagerLedgerOwner();
        var configuration = CreateConfiguration();
        configuration.MaximumInputRows = 64;
        using var workspace = new NativeSmartCoordinatorWorkspace(in configuration, owner.CreateScratch);
        workspace.EnsureRows(64); // A round opens its fact rows before its operation.
        var operation = workspace.BeginOperation();
        WriteFirstFactKey(workspace, 47);
        var allocation = SmartActiveLease(workspace).AllocationNumber;
        await Task.Yield();
        owner.SetMode(ResourceManager.Ledger.LedgerTableMode.Optimize);
        Assert.Equal(0UL, owner.Tick().Released);
        Assert.Equal(47UL, ReadFirstFactKey(workspace));
        Assert.Equal(allocation, SmartActiveLease(workspace).AllocationNumber);
        Assert.Throws<ResourceManager.Ledger.LedgerException>(workspace.Dispose);
        Assert.Throws<ObjectDisposedException>(() => workspace.BeginOperation());
        Assert.Equal(47UL, ReadFirstFactKey(workspace)); // The entered call may finish.
        operation.Complete();
        operation.Dispose();
        workspace.Dispose();
        operation.Dispose();
        Assert.Equal(0UL, owner.ReadTable().RegisteredBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SmartEndUseFailureKeepsExactLeaseUntilExplicitClose(bool throwAfterInnerReturn)
    {
        using var owner = new ResourceManager.App.Infrastructure.Adaptation.ResourceManagerLedgerOwner();
        var configuration = CreateConfiguration();
        configuration.MaximumInputRows = 64;
        using var workspace = new NativeSmartCoordinatorWorkspace(in configuration,
            length => new SmartEndUseFault(owner.CreateScratch(length), throwAfterInnerReturn));
        workspace.EnsureRows(64);
        var operation = workspace.BeginOperation();
        var lease = SmartActiveLease(workspace);
        WriteFirstFactKey(workspace, 19);
        operation.Complete();
        Assert.Throws<InvalidOperationException>(operation.Dispose);
        Assert.Same(lease, SmartActiveLease(workspace));
        Assert.Throws<InvalidOperationException>(() => workspace.BeginOperation());
        operation.Dispose(); // A using/finally's second Dispose is not an endUse retry.
        Assert.Same(lease, SmartActiveLease(workspace));
        workspace.Dispose();
        operation.Dispose();
        Assert.Equal(0UL, owner.ReadTable().RegisteredBytes);
    }

    [Fact]
    public void SmartRoundReturnsItsRegionAndTheNextRoundOpensExactRows()
    {
        using var owner = new ResourceManager.App.Infrastructure.Adaptation.ResourceManagerLedgerOwner();
        var configuration = CreateConfiguration();
        configuration.MaximumInputRows = 64;
        var regions = new List<ResourceManager.App.Infrastructure.Adaptation.ResourceManagerLedgerOwner.ComputeScratch>();
        using var workspace = new NativeSmartCoordinatorWorkspace(in configuration, length =>
        {
            var region = owner.CreateScratch(length);
            regions.Add(region);
            return region;
        });
        Assert.Empty(regions); // Nothing opens before a round.
        workspace.EnsureRows(64);
        var first = Assert.Single(regions);
        Assert.Equal(workspace.FactsLayout!.Length, first.Length);
        using (var operation = workspace.BeginOperation())
        {
            WriteFirstFactKey(workspace, 23);
            operation.Complete();
        }
        Assert.True(first.Closed);
        Assert.Null(workspace.FactsLayout);
        Assert.Empty(owner.ReadWorkspaceCapacities());
        Assert.Equal(0UL, owner.ReadTable().RegisteredBytes);
        owner.SetMode(ResourceManager.Ledger.LedgerTableMode.Optimize);
        Assert.Equal(0UL, owner.Tick().Released); // Nothing idle is left to reclaim.
        workspace.EnsureRows(40);
        Assert.Equal(2, regions.Count);
        var second = regions[1];
        Assert.NotEqual(first.Uid, second.Uid);
        Assert.Equal(workspace.FactsLayout!.Length, second.Length);
        Assert.True(second.Length < first.Length);
        using (var operation = workspace.BeginOperation())
        {
            WriteFirstFactKey(workspace, 29);
            Assert.Equal(29UL, ReadFirstFactKey(workspace));
            operation.Complete();
        }
        Assert.True(second.Closed);
    }

    private static ResourceManager.App.Infrastructure.Adaptation.ComputeScratchLease SmartActiveLease(NativeSmartCoordinatorWorkspace workspace)
        => SmartLease(workspace, "activeLease");

    private static ResourceManager.App.Infrastructure.Adaptation.ComputeScratchLease SmartLease(
        NativeSmartCoordinatorWorkspace workspace, string field)
        => Assert.IsAssignableFrom<ResourceManager.App.Infrastructure.Adaptation.ComputeScratchLease>(
            typeof(NativeSmartCoordinatorWorkspace).GetField(field,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(workspace));

    // A recorded marker in the operation's own fact region.
    private static void WriteFirstFactKey(NativeSmartCoordinatorWorkspace workspace, ulong key)
    {
        using var view = workspace.CreateScratchView();
        view.Inputs[0].TargetKey = key;
    }

    private static ulong ReadFirstFactKey(NativeSmartCoordinatorWorkspace workspace)
    {
        using var view = workspace.CreateScratchView();
        return view.Inputs[0].TargetKey;
    }

    // Recorded caller failures before/after the actual SDK return, using the real region throughout.
    private sealed class SmartEndUseFault(ResourceManager.App.Infrastructure.Adaptation.IComputeScratch inner, bool after)
        : ResourceManager.App.Infrastructure.Adaptation.IComputeScratch
    {
        public ulong Length => inner.Length;
        public ResourceManager.App.Infrastructure.Adaptation.ComputeScratchLease Acquire() => new Reading(inner.Acquire(), after);
        public void Dispose() => inner.Dispose();
        private sealed class Reading(ResourceManager.App.Infrastructure.Adaptation.ComputeScratchLease inner, bool after)
            : ResourceManager.App.Infrastructure.Adaptation.ComputeScratchLease
        {
            private bool fail = true;
            internal override ulong Length => inner.Length;
            internal override ulong AllocationNumber => inner.AllocationNumber;
            internal override bool IsLoading => inner.IsLoading;
            internal override Memory<byte> GetMemoryChunk(ulong offset, int count) => inner.GetMemoryChunk(offset, count);
            internal override void Complete() => inner.Complete();
            public override void Dispose()
            {
                if (fail)
                {
                    fail = false;
                    if (after) inner.Dispose();
                    throw new InvalidOperationException("recorded smart endUse return failure");
                }
                inner.Dispose();
            }
        }
    }

    private static NativeSmartCoordinatorConfiguration CreateConfiguration()
        => new()
        {
            AbiVersion = NativeSmartCoordinatorAbi.Version,
            StructSize = NativeSmartCoordinatorSession.SizeOf<NativeSmartCoordinatorConfiguration>(),
            Generation = 1,
            FieldMask = NativeSmartCoordinatorConfigFields.Required,
            FeatureFlags = NativeSmartCoordinatorFeatures.ProcessPolicy |
                NativeSmartCoordinatorFeatures.CriticalEvents,
            MaximumProcesses = 2,
            MaximumSoftwareGroups = 2,
            MaximumGpuStates = 1,
            MaximumInputRows = 8,
            MaximumActions = 4,
            MaximumReservations = 6,
            MaximumAtomicGroups = 2,
            NormalIntervalMilliseconds = 1_000,
            EventIntervalMilliseconds = 100,
            EventBoostRounds = 10,
            GameStartGraceMilliseconds = 1_000,
            RequiredConsecutiveDecisions = 3,
            FailureRetryMilliseconds = 100,
            ReservationTimeoutMilliseconds = 1_000,
            A1MinimumCpuScore = 1,
            DefaultMinimumCpuScoreScale = 1,
            Level1MaximumCpuScoreScale = 0.8,
            Level2MaximumCpuScoreScale = 0.6,
            Level3MaximumCpuScoreScale = 0.4,
            LowTierLevel4MaximumCpuScoreScale = 0.1,
            ReservedProcessPolicy0 = 0,
            HighTierMinimumBaseScore = 81,
            MiddleTierMinimumBaseScore = 21,
        };

    private static NativeSmartCoordinatorConfiguration CreateProcessPolicyConfiguration()
    {
        var configuration = CreateConfiguration();
        configuration.FeatureFlags = NativeSmartCoordinatorFeatures.ProcessPolicy;
        configuration.NormalIntervalMilliseconds = 15_000;
        configuration.EventIntervalMilliseconds = 1_000;
        configuration.EventBoostRounds = 10;
        configuration.GameStartGraceMilliseconds = 10_000;
        configuration.RequiredConsecutiveDecisions = 3;
        configuration.FailureRetryMilliseconds = 5_000;
        configuration.ReservationTimeoutMilliseconds = 30_000;
        configuration.A1MinimumCpuScore = 120;
        configuration.DefaultMinimumCpuScoreScale = 70;
        configuration.Level1MaximumCpuScoreScale = 60;
        configuration.Level2MaximumCpuScoreScale = 50;
        configuration.Level3MaximumCpuScoreScale = 34;
        configuration.LowTierLevel4MaximumCpuScoreScale = 34;
        configuration.ReservedProcessPolicy0 = 0;
        configuration.HighTierMinimumBaseScore = 81;
        configuration.MiddleTierMinimumBaseScore = 21;

        ReadOnlySpan<double> stateMultipliers = stackalloc double[]
        {
            1.0,
            1.35,
            1.12,
            0.75,
            0.55,
            0.45,
            0.0
        };
        NativeSmartCoordinatorConfigurationWriter.SetProcessStateMultipliers(
            ref configuration,
            stateMultipliers);

        return configuration;
    }

}
