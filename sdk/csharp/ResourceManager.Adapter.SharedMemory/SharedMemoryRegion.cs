using System.IO.MemoryMappedFiles;

namespace ResourceManager.Adapter.SharedMemory;

internal sealed unsafe class SharedMemoryRegion : IDisposable
{
    private readonly MemoryMappedFile _mapping;
    private readonly MemoryMappedViewAccessor _view;
    private byte* _pointer;
    private bool _disposed;

    private SharedMemoryRegion(
        string mappingName,
        long capacity,
        MemoryMappedFile mapping,
        MemoryMappedViewAccessor view,
        bool writable)
    {
        if (!BitConverter.IsLittleEndian)
        {
            view.Dispose();
            mapping.Dispose();
            throw new PlatformNotSupportedException(
                "The shared-resource ABI requires a little-endian host.");
        }

        MappingName = mappingName;
        Capacity = capacity;
        Writable = writable;
        _mapping = mapping;
        _view = view;
        _view.SafeMemoryMappedViewHandle.AcquirePointer(ref _pointer);
        _pointer += _view.PointerOffset;
    }

    public string MappingName { get; }

    public long Capacity { get; }

    public bool Writable { get; }

    public void* Pointer
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _pointer;
        }
    }

    public static SharedMemoryRegion CreateNew(string mappingName, long capacity)
    {
        ValidateArguments(mappingName, capacity);
        var mapping = MemoryMappedFile.CreateNew(
            mappingName,
            capacity,
            MemoryMappedFileAccess.ReadWrite);
        var view = mapping.CreateViewAccessor(0, capacity, MemoryMappedFileAccess.ReadWrite);
        return new SharedMemoryRegion(mappingName, capacity, mapping, view, writable: true);
    }

    public static SharedMemoryRegion OpenExisting(
        string mappingName,
        long capacity,
        bool writable)
    {
        ValidateArguments(mappingName, capacity);
        var mapping = MemoryMappedFile.OpenExisting(
            mappingName,
            writable ? MemoryMappedFileRights.ReadWrite : MemoryMappedFileRights.Read);
        var access = writable ? MemoryMappedFileAccess.ReadWrite : MemoryMappedFileAccess.Read;
        var view = mapping.CreateViewAccessor(0, capacity, access);
        return new SharedMemoryRegion(mappingName, capacity, mapping, view, writable);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_pointer is not null)
        {
            _pointer -= _view.PointerOffset;
            _view.SafeMemoryMappedViewHandle.ReleasePointer();
            _pointer = null;
        }

        _view.Dispose();
        _mapping.Dispose();
    }

    private static void ValidateArguments(string mappingName, long capacity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mappingName);
        if (capacity <= 0 || capacity > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }
    }
}
