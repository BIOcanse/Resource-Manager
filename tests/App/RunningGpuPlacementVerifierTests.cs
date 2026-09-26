using ResourceManager.App.Application.GpuPlacement;

namespace Resource_Manager_APP.Tests;

public sealed class RunningGpuPlacementVerifierTests
{
    [Fact]
    public void IsVerified_RequiresExpectedAdapterToHaveDominantRealEvidence()
    {
        Assert.True(RunningGpuPlacementVerifier.IsVerified(
            1,
            [
                new RunningGpuPlacementAdapterEvidence(0, 1, 2),
                new RunningGpuPlacementAdapterEvidence(1, 20, 30)
            ]));
        Assert.False(RunningGpuPlacementVerifier.IsVerified(
            1,
            [
                new RunningGpuPlacementAdapterEvidence(0, 25, 40),
                new RunningGpuPlacementAdapterEvidence(1, 5, 10)
            ]));
    }

    [Fact]
    public void IsVerified_DoesNotTreatZeroEvidenceAsMigrationSuccess()
    {
        Assert.False(RunningGpuPlacementVerifier.IsVerified(
            1,
            [new RunningGpuPlacementAdapterEvidence(1, 0, 0)]));
    }
}
