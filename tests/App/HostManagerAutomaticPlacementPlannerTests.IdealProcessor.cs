using System.Security.Cryptography;
using System.Text;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Infrastructure.Optimization;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerAutomaticPlacementPlannerTests
{
    [Fact]
    public void AllCoresScoresOccupyCorePositionsAndOtherSoftwareAvoidsTheHighScore()
    {
        var high = CreateProcess("high", 10, 80, null, AutoPolicy() with { CpuMaximumOccupancyMode = CpuMaximumOccupancyModes.AllCores });
        var low = CreateProcess("low", 20, 10, null, high.Policy);
        var plans = HostManagerAutomaticPlacementPlanner.PlanIdealProcessors(CreateTwoCcdTopology(), CreateHardwareScores(),
            [new(low, [IdealThread(low, 1, 0)], []), new(high, [IdealThread(high, 2, 0)], [])], [], null);
        Assert.Equal(["high", "low"], plans.Select(static p => p.Process.TargetId));
        Assert.Equal(0, plans[0].Processor.GroupRelativeIndex);
        Assert.NotEqual(plans[0].Processor.PhysicalCoreId, plans[1].Processor.PhysicalCoreId);
        // A preferred processor is a hint, never a one-core CpuAffinityPlan.
        Assert.Equal(15UL, plans[0].Thread.AllowedMask);
        Assert.Empty(plans[0].Thread.SelectedCpuSetIds);
    }

    [Fact]
    public void AllCoresCanPreferProcessorsAcrossBothCcdsWithoutASingleCcdCap()
    {
        var process = CreateProcess("parallel", 10, 80, null, AutoPolicy() with { CpuMaximumOccupancyMode = CpuMaximumOccupancyModes.AllCores });
        var threads = Enumerable.Range(1, 4).Select(id => IdealThread(process, id, 0)).ToArray();
        var plans = HostManagerAutomaticPlacementPlanner.PlanIdealProcessors(CreateTwoCcdTopology(), CreateHardwareScores(),
            [new(process, threads, [])], [], null);
        Assert.Equal(4, plans.Count);
        Assert.Equal(2, plans.Select(static p => p.Processor.CcdId).Distinct().Count());
        Assert.Equal(4, plans.Select(static p => p.Processor.PhysicalCoreId).Distinct().Count());
    }

    [Fact]
    public void IdealPreferenceRespectsThreadGroupMaskAndExplicitThreadCpuSets()
    {
        var process = CreateProcess("restricted", 10, 80, null, AutoPolicy() with { CpuMaximumOccupancyMode = CpuMaximumOccupancyModes.AllCores });
        var thread = IdealThread(process, 1, 2) with { AllowedMask = 12, SelectedCpuSetIds = [3] };
        var plans = HostManagerAutomaticPlacementPlanner.PlanIdealProcessors(CreateTwoCcdTopology(), CreateHardwareScores(),
            [new(process, [thread], [4])], [], null);
        Assert.Equal(2, Assert.Single(plans).Processor.GroupRelativeIndex); // thread selection overrides process default
        var noThreadSets = HostManagerAutomaticPlacementPlanner.PlanIdealProcessors(CreateTwoCcdTopology(), CreateHardwareScores(),
            [new(process, [thread with { SelectedCpuSetIds = [] }], [4])], [], null);
        Assert.Equal(3, Assert.Single(noThreadSets).Processor.GroupRelativeIndex);
    }

    [Fact]
    public void ExplicitSingleCcdAndUnverifiableThreadIdentitiesGetNoSoftOverride()
    {
        var process = CreateProcess("explicit", 10, 80, null, AutoPolicy());
        Assert.Empty(HostManagerAutomaticPlacementPlanner.PlanIdealProcessors(CreateTwoCcdTopology(), CreateHardwareScores(),
            [new(process, [IdealThread(process, 1, 0)], [])], [], null));
        process = process with { Policy = process.Policy with { CpuMaximumOccupancyMode = CpuMaximumOccupancyModes.AllCores } };
        Assert.Empty(HostManagerAutomaticPlacementPlanner.PlanIdealProcessors(CreateTwoCcdTopology(), CreateHardwareScores(),
            [new(process, [IdealThread(process, 1, 0) with { ProcessStartKey = 1 }], [])], [], null));
    }

    [Fact]
    public void EqualInputAndThreadSortKeysKeepTheirOriginalOrder()
    {
        var process = CreateProcess("stable", 10, 80, null,
            AutoPolicy() with { CpuMaximumOccupancyMode = CpuMaximumOccupancyModes.AllCores });
        var firstThread = IdealThread(process, 4, 0) with { ThreadId = 7, ThreadStartKey = 107 };
        var secondThread = IdealThread(process, 1, 0) with { ThreadId = 7, ThreadStartKey = 108 };
        var firstInput = new HostManagerIdealProcessorInput(process, [firstThread], []);
        var secondInput = new HostManagerIdealProcessorInput(process, [secondThread], []);

        var plans = HostManagerAutomaticPlacementPlanner.PlanIdealProcessors(
            CreateTwoCcdTopology(), CreateHardwareScores(), [firstInput, secondInput], [], null);

        Assert.Equal([107UL, 108UL], plans.Select(static plan => plan.Thread.ThreadStartKey));
    }

    [Fact]
    public void DuplicateThreadSortKeysKeepTheirOriginalOrder()
    {
        var process = CreateProcess("duplicate-thread-sort", 10, 80, null,
            AutoPolicy() with { CpuMaximumOccupancyMode = CpuMaximumOccupancyModes.AllCores });
        var firstThread = IdealThread(process, 4, 0) with { ThreadId = 7, ThreadStartKey = 107 };
        var secondThread = IdealThread(process, 1, 0) with { ThreadId = 7, ThreadStartKey = 108 };

        var plans = HostManagerAutomaticPlacementPlanner.PlanIdealProcessors(
            CreateTwoCcdTopology(), CreateHardwareScores(), [new(process, [firstThread, secondThread], [])], [], null);

        Assert.Equal([107UL, 108UL], plans.Select(static plan => plan.Thread.ThreadStartKey));
    }

    [Fact]
    public void DuplicateCoreAndLogicalProcessorSortKeysKeepTheirTopologyOrder()
    {
        var process = CreateProcess("duplicate-keys", 10, 80, null,
            AutoPolicy() with { CpuMaximumOccupancyMode = CpuMaximumOccupancyModes.AllCores });
        var topology = CreateTwoCcdTopology();
        var duplicateCore = topology.PhysicalCores[0] with { Id = "core:duplicate" };
        var duplicateProcessor = topology.LogicalProcessors[0] with
        {
            Id = 99,
            PhysicalCoreId = duplicateCore.Id
        };
        topology = topology with
        {
            PhysicalCores = [duplicateCore, .. topology.PhysicalCores],
            LogicalProcessors = [duplicateProcessor, .. topology.LogicalProcessors]
        };

        var plans = HostManagerAutomaticPlacementPlanner.PlanIdealProcessors(
            topology, CreateHardwareScores(), [new(process, [IdealThread(process, 1, 0)], [])], [], null);

        var plan = Assert.Single(plans);
        Assert.Equal(duplicateCore.Id, plan.Processor.PhysicalCoreId);
        Assert.Equal(99, plan.Processor.Id);
    }

    [Fact]
    public void StableThreadIdsFollowOnlyTheExistingThreadIdentityLifetime()
    {
        var process = CreateProcess("stable", 10, 80, null,
            AutoPolicy() with { CpuMaximumOccupancyMode = CpuMaximumOccupancyModes.AllCores });
        var thread = IdealThread(process, 3, 0);
        var workspace = new HostManagerIdealProcessorWorkspace();

        workspace.Inputs.Add(new(process, [thread], []));
        workspace.ReconcileStableRecordIds();
        Assert.True(workspace.TryGetStableRecordId(process.TargetId, thread, out var originalId));
        Assert.Equal(LegacyStableId(
            $"automatic-placement|ideal|{process.TargetId}|{thread.ProcessStartKey}|{thread.ThreadId}|{thread.ThreadStartKey}"),
            originalId);

        workspace.Inputs.Clear();
        var equivalentProcess = process with { TargetId = process.TargetId.ToUpperInvariant(), CanonicalCpuScore = 20 };
        var equivalentThread = thread with
        {
            IdealProcessor = new(0, 2),
            AllowedMask = 12,
            SelectedCpuSetIds = [3]
        };
        workspace.Inputs.Add(new(equivalentProcess, [equivalentThread], []));
        workspace.ReconcileStableRecordIds();
        Assert.True(workspace.TryGetStableRecordId(equivalentProcess.TargetId, equivalentThread, out var reusedId));
        Assert.Equal(originalId, reusedId);
        Assert.Single(workspace.StableRecordIds);

        workspace.Inputs.Clear();
        var replacementThread = equivalentThread with { ThreadStartKey = equivalentThread.ThreadStartKey + 1 };
        workspace.Inputs.Add(new(equivalentProcess, [replacementThread], []));
        workspace.ReconcileStableRecordIds();
        Assert.False(workspace.StableRecordIds.ContainsKey(new(
            process.TargetId, thread.ProcessStartKey, thread.ThreadId, thread.ThreadStartKey)));
        Assert.True(workspace.TryGetStableRecordId(equivalentProcess.TargetId, replacementThread, out var replacementId));
        Assert.NotEqual(originalId, replacementId);
        Assert.Single(workspace.StableRecordIds);

        workspace.Inputs.Clear();
        workspace.ReconcileStableRecordIds();
        Assert.Empty(workspace.StableRecordIds);
    }

    [Fact]
    public void WarmIdealProcessorPlannerUsesRetainedScratchWithoutPerTickAllocations()
    {
        var process = CreateProcess("warm", 10, 80, null,
            AutoPolicy() with { CpuMaximumOccupancyMode = CpuMaximumOccupancyModes.AllCores });
        var threads = Enumerable.Range(1, 4)
            .Select(id => IdealThread(process, id, 0))
            .ToArray();
        var workspace = new HostManagerIdealProcessorWorkspace();
        workspace.Inputs.Add(new(process, threads, []));
        var topology = CreateTwoCcdTopology();
        var hardwareScores = CreateHardwareScores();

        for (var index = 0; index < 4; index++)
        {
            HostManagerAutomaticPlacementPlanner.PlanIdealProcessorsIntoWorkspace(
                topology, hardwareScores, [], null, workspace);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 32; index++)
        {
            HostManagerAutomaticPlacementPlanner.PlanIdealProcessorsIntoWorkspace(
                topology, hardwareScores, [], null, workspace);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(4, workspace.Plans.Count);
        Assert.InRange(allocated, 0, 512);
    }

    [Fact]
    public void StableIdHashMatchesThePreviousValueWithoutTransientHashArrays()
    {
        const string input = "automatic-placement|ideal|Game-ß|133900000000000001|10001|133900000000000002";
        var expected = LegacyStableId(input);
        Assert.Equal(expected, HostManagerSmartCoordinator.CreateStableId(input));
        var pooledInput = string.Concat(new string('x', 1_100), "-identity-ß");
        Assert.Equal(LegacyStableId(pooledInput), HostManagerSmartCoordinator.CreateStableId(pooledInput));

        for (var index = 0; index < 3; index++)
        {
            _ = HostManagerSmartCoordinator.CreateStableId(input);
        }
        var before = GC.GetAllocatedBytesForCurrentThread();
        string? last = null;
        for (var index = 0; index < 32; index++)
        {
            last = HostManagerSmartCoordinator.CreateStableId(input);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(expected, last);
        Assert.InRange(allocated, 32 * 24, 32 * 128);
    }

    private static string LegacyStableId(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value.ToUpperInvariant()));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    private static ThreadIdealProcessorPolicySnapshot IdealThread(HostManagerAutomaticPlacementProcess process, int id, byte ideal)
        => new(process.ProcessId, process.ProcessStartKey, id, checked((ulong)id + 100), new(0, ideal), 0, 15, []);
}
