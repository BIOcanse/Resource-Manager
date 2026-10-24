using ResourceManager.App.Infrastructure.Adaptation;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.Ledger;
using System.Text.Json;
using Xunit.Abstractions;

namespace Resource_Manager_APP.Tests;

public sealed class SmartCoordinatorStagedScratchLedgerTests(ITestOutputHelper output)
{
    [Fact]
    public void SmallFactsUseSmallScratchAndGrowthClosesThePreviousIdentity()
    {
        using var owner = new ResourceManagerLedgerOwner();
        var configuration = Configuration();
        var resources = new List<ResourceManagerLedgerOwner.ComputeScratch>();
        using var workspace = new NativeSmartCoordinatorWorkspace(in configuration, bytes =>
        {
            var resource = owner.CreateScratch(bytes);
            resources.Add(resource);
            return resource;
        });
        workspace.EnsureRows(2);
        var small = resources[^1];
        var smallLayout = workspace.ScratchLayout;
        using var query = new NativeSmartCoordinatorSession(in configuration);
        var maximum = query.ScratchRequirements;
        Assert.True(smallLayout.NativeScratchByteCount < maximum.ByteCount);
        Assert.Equal(2, smallLayout.InputCapacity);
        Assert.Equal(4, smallLayout.ActionCapacity);
        workspace.EnsureRows(16);
        var larger = resources[^1];
        Assert.NotEqual(small.Uid, larger.Uid);
        Assert.True(larger.Length > small.Length);
        Assert.Throws<ObjectDisposedException>(() => small.Acquire());
        var count = resources.Count;
        workspace.EnsureRows(2);
        Assert.Equal(count, resources.Count);
        Assert.Same(larger, resources[^1]);
    }

    [Fact]
    public async Task NativeStagingUsesTheExistingLazyIdentityAcrossAwaitIdleReclaimAndRefill()
    {
        using var owner = new ResourceManagerLedgerOwner();
        var configuration = Configuration();
        using var query = new NativeSmartCoordinatorSession(in configuration);
        var requirements = query.ScratchRequirementsForRows(16);
        var expectedNative = checked((ulong)requirements.ProcessSize * requirements.ProcessCount
            + (ulong)requirements.SoftwareSize * requirements.SoftwareCount
            + (ulong)(requirements.GpuSize + requirements.SoftwareGpuSize) * requirements.GpuCount
            + 4UL * (requirements.ProcessIndexCount + requirements.SoftwareIndexCount + 2UL * requirements.GpuIndexCount));
        Assert.Equal(expectedNative, requirements.ByteCount);
        Assert.Equal(80U, NativeSmartCoordinatorSession.SizeOf<NativeSmartCoordinatorScratchRequirements>());
        ResourceManagerLedgerOwner.ComputeScratch? resource = null;
        using var workspace = new NativeSmartCoordinatorWorkspace(in configuration,
            bytes => resource = owner.CreateScratch(bytes));
        workspace.EnsureRows(16);
        var layout = workspace.ScratchLayout;
        output.WriteLine(JsonSerializer.Serialize(new
        {
            requirements.AbiVersion, requirements.StructSize, requirements.ByteCount, requirements.Alignment,
            requirements.ProcessSize, requirements.SoftwareSize, requirements.GpuSize, requirements.SoftwareGpuSize,
            requirements.ProcessCount, requirements.SoftwareCount, requirements.GpuCount,
            requirements.ProcessIndexCount, requirements.SoftwareIndexCount, requirements.GpuIndexCount,
            layout.InputCapacity, layout.ActionCapacity, layout.FeedbackCapacity,
            layout.Inputs, layout.Actions, layout.Feedback, layout.NativeScratch, layout.NativeScratchByteCount,
            layout.NativeScratchAlignment, layout.Length
        }));
        Assert.NotNull(resource);
        var uid = resource.Uid;
        Assert.Equal(requirements.ByteCount, layout.NativeScratchByteCount);
        var managed = checked(104UL * (ulong)layout.InputCapacity
            + 136UL * (ulong)layout.ActionCapacity + 96UL * (ulong)layout.FeedbackCapacity);
        Assert.Equal(managed, layout.NativeScratch);
        Assert.Equal(checked((managed + requirements.Alignment - 1 + requirements.ByteCount + 7) / 8 * 8), resource.Length);
        Assert.Equal(0UL, owner.ReadTable().RegisteredBytes);
        owner.SetMode(LedgerTableMode.Optimize);
        ulong firstAllocation;
        NativeSmartCoordinatorAction[] copiedActions;
        NativeSmartCoordinatorSnapshotRow[] copiedRows;
        using (var operation = workspace.BeginOperation())
        {
            copiedActions = Plan(workspace, configuration.Generation, 1);
            copiedActions = Plan(workspace, configuration.Generation, 2);
            copiedActions = Plan(workspace, configuration.Generation, 3);
            Assert.Contains(copiedActions, action => action.Flags.HasFlag(NativeSmartCoordinatorActionFlags.RequiresFeedback));
            await Task.Yield();
            Assert.Equal(0UL, owner.Tick().Released);
            Assert.Equal(resource.Length, owner.ReadTable().RegisteredBytes);
            uint feedbackCount = 0;
            foreach (var action in copiedActions)
            {
                if (!action.Flags.HasFlag(NativeSmartCoordinatorActionFlags.RequiresFeedback)) continue;
                workspace.SetFeedback(feedbackCount++, Succeeded(action));
            }
            Assert.Equal(NativeSmartCoordinatorStatus.Ok, workspace.ApplyFeedback(feedbackCount));
            Assert.Equal(NativeSmartCoordinatorStatus.Ok, workspace.RefreshSnapshot());
            copiedRows = workspace.CurrentSnapshotRows.ToArray();
            operation.Complete();
        }
        using (var inspect = resource.Acquire()) firstAllocation = inspect.AllocationNumber;
        var firstAction = copiedActions[0];
        var firstRow = copiedRows[0];
        Assert.Equal(1UL, owner.Tick().Released);
        Assert.Equal(0UL, owner.ReadTable().RegisteredBytes);
        Assert.Equal(NativeSmartCoordinatorStatus.Ok, workspace.RefreshSnapshot());
        Assert.Equal(0UL, owner.ReadTable().RegisteredBytes); // Canonical snapshots do not borrow staging.
        Assert.Contains(workspace.CurrentSnapshotRows.ToArray(), row => row.ProcessId == 11);
        using (var operation = workspace.BeginOperation())
        {
            _ = Plan(workspace, configuration.Generation, 4);
            Assert.Equal(uid, resource.Uid);
            Assert.Equal(firstAction, copiedActions[0]);
            Assert.Equal(firstRow, copiedRows[0]);
            Assert.Equal(NativeSmartCoordinatorStatus.Ok, workspace.RefreshSnapshot());
            Assert.Equal(0U, workspace.Snapshot.InflightCount);
            operation.Complete();
        }
        using (var inspect = resource.Acquire()) Assert.NotEqual(firstAllocation, inspect.AllocationNumber);
        workspace.Dispose();
        Assert.Equal(0UL, owner.ReadTable().RegisteredBytes);
    }

