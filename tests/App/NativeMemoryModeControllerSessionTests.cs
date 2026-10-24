using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Tests;

public sealed class NativeMemoryModeControllerSessionTests
{
    [Theory]
    [InlineData(0, 31)]
    [InlineData(20.999, 31)]
    [InlineData(21, 15)]
    [InlineData(50, 15)]
    [InlineData(80.999, 15)]
    [InlineData(81, 1)]
    [InlineData(95, 1)]
    [InlineData(100, 1)]
    public void BaseScoreAllowedGradesDoNotChangeWithRuntimeScoresOrMemoryPressure(
        double baseScore, byte expectedMask)
    {
        var configuration = CreateConfiguration();
        foreach (var freeRatio in new uint[] { 0, 2_000, 8_000 })
        {
            using var session = new NativeMemoryModeControllerSession(in configuration);
            var inputs = new[]
            {
                CreateSoftware(10, 0, 0, baseScore),
                CreateSoftware(20, 1, 95, baseScore),
                CreateSoftware(30, 2, 500, baseScore)
            };
            var outputs = new NativeMemoryModeDesiredSoftwareOutput[3];
            var snapshot = default(NativeMemoryModeSnapshot);
            var envelope = CreateEnvelope(freeRatio, allowUnrestricted: true);
            Assert.Equal(NativeMemoryModeControllerStatus.Ok,
                session.Plan(in envelope, inputs, outputs, ref snapshot));
            Assert.All(outputs, row => Assert.Equal(expectedMask, (byte)row.BaseScoreAllowedGrades));
            if (expectedMask == (byte)NativeMemoryGradeSet.Normal)
                Assert.All(outputs, row => Assert.True(row.Mode is NativeMemoryMode.Normal or NativeMemoryMode.Unrestricted));
            if ((expectedMask & (byte)NativeMemoryGradeSet.L4) == 0)
                Assert.All(outputs, row => Assert.NotEqual(NativeMemoryMode.PagedFrozen, row.Mode));
        }
    }

    [Fact]
    public void RealNativeControllerRanksLowerCpuScoresFirst()
    {
        var configuration = CreateConfiguration();
        using var session = new NativeMemoryModeControllerSession(in configuration);
        var software = new[]
        {
            CreateSoftware(10, 0, 100, 60),
            CreateSoftware(20, 1, 0, 60),
            CreateSoftware(30, 2, 50, 60)
        };
        var outputs = new NativeMemoryModeDesiredSoftwareOutput[3];
        var snapshot = default(NativeMemoryModeSnapshot);
        var envelope = CreateEnvelope(2_000, allowUnrestricted: true);

        var status = session.Plan(
            in envelope,
            software,
            outputs,
            ref snapshot);

        Assert.Equal(NativeMemoryModeControllerStatus.Ok, status);
        Assert.Equal(NativeMemoryMode.Normal, outputs[0].Mode);
        Assert.Equal(NativeMemoryMode.Optimize, outputs[1].Mode);
        Assert.Equal(NativeMemoryMode.Optimize, outputs[2].Mode);
        Assert.Equal(2U, outputs[0].Rank);
        Assert.Equal(0U, outputs[1].Rank);
        Assert.Equal(1U, outputs[2].Rank);
        Assert.Equal(3U, snapshot.OutputCount);
        Assert.Equal(2U, snapshot.OptimizeCount);
        Assert.Equal(0U, snapshot.StrongestCount);
    }

    [Fact]
    public void UnrestrictedModeRequiresTheExplicitEnvelopeFlag()
    {
        var configuration = CreateConfiguration();
        var software = new[]
        {
            CreateSoftware(10, 0, 1, 81),
            CreateSoftware(20, 1, 2, 81),
            CreateSoftware(30, 2, 3, 81)
        };

        using (var restrictedSession = new NativeMemoryModeControllerSession(in configuration))
        {
            var outputs = new NativeMemoryModeDesiredSoftwareOutput[3];
            var snapshot = default(NativeMemoryModeSnapshot);
            var envelope = CreateEnvelope(8_000, allowUnrestricted: false);
            Assert.Equal(
                NativeMemoryModeControllerStatus.Ok,
                restrictedSession.Plan(in envelope, software, outputs, ref snapshot));
            Assert.All(outputs, static row => Assert.Equal(NativeMemoryMode.Normal, row.Mode));
            Assert.False(snapshot.Flags.HasFlag(NativeMemoryModeSnapshotFlags.UnrestrictedAllowed));
        }

        using var unrestrictedSession = new NativeMemoryModeControllerSession(in configuration);
        var unrestrictedOutputs = new NativeMemoryModeDesiredSoftwareOutput[3];
        var unrestrictedSnapshot = default(NativeMemoryModeSnapshot);
        var unrestrictedEnvelope = CreateEnvelope(8_000, allowUnrestricted: true);
        Assert.Equal(
            NativeMemoryModeControllerStatus.Ok,
            unrestrictedSession.Plan(
                in unrestrictedEnvelope,
                software,
                unrestrictedOutputs,
                ref unrestrictedSnapshot));
        Assert.All(
            unrestrictedOutputs,
            static row => Assert.Equal(NativeMemoryMode.Unrestricted, row.Mode));
        Assert.True(unrestrictedSnapshot.Flags.HasFlag(
            NativeMemoryModeSnapshotFlags.UnrestrictedAllowed));
    }

