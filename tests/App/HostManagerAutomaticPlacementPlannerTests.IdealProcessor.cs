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

    private static ThreadIdealProcessorPolicySnapshot IdealThread(HostManagerAutomaticPlacementProcess process, int id, byte ideal)
        => new(process.ProcessId, process.ProcessStartKey, id, checked((ulong)id + 100), new(0, ideal), 0, 15, []);
}
