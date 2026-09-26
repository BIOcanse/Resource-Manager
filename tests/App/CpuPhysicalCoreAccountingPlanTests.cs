using ResourceManager.App.Domain.CpuTopology;
using ResourceManager.App.Infrastructure.CpuTopology;

namespace Resource_Manager_APP.Tests;

public sealed class CpuPhysicalCoreAccountingPlanTests
{
    [Theory]
    [InlineData(1, 1.0)]
    [InlineData(2, 0.5)]
    [InlineData(4, 0.25)]
    public void StartupCompilationFixesEachCoreCapacity(int siblings, double expectedShare)
    {
        var plan = HostManagerTestPlanFactory.CreatePlan(
            cpuTopology: HostManagerTestPlanFactory.CreateCpuTopology(siblings)).CpuCoreResidency;
        var core = plan.PhysicalCoreByLogicalProcessor[0];

        Assert.Equal(siblings, core.LogicalProcessorCount);
        Assert.Equal(expectedShare, core.LogicalProcessorShare);
        Assert.Equal(siblings, plan.PhysicalCoreByLogicalProcessor.Count);
        Assert.All(plan.PhysicalCoreByLogicalProcessor.Values, item => Assert.Same(core, item));
    }

    [Fact]
    public void MixedCoresUseIndependentCompiledSharesNotTheMachineSmtFlag()
    {
        var topology = HostManagerTestPlanFactory.CreateCpuTopology(2, 1, 4)
            with { SimultaneousMultithreading = false };
        var plan = HostManagerTestPlanFactory.CreatePlan(cpuTopology: topology).CpuCoreResidency;

        Assert.Equal(0.5, plan.PhysicalCoreByLogicalProcessor[0].LogicalProcessorShare);
        Assert.Equal(1, plan.PhysicalCoreByLogicalProcessor[2].LogicalProcessorShare);
        Assert.Equal(0.25, plan.PhysicalCoreByLogicalProcessor[3].LogicalProcessorShare);
    }

    [Fact]
    public void ProjectionRetainsCompiledCapacityAfterTheSourceCollectionsAreCleared()
    {
        var original = HostManagerTestPlanFactory.CreateCpuTopology(2);
        var siblings = new List<int> { 0, 1 };
        var physical = new List<CpuPhysicalCoreModel>
            { original.PhysicalCores[0] with { LogicalProcessorIds = siblings } };
        var logical = original.LogicalProcessors.ToList();
        var topology = original with { PhysicalCores = physical, LogicalProcessors = logical };
        var plan = HostManagerTestPlanFactory.CreatePlan(cpuTopology: topology).CpuCoreResidency;
        siblings.Clear();
        physical.Clear();
        logical.Clear();

        foreach (var duration in new long[] { 1000, 500, 250 })
        {
            var aggregate = new CpuResidencyAggregationSnapshot(1000, 0, 1000,
                DateTimeOffset.UnixEpoch.AddSeconds(1), true,
                [new CpuExecutionTimeAggregate(1, 100, 1000, "Process", 1, 10, 0, duration, 1)]);
            var process = Assert.Single(EtwCpuCoreResidencyReader.BuildProcessSnapshot(plan, 7, aggregate));
            Assert.Equal(duration / 20.0, Assert.Single(process.PhysicalCores).UsagePercent);
        }
        Assert.Equal(2, plan.PhysicalCoreByLogicalProcessor[0].LogicalProcessorCount);
    }

