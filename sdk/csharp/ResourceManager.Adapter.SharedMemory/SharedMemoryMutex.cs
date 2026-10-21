namespace ResourceManager.Adapter.SharedMemory;

internal sealed class SharedMemoryMutex : IDisposable
{
    private readonly Mutex _mutex;

    private SharedMemoryMutex(string name, Mutex mutex)
    {
        Name = name;
        _mutex = mutex;
    }

    public string Name { get; }

    public IntPtr Handle => _mutex.SafeWaitHandle.DangerousGetHandle();

    public static SharedMemoryMutex CreateNew(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var mutex = new Mutex(false, name, out var createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            throw new IOException("The shared-resource mutex name is already in use.");
        }
        return TakeOwnership(name, mutex);
    }

    public static SharedMemoryMutex OpenExisting(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return TakeOwnership(name, Mutex.OpenExisting(name));
    }

    private static SharedMemoryMutex TakeOwnership(string name, Mutex mutex)
    {
        try { return new SharedMemoryMutex(name, mutex); }
        catch
        {
            mutex.Dispose();
            throw;
        }
    }

    public void Dispose() => _mutex.Dispose();
}
