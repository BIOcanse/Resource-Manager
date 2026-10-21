using System.Runtime.InteropServices;

namespace ResourceManager.Ledger.Interop;

internal sealed unsafe class AlignedNativeBuffer : IDisposable
{
    private void* _pointer;
    private readonly nuint _allocationSize;

    private AlignedNativeBuffer(void* pointer, nuint allocationSize, ulong declaredSize)
    {
        _pointer = pointer;
        _allocationSize = allocationSize;
        DeclaredSize = declaredSize;
    }

    internal void* Pointer => _pointer;
    internal ulong DeclaredSize { get; }

    internal static AlignedNativeBuffer Allocate(ulong size, ulong alignment, bool clear)
    {
        if (alignment == 0 || alignment > (ulong)nuint.MaxValue || (alignment & (alignment - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(alignment));
        if (size > (ulong)nuint.MaxValue) throw new ArgumentOutOfRangeException(nameof(size));
        if (size == 0) return new AlignedNativeBuffer(null, 0, 0);

        nuint nativeAlignment = checked((nuint)alignment);
        nuint declaredSize = checked((nuint)size);
        nuint remainder = declaredSize % nativeAlignment;
        nuint allocationSize = remainder == 0 ? declaredSize : checked(declaredSize + nativeAlignment - remainder);
        void* pointer = NativeMemory.AlignedAlloc(allocationSize, nativeAlignment);
        if (pointer == null) throw new OutOfMemoryException($"Could not allocate {size} aligned Native bytes.");
        if (clear) NativeMemory.Clear(pointer, allocationSize);
        return new AlignedNativeBuffer(pointer, allocationSize, size);
    }

    public void Dispose()
    {
        void* pointer = _pointer;
        _pointer = null;
        if (pointer != null) NativeMemory.AlignedFree(pointer);
        GC.KeepAlive(_allocationSize);
    }
}
