using System.Runtime.CompilerServices;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.Optimization;

internal sealed class ComputeScoringScratchLayout
{
    internal int ProcessCapacity { get; }
    internal int GpuCapacity { get; }
    internal int OutputCapacity { get; }
    internal int CoreInputCapacity { get; }
    internal int AdapterCapacity { get; }
    internal ulong Processes { get; }
    internal ulong WeightedCpuUse { get; }
    internal ulong CoreInputs { get; }
    internal ulong Gpus { get; }
    internal ulong Outputs { get; }
    internal ulong AdapterOrder { get; }
    internal ulong NativeScratch { get; }
    internal ulong NativeScratchByteCount { get; }
    internal uint NativeScratchAlignment { get; }
    internal ulong Length { get; }

    internal ComputeScoringScratchLayout(uint processes, uint gpus, uint outputs, int cores,
        ulong nativeScratchByteCount, uint nativeScratchAlignment)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(cores);
        ProcessCapacity = checked((int)processes);
        GpuCapacity = checked((int)gpus);
        OutputCapacity = checked((int)outputs);
        CoreInputCapacity = checked(ProcessCapacity * cores);
        AdapterCapacity = Math.Max(1, GpuCapacity);
        ulong end = 0;
        ulong Column(int width, int count)
        {
            var alignment = (ulong)Math.Min(width, 8);
            var start = checked((end + alignment - 1) / alignment * alignment);
            end = checked(start + (ulong)count * (ulong)width);
            return start;
        }
        Processes = Column(Unsafe.SizeOf<NativeComputeScoringProcessInput>(), ProcessCapacity);
        WeightedCpuUse = Column(8, ProcessCapacity);
        CoreInputs = Column(Unsafe.SizeOf<NativeComputeScoringCpuCoreInput>(), CoreInputCapacity);
        Gpus = Column(Unsafe.SizeOf<NativeComputeScoringGpuInput>(), GpuCapacity);
        Outputs = Column(Unsafe.SizeOf<NativeComputeScoringOutput>(), OutputCapacity);
        AdapterOrder = Column(4, AdapterCapacity);
        if (nativeScratchAlignment == 0 || (nativeScratchAlignment & (nativeScratchAlignment - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(nativeScratchAlignment));
        NativeScratch = end;
        NativeScratchByteCount = nativeScratchByteCount;
        NativeScratchAlignment = nativeScratchAlignment;
        // Allocation base alignment is not assumed. A call-local view aligns its absolute address.
        end = checked(end + nativeScratchAlignment - 1 + nativeScratchByteCount);
        Length = checked((end + 7) / 8 * 8);
    }
}