    [Fact]
    public void FailedCloseRetainsTheCombinedRegionAndNativeSessionForExplicitRetry()
    {
        using var owner = new ResourceManagerLedgerOwner();
        var configuration = Configuration();
        ResourceManagerLedgerOwner.ComputeScratch? resource = null;
        using var workspace = new NativeSmartCoordinatorWorkspace(in configuration,
            bytes => resource = owner.CreateScratch(bytes));
        workspace.EnsureRows(16);
        using (var operation = workspace.BeginOperation())
        {
            _ = Plan(workspace, configuration.Generation, 1);
            operation.Complete();
        }
        using (var use = resource!.Acquire())
        {
            Assert.Throws<LedgerException>(workspace.Dispose);
            Assert.Throws<ObjectDisposedException>(() => workspace.RefreshSnapshot());
            Assert.True(workspace.Capacity.ProcessCapacity > 0);
            Assert.Throws<ObjectDisposedException>(() => workspace.BeginOperation());
            owner.SetMode(LedgerTableMode.Optimize);
            Assert.Equal(0UL, owner.Tick().Released);
        }
        workspace.Dispose();
        Assert.Equal(0UL, owner.ReadTable().RegisteredBytes);
    }

    [Fact]
    public void DirectNativePlanRequiresExplicitScratchAndLeavesStateUnchangedOnRejection()
    {
        var configuration = Configuration();
        using var session = new NativeSmartCoordinatorSession(in configuration);
        var input = Cycle(configuration.Generation, session.Capacity.ActionCapacity, 1);
        NativeSmartCoordinatorSnapshot before = default;
        Assert.Equal(NativeSmartCoordinatorStatus.Ok, session.GetSnapshot(ref before, []));
        NativeSmartCoordinatorSnapshot rejected = default;
        Assert.Equal(NativeSmartCoordinatorStatus.InvalidArgument,
            session.Plan(in input, [], [], IntPtr.Zero, session.ScratchRequirements.ByteCount, ref rejected));
        NativeSmartCoordinatorSnapshot after = default;
        Assert.Equal(NativeSmartCoordinatorStatus.Ok, session.GetSnapshot(ref after, []));
        Assert.Equal(before.StateRevision, after.StateRevision);
        Assert.Equal(before.PlanEpoch, after.PlanEpoch);
    }

    private static NativeSmartCoordinatorConfiguration Configuration()
    {
        var plan = HostManagerTestPlanFactory.SmartCoordinator;
        var configuration = NativeSmartCoordinatorConfigurationWriter.Create(plan.Build, plan.Recreate, plan.HotPublish, 1);
        configuration.FeatureFlags = NativeSmartCoordinatorFeatures.ProcessPolicy;
        return configuration;
    }

