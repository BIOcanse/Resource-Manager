using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerComputeScoringWorkspaceTests
{
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
            CpuCoreResidencyTestValues.Process(10, 100, ("core:0", 100)),
            CpuCoreResidencyTestValues.Process(20, 200, ("core:1", 100)));
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
            CpuCoreResidencyTestValues.Process(10, 100, ("core:0", 25)));
        var result = workspace.Score(1, facts, inventory, CreateRuntimeFacts(), physical);
        Assert.Equal(25, Assert.Single(result!.Cpu!.Scores,
            row => row.Kind == NativeComputeScoringOutputKind.SoftwareCpu).Score);
        Assert.Equal(removeScalarDataset ? 99 : 50, facts.Processes[0].CpuUsagePercent);
    }

    [Theory]
    [InlineData(10, 999, "core:0")]
    [InlineData(999, 100, "core:0")]
    [InlineData(10, 100, "unknown-core")]
    public void UnmatchedCpuRowsFallBackToProcessCpuUsageInsteadOfZeroScores(int pid, ulong startKey, string core)
    {
        var configuration = CreateConfiguration();
        using var workspace = new HostManagerComputeScoringWorkspace(in configuration,
            HostManagerTestPlanFactory.CreateCpuScoring(HostManagerTestPlanFactory.CreateCpuTopology(1)), CreateScratchFixture);
        var inventory = CreateInventory();
        var facts = CreateFacts(inventory, 10);
        var physical = CpuCoreResidencyTestValues.Create(91, DateTimeOffset.UtcNow,
            CpuCoreResidencyTestValues.Process(pid, startKey, (core, 100)));
        var result = workspace.Score(1, facts, inventory, CreateRuntimeFacts(), physical);
        Assert.Equal(HostManagerCpuOccupancySource.ProcessCpuUsage, result?.CpuOccupancySource);
        Assert.True(Assert.Single(result!.Cpu!.Scores, row =>
            row.Kind == NativeComputeScoringOutputKind.ProcessCpu && row.ProcessId == 10).Score > 0);
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
            CpuCoreResidencyTestValues.Process(10, 100, ("core:0", facts.Processes[0].CpuUsagePercent)));

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
            CpuCoreResidencyTestValues.Process(10, 100, ("core:0", facts.Processes[0].CpuUsagePercent)));

        var fallback = workspace.Score(1, facts, inventory, CreateRuntimeFacts(), null);
        var weighted = workspace.Score(2, facts, inventory, CreateRuntimeFacts(), physical);

        Assert.Equal(HostManagerCpuOccupancySource.ProcessCpuUsage, fallback?.CpuOccupancySource);
        Assert.Equal(HostManagerCpuOccupancySource.CoreResidency, weighted?.CpuOccupancySource);
        Assert.NotNull(weighted?.Memory);
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
