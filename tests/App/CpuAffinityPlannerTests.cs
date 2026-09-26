using ResourceManager.App.Application.Optimization.Scheduling;
using ResourceManager.App.Domain.CpuTopology;
using ResourceManager.App.Domain.Optimization;

namespace Resource_Manager_APP.Tests;

public sealed class CpuAffinityPlannerTests
{
    [Theory]
    [InlineData(12, 0xF00, new[] { 8, 9, 10, 11 })]
    [InlineData(16, 0xF000, new[] { 12, 13, 14, 15 })]
    [InlineData(32, unchecked((long)0xFFFF0000), new[] { 16, 17, 18, 19 })]
    public void CreateRearLogicalProcessorPlan_UsesRearProcessorSet(
        int logicalProcessorCount,
        long expectedMask,
        int[] expectedPrefix)
    {
        var plan = CpuAffinityPlanner.CreateRearLogicalProcessorPlan(logicalProcessorCount);

        Assert.True(plan.Available);
        Assert.Equal(expectedMask, plan.AffinityMask);
        Assert.Equal(expectedPrefix, plan.LogicalProcessorIds.Take(expectedPrefix.Length).ToArray());
    }

    [Fact]
    public void CreateRearLogicalProcessorPlan_DisablesSmallCpu()
    {
        var plan = CpuAffinityPlanner.CreateRearLogicalProcessorPlan(4);

        Assert.False(plan.Available);
        Assert.Null(plan.AffinityMask);
    }

    [Theory]
    [InlineData(12, 0x0FF, new[] { 0, 1, 2, 3, 4, 5, 6, 7 })]
    [InlineData(16, 0x0FFF, new[] { 0, 1, 2, 3 })]
    [InlineData(32, 0x0000FFFF, new[] { 0, 1, 2, 3 })]
    public void CreatePrimaryLogicalProcessorPlan_LeavesRearProcessorSetForSystemAndBackground(
        int logicalProcessorCount,
        long expectedMask,
        int[] expectedPrefix)
    {
        var plan = CpuAffinityPlanner.CreatePrimaryLogicalProcessorPlan(logicalProcessorCount);

        Assert.True(plan.Available);
        Assert.Equal(expectedMask, plan.AffinityMask);
        Assert.Equal(expectedPrefix, plan.LogicalProcessorIds.Take(expectedPrefix.Length).ToArray());
    }

    [Fact]
    public void CreateExplicitLogicalProcessorPlan_UsesGivenMask()
    {
        var plan = CpuAffinityPlanner.CreateExplicitLogicalProcessorPlan(
            8,
            0b_1100_0000,
            "ProtectionAvoidance",
            "保护对象避让到逻辑处理器");

        Assert.True(plan.Available);
        Assert.Equal(0b_1100_0000, plan.AffinityMask);
        Assert.Equal([6, 7], plan.LogicalProcessorIds);
    }

    [Fact]
    public void CreateExplicitLogicalProcessorPlan_DisablesOutOfRangeMask()
    {
        var plan = CpuAffinityPlanner.CreateExplicitLogicalProcessorPlan(
            4,
            0b_1_0000,
            "ProtectionAvoidance",
            "保护对象避让到逻辑处理器");

        Assert.False(plan.Available);
        Assert.Null(plan.AffinityMask);
    }

    [Fact]
    public void TryCreateFullMask_SupportsCpu63AndRejectsCrossGroupRange()
    {
        Assert.True(CpuAffinityPlanner.TryCreateFullMask(64, out var mask));
        Assert.Equal(unchecked((long)ulong.MaxValue), mask);
        Assert.False(CpuAffinityPlanner.TryCreateFullMask(65, out mask));
        Assert.Equal(0, mask);
    }

    [Fact]
    public void CreateExplicitLogicalProcessorPlan_AcceptsCpu63BitPattern()
    {
        var plan = CpuAffinityPlanner.CreateExplicitLogicalProcessorPlan(
            64,
            unchecked((long)(1UL << 63)),
            "Cpu63",
            "目标逻辑处理器");

        Assert.True(plan.Available);
        Assert.Equal(unchecked((long)(1UL << 63)), plan.AffinityMask);
        Assert.Equal([63], plan.LogicalProcessorIds);
    }

    [Fact]
    public void CpuAllowedSetPlanBuilder_UsesCpuSetIdsAcrossProcessorGroups()
    {
        var logicalProcessors = new[]
        {
            new CpuLogicalProcessorModel(0, 0, 2, "core-0", "ccd-0", 100, 0, true, 101),
            new CpuLogicalProcessorModel(64, 1, 2, "core-1", "ccd-1", 100, 0, true, 201)
        };
        var topology = new CpuTopologySnapshot(
            DateTimeOffset.UtcNow,
            "test",
            new CpuSpecificationModel("test", "test", "test", 2, 128, null, null, null, null, "test"),
            "test",
            "test",
            CpuTopologyAffinityTargetKinds.LogicalProcessorMask,
            CpuTopologyVisualLayoutKinds.Grid,
            "test",
            2,
            128,
            2,
            false,
            [],
            [],
            logicalProcessors,
            []);

        var plan = CpuAllowedSetPlanBuilder.CreatePlan(topology, ["core-0", "core-1"]);

        Assert.True(plan.Available);
        Assert.Null(plan.AffinityMask);
        Assert.Equal([101u, 201u], plan.CpuSetIds);
        Assert.Equal([0, 64], plan.LogicalProcessorIds);
    }
}
