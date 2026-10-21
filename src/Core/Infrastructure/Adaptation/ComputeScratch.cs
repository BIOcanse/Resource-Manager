using System.Buffers;

namespace ResourceManager.App.Infrastructure.Adaptation;

// A resource handle owns identity, never a borrowed region. Views exist only during a synchronous compute or operation read.
internal interface IComputeScratch : IDisposable
{
    ulong Length { get; }
    ComputeScratchLease Acquire();
}

internal abstract class ComputeScratchLease : IDisposable
{
    internal abstract ulong Length { get; }
    internal abstract ulong AllocationNumber { get; }
    internal abstract bool IsLoading { get; }
    internal abstract Memory<byte> GetMemoryChunk(ulong offset, int count);
    internal abstract void Complete();
    public abstract void Dispose();
}

internal unsafe ref struct ComputeScratchView
{
    private MemoryHandle pin;
    private readonly byte* pointer;
    private readonly ulong length;

    internal ComputeScratchView(ComputeScratchLease lease)
    {
        length = lease.Length;
        if (length == 0) throw new ArgumentException("Compute scratch must be nonempty.", nameof(lease));
        pin = lease.GetMemoryChunk(0, 1).Pin();
        pointer = (byte*)pin.Pointer;
        if (pointer == null)
        {
            pin.Dispose();
            throw new InvalidOperationException("Compute scratch returned no backing region.");
        }
    }

    internal Span<T> Column<T>(ulong offset, int count) where T : unmanaged
    {
        if (pointer == null) throw new ObjectDisposedException(nameof(ComputeScratchView));
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        var width = (ulong)sizeof(T);
        var bytes = checked((ulong)count * width);
        if (offset > length || bytes > length - offset || offset % Math.Min(width, 8UL) != 0)
            throw new ArgumentOutOfRangeException(nameof(offset));
        // A byte chunk's Int32 length must not restrict a typed column's byte size.
        // The public pin keeps the entire allocation alive; validate the FULL extent.
        var start = checked((nuint)pointer + checked((nuint)offset));
        _ = checked(start + checked((nuint)bytes));
        return new Span<T>((void*)start, count);
    }

    // Return an address only while this pinned call-local view is alive.
    internal IntPtr AlignedRegion(ulong offset, ulong byteCount, uint alignment)
    {
        if (pointer == null) throw new ObjectDisposedException(nameof(ComputeScratchView));
        if (alignment == 0 || (alignment & (alignment - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(alignment));
        if (offset > length) throw new ArgumentOutOfRangeException(nameof(offset));
        var first = checked((nuint)pointer + checked((nuint)offset));
        var mask = checked((nuint)(alignment - 1));
        var address = checked(first + mask) & ~mask;
        var skip = checked((ulong)(address - first));
        if (skip > length - offset || byteCount > length - offset - skip)
            throw new ArgumentOutOfRangeException(nameof(byteCount));
        _ = checked(address + checked((nuint)byteCount));
        return (IntPtr)(void*)address;
    }

    public void Dispose()
    {
        pin.Dispose();
        this = default;
    }
}