    [Fact]
    public void BaseScoreTiersClampThePressureRequestAcrossTheManagedAbi()
    {
        var configuration = CreateConfiguration();
        using var session = new NativeMemoryModeControllerSession(in configuration);
        var software = new[]
        {
            CreateSoftware(10, 0, 1, 20),
            CreateSoftware(20, 1, 2, 21),
            CreateSoftware(30, 2, 3, 81)
        };
        var outputs = new NativeMemoryModeDesiredSoftwareOutput[3];
        var snapshot = default(NativeMemoryModeSnapshot);
        var envelope = CreateEnvelope(0, allowUnrestricted: true);

        Assert.Equal(
            NativeMemoryModeControllerStatus.Ok,
            session.Plan(in envelope, software, outputs, ref snapshot));
        Assert.Equal(NativeMemoryMode.PagedFrozen, outputs[0].Mode);
        Assert.Equal(NativeMemoryMode.Optimize, outputs[1].Mode);
        Assert.Equal(NativeMemoryMode.Normal, outputs[2].Mode);
        Assert.Equal(NativeMemoryModeDesiredSoftwareFlags.None, outputs[0].Flags);
        Assert.Equal(
            NativeMemoryModeDesiredSoftwareFlags.None,
            outputs[1].Flags);
        Assert.Equal(
            NativeMemoryModeDesiredSoftwareFlags.None,
            outputs[2].Flags);
        Assert.Equal(1U, snapshot.StrongestCount);
        Assert.Equal(1U, snapshot.OptimizeCount);

        using var highFreeSession = new NativeMemoryModeControllerSession(in configuration);
        var highFreeOutputs = new NativeMemoryModeDesiredSoftwareOutput[3];
        var highFreeSnapshot = default(NativeMemoryModeSnapshot);
        var highFreeEnvelope = CreateEnvelope(8_000, allowUnrestricted: true);
        Assert.Equal(
            NativeMemoryModeControllerStatus.Ok,
            highFreeSession.Plan(
                in highFreeEnvelope,
                software,
                highFreeOutputs,
                ref highFreeSnapshot));
        Assert.Equal(NativeMemoryMode.Normal, highFreeOutputs[0].Mode);
        Assert.Equal(NativeMemoryMode.Normal, highFreeOutputs[1].Mode);
        Assert.Equal(NativeMemoryMode.Unrestricted, highFreeOutputs[2].Mode);
        Assert.Equal(
            NativeMemoryModeDesiredSoftwareFlags.BaseScoreClamped,
            highFreeOutputs[0].Flags);
        Assert.Equal(
            NativeMemoryModeDesiredSoftwareFlags.BaseScoreClamped,
            highFreeOutputs[1].Flags);
        Assert.Equal(NativeMemoryModeDesiredSoftwareFlags.None, highFreeOutputs[2].Flags);
    }

    [Fact]
    public void ProtectedLowCpuSoftwareDoesNotConsumeOptimizeQuota()
    {
        var configuration = CreateConfiguration();
        using var session = new NativeMemoryModeControllerSession(in configuration);
        var software = new[]
        {
            CreateSoftware(10, 0, 0, 81),
            CreateSoftware(20, 1, 1, 21)
        };
        var outputs = new NativeMemoryModeDesiredSoftwareOutput[2];
        var snapshot = default(NativeMemoryModeSnapshot);
        var envelope = CreateEnvelope(2_000, allowUnrestricted: false, softwareCount: 2);

        Assert.Equal(
            NativeMemoryModeControllerStatus.Ok,
            session.Plan(in envelope, software, outputs, ref snapshot));
        Assert.Equal(NativeMemoryMode.Normal, outputs[0].Mode);
        Assert.Equal(NativeMemoryMode.Optimize, outputs[1].Mode);
        Assert.True(outputs[0].Flags.HasFlag(
            NativeMemoryModeDesiredSoftwareFlags.BaseScoreClamped));
        Assert.Equal(1U, snapshot.OptimizeCount);
    }

