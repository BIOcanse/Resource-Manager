using System.Runtime.InteropServices;
using ResourceManager.App.Infrastructure.NativeCore;

namespace Resource_Manager_APP.Tests;

public sealed partial class NativeComputeScoringSessionTests
{
    [Fact]
    public void CapacityReportsNativeExtentAndSameRequirementsForTwoSessions()
    {
        var configuration = CreateConfiguration();
        using var cpu = new NativeComputeScoringSession(in configuration, [1]);
        using var gpu = new NativeComputeScoringSession(in configuration, [1]);
        var capacity = cpu.Capacity;
        Assert.Equal(0x00070000U, NativeComputeScoringSession.GetAbiVersion());
        Assert.Equal(72U, capacity.StructSize);
        // Each process/GPU slot stores its input row, computed score and two u32 order indexes.
        var processBytes = (ulong)Marshal.SizeOf<NativeComputeScoringProcessInput>() + sizeof(double) + 2UL * sizeof(uint);
        var gpuBytes = (ulong)Marshal.SizeOf<NativeComputeScoringGpuInput>() + sizeof(double) + 2UL * sizeof(uint);
        Assert.Equal(processBytes * configuration.MaximumProcessCount + gpuBytes * configuration.MaximumGpuRowCount,
            capacity.ScratchByteCount);
        Assert.Equal(8U, capacity.ScratchAlignment); // Executed ABI assertion still required, not a managed allocation assumption.
        Assert.Equal(capacity.ScratchByteCount, gpu.Capacity.ScratchByteCount);
        Assert.Equal(capacity.ScratchAlignment, gpu.Capacity.ScratchAlignment);
    }

    [Theory]
    [InlineData("null", NativeComputeScoringStatus.InvalidArgument)]
    [InlineData("short", NativeComputeScoringStatus.BufferTooSmall)]
    [InlineData("misaligned", NativeComputeScoringStatus.InvalidArgument)]
    [InlineData("overflow", NativeComputeScoringStatus.InvalidArgument)]
    [InlineData("length-overflow", NativeComputeScoringStatus.InvalidArgument)]
    internal void BadRegionDoesNotWriteAndSameRoundCanRetry(string kind, NativeComputeScoringStatus expected)
    {
        var configuration = CreateConfiguration();
        using var session = new NativeComputeScoringSession(in configuration, [1]);
        using var scratch = new NativeComputeScoringScratchFixture(session.Capacity);
        var before = scratch.CopyBytes();
        var pointer = kind switch { "null" => IntPtr.Zero, "misaligned" => IntPtr.Add(scratch.Pointer, 1),
            "overflow" => new IntPtr(-8), _ => scratch.Pointer };
        var length = kind switch { "short" => NativeComputeScoringSession.ScratchByteCount(1, 0) - 1,
            "length-overflow" => ulong.MaxValue, _ => scratch.Length };
        var process = CreateProcess(1, 10, 100, 1, 50);
        var outputs = new NativeComputeScoringOutput[4];
        outputs[0].Score = 987;
        var snapshot = new NativeComputeScoringSnapshot { RoundNumber = 999 };
        var envelope = CreateEnvelope(1, 1, 0, 0, outputs.Length, NativeComputeScoringEnvelopeFlags.CpuSnapshotComplete);
        Assert.Equal(expected, session.Score(in envelope, [process], [], outputs, pointer, length, ref snapshot));
        Assert.Equal(987D, outputs[0].Score);
        Assert.Equal(999UL, snapshot.RoundNumber);
        Assert.Equal<byte>(before, scratch.CopyBytes());
        Assert.Equal(NativeComputeScoringStatus.Ok,
            session.Score(in envelope, [process], [], outputs, scratch.Pointer, scratch.Length, ref snapshot));
        Assert.Equal(1UL, snapshot.RoundNumber);
        Assert.Equal(50D, outputs[0].Score);
    }

    [Fact]
    public void ExactSubmittedRowsScoreWithLessScratchThanConfiguredMaximum()
    {
        var configuration = CreateConfiguration();
        using var session = new NativeComputeScoringSession(in configuration, [1]);
        var capacity = session.Capacity;
        var compact = capacity;
        compact.ScratchByteCount = NativeComputeScoringSession.ScratchByteCount(1, 1);
        Assert.True(compact.ScratchByteCount < capacity.ScratchByteCount);
        using var scratch = new NativeComputeScoringScratchFixture(compact);
        var process = CreateProcess(1, 10, 100, 1, 50);
        var gpu = CreateGpu(process, 11, 1, 25);
        var outputs = new NativeComputeScoringOutput[5];
        var envelope = CreateEnvelope(1, 1, 1, 1, outputs.Length,
            NativeComputeScoringEnvelopeFlags.CpuSnapshotComplete | NativeComputeScoringEnvelopeFlags.GpuSnapshotComplete);
        var snapshot = default(NativeComputeScoringSnapshot);
        Assert.Equal(NativeComputeScoringStatus.Ok,
            session.Score(in envelope, [process], [gpu], outputs, scratch.Pointer, scratch.Length, ref snapshot));
        Assert.Equal(5U, snapshot.OutputCount);
        Assert.Equal(50D, outputs[0].Score);
        Assert.Equal(25D, outputs[2].Score);
    }

