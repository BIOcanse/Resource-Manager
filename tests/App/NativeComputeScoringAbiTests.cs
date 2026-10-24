using System.Runtime.InteropServices;
using ResourceManager.App.Infrastructure.NativeCore;

namespace Resource_Manager_APP.Tests;

public sealed class NativeComputeScoringAbiTests
{
    [Theory]
    [InlineData(typeof(NativeComputeScoringConfiguration), 200)]
    [InlineData(typeof(NativeComputeScoringCapacity), 72)]
    [InlineData(typeof(NativeComputeScoringGenerationEnvelope), 104)]
    [InlineData(typeof(NativeComputeScoringProcessInput), 88)]
    [InlineData(typeof(NativeComputeScoringCpuCoreInput), 16)]
    [InlineData(typeof(NativeComputeScoringGpuInput), 88)]
    [InlineData(typeof(NativeComputeScoringOutput), 96)]
    [InlineData(typeof(NativeComputeScoringSnapshot), 176)]
    public void ManagedStructSizesMatchNativeContract(Type type, int expectedSize)
        => Assert.Equal(expectedSize, Marshal.SizeOf(type));

    [Theory]
    [InlineData(typeof(NativeComputeScoringCapacity), nameof(NativeComputeScoringCapacity.ScratchByteCount), 40)]
    [InlineData(typeof(NativeComputeScoringCapacity), nameof(NativeComputeScoringCapacity.ScratchAlignment), 48)]
    [InlineData(typeof(NativeComputeScoringCapacity), nameof(NativeComputeScoringCapacity.Reserved0), 52)]
    [InlineData(typeof(NativeComputeScoringCapacity), nameof(NativeComputeScoringCapacity.Reserved), 56)]
    [InlineData(typeof(NativeComputeScoringConfiguration), nameof(NativeComputeScoringConfiguration.CpuCoreCount), 36)]
    [InlineData(typeof(NativeComputeScoringConfiguration), nameof(NativeComputeScoringConfiguration.CpuStateMultipliers), 40)]
    [InlineData(typeof(NativeComputeScoringConfiguration), nameof(NativeComputeScoringConfiguration.GpuStateMultipliers), 96)]
    [InlineData(typeof(NativeComputeScoringConfiguration), nameof(NativeComputeScoringConfiguration.CpuBaselineRatio), 168)]
    [InlineData(typeof(NativeComputeScoringConfiguration), nameof(NativeComputeScoringConfiguration.WelfareBaseScore), 176)]
    [InlineData(typeof(NativeComputeScoringConfiguration), nameof(NativeComputeScoringConfiguration.Reserved), 184)]
    [InlineData(typeof(NativeComputeScoringSnapshot), nameof(NativeComputeScoringSnapshot.CpuWelfareMultiplier), 136)]
    [InlineData(typeof(NativeComputeScoringSnapshot), nameof(NativeComputeScoringSnapshot.MemoryWelfareMultiplier), 144)]
    [InlineData(typeof(NativeComputeScoringSnapshot), nameof(NativeComputeScoringSnapshot.CpuWelfareBonus), 152)]
    [InlineData(typeof(NativeComputeScoringSnapshot), nameof(NativeComputeScoringSnapshot.MemoryWelfareBonus), 160)]
    [InlineData(typeof(NativeComputeScoringSnapshot), nameof(NativeComputeScoringSnapshot.SoftwareMemoryOutputCount), 168)]
    [InlineData(typeof(NativeComputeScoringSnapshot), nameof(NativeComputeScoringSnapshot.Reserved1), 172)]
    [InlineData(typeof(NativeComputeScoringCpuCoreInput), nameof(NativeComputeScoringCpuCoreInput.ProcessIndex), 0)]
    [InlineData(typeof(NativeComputeScoringCpuCoreInput), nameof(NativeComputeScoringCpuCoreInput.CoreIndex), 4)]
    [InlineData(typeof(NativeComputeScoringCpuCoreInput), nameof(NativeComputeScoringCpuCoreInput.UsagePercent), 8)]
    [InlineData(typeof(NativeComputeScoringGenerationEnvelope), nameof(NativeComputeScoringGenerationEnvelope.ValidMask), 32)]
    [InlineData(typeof(NativeComputeScoringGenerationEnvelope), nameof(NativeComputeScoringGenerationEnvelope.ProcessCount), 48)]
    [InlineData(typeof(NativeComputeScoringGenerationEnvelope), nameof(NativeComputeScoringGenerationEnvelope.CpuFreeRatio), 64)]
    [InlineData(typeof(NativeComputeScoringGenerationEnvelope), nameof(NativeComputeScoringGenerationEnvelope.WelfareEligibleProcessCount), 96)]
    [InlineData(typeof(NativeComputeScoringProcessInput), nameof(NativeComputeScoringProcessInput.WeightedCpuUsePercent), 56)]
    [InlineData(typeof(NativeComputeScoringProcessInput), nameof(NativeComputeScoringProcessInput.ProcessId), 68)]
    [InlineData(typeof(NativeComputeScoringGpuInput), nameof(NativeComputeScoringGpuInput.AdapterKey), 40)]
    [InlineData(typeof(NativeComputeScoringOutput), nameof(NativeComputeScoringOutput.Score), 56)]
    [InlineData(typeof(NativeComputeScoringSnapshot), nameof(NativeComputeScoringSnapshot.OutputCount), 48)]
    [InlineData(typeof(NativeComputeScoringSnapshot), nameof(NativeComputeScoringSnapshot.CpuFreeRatio), 56)]
    [InlineData(typeof(NativeComputeScoringSnapshot), nameof(NativeComputeScoringSnapshot.WelfareShare), 112)]
    public void ManagedFieldOffsetsMatchNativeContract(Type type, string field, int expectedOffset)
        => Assert.Equal(new IntPtr(expectedOffset), Marshal.OffsetOf(type, field));

    [Fact]
    public void EnumAndMaskValuesMatchNativeContract()
    {
        Assert.Equal(0x0007_0000U, NativeComputeScoringAbi.Version);
        Assert.Equal(7, NativeComputeScoringAbi.RuntimeStateCount);
        Assert.Equal(0x0FUL, (ulong)NativeComputeScoringConfigFields.Required);
        Assert.Equal(0x03UL, (ulong)NativeComputeScoringEnvelopeFlags.Known);
        Assert.Equal(0x3FUL, (ulong)NativeComputeScoringProcessValidity.CpuRequired);
        Assert.Equal(0x1FUL, (ulong)NativeComputeScoringGpuValidity.Required);
        Assert.Equal(4, (byte)NativeComputeScoringOutputKind.SoftwareGpu);
        Assert.Equal(5, (byte)NativeComputeScoringOutputKind.SoftwareMemory);
    }
}
