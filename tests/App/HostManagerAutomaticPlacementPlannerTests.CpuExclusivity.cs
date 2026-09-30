using System.Runtime.CompilerServices;
using ResourceManager.App.Domain.CpuTopology;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerAutomaticPlacementPlannerTests
{
    [Fact]
    public void CpuExclusivityAbiSizesMatchNativeExports()
    {
        Assert.Equal(80, Unsafe.SizeOf<NativeCpuExclusivityConfiguration>());
        Assert.Equal(72, Unsafe.SizeOf<NativeCpuExclusivityFrame>());
        Assert.Equal(32, Unsafe.SizeOf<NativeCpuExclusivitySoftware>());
        Assert.Equal(8, Unsafe.SizeOf<NativeCpuExclusivityCore>());
        Assert.Equal(16, Unsafe.SizeOf<NativeCpuExclusivityUsage>());
        Assert.Equal(8, Unsafe.SizeOf<NativeCpuExclusivityReservation>());
    }

    [Fact]
    public void AutomaticCoreQualifiesOnFourthDistinctWindowAndUsesStrictExitBoundary()
    {
        using var session = CreateExclusiveSession();
        var game = ExclusiveGame("game", 10, 100);
        for (var window = 1; window <= 3; window++)
        {
            Assert.Empty(Reserve(session, [game], window, [(game, new[] { ("core:0", 75d) })]).BySoftware);
            Assert.Empty(Reserve(session, [game], window, [(game, new[] { ("core:0", 75d) })]).BySoftware);
        }
        Assert.Equal(["core:0"], Reserve(session, [game], 4, [(game, new[] { ("core:0", 75d) })]).BySoftware["game"]);
        Assert.Equal(["core:0"], Reserve(session, [game], 5, [(game, new[] { ("core:0", 20d) })]).BySoftware["game"]);
        Assert.Empty(Reserve(session, [game], 6, [(game, new[] { ("core:0", 19.99d) })]).BySoftware);
        Assert.Empty(Reserve(session, [game], 7, [(game, new[] { ("core:0", 74.99d) })]).BySoftware);
    }

    [Fact]
    public void CcdAndCoreStatesTransitionIndependentlyBeforeExpansion()
    {
        using var session = CreateExclusiveSession(qualification: 0);
        var game = ExclusiveGame("game", 10, 100);
        var entered = Reserve(session, [game], 1, [(game, new[] { ("core:0", 75d), ("core:1", 45d) })]);
        Assert.Equal(["core:0", "core:1"], entered.BySoftware["game"].Order());
        var retained = Reserve(session, [game], 2, [(game, new[] { ("core:0", 40d), ("core:1", 20d) })]);
        Assert.Equal(["core:0", "core:1"], retained.BySoftware["game"].Order()); // CCD exactly 30 stays.
        var coreOnly = Reserve(session, [game], 3, [(game, new[] { ("core:0", 50d), ("core:1", 9d) })]);
        Assert.Equal(["core:0"], coreOnly.BySoftware["game"]); // CCD <30 exits, core between thresholds stays.
        Assert.Empty(Reserve(session, [game], 4, [(game, new[] { ("core:0", 19.99d) })]).BySoftware);
    }

    [Fact]
    public void MissingObservationsHoldButPidReuseOwnerExitAndSourceChangesRemoveOldState()
    {
        using var session = CreateExclusiveSession();
        var game = ExclusiveGame("game", 10, 100);
        for (var window = 1; window <= 4; window++) Reserve(session, [game], window, [(game, new[] { ("core:0", 80d) })]);
        Assert.True(Reserve(session, [game], 5, []).IsOwner("game"));
        Assert.True(Reserve(session, [game], 0, []).IsOwner("game"));
        var reused = game with { ProcessStartKey = game.ProcessStartKey + 1 };
        Assert.Empty(Reserve(session, [reused], 6, [(reused, new[] { ("core:0", 80d) })]).BySoftware);
        for (var window = 7; window <= 9; window++) Reserve(session, [reused], window, [(reused, new[] { ("core:0", 80d) })]);
        Assert.True(Reserve(session, [reused], 9, [(reused, new[] { ("core:0", 80d) })]).IsOwner("game"));
        Assert.Empty(Reserve(session, [reused], 10, [(reused, new[] { ("core:0", 80d) })], source: 2).BySoftware);
        Assert.Empty(Reserve(session, [], 11, []).BySoftware);
    }

    [Fact]
    public void WholeSoftwareOccupancyAggregatesAndOwnersAreNotSoftLocked()
    {
        using var session = CreateExclusiveSession(qualification: 0);
        var gameA = ExclusiveGame("game", 10, 100);
        var gameB = ExclusiveGame("game", 11, 100);
        var background = CreateProcess("background", 20, 5, null, AutoPolicy() with { CpuMaximumOccupancyMode = CpuMaximumOccupancyModes.AllCores });
        var reservations = Reserve(session, [gameA, gameB, background], 1,
            [(gameA, new[] { ("core:0", 40d) }), (gameB, new[] { ("core:0", 35d) })]);
        Assert.Equal(["core:0"], reservations.BySoftware["game"]);
        var cpu = PlanWithReservations([gameA, gameB, background], reservations).Cpu;
        var effect = Assert.Single(cpu);
        Assert.Equal(background.TargetId, effect.Process.TargetId);
        Assert.Equal("cpu-exclusive-avoidance", effect.Source);
        Assert.Equal(["core:1", "core:2", "core:3"], effect.PhysicalCoreIds);
    }

    [Fact]
    public void ManualOwnersAndAutomaticCcdUnionLeaveCapacityForOthers()
    {
        using var session = CreateExclusiveSession(qualification: 0);
        var manual = CreateProcess("manual", 10, 200, null, AutoPolicy() with { CpuManualExclusivePositionIds = ["core:0", "core:1"] }) with { SoftwareId = "manual" };
        var game = ExclusiveGame("game", 20, 100);
        var reservations = Reserve(session, [manual, game], 1, [(game, new[] { ("core:2", 60d), ("core:3", 60d) })]);
        Assert.Equal(["core:0", "core:1"], reservations.BySoftware["manual"].Order());
        Assert.False(reservations.IsOwner("game")); // The whole second CCD would consume the last free capacity.
        Assert.Equal(["core:0", "core:1"], reservations.ExcludedFor("game").Order());
        var all = manual with { Policy = manual.Policy with { CpuManualExclusivePositionIds = ["core:0", "core:1", "core:2", "core:3"] } };
        var clipped = Reserve(session, [all], 2, []);
        Assert.Equal(3, clipped.BySoftware["manual"].Count);
        Assert.Empty(clipped.ExcludedFor("manual"));
    }

    [Fact]
    public void AvoidanceCombinesWithExplicitLocksAndSingleCcdButDoesNotProduceEmptySelectors()
    {
        var owner = ExclusiveGame("owner", 10, 100);
        var locked = CreateProcess("locked", 20, 20, null, AutoPolicy() with { CpuManualLockedPositionIds = ["core:0", "core:2"] });
        var ordinary = CreateProcess("ordinary", 30, 5, null, AutoPolicy());
        var reservations = new HostManagerCpuReservations(new Dictionary<string, IReadOnlySet<string>>
            { ["owner"] = new HashSet<string>(["core:0", "core:1"]) });
        var effects = PlanWithReservations([owner, locked, ordinary], reservations).Cpu;
        Assert.Equal(["core:2"], effects.Single(item => item.Process.TargetId == locked.TargetId).PhysicalCoreIds);
        Assert.Equal(["core:2", "core:3"], effects.Single(item => item.Process.TargetId == ordinary.TargetId).PhysicalCoreIds);
        var impossible = locked with { Policy = locked.Policy with { CpuManualLockedPositionIds = ["core:0"] } };
        Assert.Empty(PlanWithReservations([owner, impossible], reservations).Cpu);
    }

    [Fact]
    public void DisabledConfigurationRemovesAutomaticButPreservesManualReservations()
    {
        using var session = CreateExclusiveSession(qualification: 0, enabled: false);
        var game = ExclusiveGame("game", 10, 100) with { Policy = AutoPolicy() with { CpuManualExclusivePositionIds = ["core:2"] } };
        var reservations = Reserve(session, [game], 1, [(game, new[] { ("core:0", 100d) })]);
        Assert.Equal(["core:2"], reservations.BySoftware["game"]);
    }

    [Fact]
    public void CapacityFailureAndStaleFramesDoNotAdvanceQualification()
    {
        using var session = CreateExclusiveSession(qualification: 0);
        var game = ExclusiveGame("game", 10, 100);
        var projection = ExclusiveProjection([game], 1, [(game, new[] { ("core:0", 80d) })]);
        var failed = projection.Frame; failed.OutputCapacity = 0;
        Assert.Equal(NativePlacementCoordinatorStatus.BufferTooSmall, session.PlanCpuExclusivity(ref failed,
            projection.Cores, projection.Software, projection.Usage, projection.Manual, []));
        Assert.True(Reserve(session, [game], 1, [(game, new[] { ("core:0", 80d) })]).IsOwner("game"));
        Assert.True(Reserve(session, [game], 3, [(game, new[] { ("core:0", 50d) })]).IsOwner("game"));
        var stale = ExclusiveProjection([game], 2, [(game, new[] { ("core:0", 0d) })]);
        var frame = stale.Frame;
        Assert.Equal(NativePlacementCoordinatorStatus.StaleFrame, session.PlanCpuExclusivity(ref frame, stale.Cores,
            stale.Software, stale.Usage, stale.Manual, stale.Output));
        Assert.True(Reserve(session, [game], 4, []).IsOwner("game"));
    }

    private static HostManagerAutomaticPlacementProcess ExclusiveGame(string software, int pid, double score)
        => CreateProcess(software, pid, score, null, AutoPolicy()) with { SoftwareKind = "game", SoftwareId = software };

    [Fact]
    public void ObservationFactCapacityUsesSoftwareTimesCoresRatherThanReservationCount()
    {
        using var session = CreateExclusiveSession(qualification: 0);
        var game = ExclusiveGame("game", 10, 100);
        var processes = new[] { game }.Concat(Enumerable.Range(20, 4).Select(pid => CreateProcess($"other-{pid}", pid, 5, null, AutoPolicy()))).ToArray();
        var facts = processes.Select(process => (process,
            new[] { ("core:0", process == game ? 75d : 1d), ("core:1", 0d), ("core:2", 0d), ("core:3", 0d) })).ToArray();
        Assert.Equal(["core:0"], Reserve(session, processes, 1, facts).BySoftware["game"]);
        Assert.Equal(NativePlacementCoordinatorStatus.Ok, session.ResetCpuExclusivity());
        Assert.Empty(Reserve(session, processes, 0, []).BySoftware);
    }

    private static HostManagerAutomaticPlacementPlan PlanWithReservations(HostManagerAutomaticPlacementProcess[] processes,
        HostManagerCpuReservations reservations)
        => HostManagerAutomaticPlacementPlanner.Plan(CreateTwoCcdTopology(), CreateHardware(), CreateHardwareScores(),
            processes, CreateCapacity(), gpuPlacementEnabled: false, cpuReservations: reservations);

    private static NativePlacementCoordinatorSession CreateExclusiveSession(uint qualification = 3, bool enabled = true)
    {
        var configuration = new NativePlacementCoordinatorConfiguration
        {
            AbiVersion = NativePlacementCoordinatorAbi.Version, StructSize = 80, Generation = 1,
            MaximumDesiredCount = 16, MaximumAppliedCount = 16, MaximumActionCount = 16, MaximumStateCount = 16,
            RetryDelayMilliseconds = 10, ActionTimeoutMilliseconds = 100, MaximumFutureSkewMilliseconds = 10
        };
        var session = new NativePlacementCoordinatorSession(in configuration);
        var cpu = new NativeCpuExclusivityConfiguration
        {
            AbiVersion = NativePlacementCoordinatorAbi.Version, StructSize = 80, Generation = 1,
            CoreCapacity = 4, CcdCapacity = 2, SoftwareCapacity = 16, ReservationCapacity = 16,
            CoreEnter = 75, CoreExit = 20, CcdEnter = 60, CcdExit = 30, QualificationRounds = qualification, Enabled = enabled ? 1U : 0U
        };
        Assert.Equal(NativePlacementCoordinatorStatus.Ok, session.ConfigureCpuExclusivity(in cpu));
        return session;
    }

    private static HostManagerCpuReservations Reserve(NativePlacementCoordinatorSession session, HostManagerAutomaticPlacementProcess[] processes,
        int window, (HostManagerAutomaticPlacementProcess Process, (string Core, double Usage)[] Uses)[] facts, long source = 1)
    {
        var projection = ExclusiveProjection(processes, window, facts, source);
        var frame = projection.Frame;
        Assert.Equal(NativePlacementCoordinatorStatus.Ok, session.PlanCpuExclusivity(ref frame, projection.Cores,
            projection.Software, projection.Usage, projection.Manual, projection.Output));
        return projection.Map(frame.OutputCount);
    }

    private static HostManagerCpuExclusivityProjection ExclusiveProjection(HostManagerAutomaticPlacementProcess[] processes,
        int window, (HostManagerAutomaticPlacementProcess Process, (string Core, double Usage)[] Uses)[] facts, long source = 1)
        => HostManagerCpuExclusivityProjection.Create(CreateTwoCcdTopology(), window == 0 ? null :
            CpuCoreResidencyTestValues.Create(source, DateTimeOffset.UnixEpoch.AddSeconds(window),
                facts.Select(fact => CpuCoreResidencyTestValues.Process(fact.Process.ProcessId, fact.Process.ProcessStartKey, fact.Uses)).ToArray()),
            processes, 1, CreateCapacity());
}
