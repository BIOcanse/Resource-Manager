using ResourceManager.App.Infrastructure.NativeCore;

namespace Resource_Manager_APP.Tests;

public sealed partial class NativeComputeScoringSessionTests
{
    [Fact]
    public void WelfareTracksDirectFreeCapacityWithoutAKnee()
    {
        var configuration = CreateConfiguration(welfareBaseScore: 100);
        using var session = new NativeComputeScoringSession(in configuration, [1]);
        var process = CreateProcess(1, 10, 100, 1, 0);
        var outputs = new NativeComputeScoringOutput[4];
        var envelope = CreateEnvelope(1, 1, 0, 0, outputs.Length,
            NativeComputeScoringEnvelopeFlags.CpuSnapshotComplete);
        var previous = 101D;
        for (var step = 0; step <= 1000; step++)
        {
            var free = 1 - step / 1000D;
            envelope.RoundNumber = checked((ulong)step + 1);
            envelope.CpuFreeRatio = free;
            envelope.MemoryFreeRatio = free;
            var snapshot = default(NativeComputeScoringSnapshot);
            Assert.Equal(NativeComputeScoringStatus.Ok, session.ScoreWithScratchFixture(in envelope, [process],
                ReadOnlySpan<NativeComputeScoringGpuInput>.Empty, outputs, ref snapshot));
            Assert.Equal(free, snapshot.CpuWelfareMultiplier, 10);
            Assert.Equal(free, snapshot.MemoryWelfareMultiplier, 10);
            Assert.Equal(100 * free, snapshot.CpuWelfareBonus, 10);
            Assert.Equal(100 * free, snapshot.MemoryWelfareBonus, 10);
            Assert.True(snapshot.CpuWelfareBonus < previous);
            if (step > 0) Assert.Equal(0.1, previous - snapshot.CpuWelfareBonus, 10);
            previous = snapshot.CpuWelfareBonus;
            Assert.Equal(3U, snapshot.OutputCount);
            foreach (var output in outputs[..checked((int)snapshot.OutputCount)])
                Assert.Equal(100 * free, output.Score, 10);
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void NativeConfigurationRejectsInvalidWelfareBase(double welfareBase)
    {
        var configuration = CreateConfiguration(welfareBaseScore: welfareBase);
        Assert.Throws<InvalidOperationException>(() => new NativeComputeScoringSession(in configuration, [1]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    public void WelfareBaseAcceptsBothImportanceBounds(double welfareBase)
    {
        var configuration = CreateConfiguration(welfareBaseScore: welfareBase);
        using var session = new NativeComputeScoringSession(in configuration, [1]);
        var process = CreateProcess(1, 10, 100, 1, 0);
        var outputs = new NativeComputeScoringOutput[4];
        var envelope = CreateEnvelope(1, 1, 0, 0, outputs.Length,
            NativeComputeScoringEnvelopeFlags.CpuSnapshotComplete);
        envelope.CpuFreeRatio = envelope.MemoryFreeRatio = 1;
        var snapshot = default(NativeComputeScoringSnapshot);
        Assert.Equal(NativeComputeScoringStatus.Ok, session.ScoreWithScratchFixture(in envelope, [process],
            ReadOnlySpan<NativeComputeScoringGpuInput>.Empty, outputs, ref snapshot));
        Assert.Equal(welfareBase, snapshot.CpuWelfareBonus);
        Assert.Equal(welfareBase, snapshot.MemoryWelfareBonus);
    }

    [Theory]
    [InlineData(35, 0, 39, 60)]
    [InlineData(0, 35, 60, 39)]
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

        Assert.Equal(NativeComputeScoringStatus.Ok, session.ScoreWithScratchFixture(in envelope, [process],
            ReadOnlySpan<NativeComputeScoringGpuInput>.Empty, outputs, ref snapshot));

        var emitted = NameOutputs(in snapshot, [process], ReadOnlySpan<NativeComputeScoringGpuInput>.Empty, outputs);
        Assert.Equal(cpuBonus, snapshot.CpuWelfareBonus, 10);
        Assert.Equal(memoryBonus, snapshot.MemoryWelfareBonus, 10);
        Assert.Equal(200 + cpuBonus, Assert.Single(emitted, static row =>
            row.Kind == NativeComputeScoringOutputKind.SoftwareCpu).Score, 10);
        Assert.Equal(200 + memoryBonus, Assert.Single(emitted, static row =>
            row.Kind == NativeComputeScoringOutputKind.SoftwareMemory).Score, 10);
    }

    [Fact]
    public void ReplacementSessionBaseChangesWelfareWithoutChangingRawScore()
    {
        var configuration = CreateConfiguration(welfareBaseScore: 100);
        using var session = new NativeComputeScoringSession(in configuration, [1]);
        var process = CreateProcess(1, 10, 100, 1, 50);
        var outputs = new NativeComputeScoringOutput[4];
        var envelope = CreateEnvelope(1, 1, 0, 0, outputs.Length,
            NativeComputeScoringEnvelopeFlags.CpuSnapshotComplete);
        envelope.CpuFreeRatio = 0.65;
        envelope.MemoryFreeRatio = 0.65;
        var snapshot = default(NativeComputeScoringSnapshot);
        Assert.Equal(NativeComputeScoringStatus.Ok, session.ScoreWithScratchFixture(in envelope, [process],
            ReadOnlySpan<NativeComputeScoringGpuInput>.Empty, outputs, ref snapshot));
        Assert.Equal(115, outputs[0].Score, 10);

        configuration.Generation = 2;
        configuration.WelfareBaseScore = 60;
        using var replacement = new NativeComputeScoringSession(in configuration, [1]);
        envelope.RoundNumber = 2;
        Assert.Equal(NativeComputeScoringStatus.Ok, replacement.ScoreWithScratchFixture(in envelope, [process],
            ReadOnlySpan<NativeComputeScoringGpuInput>.Empty, outputs, ref snapshot));
        Assert.Equal(89, outputs[0].Score, 10);
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

        Assert.Equal(NativeComputeScoringStatus.NoData, session.ScoreWithScratchFixture(in envelope, [process],
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
