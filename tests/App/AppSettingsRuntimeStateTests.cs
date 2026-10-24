using ResourceManager.App.Application.Settings;
using ResourceManager.App.Domain.Settings;

namespace Resource_Manager_APP.Tests;

public sealed class AppSettingsRuntimeStateTests
{
    private static AppSettingsUpdateResult Stored(string revision = "a") => new(
        AppSettingsDefaults.Create(), DateTimeOffset.UtcNow, "settings.json",
        new AppSettingsSourceMetadata(AppSettingsSourceKind.Persisted, AppSettingsDefaults.CurrentVersion,
            revision, revision, false));

    [Theory]
    [InlineData("committedAndApplied", null, 0)]
    [InlineData("committedWithCapabilityConstraints", null, 0)]
    [InlineData("committedWithDeliveryFailures", null, 2)]
    [InlineData("savedNotApplied", "runtime-plan-publication-failed", 0)]
    public void ReadPreservesCurrentRuntimeOutcome(string disposition, string? failure, int count)
    {
        var stored = Stored();
        var applied = stored with
        {
            RuntimeApplicationDisposition = disposition,
            RuntimeFailureCode = failure,
            RuntimeDeliveryFailureCount = count,
            RuntimePlanVersion = 7,
            RuntimePublicationSequence = 8,
            RuntimeCapabilityConstrainedPaths = ["performance.preciseGpuPlacementEnabled"]
        };
        var read = AppSettingsRuntimeState.Project(stored, applied);
        Assert.Equal(disposition, read.RuntimeApplicationDisposition);
        Assert.Equal(failure, read.RuntimeFailureCode);
        Assert.Equal(count, read.RuntimeDeliveryFailureCount);
        Assert.Equal(7, read.RuntimePlanVersion);
        Assert.Equal(8UL, read.RuntimePublicationSequence);
        Assert.Equal(applied.RuntimeCapabilityConstrainedPaths, read.RuntimeCapabilityConstrainedPaths);
        Assert.Same(stored.Settings, read.Settings);
    }

    [Fact]
    public void UnknownOrDifferentStoredRevisionCannotClaimApplied()
    {
        var stored = Stored();
        Assert.Equal("runtime-plan-not-published", AppSettingsRuntimeState.Project(stored, null).RuntimeFailureCode);
        var read = AppSettingsRuntimeState.Project(stored, Stored("b") with
        { RuntimeApplicationDisposition = "committedAndApplied", RuntimePlanVersion = 9 });
        Assert.Equal("savedNotApplied", read.RuntimeApplicationDisposition);
        Assert.Equal("settings-runtime-revision-mismatch", read.RuntimeFailureCode);
        Assert.Null(read.RuntimePlanVersion);
    }
}
