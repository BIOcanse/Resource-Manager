using System.Reflection;
using System.Runtime.CompilerServices;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Infrastructure.Optimization;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerSmartCoordinatorScoreOnlyCompositionTests
{
    [Fact]
    public void PlacementRecoveryQueueWrapsAcrossGroupsWithoutExceedingBudget()
    {
        var coordinator = (HostManagerSmartCoordinator)RuntimeHelpers.GetUninitializedObject(
            typeof(HostManagerSmartCoordinator));
        var select = typeof(HostManagerSmartCoordinator).GetMethod("SelectPlacementRecordsForRecovery",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var first = GpuShimPolicyLedgerTests.Placement(new("test", "a")) with
        {
            Records = [new("test", "a"), new("test", "b"), new("test", "c")]
        };
        var second = first with
        {
            TargetId = "second",
            Records = [new("test", "d"), new("test", "e")]
        };
        IReadOnlyList<HostManagerAppliedPlacementReceipt> queue = [first, second];
        Assert.Equal(["a", "b"], Select(2));
        Assert.Equal(["c", "d"], Select(2));
        Assert.Empty(Select(0));
        Assert.Equal(["e", "a"], Select(2));
        queue = [first with { Records = [new("test", "c")] }, second];
        Assert.Equal(["d", "e"], Select(2));
        Assert.Equal(["c", "d", "e"], Select(uint.MaxValue));
        Assert.Equal(["c"], Select(1));
        queue = [];
        Assert.Empty(Select(2));
        queue = [first, second];
        Assert.Equal(["a"], Select(1));

        string[] Select(uint budget)
        {
            var selected = Assert.IsAssignableFrom<IReadOnlyList<HostManagerAppliedPlacementReceipt>>(
                select.Invoke(coordinator, [queue, budget]));
            return selected.SelectMany(p => p.Records).Select(r => r.RecordId).ToArray();
        }
    }
}
