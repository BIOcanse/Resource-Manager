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
        var smallFacts = workspace.FactsLayout!;
        using var query = new NativeSmartCoordinatorSession(in configuration);
        var maximum = query.ScratchRequirements;
        Assert.Equal(2, smallFacts.InputCapacity);
        Assert.Equal(NativeSmartColumns.Round(2UL * 104 + 4UL * (ulong)smallFacts.MeasureTableCount), small.Length);
        using (var operation = workspace.BeginOperation())
        {
            _ = Plan(workspace, configuration.Generation, 1);
            Assert.True(workspace.PlanLayout!.NativeScratchByteCount < maximum.ByteCount);
            Assert.Equal(1, workspace.PlanLayout.ActionCapacity); // One staged process is one candidate row.
            operation.Complete();
        }
        workspace.EnsureRows(16);
        var larger = resources[^1];
        Assert.NotEqual(small.Uid, larger.Uid);
        Assert.True(larger.Length > small.Length);
        Assert.Throws<ObjectDisposedException>(() => small.Acquire());
        var count = resources.Count;
        workspace.EnsureRows(2);
        // A smaller round reopens exactly instead of keeping the larger region.
        Assert.Equal(count + 1, resources.Count);
        Assert.Throws<ObjectDisposedException>(() => larger.Acquire());
        Assert.Equal(small.Length, resources[^1].Length);
    }

    [Fact]
    public async Task EachRoundOpensExactRegionsMatchesAFullCapacityReferenceAndReturnsThemAtItsEnd()
    {
        using var owner = new ResourceManagerLedgerOwner();
        var configuration = Configuration();
        using var reference = new NativeSmartCoordinatorSession(in configuration);
        var full = reference.ScratchRequirements;
        Assert.Equal(80U, NativeSmartCoordinatorSession.SizeOf<NativeSmartCoordinatorScratchRequirements>());
        Assert.Equal((112U, 120U, 72U, 48U), (full.ProcessSize, full.SoftwareSize, full.GpuSize, full.SoftwareGpuSize));
        Assert.Equal(AlignUp(112UL * full.ProcessCount + 120UL * full.SoftwareCount + (72UL + 48UL) * full.GpuCount
            + 4UL * (full.ProcessIndexCount + full.SoftwareIndexCount + 2UL * full.GpuIndexCount), full.Alignment), full.ByteCount);
        var regions = new List<ResourceManagerLedgerOwner.ComputeScratch>();
        using var workspace = new NativeSmartCoordinatorWorkspace(in configuration, bytes =>
        {
            var region = owner.CreateScratch(bytes);
            regions.Add(region);
            return region;
        });
        Assert.Empty(regions); // Nothing opens before a round.
        var referenceActions = new NativeSmartCoordinatorAction[reference.Capacity.ActionCapacity];
        owner.SetMode(LedgerTableMode.Optimize);
        var answered = 0;
        // Rounds 1-3 hold 40 processes in 5 software groups with 3 GPU rows; round 4 adds
        // 24 processes, one group and one GPU row, so its facts, plan and feedback grow.
        foreach (var (sequence, processes, software, gpus) in new[] { (1UL, 40, 5, 3), (2UL, 40, 5, 3), (3UL, 40, 5, 3), (4UL, 64, 6, 4) })
        {
            var facts = Facts(processes, software, gpus);
            Assert.True((uint)facts.Length <= workspace.Capacity.InputRowCapacity && (uint)processes <= workspace.Capacity.ProcessCapacity &&
                (uint)processes <= workspace.Capacity.ActionCapacity && (uint)software <= workspace.Capacity.SoftwareCapacity &&
                (uint)gpus <= workspace.Capacity.GpuStateCapacity);
            var first = regions.Count;
            workspace.EnsureRows((uint)facts.Length);
            var cycle = Cycle(configuration.Generation, workspace.Capacity.ActionCapacity, sequence);
            cycle.InputCount = (uint)facts.Length;
            using (var operation = workspace.BeginOperation())
            {
                Fill(workspace, facts);
                Assert.Equal(NativeSmartCoordinatorStatus.Ok, workspace.Plan(in cycle));
                var actions = workspace.PlannedActions();
                var opened = regions.Skip(first).ToArray();
                var plan = workspace.PlanLayout!;
                var feedbackRows = Math.Min(workspace.PlannedActionCount, workspace.Capacity.FeedbackCapacity);
                output.WriteLine(JsonSerializer.Serialize(new
                {
                    sequence, facts = facts.Length, processes, software, gpus, actionRows = plan.ActionCapacity,
                    staging = plan.NativeScratchByteCount, feedbackRows, regions = opened.Select(static region => region.Length)
                }));
                // N facts and their measure table; A candidate rows and the P/S/G staging
                // columns; one feedback row per issued action.
                var n = (ulong)facts.Length;
                Assert.Equal(NativeSmartColumns.Round(104UL * n + 4UL * Slots(n)), opened[0].Length);
                var staging = AlignUp(112UL * (ulong)processes + 120UL * (ulong)software + (72UL + 48UL) * (ulong)gpus
                    + 4UL * (Slots((ulong)processes) + Slots((ulong)software) + 2UL * Slots((ulong)gpus)), full.Alignment);
                Assert.Equal(staging, plan.NativeScratchByteCount);
                Assert.Equal(processes, plan.ActionCapacity); // Every staged process is one candidate row.
                Assert.Equal(NativeSmartColumns.Round(120UL * (ulong)processes + full.Alignment - 1 + staging), opened[1].Length);
                if (feedbackRows == 0) Assert.Equal(2, opened.Length);
                else Assert.Equal(96UL * feedbackRows, Assert.Single(opened.Skip(2)).Length);
                Assert.Equal(opened.Where(static region => region.InLedger).Aggregate(0UL, static (sum, region) => sum + region.Length),
                    owner.ReadTable().RegisteredBytes);
                await Task.Yield();
                Assert.Equal(0UL, owner.Tick().Released); // The round's leases keep its regions across await.

                // A full-capacity reference sees the same facts; exact sizing loses no action.
                NativeSmartCoordinatorSnapshot referenceSnapshot = default;
                Assert.Equal(NativeSmartCoordinatorStatus.Ok,
                    reference.PlanCurrent(in cycle, facts, referenceActions, ref referenceSnapshot));
                Assert.Equal(referenceActions[..checked((int)referenceSnapshot.ActionCount)], actions);
                var feedback = actions.Where(static action => action.Flags.HasFlag(NativeSmartCoordinatorActionFlags.RequiresFeedback))
                    .Select(Succeeded).ToArray();
                for (var index = 0; index < feedback.Length; index++)
                    workspace.SetFeedback((uint)index, feedback[index]);
                answered += feedback.Length;
                if (feedback.Length > 0)
                {
                    Assert.Equal(NativeSmartCoordinatorStatus.Ok, workspace.ApplyFeedback((uint)feedback.Length));
                    Assert.Equal(NativeSmartCoordinatorStatus.Ok, reference.ApplyFeedback(feedback, ref referenceSnapshot));
                    Assert.Equal(referenceSnapshot.StateRevision, workspace.Snapshot.StateRevision);
                }
                operation.Complete();
            }
            Assert.All(regions, static region => Assert.True(region.Closed));
            Assert.Empty(owner.ReadWorkspaceCapacities());
            Assert.Equal(0UL, owner.ReadTable().RegisteredBytes);
            Assert.Equal(0UL, owner.Tick().Released);
        }
        Assert.True(answered > 0); // Some round issued actions that needed feedback rows.
        Assert.Equal(NativeSmartCoordinatorStatus.Ok, workspace.RefreshSnapshot());
        Assert.Equal(0UL, owner.ReadTable().RegisteredBytes); // Canonical snapshots do not borrow staging.
        var referenceRows = new NativeSmartCoordinatorSnapshotRow[reference.Capacity.SnapshotRowCapacity];
        NativeSmartCoordinatorSnapshot referenceAfter = default;
        Assert.Equal(NativeSmartCoordinatorStatus.Ok, reference.GetSnapshot(ref referenceAfter, referenceRows));
        Assert.Equal(referenceRows[..checked((int)referenceAfter.SnapshotRowCount)], workspace.CurrentSnapshotRows.ToArray());
        workspace.Dispose();
        Assert.Equal(0UL, owner.ReadTable().RegisteredBytes);
    }

    [Fact]
    public void FailedCloseRetainsTheOpenRegionAndNativeSessionForExplicitRetry()
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
        workspace.EnsureRows(16); // The next round's fact region, open before its operation.
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
        var requirements = session.ScratchRequirements;
        Assert.Equal(NativeSmartCoordinatorStatus.InvalidArgument,
            session.Plan(in input, [], [], IntPtr.Zero, in requirements, ref rejected));
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
        var process = Process(0, 1001,
            sequence > 3 ? NativeSmartCoordinatorProcessGrade.Level3 : NativeSmartCoordinatorProcessGrade.Normal);
        Fill(workspace, [process, CpuMetric(process)]);
        var cycle = Cycle(generation, workspace.Capacity.ActionCapacity, sequence);
        cycle.InputCount = 2;
        Assert.Equal(NativeSmartCoordinatorStatus.Ok, workspace.Plan(in cycle));
        return workspace.PlannedActions();
    }

    private static void Fill(NativeSmartCoordinatorWorkspace workspace, ReadOnlySpan<NativeSmartCoordinatorInputRow> facts)
    {
        using var view = workspace.CreateScratchView();
        facts.CopyTo(view.Inputs);
    }

    // P processes over S software groups: an identity row and a CPU metric row each,
    // then one GPU metric row for each of the first G processes.
    private static NativeSmartCoordinatorInputRow[] Facts(int processes, int software, int gpus)
    {
        var rows = new NativeSmartCoordinatorInputRow[2 * processes + gpus];
        for (var index = 0; index < processes; index++)
        {
            var process = Process((ulong)index, 1001UL + (ulong)(index % software), NativeSmartCoordinatorProcessGrade.Normal);
            rows[2 * index] = process;
            rows[2 * index + 1] = CpuMetric(process);
            if (index < gpus) rows[2 * processes + index] = GpuMetric(process);
        }
        // Native accepts a row only when its source index is its position, so every software and GPU row is staged.
        for (var index = 0; index < rows.Length; index++) rows[index].SourceIndex = (uint)index;
        return rows;
    }

    private static NativeSmartCoordinatorInputRow Process(ulong index, ulong softwareKey, NativeSmartCoordinatorProcessGrade applied)
        => new()
        {
            StructSize = NativeSmartCoordinatorSession.SizeOf<NativeSmartCoordinatorInputRow>(),
            SoftwareKind = NativeSmartCoordinatorSoftwareKind.GeneralApplication,
            ValidMask = NativeSmartCoordinatorInputValidity.Known & ~(NativeSmartCoordinatorInputValidity.Metric |
                NativeSmartCoordinatorInputValidity.ProcessScore | NativeSmartCoordinatorInputValidity.SoftwareScore |
                NativeSmartCoordinatorInputValidity.ScoreMemberCount),
            Flags = NativeSmartCoordinatorInputFlags.Running | NativeSmartCoordinatorInputFlags.ProcessCpuMetricsComplete |
                NativeSmartCoordinatorInputFlags.ProcessGpuMetricsComplete | NativeSmartCoordinatorInputFlags.CanApplyProcessPolicy |
                NativeSmartCoordinatorInputFlags.HardwareSchedulingEligible,
            TargetKey = 101 + index, SoftwareKey = softwareKey, ProcessStartKey = 111 + index, BaseScore = 20,
            ProcessId = checked((uint)(11 + index)), AppliedProcessGrade = applied
        };

    private static NativeSmartCoordinatorInputRow CpuMetric(NativeSmartCoordinatorInputRow process)
    {
        var metric = process;
        metric.SourceIndex = 1;
        metric.MetricKind = NativeSmartCoordinatorMetricKind.CpuUsagePercent;
        metric.MetricValue = 50;
        metric.ValidMask |= NativeSmartCoordinatorInputValidity.Metric | NativeSmartCoordinatorInputValidity.ProcessScore |
            NativeSmartCoordinatorInputValidity.SoftwareScore | NativeSmartCoordinatorInputValidity.ScoreMemberCount;
        metric.ProcessScore = metric.SoftwareScore = 4.5;
        metric.ScoreMemberCount = 1;
        return metric;
    }

    private static NativeSmartCoordinatorInputRow GpuMetric(NativeSmartCoordinatorInputRow process)
    {
        var metric = process;
        metric.SourceIndex = 1;
        metric.MetricKind = NativeSmartCoordinatorMetricKind.GpuUsagePercent;
        metric.MetricValue = 30;
        metric.ValidMask |= NativeSmartCoordinatorInputValidity.Metric;
        return metric;
    }

    // Native hash columns keep at least half their slots empty: the power of two at or above 2n, at least 2.
    private static ulong Slots(ulong count)
    {
        ulong slots = 2;
        while (slots < 2 * count) slots *= 2;
        return slots;
    }

    private static ulong AlignUp(ulong value, uint alignment) => checked((value + alignment - 1) / alignment * alignment);

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
