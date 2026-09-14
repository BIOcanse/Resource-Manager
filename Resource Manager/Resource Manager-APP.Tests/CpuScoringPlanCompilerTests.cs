using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace Resource_Manager_APP.Tests;

public sealed class CpuScoringPlanCompilerTests
{
    [Fact]
    public void ManualWeightsAreCopiedInPhysicalCoreOrderWithoutApplyingSmtAgain()
    {
        var topology = HostManagerTestPlanFactory.CreateCpuTopology(1, 2, 4);
        var raw = new Dictionary<int, double> { [2] = 400, [0] = 800, [1] = 600 };
        var plan = CpuScoringPlanCompiler.Compile(topology with
        {
            PhysicalCores = topology.PhysicalCores.Reverse().ToArray()
        }, raw, 0.5);
        raw[0] = 1;
        Assert.Equal(0.5, plan.BaselineRatio);
        Assert.Equal("manual", plan.CoreWeights.Source);
        Assert.Equal(new[] { "core:0", "core:1", "core:2" }, plan.CoreWeights.Cores.Select(core => core.PhysicalCoreId));
        Assert.Equal(new[] { 800d, 600d, 400d }, plan.CoreWeights.Cores.Select(core => core.ReferenceWeight));
        Assert.Equal(new[] { 0, 1, 2 }, plan.CoreWeights.Cores.Select(core => core.PhysicalCoreIndex));
    }

    [Theory]
    [InlineData("AMD Ryzen 9 7950X3D 16-Core Processor")]
    [InlineData("Intel Core i7-10700K")]
    public void KnownHomogeneousCoresUseCommonRelativeWeightsNotTopologyDisplayScores(string cpuName)
    {
        var topology = HostManagerTestPlanFactory.CreateCpuTopology(2, 2) with { CpuName = cpuName };
        topology = topology with
        {
            PhysicalCores = topology.PhysicalCores.Select((core, index) => core with
            {
                PerformanceScore = index == 0 ? 100 : 65
            }).ToArray()
        };
        var plan = CpuScoringPlanCompiler.Compile(topology, new Dictionary<int, double>(), 0.5);
        Assert.StartsWith("homogeneous-relative:", plan.CoreWeights.Source);
        Assert.Equal(new[] { 1d, 1d }, plan.CoreWeights.Cores.Select(core => core.ReferenceWeight));
    }

    [Theory]
    [InlineData("Intel Core i9-13900K")]
    [InlineData("Intel Core Ultra 9 285H")]
    [InlineData("AMD Ryzen AI 9 HX 370")]
    [InlineData("Intel unknown processor")]
    [InlineData("Unknown CPU")]
    public void UnmeasuredMixedOrUnknownCoresDoNotReuseHeuristicDisplayScores(string cpuName)
    {
        var topology = HostManagerTestPlanFactory.CreateCpuTopology(2, 1) with { CpuName = cpuName };
        var plan = CpuScoringPlanCompiler.Compile(topology, new Dictionary<int, double>(), 0.5);
        Assert.False(plan.CoreWeights.IsAvailable);
        Assert.Empty(plan.CoreWeights.Cores);
        Assert.Equal(0.5, plan.BaselineRatio);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidManualVectorDoesNotFallBackToHomogeneousOrHeuristicValues(double weight)
    {
        var topology = HostManagerTestPlanFactory.CreateCpuTopology(2, 2) with { CpuName = "AMD Ryzen 9 7950X3D" };
        var plan = CpuScoringPlanCompiler.Compile(topology, new Dictionary<int, double> { [0] = 800, [1] = weight }, 1);
        Assert.False(plan.CoreWeights.IsAvailable);
    }

    [Fact]
    public void PartialManualVectorCannotBeFilledFromAnotherSource()
    {
        var topology = HostManagerTestPlanFactory.CreateCpuTopology(2, 2) with { CpuName = "AMD Ryzen 9 7950X3D" };
        Assert.False(CpuScoringPlanCompiler.Compile(topology,
            new Dictionary<int, double> { [0] = 800 }, 1).CoreWeights.IsAvailable);
        Assert.False(CpuScoringPlanCompiler.Compile(topology,
            new Dictionary<int, double> { [0] = 800, [9] = 800 }, 1).CoreWeights.IsAvailable);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1.1)]
    [InlineData(double.NaN)]
    [InlineData(double.Epsilon)]
    public void InvalidBaselineNeverReachesTheNativePlan(double ratio)
        => Assert.Throws<InvalidOperationException>(() => CpuScoringPlanCompiler.Compile(
            HostManagerTestPlanFactory.CreateCpuTopology(1), new Dictionary<int, double> { [0] = 1 }, ratio));

    [Fact]
    public void CompiledCpuChoicesReachTheirOnlyFreedomPointsAndThePlanDigest()
    {
        var topology = HostManagerTestPlanFactory.CreateCpuTopology(2, 1);
        var cpu = CpuScoringPlanCompiler.Compile(topology, new Dictionary<int, double> { [0] = 800, [1] = 400 }, 0.5);
        var baseline = HostManagerTestPlanFactory.CreatePlan(cpuTopology: topology, cpuScoring: cpu);
        var changed = HostManagerTestPlanFactory.CreatePlan(cpuTopology: topology, cpuScoring: cpu with { BaselineRatio = 0.75 });
        var weightsChanged = HostManagerTestPlanFactory.CreatePlan(cpuTopology: topology,
            cpuScoring: CpuScoringPlanCompiler.Compile(topology, new Dictionary<int, double> { [0] = 900, [1] = 400 }, 0.5));
        Assert.Equal(cpu.BaselineRatio, baseline.CpuScoring!.BaselineRatio);
        Assert.Equal(cpu.CoreWeights.Cores.ToArray(), baseline.CpuScoring.CoreWeights.Cores.ToArray());
        Assert.NotEqual(baseline.PlanSha256, changed.PlanSha256);
        Assert.NotEqual(baseline.HotPublishSha256, changed.HotPublishSha256);
        Assert.NotEqual(baseline.HotPublishSha256, weightsChanged.HotPublishSha256);
        Assert.Equal(baseline.BuildSha256, changed.BuildSha256);
        var points = baseline.FreedomPoints.EnumeratePoints().ToArray();
        foreach (var id in new[] { "machine_baseline_ratio", "core_performance_weights" })
        {
            var point = Assert.Single(points, point => point.Id == id);
            Assert.Equal("active", point.Status);
            Assert.Equal("cpu_configuration", point.ValueSource);
            Assert.Equal("HostManagerPlanCompiler.CompileCpuScoring", Assert.Single(point.Consumers));
        }
    }
}
