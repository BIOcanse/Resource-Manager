using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.FreedomPoints;

namespace Resource_Manager_APP.Tests;

public sealed partial class NativeComputeScoringSessionTests
{
    [Fact]
    public void PublishedSdkCandidateChangesActualNativeScoreAndRetainedSessionRemainsValid()
    {
        var provider = new RuntimePlanProvider(new HostManagerDeploymentState());
        var first = FreedomPointApplicationTests.Plan(1, 1);
        provider.Publish(first);
        var firstConfiguration = HostManagerComputeScoringConfigurationFactory.Create(
            first.HostManager.SmartCoordinator, first.HostManager.CpuScoring!);
        using var retained = new NativeComputeScoringSession(in firstConfiguration, [1]);
        var before = Score(retained, firstConfiguration.Generation);
        var candidate = FreedomPointPlanApplication.Prepare(first, FreedomPointApplicationTests.Plan(2, 0.5));
        provider.Publish(candidate);
        var current = provider.Current.HostManager;
        var configuration = HostManagerComputeScoringConfigurationFactory.Create(current.SmartCoordinator, current.CpuScoring!);
        using var applied = new NativeComputeScoringSession(in configuration, [1]);
        Assert.True(before > 0);
        Assert.Equal(before / 0.5, Score(applied, configuration.Generation), precision: 10);
        Assert.Equal(before, Score(retained, firstConfiguration.Generation, sampleGeneration: 2), precision: 10);
        retained.Dispose();
        Assert.Throws<ObjectDisposedException>(() => Score(retained, firstConfiguration.Generation));

        double Score(NativeComputeScoringSession session, ulong generation, ulong sampleGeneration = 1)
        {
            var input = CreateProcess(1, 10, 100, 1, 50);
            var outputs = new NativeComputeScoringOutput[4];
            var envelope = CreateEnvelope(sampleGeneration, 1, 0, 0, outputs.Length, NativeComputeScoringEnvelopeFlags.CpuSnapshotComplete);
            var snapshot = default(NativeComputeScoringSnapshot);
            Assert.Equal(NativeComputeScoringStatus.Ok, session.ScoreWithScratchFixture(in envelope, [input],
                ReadOnlySpan<NativeComputeScoringGpuInput>.Empty, outputs, ref snapshot));
            return outputs[0].Score;
        }
    }
}