    [Fact]
    public void RecompilingDifferentCoreMembershipChangesAccountingAndScoringCapacityTogether()
    {
        var smt = HostManagerTestPlanFactory.CreatePlan(cpuTopology: HostManagerTestPlanFactory.CreateCpuTopology(2));
        var singleThreaded = HostManagerTestPlanFactory.CreatePlan(
            cpuTopology: HostManagerTestPlanFactory.CreateCpuTopology(1, 1));

        Assert.Equal(smt.ProfileSha256, singleThreaded.ProfileSha256);
        Assert.Equal(smt.FreedomPoints.DeclarationSha256, singleThreaded.FreedomPoints.DeclarationSha256);
        Assert.NotEqual(smt.BuildSha256, singleThreaded.BuildSha256);
        Assert.NotEqual(smt.PlanSha256, singleThreaded.PlanSha256);
        Assert.Equal(smt.RecreateSha256, singleThreaded.RecreateSha256);
        Assert.NotEqual(smt.HotPublishSha256, singleThreaded.HotPublishSha256);
        Assert.Single(smt.CpuScoring!.CoreWeights.Cores);
        Assert.Equal(2, singleThreaded.CpuScoring!.CoreWeights.Cores.Length);
        Assert.Equal(0.5, smt.CpuCoreResidency.PhysicalCoreByLogicalProcessor[0].LogicalProcessorShare);
        Assert.Equal(1, singleThreaded.CpuCoreResidency.PhysicalCoreByLogicalProcessor[0].LogicalProcessorShare);
    }

    [Fact]
    public void EnumerationOrderDisplayAndUsageCannotChangeCompiledCapacityOrDigest()
    {
        var topology = HostManagerTestPlanFactory.CreateCpuTopology(2, 1, 4);
        var original = HostManagerTestPlanFactory.CreatePlan(cpuTopology: topology);
        var changed = HostManagerTestPlanFactory.CreatePlan(cpuTopology: topology with
        {
            CapturedAt = DateTimeOffset.UnixEpoch.AddDays(1),
            SimultaneousMultithreading = false,
            PhysicalCores = topology.PhysicalCores.Reverse().Select(core => core with
            {
                Label = "Different display label",
                UsagePercent = 99,
                PerformanceScore = 900,
                LogicalProcessorIds = core.LogicalProcessorIds.Reverse().ToArray()
            }).ToArray(),
            LogicalProcessors = topology.LogicalProcessors.Reverse()
                .Select(logical => logical with { UsagePercent = 88, PerformanceScore = 450 }).ToArray()
        });

        Assert.Equal(original.BuildSha256, changed.BuildSha256);
        Assert.Equal(original.PlanSha256, changed.PlanSha256);
        foreach (var pair in original.CpuCoreResidency.PhysicalCoreByLogicalProcessor)
            Assert.Equal(pair.Value, changed.CpuCoreResidency.PhysicalCoreByLogicalProcessor[pair.Key]);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("no-siblings")]
    [InlineData("duplicate-sibling")]
    [InlineData("duplicate-logical")]
    [InlineData("duplicate-core")]
    [InlineData("unknown-logical")]
    [InlineData("missing-sibling")]
    [InlineData("wrong-core")]
    [InlineData("wrong-ccd")]
    public void MalformedMappingsAreRejectedAtCompilation(string mutation)
    {
        var topology = HostManagerTestPlanFactory.CreateCpuTopology(2);
        var core = topology.PhysicalCores[0];
        var logical = topology.LogicalProcessors;
        var malformed = mutation switch
        {
            "empty" => topology with { PhysicalCores = [], LogicalProcessors = [] },
            "no-siblings" => topology with { PhysicalCores = [core with { LogicalProcessorIds = [] }] },
            "duplicate-sibling" => topology with { PhysicalCores = [core with { LogicalProcessorIds = [0, 0] }] },
            "duplicate-logical" => topology with { LogicalProcessors = [logical[0], logical[0]] },
            "duplicate-core" => topology with { PhysicalCores = [core, core] },
            "unknown-logical" => topology with { PhysicalCores = [core with { LogicalProcessorIds = [0, 99] }] },
            "missing-sibling" => topology with { PhysicalCores = [core with { LogicalProcessorIds = [0] }] },
            "wrong-core" => topology with { LogicalProcessors = [logical[0], logical[1] with { PhysicalCoreId = "other" }] },
            "wrong-ccd" => topology with { LogicalProcessors = [logical[0], logical[1] with { CcdId = "other" }] },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation))
        };

        var scoring = HostManagerTestPlanFactory.CreateCpuScoring(topology);
        Assert.Throws<InvalidDataException>(() => HostManagerTestPlanFactory.CreatePlan(
            cpuTopology: malformed, cpuScoring: scoring));
    }
}