    [Theory]
    [InlineData(2_380U, 19U, 0U)]
    [InlineData(1_000U, 59U, 0U)]
    [InlineData(500U, 49U, 10U)]
    [InlineData(0U, 39U, 20U)]
    public void MixedPopulationUsesEligibleCountsAndRestrictsLowestCpuScoresFirst(
        uint freeRatio, uint optimizeCount, uint strongestCount)
    {
        const int protectedCount = 130;
        const int count = 189;
        var configuration = CreateConfiguration(count);
        using var session = new NativeMemoryModeControllerSession(in configuration);
        var software = Enumerable.Range(0, count).Select(index => CreateSoftware(
            (ulong)index + 1,
            (uint)index,
            index < protectedCount ? 0 : count - index,
            index < protectedCount ? 90 : (index - protectedCount) % 3 == 0 ? 10 : 60)).ToArray();
        var outputs = new NativeMemoryModeDesiredSoftwareOutput[count];
        var snapshot = default(NativeMemoryModeSnapshot);
        var envelope = CreateEnvelope(freeRatio, allowUnrestricted: false, softwareCount: count);

        Assert.Equal(NativeMemoryModeControllerStatus.Ok,
            session.Plan(in envelope, software, outputs, ref snapshot));
        Assert.Equal((uint)count, snapshot.OutputCount);
        Assert.Equal(optimizeCount, snapshot.OptimizeCount);
        Assert.Equal(strongestCount, snapshot.StrongestCount);
        Assert.All(outputs.Take(protectedCount), row => Assert.Equal(NativeMemoryMode.Normal, row.Mode));
        var strongest = software.Where(row => row.BaseScore < 21)
            .OrderBy(row => row.CpuScore).Take((int)strongestCount).Select(row => row.SoftwareKey).ToHashSet();
        var optimizing = software.Where(row => row.BaseScore < 81 && !strongest.Contains(row.SoftwareKey))
            .OrderBy(row => row.CpuScore).Take((int)optimizeCount).Select(row => row.SoftwareKey).ToHashSet();
        Assert.All(outputs, row => Assert.Equal(
            strongest.Contains(row.SoftwareKey) ? NativeMemoryMode.PagedFrozen :
            optimizing.Contains(row.SoftwareKey) ? NativeMemoryMode.Optimize : NativeMemoryMode.Normal,
            row.Mode));
        if (freeRatio == 2_380)
        {
            Assert.Equal(NativeMemoryMode.Normal, outputs[protectedCount].Mode);
            Assert.Equal(NativeMemoryMode.Optimize, outputs[^1].Mode);
        }
    }

    [Fact]
    public void EqualBaseScoreTierThresholdsAreRejected()
    {
        var configuration = NativeMemoryModeConfigurationWriter.Create(
            generation: 1,
            maximumSoftwareCount: 1,
            ratioUnitsMaximum: 10_000,
            unrestrictedMinimumFreeRatioUnits: 6_000,
            normalMinimumFreeRatioUnits: 3_000,
            strongBeginFreeRatioUnits: 1_000,
            middleTierMinimumBaseScore: 21,
            highTierMinimumBaseScore: 21);

        _ = Assert.Throws<InvalidOperationException>(() =>
            new NativeMemoryModeControllerSession(in configuration));
    }

    [Theory]
    [InlineData(0x0005_0000U)]
    [InlineData(0x0006_0000U)]
    [InlineData(0x0006_0001U)]
    public void PriorAbiConfigurationAndEnvelopeAreRejectedWithoutFallback(uint oldVersion)
    {
        var oldConfiguration = CreateConfiguration();
        oldConfiguration.AbiVersion = oldVersion;
        Assert.Throws<InvalidOperationException>(() =>
            new NativeMemoryModeControllerSession(in oldConfiguration));

        var configuration = CreateConfiguration();
        using var session = new NativeMemoryModeControllerSession(in configuration);
        var software = new[]
        {
            CreateSoftware(10, 0, 1, 60),
            CreateSoftware(20, 1, 2, 60),
            CreateSoftware(30, 2, 3, 60)
        };
        var outputs = new NativeMemoryModeDesiredSoftwareOutput[3];
        var snapshot = default(NativeMemoryModeSnapshot);
        var envelope = CreateEnvelope(4_000, allowUnrestricted: false);
        envelope.AbiVersion = oldVersion;

        Assert.Equal(
            NativeMemoryModeControllerStatus.AbiMismatch,
            session.Plan(in envelope, software, outputs, ref snapshot));
    }

