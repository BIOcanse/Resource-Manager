using ResourceManager.App.Infrastructure.NativeCore;

namespace Resource_Manager_APP.Tests;

public sealed partial class NativeComputeScoringSessionTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(17.5, 0.75)]
    [InlineData(35, 0.5)]
    [InlineData(52.5, 0.25)]
    [InlineData(70, 0)]
    [InlineData(85, 0)]
    [InlineData(100, 0)]
    public void WelfareCutoffControlsTheActualNativeBonus(double usage, double expectedRatio)
    {
        var configuration = CreateConfiguration();
        using var session = new NativeComputeScoringSession(in configuration, [1]);
        var process = CreateProcess(1, 10, 100, 1, 0);
        var outputs = new NativeComputeScoringOutput[4];
        var envelope = CreateEnvelope(1, 1, 0, 0, outputs.Length,
            NativeComputeScoringEnvelopeFlags.CpuSnapshotComplete);
        envelope.CpuFreeRatio = 1 - usage / 100;
        envelope.MemoryFreeRatio = 1 - usage / 100;
        var snapshot = default(NativeComputeScoringSnapshot);

        Assert.Equal(NativeComputeScoringStatus.Ok, session.Score(in envelope, [process],
            ReadOnlySpan<NativeComputeScoringGpuInput>.Empty, outputs, ref snapshot));

        Assert.Equal(expectedRatio, snapshot.CpuWelfareMultiplier, 10);
        Assert.Equal(expectedRatio, snapshot.MemoryWelfareMultiplier, 10);
        Assert.Equal(60 * expectedRatio, snapshot.CpuWelfareBonus, 10);
        Assert.Equal(60 * expectedRatio, snapshot.MemoryWelfareBonus, 10);
        Assert.Equal(3U, snapshot.OutputCount);
        foreach (var output in outputs[..checked((int)snapshot.OutputCount)])
            Assert.Equal(60 * expectedRatio, output.Score, 10);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(101)]
    [InlineData(140)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void NativeConfigurationRejectsInvalidWelfareCutoff(double cutoff)
    {
        var configuration = CreateConfiguration(welfareUtilizationBaselinePercent: cutoff);
        Assert.Throws<InvalidOperationException>(() => new NativeComputeScoringSession(in configuration, [1]));
    }

    [Theory]
    [InlineData(35, 0, 30, 60)]
    [InlineData(0, 35, 60, 30)]
    [InlineData(100, 0, 0, 60)]
    [InlineData(0, 100, 60, 0)]
    public void MemoryUsesCpuRawScoreAndItsOwnWelfareWithoutApplyingPolicyTwice(
        double cpuUsage, double memoryUsage, double cpuBonus, double memoryBonus)
    {
        var configuration = CreateConfiguration(cpuBaselineRatio: 0.5);
        using var session = new NativeComputeScoringSession(in configuration, [1]);
        var process = CreateProcess(1, 10, 100, 1, 25);
        process.CpuPolicyMultiplier = 4;
        var outputs = new NativeComputeScoringOutput[4];
        var envelope = CreateEnvelope(1, 1, 0, 0, outputs.Length,
            NativeComputeScoringEnvelopeFlags.CpuSnapshotComplete);
        envelope.CpuFreeRatio = 1 - cpuUsage / 100;
        envelope.MemoryFreeRatio = 1 - memoryUsage / 100;
        var snapshot = default(NativeComputeScoringSnapshot);

        Assert.Equal(NativeComputeScoringStatus.Ok, session.Score(in envelope, [process],
            ReadOnlySpan<NativeComputeScoringGpuInput>.Empty, outputs, ref snapshot));

        var emitted = outputs[..checked((int)snapshot.OutputCount)];
        Assert.Equal(cpuBonus, snapshot.CpuWelfareBonus, 10);
        Assert.Equal(memoryBonus, snapshot.MemoryWelfareBonus, 10);
        Assert.Equal(200 + cpuBonus, Assert.Single(emitted, static row =>
            row.Kind == NativeComputeScoringOutputKind.SoftwareCpu).Score, 10);
        Assert.Equal(200 + memoryBonus, Assert.Single(emitted, static row =>
            row.Kind == NativeComputeScoringOutputKind.SoftwareMemory).Score, 10);
    }

    [Fact]
    public void CutoffReconfigurationChangesWelfareWithoutChangingRawScore()
    {
        var configuration = CreateConfiguration(welfareUtilizationBaselinePercent: 100);
        using var session = new NativeComputeScoringSession(in configuration, [1]);
        var process = CreateProcess(1, 10, 100, 1, 50);
        var outputs = new NativeComputeScoringOutput[4];
        var envelope = CreateEnvelope(1, 1, 0, 0, outputs.Length,
            NativeComputeScoringEnvelopeFlags.CpuSnapshotComplete);
        envelope.CpuFreeRatio = 0.65;
        envelope.MemoryFreeRatio = 0.65;
        var snapshot = default(NativeComputeScoringSnapshot);
        Assert.Equal(NativeComputeScoringStatus.Ok, session.Score(in envelope, [process],
            ReadOnlySpan<NativeComputeScoringGpuInput>.Empty, outputs, ref snapshot));
        Assert.Equal(89, outputs[0].Score, 10);

        configuration.Generation = 2;
        configuration.WelfareUtilizationBaselinePercent = 70;
        Assert.Equal(NativeComputeScoringStatus.Ok, session.Reconfigure(in configuration, [1]));
        envelope.SchedulingGeneration = 2;
        envelope.ConfigurationGeneration = 2;
        Assert.Equal(NativeComputeScoringStatus.Ok, session.Score(in envelope, [process],
            ReadOnlySpan<NativeComputeScoringGpuInput>.Empty, outputs, ref snapshot));
        Assert.Equal(80, outputs[0].Score, 10);
    }

    [Fact]
    public void IncompleteCpuInputDoesNotInventMemoryScoreFromWelfare()
    {
        var configuration = CreateConfiguration();
        using var session = new NativeComputeScoringSession(in configuration, [1]);
        var process = CreateProcess(1, 10, 100, 1, 0);
        var outputs = new NativeComputeScoringOutput[4];
        var envelope = CreateEnvelope(1, 1, 0, 0, outputs.Length, NativeComputeScoringEnvelopeFlags.None);
        envelope.CpuFreeRatio = 1;
        envelope.MemoryFreeRatio = 1;
        var snapshot = default(NativeComputeScoringSnapshot);

        Assert.Equal(NativeComputeScoringStatus.NoData, session.Score(in envelope, [process],
            ReadOnlySpan<NativeComputeScoringGpuInput>.Empty, outputs, ref snapshot));
        Assert.Equal(0U, snapshot.OutputCount);
        Assert.Equal(0U, snapshot.SoftwareMemoryOutputCount);
    }

    [Fact]
    public void ConfigurationCapacityIncludesSoftwareMemoryOutputs()
    {
        var configuration = CreateConfiguration();
        configuration.MaximumOutputCount = 96;
        Assert.Throws<InvalidOperationException>(() => new NativeComputeScoringSession(in configuration, [1]));
    }
}
