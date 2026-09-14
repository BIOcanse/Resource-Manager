using ResourceManager.App.Infrastructure.Optimization;

namespace Resource_Manager_APP.Tests;

public sealed class HostManagerRealtimeActionLimitTests
{
    [Fact]
    public void RealtimeCycleUsesTheExplicitCompiledLimit()
    {
        Assert.Equal(
            1U,
            HostManagerSmartCoordinator.ResolveMaximumActionsThisCycle(
                realtimeCycle: true,
                actionCapacity: 4096,
                maximumActionsPerRealtimeTick: 1));
    }

    [Fact]
    public void CompleteCycleUsesTheFullStaticCapacity()
    {
        Assert.Equal(
            4096U,
            HostManagerSmartCoordinator.ResolveMaximumActionsThisCycle(
                realtimeCycle: false,
                actionCapacity: 4096,
                maximumActionsPerRealtimeTick: 1));
    }

    [Theory]
    [InlineData(0U, 1)]
    [InlineData(8U, 0)]
    [InlineData(8U, 9)]
    public void InconsistentPublishedLimitFailsClosed(
        uint actionCapacity,
        int maximumActionsPerRealtimeTick)
    {
        Assert.Throws<InvalidDataException>(
            () => HostManagerSmartCoordinator.ResolveMaximumActionsThisCycle(
                realtimeCycle: true,
                actionCapacity,
                maximumActionsPerRealtimeTick));
    }
}
