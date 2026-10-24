using System.Runtime.InteropServices;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Tests;

public sealed class NativeMemoryModeControllerAbiTests
{
    [Theory]
    [InlineData(typeof(NativeMemoryModeConfiguration), 80)]
    [InlineData(typeof(NativeMemoryModeCapacity), 64)]
    [InlineData(typeof(NativeMemoryModeGenerationEnvelope), 88)]
    [InlineData(typeof(NativeMemoryModeSoftwareInput), 56)]
    [InlineData(typeof(NativeMemoryModeDesiredSoftwareOutput), 56)]
    [InlineData(typeof(NativeMemoryModeSnapshot), 72)]
    public void NativeStructSizesAreStable(Type type, int expected)
        => Assert.Equal(expected, Marshal.SizeOf(type));

    [Theory]
    [InlineData(typeof(NativeMemoryModeConfiguration), nameof(NativeMemoryModeConfiguration.RatioUnitsMaximum), 24)]
    [InlineData(typeof(NativeMemoryModeConfiguration), nameof(NativeMemoryModeConfiguration.UnrestrictedMinimumFreeRatioUnits), 28)]
    [InlineData(typeof(NativeMemoryModeConfiguration), nameof(NativeMemoryModeConfiguration.NormalMinimumFreeRatioUnits), 32)]
    [InlineData(typeof(NativeMemoryModeConfiguration), nameof(NativeMemoryModeConfiguration.StrongBeginFreeRatioUnits), 36)]
    [InlineData(typeof(NativeMemoryModeConfiguration), nameof(NativeMemoryModeConfiguration.MiddleTierMinimumBaseScore), 40)]
    [InlineData(typeof(NativeMemoryModeConfiguration), nameof(NativeMemoryModeConfiguration.HighTierMinimumBaseScore), 48)]
    [InlineData(typeof(NativeMemoryModeGenerationEnvelope), nameof(NativeMemoryModeGenerationEnvelope.ValidMask), 32)]
    [InlineData(typeof(NativeMemoryModeGenerationEnvelope), nameof(NativeMemoryModeGenerationEnvelope.MemoryFreeRatioUnits), 48)]
    [InlineData(typeof(NativeMemoryModeGenerationEnvelope), nameof(NativeMemoryModeGenerationEnvelope.SoftwareCount), 56)]
    [InlineData(typeof(NativeMemoryModeSoftwareInput), nameof(NativeMemoryModeSoftwareInput.CpuScore), 24)]
    [InlineData(typeof(NativeMemoryModeSoftwareInput), nameof(NativeMemoryModeSoftwareInput.BaseScore), 32)]
    [InlineData(typeof(NativeMemoryModeDesiredSoftwareOutput), nameof(NativeMemoryModeDesiredSoftwareOutput.Score), 32)]
    [InlineData(typeof(NativeMemoryModeDesiredSoftwareOutput), nameof(NativeMemoryModeDesiredSoftwareOutput.BaseScoreAllowedGrades), 6)]
    [InlineData(typeof(NativeMemoryModeDesiredSoftwareOutput), nameof(NativeMemoryModeDesiredSoftwareOutput.Reserved0), 7)]
    [InlineData(typeof(NativeMemoryModeDesiredSoftwareOutput), nameof(NativeMemoryModeDesiredSoftwareOutput.BaseScore), 40)]
    [InlineData(typeof(NativeMemoryModeSnapshot), nameof(NativeMemoryModeSnapshot.OutputCount), 32)]
    public void NativeFieldOffsetsAreStable(Type type, string field, int expected)
        => Assert.Equal(expected, Marshal.OffsetOf(type, field).ToInt32());

    [Fact]
    public void SingleMemoryDomainAndRequiredMasksRemainStable()
    {
        Assert.Equal(0x0007_0000U, NativeMemoryModeControllerAbi.Version);
        Assert.Equal(4, (byte)NativeMemoryMode.PagedFrozen);
        Assert.Equal(0x06UL, (ulong)NativeMemoryModeEnvelopeValidity.Required);
        Assert.Equal(0x01UL, (ulong)NativeMemoryModeEnvelopeFlags.Required);
        Assert.Equal(0x0DUL, (ulong)NativeMemoryModeSoftwareValidity.Required);
        Assert.Equal(0xFDUL, (ulong)NativeMemoryModeDesiredSoftwareValidity.Required);
        Assert.Equal(0x1F, (byte)NativeMemoryGradeSet.Known);
    }
}