    [Fact]
    public void CpuAndGpuRecomputeWhileReusingOnePoisonedRegion()
    {
        var configuration = CreateConfiguration();
        using var cpu = new NativeComputeScoringSession(in configuration, [1]);
        using var gpu = new NativeComputeScoringSession(in configuration, [1]);
        using var scratch = new NativeComputeScoringScratchFixture(cpu.Capacity);
        var process = CreateProcess(1, 10, 100, 1, 50);
        var cpuOutputs = new NativeComputeScoringOutput[4];
        var gpuOutputs = new NativeComputeScoringOutput[4];
        var cpuSnapshot = default(NativeComputeScoringSnapshot);
        var gpuSnapshot = default(NativeComputeScoringSnapshot);
        var cpuEnvelope = CreateEnvelope(1, 1, 0, 0, 4, NativeComputeScoringEnvelopeFlags.CpuSnapshotComplete);
        var gpuEnvelope = CreateEnvelope(1, 1, 1, 1, 4, NativeComputeScoringEnvelopeFlags.GpuSnapshotComplete);
        var row = CreateGpu(process, 11, 1, 25);
        Assert.Equal(NativeComputeScoringStatus.Ok,
            cpu.Score(in cpuEnvelope, [process], [], cpuOutputs, scratch.Pointer, scratch.Length, ref cpuSnapshot));
        var saved = cpuOutputs.ToArray();
        scratch.Poison(0x5A);
        Assert.Equal(NativeComputeScoringStatus.Ok,
            gpu.Score(in gpuEnvelope, [process], [row], gpuOutputs, scratch.Pointer, scratch.Length, ref gpuSnapshot));
        Assert.Equal(50D, cpuOutputs[0].Score);
        Assert.Equal(25D, gpuOutputs[0].Score);
        Assert.Equal<NativeComputeScoringOutput>(saved, cpuOutputs);
        cpuEnvelope.RoundNumber = 2;
        scratch.Poison(0xFF);
        Assert.Equal(NativeComputeScoringStatus.Ok,
            cpu.Score(in cpuEnvelope, [process], [], cpuOutputs, scratch.Pointer, scratch.Length, ref cpuSnapshot));
        Assert.Equal(1UL, gpuSnapshot.RoundNumber);
        Assert.Equal(NativeComputeScoringStatus.Ok,
            gpu.Score(in gpuEnvelope, [process], [row], gpuOutputs, scratch.Pointer, scratch.Length, ref gpuSnapshot));
        gpuEnvelope.RoundNumber = 2;
        Assert.Equal(NativeComputeScoringStatus.Ok,
            gpu.Score(in gpuEnvelope, [process], [row], gpuOutputs, scratch.Pointer, scratch.Length, ref gpuSnapshot));
    }

}

// Test-only ordinary allocation; production Session never allocates or retains scratch.
internal sealed class NativeComputeScoringScratchFixture : IDisposable
{
    private readonly byte[] bytes;
    private GCHandle pin;
    internal IntPtr Pointer { get; }
    internal ulong Length { get; }

    internal NativeComputeScoringScratchFixture(in NativeComputeScoringCapacity capacity)
    {
        Length = capacity.ScratchByteCount;
        if (Length == 0 || capacity.ScratchAlignment == 0 ||
            (capacity.ScratchAlignment & (capacity.ScratchAlignment - 1)) != 0)
            throw new ArgumentException("Invalid native scratch requirement.", nameof(capacity));
        bytes = new byte[checked((int)(Length + capacity.ScratchAlignment - 1))];
        pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            var mask = (ulong)capacity.ScratchAlignment - 1;
            var address = checked((ulong)pin.AddrOfPinnedObject().ToInt64());
            Pointer = new IntPtr(checked((long)(checked(address + mask) & ~mask)));
            Poison(0xA5);
        }
        catch { pin.Free(); throw; }
    }

    internal void Poison(byte value) => Array.Fill(bytes, value);
    internal byte[] CopyBytes() => bytes.ToArray();
    public void Dispose() { if (pin.IsAllocated) pin.Free(); }
}

internal static class NativeComputeScoringScratchFixtureCalls
{
    internal static NativeComputeScoringStatus ScoreWithScratchFixture(this NativeComputeScoringSession session,
        in NativeComputeScoringGenerationEnvelope envelope,
        ReadOnlySpan<NativeComputeScoringProcessInput> processes,
        ReadOnlySpan<NativeComputeScoringGpuInput> gpus,
        Span<NativeComputeScoringOutput> outputs,
        ref NativeComputeScoringSnapshot snapshot)
    {
        using var scratch = new NativeComputeScoringScratchFixture(session.Capacity);
        return session.Score(in envelope, processes, gpus, outputs, scratch.Pointer, scratch.Length, ref snapshot);
    }
}
