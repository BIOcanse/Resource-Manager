using System.IO.MemoryMappedFiles;
using ResourceManager.Adapter.SharedMemory;

namespace ResourceManager.Adapter.SharedMemory.Tests;

public sealed class SharedMemoryObjectLifetimeTests
{
    private static string NewName() => @"Local\ResourceManager.SharedObject.Tests." + Guid.NewGuid().ToString("N");

    [Fact]
    public void FailedWritableViewClosesTheAcquiredReadOnlyMapping()
    {
        var name = NewName();
        using var owner = MemoryMappedFile.CreateNew(name, 4096, MemoryMappedFileAccess.ReadWrite);
        using var reader = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.Read);
        var handle = reader.SafeMemoryMappedFileHandle;

        Assert.Throws<UnauthorizedAccessException>(() => SharedMemoryRegion.CreateView(
            name, 4096, reader, writable: true));

        Assert.True(handle.IsClosed);
        Assert.False(owner.SafeMemoryMappedFileHandle.IsClosed);
        using var stillReadable = SharedMemoryRegion.OpenExisting(name, 4096, writable: false);
        Assert.False(stillReadable.Writable);
    }

    [Fact]
    public void SuccessfulViewOwnsTheMappingUntilDisposed()
    {
        var name = NewName();
        using var mapping = MemoryMappedFile.CreateNew(name, 4096, MemoryMappedFileAccess.ReadWrite);
        var handle = mapping.SafeMemoryMappedFileHandle;
        var region = SharedMemoryRegion.CreateView(name, 4096, mapping, writable: true);
        try
        {
            Assert.False(handle.IsClosed);
            Assert.True(region.Writable);
            Assert.Equal(4096L, region.Capacity);
            using var reader = SharedMemoryRegion.OpenExisting(name, 4096, writable: false);
            Assert.False(reader.Writable);
        }
        finally
        {
            region.Dispose();
        }
        Assert.True(handle.IsClosed);
        region.Dispose();
        Assert.Throws<FileNotFoundException>(() => MemoryMappedFile.OpenExisting(name));
    }

    [Fact]
    public void CreateMutexRejectsCollisionWithoutClosingTheOriginalOwner()
    {
        var name = NewName();
        using var owner = SharedMemoryMutex.CreateNew(name);
        Assert.Throws<IOException>(() => SharedMemoryMutex.CreateNew(name));
        using var client = SharedMemoryMutex.OpenExisting(name);
        using var observer = Mutex.OpenExisting(name);
        Assert.True(observer.WaitOne(TimeSpan.Zero));
        observer.ReleaseMutex();
        Assert.NotEqual(IntPtr.Zero, owner.Handle);
        Assert.NotEqual(IntPtr.Zero, client.Handle);
    }
}
