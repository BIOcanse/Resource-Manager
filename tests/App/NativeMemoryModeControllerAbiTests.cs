using System.Runtime.InteropServices;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Tests;

public sealed class NativeMemoryModeControllerAbiTests
{
    [Theory]
    [InlineData(typeof(NativeMemoryModeConfiguration), 80)]
    [InlineData(typeof(NativeMemoryModeCapacity), 64)]
    [InlineData(typeof(NativeMemoryModeGenerationEnvelope), 112)]
    [InlineData(typeof(NativeMemoryModeSoftwareInput), 64)]
    [InlineData(typeof(NativeMemoryModeDesiredSoftwareOutput), 64)]
    [InlineData(typeof(NativeMemoryModeSnapshot), 96)]
    public void NativeStructSizesAreStable(Type type, int expected)
        => Assert.Equal(expected, Marshal.SizeOf(type));

    [Theory]
    [InlineData(typeof(NativeMemoryModeConfiguration), nameof(NativeMemoryModeConfiguration.RatioUnitsMaximum), 24)]
    [InlineData(typeof(NativeMemoryModeConfiguration), nameof(NativeMemoryModeConfiguration.UnrestrictedMinimumFreeRatioUnits), 28)]
    [InlineData(typeof(NativeMemoryModeConfiguration), nameof(NativeMemoryModeConfiguration.NormalMinimumFreeRatioUnits), 32)]
    [InlineData(typeof(NativeMemoryModeConfiguration), nameof(NativeMemoryModeConfiguration.StrongBeginFreeRatioUnits), 36)]
    [InlineData(typeof(NativeMemoryModeConfiguration), nameof(NativeMemoryModeConfiguration.MiddleTierMinimumBaseScore), 40)]
    [InlineData(typeof(NativeMemoryModeConfiguration), nameof(NativeMemoryModeConfiguration.HighTierMinimumBaseScore), 48)]
    [InlineData(typeof(NativeMemoryModeGenerationEnvelope), nameof(NativeMemoryModeGenerationEnvelope.MemorySourceWorkspaceIdentity), 32)]
    [InlineData(typeof(NativeMemoryModeGenerationEnvelope), nameof(NativeMemoryModeGenerationEnvelope.MemorySourceCommittedGeneration), 40)]
    [InlineData(typeof(NativeMemoryModeGenerationEnvelope), nameof(NativeMemoryModeGenerationEnvelope.ValidMask), 56)]
    [InlineData(typeof(NativeMemoryModeGenerationEnvelope), nameof(NativeMemoryModeGenerationEnvelope.MemoryFreeRatioUnits), 72)]
    [InlineData(typeof(NativeMemoryModeGenerationEnvelope), nameof(NativeMemoryModeGenerationEnvelope.SoftwareCount), 80)]
    [InlineData(typeof(NativeMemoryModeSoftwareInput), nameof(NativeMemoryModeSoftwareInput.CpuScore), 32)]
    [InlineData(typeof(NativeMemoryModeSoftwareInput), nameof(NativeMemoryModeSoftwareInput.BaseScore), 40)]
    [InlineData(typeof(NativeMemoryModeDesiredSoftwareOutput), nameof(NativeMemoryModeDesiredSoftwareOutput.Score), 40)]
    [InlineData(typeof(NativeMemoryModeDesiredSoftwareOutput), nameof(NativeMemoryModeDesiredSoftwareOutput.BaseScoreAllowedGrades), 6)]
    [InlineData(typeof(NativeMemoryModeDesiredSoftwareOutput), nameof(NativeMemoryModeDesiredSoftwareOutput.Reserved0), 7)]
    [InlineData(typeof(NativeMemoryModeDesiredSoftwareOutput), nameof(NativeMemoryModeDesiredSoftwareOutput.BaseScore), 48)]
    [InlineData(typeof(NativeMemoryModeSnapshot), nameof(NativeMemoryModeSnapshot.MemorySourceWorkspaceIdentity), 24)]
    [InlineData(typeof(NativeMemoryModeSnapshot), nameof(NativeMemoryModeSnapshot.MemorySourceCommittedGeneration), 32)]
    [InlineData(typeof(NativeMemoryModeSnapshot), nameof(NativeMemoryModeSnapshot.OutputCount), 56)]
    public void NativeFieldOffsetsAreStable(Type type, string field, int expected)
        => Assert.Equal(expected, Marshal.OffsetOf(type, field).ToInt32());

    [Fact]
    public void SingleMemoryDomainAndRequiredMasksRemainStable()
    {
        Assert.Equal(0x0006_0001U, NativeMemoryModeControllerAbi.Version);
        Assert.Equal(4, (byte)NativeMemoryMode.PagedFrozen);
        Assert.Equal(0x07UL, (ulong)NativeMemoryModeEnvelopeValidity.Required);
        Assert.Equal(0x01UL, (ulong)NativeMemoryModeEnvelopeFlags.Required);
        Assert.Equal(0x0FUL, (ulong)NativeMemoryModeSoftwareValidity.Required);
        Assert.Equal(0xFFUL, (ulong)NativeMemoryModeDesiredSoftwareValidity.Required);
        Assert.Equal(0x1F, (byte)NativeMemoryGradeSet.Known);
    }
}
