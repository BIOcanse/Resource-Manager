using ResourceManager.App.Application.MemoryProtection;
using ResourceManager.App.Infrastructure.MemoryProtection;

namespace Resource_Manager_APP.Tests;

public sealed class HostMemoryProtectionTreesTests
{
    private static HostMemoryProcess P(int pid, int start, int? parent, ulong bytes)
        => new(pid, start.ToString(), "fixture", bytes) { ParentProcessId = parent };

    [Fact]
    public void MemorySumBeatsChildCountAndDoesNotIncludeGrandchildrenInRootScore()
    {
        var point = new[] { P(10, 10, null, 100), P(20, 20, null, 100) };
        var current = new[] { point[0], point[1], P(11, 30, 10, 10), P(12, 40, 10, 10), P(13, 50, 10, 10), P(21, 60, 20, 100) };
        var choice = new HostMemoryProtectionTrees(current, point).Ranked().First();
        Assert.Equal(20, choice.Root.ProcessId);
        Assert.Equal(100m, choice.GrowthBytes);
        Assert.Equal(new[] { 21, 20 }, choice.LeavesFirst.Select(item => item.ProcessId));
    }

    [Fact]
    public void ExitedPointChildIsStillSubtractedAndPidReuseDoesNotLink()
    {
        var a = P(10, 10, null, 100); var b = P(20, 20, null, 100);
        var point = new[] { a, b, P(11, 30, 10, 90) };
        var choices = new HostMemoryProtectionTrees([a, b, P(12, 40, 10, 100), P(21, 50, 20, 20)], point).Ranked().ToArray();
        Assert.Equal(20, choices[0].Root.ProcessId);
        Assert.Equal(10m, choices.Single(item => item.Root.ProcessId == 10).GrowthBytes);
        // PID 10 now belongs to a process that started after the child: the child stays with its exited
        // original parent, and the live reuser is neither the root nor a member.
        var reused = new HostMemoryProtectionTrees([P(10, 100, null, 100), P(11, 90, 10, 200)], []).Ranked().Single();
        Assert.Equal(10, reused.Root.ProcessId);
        Assert.Null(reused.Root.ProcessStartKey);
        Assert.Equal(200m, reused.GrowthBytes);
        Assert.Equal(new[] { 11 }, reused.LeavesFirst.Select(item => item.ProcessId));
    }

    [Fact]
    public void ExitedParentIsANodeAndTraversalIsLeavesFirst()
    {
        var root = P(10, 10, null, 100);
        var choice = new HostMemoryProtectionTrees([P(11, 20, 10, 200), P(12, 30, 11, 100)], [root]).Ranked().First();
        Assert.Equal(10, choice.Root.ProcessId);
        Assert.Equal("10", choice.Root.ProcessStartKey);
        Assert.Equal(new[] { 12, 11 }, choice.LeavesFirst.Select(item => item.ProcessId));
        var unknown = new HostMemoryProtectionTrees([P(11, 20, 10, 100)], []).Ranked().Single();
        Assert.Null(unknown.Root.ProcessStartKey);
    }

    [Fact]
    public void ZeroOrNegativeGrowthDoesNotChooseATreeAndTiesUseLowestPid()
    {
        var point = new[] { P(10, 10, null, 100), P(11, 20, 10, 100) };
        Assert.Empty(new HostMemoryProtectionTrees(point, point).Ranked());
        Assert.Empty(new HostMemoryProtectionTrees([point[0]], point).Ranked());
        var choices = new HostMemoryProtectionTrees([P(20, 20, null, 1), P(10, 10, null, 1), P(21, 30, 20, 10), P(11, 40, 10, 10)], []).Ranked();
        Assert.Equal(10, choices.First().Root.ProcessId);
    }
}
