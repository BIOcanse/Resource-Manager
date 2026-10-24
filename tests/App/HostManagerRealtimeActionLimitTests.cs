using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Infrastructure.Optimization;

namespace Resource_Manager_APP.Tests;

public sealed class HostManagerRealtimeActionLimitTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(32)]
    [InlineData(123)]
    [InlineData(8192)]
    public void RealtimeCycleUsesTheExplicitFixedCount(int count)
    {
        Assert.Equal(
            (uint)count,
            HostManagerSmartCoordinator.ResolveMaximumActionsThisCycle(
                realtimeCycle: true,
                actionCapacity: 8192,
                new CompiledHostManagerRealtimeActionBudgetPlan(AppRealtimeActionBudgetModes.Fixed, count, 5),
                processCount: 12));
    }

    [Theory]
    [InlineData(5, 12, 1U)]
    [InlineData(5, 41, 3U)]
    [InlineData(100, 4096, 4096U)]
    public void RealtimePercentUsesCurrentInventoryAndRoundsUp(int percent, int processCount, uint expected)
    {
        Assert.Equal(
            expected,
            HostManagerSmartCoordinator.ResolveMaximumActionsThisCycle(
                realtimeCycle: true,
                actionCapacity: 4096,
                new CompiledHostManagerRealtimeActionBudgetPlan(AppRealtimeActionBudgetModes.Percent, 32, percent),
                processCount));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(12)]
    public void RealtimeFixedCountDoesNotDependOnProcessInventory(int? processCount)
    {
        Assert.Equal(
            32U,
            HostManagerSmartCoordinator.ResolveMaximumActionsThisCycle(
                realtimeCycle: true,
                actionCapacity: 4096,
                new CompiledHostManagerRealtimeActionBudgetPlan(AppRealtimeActionBudgetModes.Fixed, 32, 5),
                processCount));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    public void RealtimePercentRequiresPositiveProcessInventory(int? processCount)
    {
        Assert.Throws<InvalidDataException>(
            () => HostManagerSmartCoordinator.ResolveMaximumActionsThisCycle(
                realtimeCycle: true,
                actionCapacity: 4096,
                new CompiledHostManagerRealtimeActionBudgetPlan(AppRealtimeActionBudgetModes.Percent, 32, 5),
                processCount));
    }

    [Theory]
    [InlineData(AppRealtimeActionBudgetModes.Fixed, null)]
    [InlineData(AppRealtimeActionBudgetModes.Fixed, 0)]
    [InlineData(AppRealtimeActionBudgetModes.Fixed, 12)]
    [InlineData(AppRealtimeActionBudgetModes.Percent, null)]
    [InlineData(AppRealtimeActionBudgetModes.Percent, 0)]
    [InlineData(AppRealtimeActionBudgetModes.Percent, 12)]
    public void CompleteCycleUsesTheFullStaticCapacity(string mode, int? processCount)
    {
        Assert.Equal(
            4096U,
            HostManagerSmartCoordinator.ResolveMaximumActionsThisCycle(
                realtimeCycle: false,
                actionCapacity: 4096,
                new CompiledHostManagerRealtimeActionBudgetPlan(mode, 32, 5),
                processCount));
    }

    [Theory]
    [InlineData(true, 0U, "fixed", 1, 5, 12)]
    [InlineData(true, 8U, "fixed", 0, 5, 12)]
    [InlineData(true, 8U, "fixed", 9, 5, 12)]
    [InlineData(true, 8192U, "fixed", 8193, 5, 12)]
    [InlineData(true, 4096U, "percent", 32, 0, 12)]
    [InlineData(true, 4096U, "percent", 32, 101, 12)]
    [InlineData(true, 8U, "percent", 9, 5, 12)]
    [InlineData(true, 4096U, "invalid", 32, 5, 12)]
    [InlineData(false, 0U, "fixed", 1, 5, 0)]
    [InlineData(false, 8U, "percent", 9, 5, 0)]
    public void InconsistentPublishedRuleFailsClosedWithoutClamping(
        bool realtimeCycle, uint actionCapacity, string mode, int count, int percent, int processCount)
    {
        Assert.Throws<InvalidDataException>(
            () => HostManagerSmartCoordinator.ResolveMaximumActionsThisCycle(
                realtimeCycle,
                actionCapacity,
                new CompiledHostManagerRealtimeActionBudgetPlan(mode, count, percent),
                processCount));
    }
}
