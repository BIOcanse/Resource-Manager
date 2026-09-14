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
        return new SharedMemoryMutex(name, new Mutex(false, name));
    }

    public static SharedMemoryMutex OpenExisting(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new SharedMemoryMutex(name, Mutex.OpenExisting(name));
    }

    public void Dispose() => _mutex.Dispose();
}
