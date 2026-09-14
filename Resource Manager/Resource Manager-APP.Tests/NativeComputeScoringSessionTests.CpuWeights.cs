using ResourceManager.App.Infrastructure.NativeCore;

namespace Resource_Manager_APP.Tests;

public sealed partial class NativeComputeScoringSessionTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(1000)]
    public void PhysicalCoreWeightsAndSharedBaselineReachActualNativeScores(double scale)
    {
        var configuration = CreateConfiguration(cpuBaselineRatio: 0.5, cpuCoreCount: 2);
        using var session = new NativeComputeScoringSession(in configuration, [800 * scale, 400 * scale]);
        double[] weighted = [double.NaN, double.NaN, double.NaN, double.NaN];
        NativeComputeScoringCpuCoreInput[] cores =
        [
            Core(0, 0, 100), Core(1, 1, 100),
            Core(2, 0, 100), Core(2, 1, 100), Core(3, 0, 0)
        ];
        Assert.Equal(NativeComputeScoringStatus.Ok, session.CalculateWeightedCpuUse(cores, weighted));
        Assert.Equal(200d / 3, weighted[0], precision: 10);
        Assert.Equal(100d / 3, weighted[1], precision: 10);
        Assert.Equal(100, weighted[2], precision: 10);
        Assert.Equal(0, weighted[3]);

        var processes = weighted.Select((value, index) =>
            CreateProcess((uint)index + 1, (uint)index + 10, (ulong)index + 100, (ulong)index + 1, value)).ToArray();
        var outputs = new NativeComputeScoringOutput[12];
        var envelope = CreateEnvelope(1, 4, 0, 0, outputs.Length, NativeComputeScoringEnvelopeFlags.CpuSnapshotComplete);
        var snapshot = default(NativeComputeScoringSnapshot);
        Assert.Equal(NativeComputeScoringStatus.Ok, session.Score(in envelope, processes, [], outputs, ref snapshot));
        Assert.Equal(4U, snapshot.ProcessCpuOutputCount);
        for (var index = 0; index < 4; index++)
            Assert.Equal(weighted[index] / 0.5, outputs[index].Score, precision: 10);
    }

    [Fact]
    public void WeightCompilationOwnsItsCopyAndReplacementIsAtomic()
    {
        var configuration = CreateConfiguration(cpuCoreCount: 2);
        double[] raw = [1, 3];
        using var session = new NativeComputeScoringSession(in configuration, raw);
        raw[0] = 1000;
        double[] weighted = [0];
        Assert.Equal(NativeComputeScoringStatus.Ok, session.CalculateWeightedCpuUse([Core(0, 0, 100)], weighted));
        Assert.Equal(25, weighted[0]);

        var replacement = configuration;
        replacement.Generation = 2;
        replacement.CpuBaselineRatio = 0.25;
        Assert.Equal(NativeComputeScoringStatus.InvalidArgument, session.Reconfigure(in replacement, [3, double.NaN]));
        Assert.Equal(1UL, session.Capacity.ConfigurationGeneration);
        Assert.Equal(NativeComputeScoringStatus.Ok, session.CalculateWeightedCpuUse([Core(0, 0, 100)], weighted));
        Assert.Equal(25, weighted[0]);

        double[] newRaw = [3, 1];
        Assert.Equal(NativeComputeScoringStatus.Ok, session.Reconfigure(in replacement, newRaw));
        newRaw[0] = 0;
        Assert.Equal(NativeComputeScoringStatus.Ok, session.CalculateWeightedCpuUse([Core(0, 0, 100)], weighted));
        Assert.Equal(75, weighted[0]);
        var outputs = new NativeComputeScoringOutput[3];
        var envelope = CreateEnvelope(1, 1, 0, 0, outputs.Length, NativeComputeScoringEnvelopeFlags.CpuSnapshotComplete);
        envelope.ConfigurationGeneration = 2;
        var snapshot = default(NativeComputeScoringSnapshot);
        Assert.Equal(NativeComputeScoringStatus.Ok, session.Score(in envelope,
            [CreateProcess(1, 10, 100, 1, weighted[0])], [], outputs, ref snapshot));
        Assert.Equal(300, outputs[0].Score);

        replacement.Generation = 3;
        replacement.CpuCoreCount = 3;
        Assert.Equal(NativeComputeScoringStatus.RecreateRequired, session.Reconfigure(in replacement, [1, 1, 1]));
        Assert.Equal(2UL, session.Capacity.ConfigurationGeneration);
    }

    [Theory]
    [InlineData(double.Epsilon, 2)]
    [InlineData(1e-300, 1e100)]
    [InlineData(double.MaxValue, double.MaxValue)]
    public void FinitePositiveWeightsRemainUsableAtFloatingPointExtremes(double first, double second)
    {
        var configuration = CreateConfiguration(cpuCoreCount: 2);
        using var session = new NativeComputeScoringSession(in configuration, [first, second]);
        double[] weighted = [0];
        Assert.Equal(NativeComputeScoringStatus.Ok, session.CalculateWeightedCpuUse(
            [Core(0, 0, 100), Core(0, 1, 100)], weighted));
        Assert.Equal(100, weighted[0], precision: 10);
    }

    [Theory]
    [InlineData(0, 0, -1)]
    [InlineData(0, 0, 101)]
    [InlineData(0, 0, double.NaN)]
    [InlineData(0, 0, double.PositiveInfinity)]
    [InlineData(1, 0, 50)]
    [InlineData(0, 2, 50)]
    public void InvalidCoreRowsDoNotPartiallyOverwriteOutput(uint process, uint core, double use)
    {
        var configuration = CreateConfiguration(cpuCoreCount: 2);
        using var session = new NativeComputeScoringSession(in configuration, [1, 1]);
        double[] weighted = [123];
        Assert.Equal(NativeComputeScoringStatus.InvalidFacts,
            session.CalculateWeightedCpuUse([Core(process, core, use)], weighted));
        Assert.Equal(123, weighted[0]);
    }

    [Fact]
    public void DuplicateCoreRowsCannotDoubleCountUsage()
    {
        var configuration = CreateConfiguration(cpuCoreCount: 2);
        using var session = new NativeComputeScoringSession(in configuration, [1, 1]);
        double[] weighted = [123];
        Assert.Equal(NativeComputeScoringStatus.ConflictingFacts,
            session.CalculateWeightedCpuUse([Core(0, 0, 50), Core(0, 0, 50)], weighted));
        Assert.Equal(123, weighted[0]);
    }

    [Fact]
    public void MissingCpuWeightsNeverAcceptScalarCpuButStillAllowGpuOnlyScoring()
    {
        var configuration = CreateConfiguration(cpuCoreCount: 0);
        using var session = new NativeComputeScoringSession(in configuration, []);
        double[] weighted = [123];
        Assert.Equal(NativeComputeScoringStatus.NoData, session.CalculateWeightedCpuUse([], weighted));
        Assert.Equal(123, weighted[0]);
        var process = CreateProcess(1, 10, 100, 1, 99);
        var outputs = new NativeComputeScoringOutput[2];
        var snapshot = default(NativeComputeScoringSnapshot);
        var cpu = CreateEnvelope(1, 1, 0, 0, 2, NativeComputeScoringEnvelopeFlags.CpuSnapshotComplete);
        Assert.Equal(NativeComputeScoringStatus.NoData, session.Score(in cpu, [process], [], outputs, ref snapshot));
        var gpu = CreateEnvelope(1, 1, 1, 1, 2, NativeComputeScoringEnvelopeFlags.GpuSnapshotComplete);
        Assert.Equal(NativeComputeScoringStatus.Ok, session.Score(in gpu, [process],
            [CreateGpu(process, 11, 1, 50)], outputs, ref snapshot));
        Assert.Equal(0U, snapshot.ProcessCpuOutputCount);
        Assert.Equal(50, outputs[0].Score);
    }

    private static NativeComputeScoringCpuCoreInput Core(uint process, uint core, double use)
        => new() { ProcessIndex = process, CoreIndex = core, UsagePercent = use };
}
