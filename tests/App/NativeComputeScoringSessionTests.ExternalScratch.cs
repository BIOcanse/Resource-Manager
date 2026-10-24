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
        Assert.Equal(0x00050000U, NativeComputeScoringSession.GetAbiVersion());
        Assert.Equal(72U, capacity.StructSize);
        Assert.Equal(112UL * (configuration.MaximumProcessCount + configuration.MaximumGpuRowCount), capacity.ScratchByteCount);
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
    internal void BadRegionDoesNotWriteOrCommitAndSameGenerationCanRetry(string kind, NativeComputeScoringStatus expected)
    {
        var configuration = CreateConfiguration();
        using var session = new NativeComputeScoringSession(in configuration, [1]);
        using var scratch = new NativeComputeScoringScratchFixture(session.Capacity);
        var before = scratch.CopyBytes();
        var pointer = kind switch { "null" => IntPtr.Zero, "misaligned" => IntPtr.Add(scratch.Pointer, 1),
            "overflow" => new IntPtr(-8), _ => scratch.Pointer };
        var length = kind switch { "short" => scratch.Length - 1, "length-overflow" => ulong.MaxValue, _ => scratch.Length };
        var process = CreateProcess(1, 10, 100, 1, 50);
        var outputs = new NativeComputeScoringOutput[4];
        outputs[0].Score = 987;
        var snapshot = new NativeComputeScoringSnapshot { SchedulingGeneration = 999 };
        var envelope = CreateEnvelope(1, 1, 0, 0, outputs.Length, NativeComputeScoringEnvelopeFlags.CpuSnapshotComplete);
        Assert.Equal(expected, session.Score(in envelope, [process], [], outputs, pointer, length, ref snapshot));
        Assert.Equal(987D, outputs[0].Score);
        Assert.Equal(999UL, snapshot.SchedulingGeneration);
        Assert.Equal<byte>(before, scratch.CopyBytes());
        Assert.Equal(NativeComputeScoringStatus.Ok,
            session.Score(in envelope, [process], [], outputs, scratch.Pointer, scratch.Length, ref snapshot));
        Assert.Equal(1UL, snapshot.SchedulingGeneration);
        Assert.Equal(50D, outputs[0].Score);
    }

    [Fact]
    public void CpuAndGpuKeepIndependentGenerationsWhileReusingOnePoisonedRegion()
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
        Assert.Equal(0UL, cpuSnapshot.GpuSourceGeneration);
        Assert.Equal(202UL, gpuSnapshot.GpuSourceGeneration);
        cpuEnvelope.SchedulingGeneration = 2;
        scratch.Poison(0xFF);
        Assert.Equal(NativeComputeScoringStatus.Ok,
            cpu.Score(in cpuEnvelope, [process], [], cpuOutputs, scratch.Pointer, scratch.Length, ref cpuSnapshot));
        Assert.Equal(1UL, gpuSnapshot.SchedulingGeneration);
        Assert.Equal(NativeComputeScoringStatus.StaleGeneration,
            gpu.Score(in gpuEnvelope, [process], [row], gpuOutputs, scratch.Pointer, scratch.Length, ref gpuSnapshot));
        gpuEnvelope.SchedulingGeneration = 2;
        Assert.Equal(NativeComputeScoringStatus.Ok,
            gpu.Score(in gpuEnvelope, [process], [row], gpuOutputs, scratch.Pointer, scratch.Length, ref gpuSnapshot));
    }

    [Fact]
    public void ReconfigureKeepsScratchRequirementAndExplicitlyResetsOnlySessionGeneration()
    {
        var configuration = CreateConfiguration();
        using var session = new NativeComputeScoringSession(in configuration, [1]);
        using var scratch = new NativeComputeScoringScratchFixture(session.Capacity);
        var process = CreateProcess(1, 10, 100, 1, 50);
        var outputs = new NativeComputeScoringOutput[4];
        var snapshot = default(NativeComputeScoringSnapshot);
        var envelope = CreateEnvelope(1, 1, 0, 0, 4, NativeComputeScoringEnvelopeFlags.CpuSnapshotComplete);
        Assert.Equal(NativeComputeScoringStatus.Ok, session.Score(in envelope, [process], [], outputs,
            scratch.Pointer, scratch.Length, ref snapshot));
        var previous = session.Capacity;
        configuration.Generation = 2;
        Assert.Equal(NativeComputeScoringStatus.Ok, session.Reconfigure(in configuration, [1]));
        Assert.Equal(previous.ScratchByteCount, session.Capacity.ScratchByteCount);
        Assert.Equal(previous.ScratchAlignment, session.Capacity.ScratchAlignment);
        envelope.ConfigurationGeneration = 2;
        Assert.Equal(NativeComputeScoringStatus.Ok, session.Score(in envelope, [process], [], outputs,
            scratch.Pointer, scratch.Length, ref snapshot));
        Assert.Equal(1UL, snapshot.SchedulingGeneration);
        Assert.Equal(2UL, snapshot.ConfigurationGeneration);
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
