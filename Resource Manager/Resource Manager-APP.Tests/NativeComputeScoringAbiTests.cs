using System.Runtime.InteropServices;
using ResourceManager.App.Infrastructure.NativeCore;

namespace Resource_Manager_APP.Tests;

public sealed class NativeComputeScoringAbiTests
{
    [Theory]
    [InlineData(typeof(NativeComputeScoringConfiguration), 200)]
    [InlineData(typeof(NativeComputeScoringCapacity), 72)]
    [InlineData(typeof(NativeComputeScoringGenerationEnvelope), 168)]
    [InlineData(typeof(NativeComputeScoringProcessInput), 96)]
    [InlineData(typeof(NativeComputeScoringCpuCoreInput), 16)]
    [InlineData(typeof(NativeComputeScoringGpuInput), 96)]
    [InlineData(typeof(NativeComputeScoringOutput), 96)]
    [InlineData(typeof(NativeComputeScoringSnapshot), 224)]
    public void ManagedStructSizesMatchNativeContract(Type type, int expectedSize)
        => Assert.Equal(expectedSize, Marshal.SizeOf(type));

    [Theory]
    [InlineData(typeof(NativeComputeScoringConfiguration), nameof(NativeComputeScoringConfiguration.CpuCoreCount), 36)]
    [InlineData(typeof(NativeComputeScoringConfiguration), nameof(NativeComputeScoringConfiguration.CpuStateMultipliers), 40)]
    [InlineData(typeof(NativeComputeScoringConfiguration), nameof(NativeComputeScoringConfiguration.GpuStateMultipliers), 96)]
    [InlineData(typeof(NativeComputeScoringConfiguration), nameof(NativeComputeScoringConfiguration.CpuBaselineRatio), 168)]
    [InlineData(typeof(NativeComputeScoringConfiguration), nameof(NativeComputeScoringConfiguration.WelfareUtilizationBaselinePercent), 176)]
    [InlineData(typeof(NativeComputeScoringConfiguration), nameof(NativeComputeScoringConfiguration.Reserved), 184)]
    [InlineData(typeof(NativeComputeScoringGenerationEnvelope), nameof(NativeComputeScoringGenerationEnvelope.SoftwareBaseMean), 160)]
    [InlineData(typeof(NativeComputeScoringSnapshot), nameof(NativeComputeScoringSnapshot.SoftwareBaseMean), 176)]
    [InlineData(typeof(NativeComputeScoringSnapshot), nameof(NativeComputeScoringSnapshot.CpuWelfareMultiplier), 184)]
    [InlineData(typeof(NativeComputeScoringSnapshot), nameof(NativeComputeScoringSnapshot.MemoryWelfareMultiplier), 192)]
    [InlineData(typeof(NativeComputeScoringSnapshot), nameof(NativeComputeScoringSnapshot.CpuWelfareBonus), 200)]
    [InlineData(typeof(NativeComputeScoringSnapshot), nameof(NativeComputeScoringSnapshot.MemoryWelfareBonus), 208)]
    [InlineData(typeof(NativeComputeScoringSnapshot), nameof(NativeComputeScoringSnapshot.SoftwareMemoryOutputCount), 216)]
    [InlineData(typeof(NativeComputeScoringSnapshot), nameof(NativeComputeScoringSnapshot.Reserved1), 220)]
    [InlineData(typeof(NativeComputeScoringCpuCoreInput), nameof(NativeComputeScoringCpuCoreInput.ProcessIndex), 0)]
    [InlineData(typeof(NativeComputeScoringCpuCoreInput), nameof(NativeComputeScoringCpuCoreInput.CoreIndex), 4)]
    [InlineData(typeof(NativeComputeScoringCpuCoreInput), nameof(NativeComputeScoringCpuCoreInput.UsagePercent), 8)]
    [InlineData(typeof(NativeComputeScoringGenerationEnvelope), nameof(NativeComputeScoringGenerationEnvelope.GpuTopologyGeneration), 56)]
    [InlineData(typeof(NativeComputeScoringGenerationEnvelope), nameof(NativeComputeScoringGenerationEnvelope.GpuTopologyFingerprint), 64)]
    [InlineData(typeof(NativeComputeScoringGenerationEnvelope), nameof(NativeComputeScoringGenerationEnvelope.ValidMask), 88)]
    [InlineData(typeof(NativeComputeScoringGenerationEnvelope), nameof(NativeComputeScoringGenerationEnvelope.ProcessCount), 104)]
    [InlineData(typeof(NativeComputeScoringGenerationEnvelope), nameof(NativeComputeScoringGenerationEnvelope.CpuFreeRatio), 120)]
    [InlineData(typeof(NativeComputeScoringGenerationEnvelope), nameof(NativeComputeScoringGenerationEnvelope.WelfareEligibleProcessCount), 152)]
    [InlineData(typeof(NativeComputeScoringProcessInput), nameof(NativeComputeScoringProcessInput.WeightedCpuUsePercent), 64)]
    [InlineData(typeof(NativeComputeScoringProcessInput), nameof(NativeComputeScoringProcessInput.ProcessId), 76)]
    [InlineData(typeof(NativeComputeScoringGpuInput), nameof(NativeComputeScoringGpuInput.AdapterKey), 48)]
    [InlineData(typeof(NativeComputeScoringOutput), nameof(NativeComputeScoringOutput.Score), 56)]
    [InlineData(typeof(NativeComputeScoringSnapshot), nameof(NativeComputeScoringSnapshot.GpuTopologyGeneration), 40)]
    [InlineData(typeof(NativeComputeScoringSnapshot), nameof(NativeComputeScoringSnapshot.GpuTopologyFingerprint), 48)]
    [InlineData(typeof(NativeComputeScoringSnapshot), nameof(NativeComputeScoringSnapshot.OutputCount), 88)]
    [InlineData(typeof(NativeComputeScoringSnapshot), nameof(NativeComputeScoringSnapshot.CpuFreeRatio), 96)]
    [InlineData(typeof(NativeComputeScoringSnapshot), nameof(NativeComputeScoringSnapshot.WelfareShare), 152)]
    public void ManagedFieldOffsetsMatchNativeContract(Type type, string field, int expectedOffset)
        => Assert.Equal(new IntPtr(expectedOffset), Marshal.OffsetOf(type, field));

    [Fact]
    public void EnumAndMaskValuesMatchNativeContract()
    {
        Assert.Equal(0x0004_0004U, NativeComputeScoringAbi.Version);
        Assert.Equal(7, NativeComputeScoringAbi.RuntimeStateCount);
        Assert.Equal(0x0FUL, (ulong)NativeComputeScoringConfigFields.Required);
        Assert.Equal(0x03UL, (ulong)NativeComputeScoringEnvelopeFlags.Known);
        Assert.Equal(0x7FUL, (ulong)NativeComputeScoringProcessValidity.CpuRequired);
        Assert.Equal(0x3FUL, (ulong)NativeComputeScoringGpuValidity.Required);
        Assert.Equal(4, (byte)NativeComputeScoringOutputKind.SoftwareGpu);
        Assert.Equal(5, (byte)NativeComputeScoringOutputKind.SoftwareMemory);
    }
}