    private static NativeSmartCoordinatorAction[] Plan(NativeSmartCoordinatorWorkspace workspace, ulong generation, ulong sequence)
    {
        using var view = workspace.CreateScratchView();
        var process = new NativeSmartCoordinatorInputRow
        {
            StructSize = NativeSmartCoordinatorSession.SizeOf<NativeSmartCoordinatorInputRow>(),
            SoftwareKind = NativeSmartCoordinatorSoftwareKind.GeneralApplication,
            ValidMask = NativeSmartCoordinatorInputValidity.Known & ~(NativeSmartCoordinatorInputValidity.Metric |
                NativeSmartCoordinatorInputValidity.ProcessScore | NativeSmartCoordinatorInputValidity.SoftwareScore |
                NativeSmartCoordinatorInputValidity.ScoreMemberCount),
            Flags = NativeSmartCoordinatorInputFlags.Running | NativeSmartCoordinatorInputFlags.ProcessCpuMetricsComplete |
                NativeSmartCoordinatorInputFlags.ProcessGpuMetricsComplete | NativeSmartCoordinatorInputFlags.CanApplyProcessPolicy |
                NativeSmartCoordinatorInputFlags.CanApplyAdapterPolicy | NativeSmartCoordinatorInputFlags.HardwareSchedulingEligible,
            TargetKey = 101, SoftwareKey = 1001, ProcessStartKey = 111, BaseScore = 20, ProcessId = 11,
            CpuCapabilityMask = 15, GpuCapabilityMask = 15,
            AppliedCpuGrade = NativeSmartCoordinatorAdapterGrade.Normal,
            AppliedGpuGrade = NativeSmartCoordinatorAdapterGrade.Normal,
            AppliedProcessGrade = sequence > 3 ? NativeSmartCoordinatorProcessGrade.Level3 : NativeSmartCoordinatorProcessGrade.Normal
        };
        var metric = process;
        metric.SourceIndex = 1;
        metric.MetricKind = NativeSmartCoordinatorMetricKind.CpuUsagePercent;
        metric.MetricValue = 50;
        metric.ValidMask |= NativeSmartCoordinatorInputValidity.Metric | NativeSmartCoordinatorInputValidity.ProcessScore |
            NativeSmartCoordinatorInputValidity.SoftwareScore | NativeSmartCoordinatorInputValidity.ScoreMemberCount;
        metric.ProcessScore = metric.SoftwareScore = 4.5;
        metric.ScoreMemberCount = 1;
        view.Inputs[0] = process;
        view.Inputs[1] = metric;
        var cycle = Cycle(generation, checked((uint)workspace.ScratchLayout.ActionCapacity), sequence);
        cycle.InputCount = 2;
        Assert.Equal(NativeSmartCoordinatorStatus.Ok, workspace.Plan(in cycle));
        return view.Actions[..checked((int)workspace.PlannedActionCount)].ToArray();
    }

    private static NativeSmartCoordinatorCycleInput Cycle(ulong generation, uint capacity, ulong sequence)
        => new()
        {
            AbiVersion = NativeSmartCoordinatorAbi.Version,
            StructSize = NativeSmartCoordinatorSession.SizeOf<NativeSmartCoordinatorCycleInput>(),
            InputRowStructSize = NativeSmartCoordinatorSession.SizeOf<NativeSmartCoordinatorInputRow>(),
            ActionStructSize = NativeSmartCoordinatorSession.SizeOf<NativeSmartCoordinatorAction>(),
            ConfigurationGeneration = generation, CycleSequence = sequence, RoundNumber = sequence,
            ObservedAtMilliseconds = checked((long)sequence * 1000),
            ValidMask = NativeSmartCoordinatorCycleValidity.Required,
            Flags = NativeSmartCoordinatorCycleFlags.FullProcessSnapshot |
                (sequence == 1 ? NativeSmartCoordinatorCycleFlags.AuthoritativeAppliedFacts : 0),
            ActionCapacity = capacity, MaximumActionsThisCycle = capacity
        };

    private static NativeSmartCoordinatorFeedback Succeeded(NativeSmartCoordinatorAction action)
        => new()
        {
            StructSize = NativeSmartCoordinatorSession.SizeOf<NativeSmartCoordinatorFeedback>(),
            Flags = NativeSmartCoordinatorFeedbackFlags.ProcessOwned | NativeSmartCoordinatorFeedbackFlags.RollbackPayloadPersisted,
            ValidMask = NativeSmartCoordinatorFeedbackValidity.CompletedAt | NativeSmartCoordinatorFeedbackValidity.ActualProcessGrade,
            ActionId = action.ActionId, PlanEpoch = action.PlanEpoch, ConfigurationGeneration = action.ConfigurationGeneration,
            TargetKey = action.TargetKey, SoftwareKey = action.SoftwareKey, ProcessStartKey = action.ProcessStartKey,
            CompletedAtMilliseconds = 3100, ProcessId = action.ProcessId,
            Status = NativeSmartCoordinatorFeedbackStatus.Succeeded, Scope = action.Scope, ActualProcessGrade = action.ToProcessGrade
        };
}