    [Fact]
    public void RepeatingARoundIsDeterministicAndRecomputesTheCurrentPressure()
    {
        var configuration = CreateConfiguration();
        using var session = new NativeMemoryModeControllerSession(in configuration);
        var inputs = new[] { CreateSoftware(10, 0, 1, 20), CreateSoftware(20, 1, 2, 20), CreateSoftware(30, 2, 3, 20) };
        var outputs = new NativeMemoryModeDesiredSoftwareOutput[3];
        var snapshot = default(NativeMemoryModeSnapshot);
        var envelope = CreateEnvelope(4_000, false);
        Assert.Equal(NativeMemoryModeControllerStatus.Ok, session.Plan(in envelope, inputs, outputs, ref snapshot));
        var first = outputs.ToArray();
        Assert.Equal(NativeMemoryModeControllerStatus.Ok, session.Plan(in envelope, inputs, outputs, ref snapshot));
        Assert.Equal(first.Select(row => (row.Mode, row.Score, row.Rank)), outputs.Select(row => (row.Mode, row.Score, row.Rank)));
        envelope.MemoryFreeRatioUnits = 500;
        Assert.Equal(NativeMemoryModeControllerStatus.Ok, session.Plan(in envelope, inputs, outputs, ref snapshot));
        Assert.Equal(envelope.RoundNumber, snapshot.RoundNumber);
        Assert.Equal(NativeMemoryMode.PagedFrozen, outputs[0].Mode);
    }

    private static NativeMemoryModeConfiguration CreateConfiguration(uint maximumSoftwareCount = 3)
        => NativeMemoryModeConfigurationWriter.Create(
            generation: 1,
            maximumSoftwareCount: maximumSoftwareCount,
            ratioUnitsMaximum: 10_000,
            unrestrictedMinimumFreeRatioUnits: 6_000,
            normalMinimumFreeRatioUnits: 3_000,
            strongBeginFreeRatioUnits: 1_000,
            middleTierMinimumBaseScore: 21,
            highTierMinimumBaseScore: 81);

    private static NativeMemoryModeGenerationEnvelope CreateEnvelope(
        uint memoryFreeRatioUnits,
        bool allowUnrestricted,
        uint softwareCount = 3)
        => new()
        {
            AbiVersion = NativeMemoryModeControllerAbi.Version,
            StructSize = NativeMemoryModeControllerSession.SizeOf<NativeMemoryModeGenerationEnvelope>(),
            SoftwareInputStructSize = NativeMemoryModeControllerSession.SizeOf<NativeMemoryModeSoftwareInput>(),
            SoftwareOutputStructSize = NativeMemoryModeControllerSession.SizeOf<NativeMemoryModeDesiredSoftwareOutput>(),
            ConfigurationGeneration = 1,
            RoundNumber = 10,
            ValidMask = NativeMemoryModeEnvelopeValidity.Required,
            Flags = NativeMemoryModeEnvelopeFlags.Required |
                (allowUnrestricted ? NativeMemoryModeEnvelopeFlags.AllowUnrestricted : 0),
            MemoryFreeRatioUnits = memoryFreeRatioUnits,
            SoftwareCount = softwareCount,
            OutputCapacity = softwareCount
        };

    [Theory]
    [InlineData(999, (int)NativeMemoryMode.PagedFrozen)]
    [InlineData(1_000, (int)NativeMemoryMode.Optimize)]
    [InlineData(2_999, (int)NativeMemoryMode.Optimize)]
    [InlineData(3_000, (int)NativeMemoryMode.Normal)]
    [InlineData(5_999, (int)NativeMemoryMode.Normal)]
    [InlineData(6_000, (int)NativeMemoryMode.Unrestricted)]
    public void FixedPointThresholdBoundariesAreExact(
        uint freeRatioUnits,
        int expectedLowestMode)
    {
        var configuration = CreateConfiguration();
        using var session = new NativeMemoryModeControllerSession(in configuration);
        var software = new[]
        {
            CreateSoftware(10, 0, 1, freeRatioUnits >= 6_000 ? 81 : 20),
            CreateSoftware(20, 1, 2, freeRatioUnits >= 6_000 ? 81 : 20),
            CreateSoftware(30, 2, 3, freeRatioUnits >= 6_000 ? 81 : 20)
        };
        var outputs = new NativeMemoryModeDesiredSoftwareOutput[3];
        var snapshot = default(NativeMemoryModeSnapshot);
        var envelope = CreateEnvelope(freeRatioUnits, allowUnrestricted: true);

        Assert.Equal(
            NativeMemoryModeControllerStatus.Ok,
            session.Plan(in envelope, software, outputs, ref snapshot));
        Assert.Equal((NativeMemoryMode)expectedLowestMode,
            outputs.Single(row => row.Rank == 0).Mode);
    }

    private static NativeMemoryModeSoftwareInput CreateSoftware(
        ulong softwareKey,
        uint sourceIndex,
        double score,
        double baseScore)
        => new()
        {
            StructSize = NativeMemoryModeControllerSession.SizeOf<NativeMemoryModeSoftwareInput>(),
            ValidMask = NativeMemoryModeSoftwareValidity.Required,
            SoftwareKey = softwareKey,
            CpuScore = score,
            BaseScore = baseScore,
            SourceIndex = sourceIndex
        };

}
