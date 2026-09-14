using ResourceManager.App.Application.GpuPlacement;

namespace Resource_Manager_APP.Tests;

public sealed class RunningGpuPlacementActionResultTests
{
    private static readonly RunningGpuPlacementActionRecord Record = new(
        "record",
        "method",
        new Dictionary<string, string>());

    [Fact]
    public void RecreateRequested_IsTriggeredButNotApplied()
    {
        var result = new RunningGpuPlacementActionResult(
            [Record],
            "requested",
            RunningGpuPlacementActionStatuses.RecreateRequested);

        Assert.True(result.Triggered);
        Assert.False(result.Applied);
    }

    [Fact]
    public void AppliedRequiresExplicitVerifiedStatus()
    {
        var result = new RunningGpuPlacementActionResult(
            [Record],
            "verified",
            RunningGpuPlacementActionStatuses.Applied);

        Assert.False(result.Triggered);
        Assert.True(result.Applied);
    }
}
