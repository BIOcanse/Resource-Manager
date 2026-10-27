using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerComputeScoringWorkspaceTests
{
    [Fact]
    public void EmptyTrustedResidencyWindowScoresEveryCoveredProcessAtZero()
    {
        var configuration = CreateConfiguration();
        using var workspace = new HostManagerComputeScoringWorkspace(in configuration,
            HostManagerTestPlanFactory.CreateCpuScoring(HostManagerTestPlanFactory.CreateCpuTopology(1)), CreateScratchFixture);
        var inventory = CreateInventory();
        var physical = CpuCoreResidencyTestValues.Create(91, DateTimeOffset.UtcNow);

        var result = workspace.Score(1, CreateFacts(inventory, 10), inventory, CreateRuntimeFacts(), physical);

        Assert.Equal(HostManagerCpuOccupancySource.CoreResidency, result?.CpuOccupancySource);
        Assert.Equal(2, result!.Cpu!.Scores.Count(row => row.Kind == NativeComputeScoringOutputKind.ProcessCpu));
        Assert.All(result.Cpu.Scores, row => Assert.Equal(0, row.Score));
        Assert.Equal(2U, Assert.Single(result.Cpu.Scores, row => row.Kind == NativeComputeScoringOutputKind.SoftwareCpu).MemberCount);
        Assert.NotNull(result.Gpu);
    }

    [Fact]
    public void ProcessBornAfterResidencyWindowIsNotCoveredAndKeepsGpu()
    {
        var configuration = CreateConfiguration();
        using var workspace = new HostManagerComputeScoringWorkspace(in configuration,
            HostManagerTestPlanFactory.CreateCpuScoring(HostManagerTestPlanFactory.CreateCpuTopology(1)), CreateScratchFixture);
        var inventory = CreateInventory();
        var physical = CpuCoreResidencyTestValues.Create(91, DateTimeOffset.UtcNow);
        var futureStartKey = checked((ulong)physical.MeasuredThrough.AddSeconds(1).ToFileTime());
        var facts = CreateFacts(inventory, 10);
        facts = facts with { Processes = facts.Processes.Select(process => process.ProcessId == 20
            ? process with { ProcessStartKey = futureStartKey } : process).ToArray() };
        var runtime = CreateRuntimeFacts().ToDictionary(pair => pair.Key.ProcessId == 20
            ? new HostManagerComputeProcessIdentity(20, futureStartKey) : pair.Key, pair => pair.Value);
        var exclusions = new List<ResourceManager.App.Domain.ResourceBreakdown.SchedulingFactExclusion>();

        var result = workspace.Score(1, facts, inventory, runtime, physical, exclusions: exclusions);

        Assert.Equal(10, Assert.Single(result!.Cpu!.Scores, row => row.Kind == NativeComputeScoringOutputKind.ProcessCpu).ProcessId);
        Assert.Contains(result.Gpu!.Scores, row => row.ProcessId == 20);
        Assert.Contains(exclusions, row => row.ProcessId == 20 && row.Reason == SchedulingFactExclusionReason.CpuResidencyNotCovered);
    }

    [Theory]
    [InlineData("session")]
    [InlineData("window")]
    [InlineData("reversed")]
    [InlineData("duration")]
    [InlineData("capture-before-window")]
    public void UntrustedResidencyDatasetUsesExplicitScalarCpuSource(string invalid)
    {
        var configuration = CreateConfiguration();
        using var workspace = new HostManagerComputeScoringWorkspace(in configuration,
            HostManagerTestPlanFactory.CreateCpuScoring(HostManagerTestPlanFactory.CreateCpuTopology(1)), CreateScratchFixture);
        var inventory = CreateInventory();
        var physical = CpuCoreResidencyTestValues.Create(91, DateTimeOffset.UtcNow);
        physical = invalid switch
        {
            "session" => physical with { SessionGeneration = 0 },
            "window" => physical with { Window = TimeSpan.Zero },
            "reversed" => physical with { MeasuredThrough = physical.MeasuredFrom.AddSeconds(-1) },
            "duration" => physical with { MeasuredFrom = physical.MeasuredFrom.AddSeconds(1) },
            _ => physical with { CapturedAt = physical.MeasuredThrough.AddSeconds(-1) }
        };

        var result = workspace.Score(1, CreateFacts(inventory, 10), inventory, CreateRuntimeFacts(), physical);

        Assert.Equal(HostManagerCpuOccupancySource.ProcessCpuUsage, result?.CpuOccupancySource);
        Assert.True(Assert.Single(result!.Cpu!.Scores, row => row.Kind == NativeComputeScoringOutputKind.ProcessCpu
            && row.ProcessId == 10).Score > 0);
        Assert.NotNull(result.Gpu);
    }

    [Theory]
    [InlineData("unknown-core")]
    [InlineData("blank-core")]
    [InlineData("nan")]
    [InlineData("negative")]
    [InlineData("over-percent")]
    [InlineData("duplicate-core")]
    [InlineData("missing-start-key")]
    [InlineData("empty-cores")]
    public void MalformedResidencyRowOmitsOnlyThatProcessAndRecordsInvalid(string invalid)
    {
        var configuration = CreateConfiguration();
        using var workspace = new HostManagerComputeScoringWorkspace(in configuration,
            HostManagerTestPlanFactory.CreateCpuScoring(HostManagerTestPlanFactory.CreateCpuTopology(1)), CreateScratchFixture);
        var inventory = CreateInventory();
        var core = invalid == "unknown-core" ? "unknown" : invalid == "blank-core" ? " " : "core:0";
        var use = invalid == "nan" ? double.NaN : invalid == "negative" ? -1 : invalid == "over-percent" ? 101 : 25;
        var bad = invalid == "duplicate-core"
            ? CpuCoreResidencyTestValues.Process(10, FirstStartKey, ("core:0", 25), ("CORE:0", 25))
            : CpuCoreResidencyTestValues.Process(10, FirstStartKey, (core, use));
        if (invalid == "missing-start-key") bad = bad with { ProcessStartKey = null };
        if (invalid == "empty-cores") bad = bad with { PhysicalCores = [] };
        var physical = CpuCoreResidencyTestValues.Create(91, DateTimeOffset.UtcNow, bad,
            CpuCoreResidencyTestValues.Process(20, SecondStartKey, ("core:0", 0)));
        var exclusions = new List<ResourceManager.App.Domain.ResourceBreakdown.SchedulingFactExclusion>();

        var result = workspace.Score(1, CreateFacts(inventory, 10), inventory, CreateRuntimeFacts(), physical, exclusions: exclusions);

        Assert.Equal(20, Assert.Single(result!.Cpu!.Scores, row => row.Kind == NativeComputeScoringOutputKind.ProcessCpu).ProcessId);
        Assert.Contains(result.Gpu!.Scores, row => row.ProcessId == 10);
        Assert.Contains(exclusions, row => row.ProcessId == 10 && row.Reason == SchedulingFactExclusionReason.CpuResidencyInvalid);
    }

    [Fact]
    public void CpuScoreUsesPhysicalCoreRowsInsteadOfRawProcessCpuAndKeepsDisplayValues()
    {
        var topology = HostManagerTestPlanFactory.CreateCpuTopology(2, 1);
        var cpu = CpuScoringPlanCompiler.Compile(topology,
            new Dictionary<int, double> { [0] = 800, [1] = 400 }, 0.5);
        var configuration = CreateConfiguration(0.5, 2);
        using var workspace = new HostManagerComputeScoringWorkspace(in configuration, cpu, CreateScratchFixture);
        var inventory = CreateInventory();
        var facts = CreateFacts(inventory, 99);
        var physical = CpuCoreResidencyTestValues.Create(91, DateTimeOffset.UtcNow,
            CpuCoreResidencyTestValues.Process(10, FirstStartKey, ("core:0", 100)),
            CpuCoreResidencyTestValues.Process(20, SecondStartKey, ("core:1", 100)));
        var result = workspace.Score(1, facts, inventory, CreateRuntimeFacts(), physical);
        var scores = Assert.IsType<HostManagerComputeScoreDomainSnapshot>(result?.Cpu).Scores;
        Assert.Equal(400d / 3, Assert.Single(scores, row => row.Kind == NativeComputeScoringOutputKind.ProcessCpu && row.ProcessId == 10).Score, 10);
        Assert.Equal(200d / 3, Assert.Single(scores, row => row.Kind == NativeComputeScoringOutputKind.ProcessCpu && row.ProcessId == 20).Score, 10);
        Assert.Equal(200, Assert.Single(scores, row => row.Kind == NativeComputeScoringOutputKind.SoftwareCpu).Score, 10);
        Assert.Equal(50, facts.Processes[0].CpuUsagePercent);
        Assert.Equal(0, facts.Processes[1].CpuUsagePercent);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PhysicalCpuPublicationDoesNotRequireTheOldScalarCpuDataset(bool removeScalarDataset)
    {
        var configuration = CreateConfiguration();
        using var workspace = new HostManagerComputeScoringWorkspace(in configuration,
            HostManagerTestPlanFactory.CreateCpuScoring(HostManagerTestPlanFactory.CreateCpuTopology(1)), CreateScratchFixture);
        var inventory = CreateInventory();
        var facts = CreateFacts(inventory, 99);
        if (removeScalarDataset)
        {
            facts = facts with
            {
                CurrentMetricMask = facts.CurrentMetricMask & ~SchedulingProcessMetricMask.CpuUsage,
                Processes = facts.Processes.Select(process => process with
                {
                    ValidMetricMask = process.ValidMetricMask & ~SchedulingProcessMetricMask.CpuUsage,
                    CpuUsagePercent = 99
                }).ToArray(),
                DatasetObservations = facts.DatasetObservations.Where(pair => pair.Key != SchedulingProcessMetricMask.CpuUsage)
                    .ToDictionary()
            };
        }
        var physical = CpuCoreResidencyTestValues.Create(91, DateTimeOffset.UtcNow,
            CpuCoreResidencyTestValues.Process(10, FirstStartKey, ("core:0", 25)));
        var result = workspace.Score(1, facts, inventory, CreateRuntimeFacts(), physical);
        Assert.Equal(25, Assert.Single(result!.Cpu!.Scores,
            row => row.Kind == NativeComputeScoringOutputKind.SoftwareCpu).Score);
        Assert.Equal(removeScalarDataset ? 99 : 50, facts.Processes[0].CpuUsagePercent);
    }

    [Theory]
    [InlineData(10, 999, "core:0")]
    [InlineData(999, FirstStartKey, "core:0")]
    [InlineData(10, FirstStartKey, "unknown-core")]
    public void UnmatchedCoreIdentityIsZeroAndUnknownCoreOmitsOnlyItsProcess(int pid, ulong startKey, string core)
    {
        var configuration = CreateConfiguration();
        using var workspace = new HostManagerComputeScoringWorkspace(in configuration,
            HostManagerTestPlanFactory.CreateCpuScoring(HostManagerTestPlanFactory.CreateCpuTopology(1)), CreateScratchFixture);
        var inventory = CreateInventory();
        var facts = CreateFacts(inventory, 10);
        var physical = CpuCoreResidencyTestValues.Create(91, DateTimeOffset.UtcNow,
            CpuCoreResidencyTestValues.Process(pid, startKey, (core, 100)));
        var result = workspace.Score(1, facts, inventory, CreateRuntimeFacts(), physical);
        Assert.Equal(HostManagerCpuOccupancySource.CoreResidency, result?.CpuOccupancySource);
        var processScores = result!.Cpu!.Scores.Where(row => row.Kind == NativeComputeScoringOutputKind.ProcessCpu).ToArray();
        Assert.All(processScores, row => Assert.Equal(0, row.Score));
        Assert.Equal(core == "unknown-core" ? 1 : 2, processScores.Length);
        Assert.NotNull(result.Gpu);
    }

    [Fact]
    public void MissingCoreResidencyScoresCpuFromProcessCpuUsage()
    {
        var configuration = CreateConfiguration();
        using var workspace = new HostManagerComputeScoringWorkspace(in configuration,
            HostManagerTestPlanFactory.CreateCpuScoring(HostManagerTestPlanFactory.CreateCpuTopology(1)), CreateScratchFixture);
        var inventory = CreateInventory();
        var facts = CreateFacts(inventory, 10);
        var physical = CpuCoreResidencyTestValues.Create(91, DateTimeOffset.UtcNow,
            CpuCoreResidencyTestValues.Process(10, FirstStartKey, ("core:0", facts.Processes[0].CpuUsagePercent)));

        var weighted = workspace.Score(1, facts, inventory, CreateRuntimeFacts(), physical);
        var fallback = workspace.Score(2, facts, inventory, CreateRuntimeFacts(), null);

        Assert.Equal(HostManagerCpuOccupancySource.CoreResidency, weighted?.CpuOccupancySource);
        Assert.Equal(HostManagerCpuOccupancySource.ProcessCpuUsage, fallback?.CpuOccupancySource);
        Assert.NotNull(fallback?.Memory);
        // One core with equal weight: the process's machine share equals its weighted core use.
        Assert.Equal(
            Assert.Single(weighted!.Cpu!.Scores, row =>
                row.Kind == NativeComputeScoringOutputKind.ProcessCpu && row.ProcessId == 10).Score,
            Assert.Single(fallback!.Cpu!.Scores, row =>
                row.Kind == NativeComputeScoringOutputKind.ProcessCpu && row.ProcessId == 10).Score,
            10);
    }

    [Fact]
    public void CoreResidencyArrivingAfterProcessCpuUsageStillScores()
    {
        // Live failure: cycles scored from process CPU usage (dataset generation 101) until the kernel
        // session (generation 1) delivered residency; every later cycle was then rejected as stale.
        var configuration = CreateConfiguration();
        using var workspace = new HostManagerComputeScoringWorkspace(in configuration,
            HostManagerTestPlanFactory.CreateCpuScoring(HostManagerTestPlanFactory.CreateCpuTopology(1)), CreateScratchFixture);
        var inventory = CreateInventory();
        var facts = CreateFacts(inventory, 10);
        var physical = CpuCoreResidencyTestValues.Create(1, DateTimeOffset.UtcNow,
            CpuCoreResidencyTestValues.Process(10, FirstStartKey, ("core:0", facts.Processes[0].CpuUsagePercent)));

        var fallback = workspace.Score(1, facts, inventory, CreateRuntimeFacts(), null);
        var weighted = workspace.Score(2, facts, inventory, CreateRuntimeFacts(), physical);
        var recomputed = workspace.Score(2, facts, inventory, CreateRuntimeFacts(), physical);

        Assert.Equal(HostManagerCpuOccupancySource.ProcessCpuUsage, fallback?.CpuOccupancySource);
        Assert.Equal(HostManagerCpuOccupancySource.CoreResidency, weighted?.CpuOccupancySource);
        Assert.NotNull(weighted?.Memory);
        Assert.Equal(2UL, weighted.RoundNumber);
        Assert.Equal<HostManagerComputeScore>(weighted.Cpu!.Scores, recomputed!.Cpu!.Scores);
    }

    [Fact]
    public void EmptyCurrentCpuValueDoesNotRetainThePreviousScoreOrSuppressGpu()
    {
        var configuration = CreateConfiguration();
        using var workspace = new HostManagerComputeScoringWorkspace(in configuration,
            HostManagerTestPlanFactory.CreateCpuScoring(HostManagerTestPlanFactory.CreateCpuTopology(1)), CreateScratchFixture);
        var inventory = CreateInventory();
        var facts = CreateFacts(inventory, 10);
        Assert.NotNull(workspace.Score(1, facts, inventory, CreateRuntimeFacts(), CreateCpuResidency())?.Cpu);
        var result = workspace.Score(2, facts, inventory, CreateRuntimeFacts(), null);
        Assert.Equal(HostManagerCpuOccupancySource.ProcessCpuUsage, result?.CpuOccupancySource);
        Assert.NotNull(result?.Gpu);
    }
}
