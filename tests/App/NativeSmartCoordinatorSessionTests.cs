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
    public void CpuAdapterStateMultiplierDriftIsRejectedAtNativeCapacityBoundary()
    {
        var configuration = CreateConfiguration();
        Span<double> driftedCpuStateMultipliers = stackalloc double[
            NativeSmartCoordinatorAbi.RuntimeStateCount];
        driftedCpuStateMultipliers[0] = 1;
        NativeSmartCoordinatorConfigurationWriter.SetAdapterStateMultipliers(
            ref configuration.CpuAdapter,
            driftedCpuStateMultipliers);

        var result = NativeSmartCoordinatorSession.CapacityForConfiguration(
            in configuration,
            out var capacity);

        Assert.Equal(NativeSmartCoordinatorStatus.InvalidArgument, result);
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
    public void ReconfigureRequiresNewGenerationAndStableCapacityShape()
    {
        var configuration = CreateConfiguration();
        using var session = new NativeSmartCoordinatorSession(in configuration);

        Assert.Equal(
            NativeSmartCoordinatorStatus.StaleGeneration,
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
            ScoreSchedulingGeneration = 1,
            CpuScoreSourceFingerprint = 0xC000_0000_0000_0001UL,
            GpuScoreSourceFingerprint = 0xD000_0000_0000_0001UL,
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
        using var workspace = new NativeSmartCoordinatorWorkspace(in configuration);
        Assert.Equal(configuration.MaximumInputRows, (uint)workspace.InputRows.Length);
        Assert.Equal(configuration.MaximumReservations, (uint)workspace.FeedbackRows.Length);

        var input = CreateEmptyCycleInput(
            configuration.Generation,
            workspace.Capacity.ActionCapacity,
            cycleSequence: 1,
            authoritative: true);
        Assert.Equal(NativeSmartCoordinatorStatus.Ok, workspace.PlanCurrent(in input));
        Assert.Empty(workspace.PlannedActions.ToArray());
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
        using var workspace = new NativeSmartCoordinatorWorkspace(in configuration);
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
        using var workspace = new NativeSmartCoordinatorWorkspace(in configuration);
        var process = CreateProcessFact();
        workspace.InputRows[0] = process;
        workspace.InputRows[1] = CreateMetricFact(
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
            Assert.True(TryFindProcessAction(workspace.PlannedActions, process.TargetKey, out var action));
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

        workspace.FeedbackRows[0] = CreateSucceededFeedback(in appliedAction, completedAtMilliseconds: 3_100);
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
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void AdapterActionScoresMatchTheDomainsReadyForExecution(bool cpuFirst, bool simultaneous)
    {
        var configuration = CreateProcessPolicyConfiguration();
        configuration.FeatureFlags = NativeSmartCoordinatorFeatures.AdapterCpu |
            NativeSmartCoordinatorFeatures.AdapterGpu;
        configuration.CpuAdapter.ExtremeMinimumScore = 120;
        configuration.CpuAdapter.NormalMinimumScore = 60;
        configuration.CpuAdapter.OptimizeMinimumScore = 25;
        configuration.GpuAdapter = configuration.CpuAdapter;
        using var workspace = new NativeSmartCoordinatorWorkspace(in configuration);
        var process = CreateProcessFact();
        process.SoftwareKind = NativeSmartCoordinatorSoftwareKind.Adapted;
        process.BaseScore = 60;
        NativeSmartCoordinatorAction readyAction = default;
        for (ulong sequence = 1; sequence <= 3; sequence++)
        {
            uint count = 0;
            workspace.InputRows[checked((int)count++)] = process;
            if (cpuFirst || simultaneous || sequence > 1)
            {
                workspace.InputRows[checked((int)count)] = CreateMetricFact(
                    in process, count, NativeSmartCoordinatorMetricKind.CpuUsagePercent, 100);
                count++;
            }
            if (!cpuFirst || simultaneous || sequence > 1)
            {
                var gpu = CreateMetricFact(
                    in process, count, NativeSmartCoordinatorMetricKind.GpuUsagePercent, 2);
                gpu.DeviceIndex = 5;
                workspace.InputRows[checked((int)count++)] = gpu;
                var vram = process;
                vram.SourceIndex = count;
                vram.MetricKind = NativeSmartCoordinatorMetricKind.VramUsagePercent;
                vram.MetricValue = 1;
                vram.DeviceIndex = 5;
                vram.ValidMask |= NativeSmartCoordinatorInputValidity.Metric;
                workspace.InputRows[checked((int)count++)] = vram;
            }
            var cycle = CreateEmptyCycleInput(configuration.Generation,
                workspace.Capacity.ActionCapacity, sequence, authoritative: sequence == 1);
            cycle.InputCount = count;
            cycle.ObservedAtMilliseconds = checked((long)sequence * 1_000);
            Assert.Equal(NativeSmartCoordinatorStatus.Ok, workspace.PlanCurrent(in cycle));
            var requiresFeedback = HostManagerSmartCoordinator.ValidateNativeActionBatch(workspace);
            if (sequence == 3)
            {
                Assert.Equal(1u, requiresFeedback);
                readyAction = Assert.Single(workspace.PlannedActions.ToArray(),
                    action => action.Flags.HasFlag(NativeSmartCoordinatorActionFlags.RequiresFeedback));
            }
        }
        var expectedDomains = simultaneous
            ? NativeSmartCoordinatorGradeDomains.Cpu | NativeSmartCoordinatorGradeDomains.Gpu
            : cpuFirst ? NativeSmartCoordinatorGradeDomains.Cpu : NativeSmartCoordinatorGradeDomains.Gpu;
        Assert.Equal(expectedDomains, readyAction.DomainMask);
        Assert.Equal(expectedDomains.HasFlag(NativeSmartCoordinatorGradeDomains.Cpu),
            readyAction.ValidMask.HasFlag(NativeSmartCoordinatorActionValidity.CpuScore));
        Assert.Equal(expectedDomains.HasFlag(NativeSmartCoordinatorGradeDomains.Gpu),
            readyAction.ValidMask.HasFlag(NativeSmartCoordinatorActionValidity.GpuScore));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DeferredAdapterActionWaitsWhenItsCurrentScoreIsMissing(bool cpu)
    {
        var configuration = CreateProcessPolicyConfiguration();
        configuration.MaximumGpuStates = 2;
        configuration.FeatureFlags = cpu
            ? NativeSmartCoordinatorFeatures.AdapterCpu
            : NativeSmartCoordinatorFeatures.AdapterGpu;
        configuration.CpuAdapter.ExtremeMinimumScore = 120;
        configuration.CpuAdapter.NormalMinimumScore = 60;
        configuration.CpuAdapter.OptimizeMinimumScore = 25;
        configuration.GpuAdapter = configuration.CpuAdapter;
        using var workspace = new NativeSmartCoordinatorWorkspace(in configuration);
        ulong deferredSoftware = 0;
        for (ulong sequence = 1; sequence <= 5; sequence++)
        {
            uint count = 0;
            for (uint member = 0; member < 2; member++)
            {
                var process = CreateProcessFact();
                process.TargetKey += member;
                process.SoftwareKey += member;
                process.ProcessId += member;
                process.ProcessStartKey += member;
                process.SoftwareKind = NativeSmartCoordinatorSoftwareKind.Adapted;
                process.BaseScore = 60;
                process.SourceIndex = count;
                workspace.InputRows[checked((int)count++)] = process;
                var metric = CreateMetricFact(in process, count,
                    cpu ? NativeSmartCoordinatorMetricKind.CpuUsagePercent : NativeSmartCoordinatorMetricKind.GpuUsagePercent,
                    cpu ? 100 : 2);
                metric.DeviceIndex = cpu ? 0u : 5u;
                if (sequence == 4 && process.SoftwareKey == deferredSoftware)
                {
                    metric.ValidMask &= ~(NativeSmartCoordinatorInputValidity.ProcessScore |
                        NativeSmartCoordinatorInputValidity.SoftwareScore | NativeSmartCoordinatorInputValidity.ScoreMemberCount);
                    metric.ProcessScore = 0;
                    metric.SoftwareScore = 0;
                    metric.ScoreMemberCount = 0;
                }
                workspace.InputRows[checked((int)count++)] = metric;
                if (!cpu)
                {
                    var vram = process;
                    vram.SourceIndex = count;
                    vram.MetricKind = NativeSmartCoordinatorMetricKind.VramUsagePercent;
                    vram.MetricValue = 1;
                    vram.DeviceIndex = 5;
                    vram.ValidMask |= NativeSmartCoordinatorInputValidity.Metric;
                    workspace.InputRows[checked((int)count++)] = vram;
                }
            }
            var cycle = CreateEmptyCycleInput(configuration.Generation,
                workspace.Capacity.ActionCapacity, sequence, authoritative: sequence == 1);
            cycle.InputCount = count;
            cycle.ObservedAtMilliseconds = checked((long)sequence * 1_000);
            cycle.MaximumActionsThisCycle = 1;
            Assert.Equal(NativeSmartCoordinatorStatus.Ok, workspace.PlanCurrent(in cycle));
            var feedbackCount = HostManagerSmartCoordinator.ValidateNativeActionBatch(workspace);
            if (sequence == 3)
            {
                Assert.Equal(1u, feedbackCount);
                var selected = Assert.Single(workspace.PlannedActions.ToArray(),
                    action => action.Flags.HasFlag(NativeSmartCoordinatorActionFlags.RequiresFeedback));
                deferredSoftware = selected.SoftwareKey == 1_001 ? 1_002u : 1_001u;
            }
            if (sequence == 4)
            {
                Assert.Equal(0u, feedbackCount);
                Assert.Equal(1u, workspace.Snapshot.InflightCount);
            }
            if (sequence == 5)
            {
                Assert.Equal(1u, feedbackCount);
                var resumed = Assert.Single(workspace.PlannedActions.ToArray(),
                    action => action.Flags.HasFlag(NativeSmartCoordinatorActionFlags.RequiresFeedback));
                Assert.Equal(deferredSoftware, resumed.SoftwareKey);
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ZeroActionBudgetDoesNotBecomeScoreOnly(bool explicitScoreOnly)
    {
        var configuration = CreateProcessPolicyConfiguration();
        using var workspace = new NativeSmartCoordinatorWorkspace(in configuration);
        var process = CreateProcessFact();
        process.BaseScore = 40;
        workspace.InputRows[0] = process;
        workspace.InputRows[1] = CreateMetricFact(
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
                Assert.Empty(workspace.PlannedActions.ToArray());
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
                NativeSmartCoordinatorInputValidity.CpuCapabilities |
                NativeSmartCoordinatorInputValidity.GpuCapabilities |
                NativeSmartCoordinatorInputValidity.AppliedProcessGrade |
                NativeSmartCoordinatorInputValidity.AppliedCpuGrade |
                NativeSmartCoordinatorInputValidity.AppliedGpuGrade,
            Flags = NativeSmartCoordinatorInputFlags.Running |
                NativeSmartCoordinatorInputFlags.ProcessCpuMetricsComplete |
                NativeSmartCoordinatorInputFlags.ProcessGpuMetricsComplete |
                NativeSmartCoordinatorInputFlags.CanApplyProcessPolicy |
                NativeSmartCoordinatorInputFlags.CanApplyAdapterPolicy |
                NativeSmartCoordinatorInputFlags.HardwareSchedulingEligible,
            TargetKey = 101,
            SoftwareKey = 1_001,
            ProcessStartKey = 111,
            BaseScore = 20,
            SourceIndex = 0,
            ProcessId = 11,
            CpuCapabilityMask = 0x0f,
            GpuCapabilityMask = 0x0f,
            AppliedProcessGrade = NativeSmartCoordinatorProcessGrade.Normal,
            AppliedCpuGrade = NativeSmartCoordinatorAdapterGrade.Normal,
            AppliedGpuGrade = NativeSmartCoordinatorAdapterGrade.Normal,
            AppliedEpoch = 0
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
            ScoreSchedulingGeneration = cycleSequence,
            CpuScoreSourceFingerprint = 0xC000_0000_0000_0000UL | cycleSequence,
            GpuScoreSourceFingerprint = 0xD000_0000_0000_0000UL | cycleSequence,
            MaximumActionsThisCycle = actionCapacity
        };

    private static NativeSmartCoordinatorConfiguration CreateConfiguration()
        => new()
        {
            AbiVersion = NativeSmartCoordinatorAbi.Version,
            StructSize = NativeSmartCoordinatorSession.SizeOf<NativeSmartCoordinatorConfiguration>(),
            Generation = 1,
            FieldMask = NativeSmartCoordinatorConfigFields.Required,
            FeatureFlags = NativeSmartCoordinatorFeatures.ProcessPolicy |
                NativeSmartCoordinatorFeatures.AdapterCpu |
                NativeSmartCoordinatorFeatures.AdapterGpu |
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
            EventBoostMilliseconds = 1_000,
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
            CpuAdapter = CreateAdapterPolicy(),
            GpuAdapter = CreateAdapterPolicy()
        };

    private static NativeSmartCoordinatorConfiguration CreateProcessPolicyConfiguration()
    {
        var configuration = CreateConfiguration();
        configuration.FeatureFlags = NativeSmartCoordinatorFeatures.ProcessPolicy;
        configuration.NormalIntervalMilliseconds = 15_000;
        configuration.EventIntervalMilliseconds = 1_000;
        configuration.EventBoostMilliseconds = 10_000;
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
        NativeSmartCoordinatorConfigurationWriter.SetAdapterStateMultipliers(
            ref configuration.CpuAdapter,
            stateMultipliers);

        return configuration;
    }

    private static NativeSmartCoordinatorAdapterPolicyConfiguration CreateAdapterPolicy()
        => new()
        {
            ExtremeMinimumScore = 3,
            NormalMinimumScore = 2,
            OptimizeMinimumScore = 1
        };
}
