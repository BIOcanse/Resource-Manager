using System.IO.MemoryMappedFiles;

namespace ResourceManager.App.Infrastructure.Security.TrustedAccess;

// All owners map the same private, non-replaced file. Its handles deny deletion,
// so recovery cannot leave an owner reading an orphaned map. The database remains
// authoritative: only a lock-held verified load or commit supplies a revision.
internal sealed unsafe class TrustedAccessStoreRevision : IDisposable
{
    internal const int Length = 32;
    private readonly FileStream file;
    private readonly MemoryMappedFile mapping;
    private readonly MemoryMappedViewAccessor view;
    private byte* address;

    internal TrustedAccessStoreRevision(string path, bool initialize)
    {
        file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        try
        {
            if (file.Length == 0 && initialize) file.SetLength(Length);
            if (file.Length != Length)
                throw new InvalidDataException("The trusted access revision file is invalid.");
            mapping = MemoryMappedFile.CreateFromFile(file, null, Length,
                MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, leaveOpen: true);
            try
            {
                view = mapping.CreateViewAccessor(0, Length, MemoryMappedFileAccess.ReadWrite);
                view.SafeMemoryMappedViewHandle.AcquirePointer(ref address);
                address += view.PointerOffset;
            }
            catch
            {
                view?.Dispose();
                mapping.Dispose();
                throw;
            }
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    internal bool IsCurrent(string revision)
    {
        if (!Guid.TryParseExact(revision, "N", out var expected)) return false;
        // One bounded coherent read. A concurrent writer or an interrupted write
        // makes this false and the caller reconciles under the mutation lock.
        var before = Volatile.Read(ref *(long*)address);
        if (before == 0 || (before & 1) != 0) return false;
        var first = Volatile.Read(ref *(long*)(address + 8));
        var second = Volatile.Read(ref *(long*)(address + 16));
        Thread.MemoryBarrier();
        if (before != Volatile.Read(ref *(long*)address)) return false;
        Span<byte> bytes = stackalloc byte[16];
        expected.TryWriteBytes(bytes);
        return first == System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(bytes)
            && second == System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(bytes[8..]);
    }

    internal void BeginChange()
    {
        var current = Volatile.Read(ref *(long*)address);
        // An odd value is deliberately retained after a crash or failed write.
        Interlocked.Exchange(ref *(long*)address, (current & ~1L) + 1);
    }

    internal void Publish(string revision)
    {
        Span<byte> bytes = stackalloc byte[16];
        Guid.ParseExact(revision, "N").TryWriteBytes(bytes);
        Volatile.Write(ref *(long*)(address + 8), System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(bytes));
        Volatile.Write(ref *(long*)(address + 16), System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(bytes[8..]));
        Interlocked.Exchange(ref *(long*)address, (Volatile.Read(ref *(long*)address) | 1L) + 1);
    }

    public void Dispose()
    {
        if (address == null) return;
        view.SafeMemoryMappedViewHandle.ReleasePointer();
        address = null;
        view.Dispose();
        mapping.Dispose();
        file.Dispose();
    }
}
