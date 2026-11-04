using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
using Microsoft.Win32.SafeHandles;

namespace ResourceManager.App.Infrastructure.MemoryProtection;

/// <summary>
/// DbgHelp captures native module/thread/context metadata first. The frozen private layout then
/// supplies a standard Memory64ListStream, whose complete header precedes its sequential payload.
/// Each page-aligned payload chunk is written before decommit is attempted. OS rejection is reported
/// explicitly while the remaining payload is preserved and other private allocations are released.
/// </summary>
internal static class HostMemoryMinidumpWriter
{
    // All in-process DbgHelp callers must share this gate; DbgHelp is single threaded.
    internal static readonly object DbgHelpGate = new();
    private const uint MemoryListStream = 5;
    private const uint Memory64ListStream = 9;
    private const int ChunkSize = 1024 * 1024;
    // Windows maps this OS-owned, read-only shared page into every process. Its synthetic
    // VirtualQuery/QueryVirtualMemoryInformation descriptors nevertheless say Private/Commit.
    // https://www.microsoft.com/msrc/blog/2022/04/randomizing-the-kuser_shared_data-structure-on-windows
    internal const nuint SharedUserDataAddress = 0x7ffe0000;

    internal static List<PrivateMemoryRegion> ReadLayout(SafeProcessHandle process)
    {
        List<PrivateMemoryRegion> regions = [];
        nuint address = 0;
        for (var count = 0; count < 1_000_000; count++)
        {
            if (HostMemoryProtectionNative.VirtualQueryEx(process, address, out var info,
                    (nuint)Marshal.SizeOf<HostMemoryProtectionNative.MemoryBasicInformation>()) == 0)
            {
                var error = Marshal.GetLastWin32Error();
                if (error == 87) return regions; // End of the process address space.
                throw new Win32Exception(error, "private-memory-layout-failed");
            }

            if (info.RegionSize == 0 || info.BaseAddress < address)
                throw new InvalidDataException("invalid-private-memory-layout");
            if (info.State == HostMemoryProtectionNative.MemCommit && info.Type == HostMemoryProtectionNative.MemPrivate)
                regions.Add(new(info.BaseAddress, info.RegionSize, info.Protect,
                    info.BaseAddress == SharedUserDataAddress && info.RegionSize == 4096 && info.Protect == 0x02));
            var next = checked(info.BaseAddress + info.RegionSize);
            if (next <= address) throw new InvalidDataException("invalid-private-memory-layout");
            address = next;
        }
        throw new InvalidDataException("private-memory-layout-limit");
    }

    internal static ulong Measure(SafeProcessHandle process, int processId, IReadOnlyList<PrivateMemoryRegion> regions)
    {
        // Preflight never creates a file or retains process stack payload. DbgHelp's random writes
        // update only a byte count and a bounded header/directory prefix. A format outside that
        // explicit bound fails before the caller suspends or terminates the process.
        using var metadata = new MetadataSizingStream();
        WriteMetadata(process, processId, metadata);
        var directory = ReadDirectory(metadata);
        return CalculateLayout((ulong)metadata.Length, directory.Count, regions).TotalBytes;
    }

    internal static string? Write(SafeProcessHandle process, int processId, Stream file,
        IReadOnlyList<PrivateMemoryRegion> regions, Action<SafeProcessHandle, ulong, ulong>? chunkDecommitted,
        Action<nuint, nuint, int>? decommitFailed)
    {
        WriteMetadata(process, processId, file);
        AppendMemoryLayout(file, regions);
        var buffer = new byte[ChunkSize];
        ulong released = 0;
        ulong unreleasedBytes = 0;
        var unreleasedChunks = 0;
        List<string> releaseLimits = [];
        foreach (var region in regions)
        {
            // Guard and no-access pages are still committed private memory. The frozen process will
            // be terminated, so making those pages readable does not need a later protection restore.
            if ((region.Protection & 0x100) != 0 || (region.Protection & 0xff) is 0 or 1 or 0x10)
            {
                if (!HostMemoryProtectionNative.VirtualProtectEx(process, region.Address, region.Size, 0x04, out _))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "private-memory-protection-failed");
            }
            nuint offset = 0;
            while (offset < region.Size)
            {
                var length = (int)nuint.Min((nuint)buffer.Length, region.Size - offset);
                var address = checked(region.Address + offset);
                if (!HostMemoryProtectionNative.ReadProcessMemory(process, address, buffer, (nuint)length, out var read)
                    || read != (nuint)length)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "private-memory-read-failed");

                file.Write(buffer, 0, length);
                // The reserve stream funds and writes the chunk before its source is discarded.
                // SharedUserData is captured for the debugger, but cannot be decommitted by a process.
                if (region.SharedSystemMemory)
                {
                    offset += (nuint)length;
                    continue;
                }
                if (!HostMemoryProtectionNative.VirtualFreeEx(process, address, (nuint)length, HostMemoryProtectionNative.MemDecommit))
                {
                    var error = Marshal.GetLastWin32Error();
                    // Some OS-managed private allocations forbid decommit. Preserve every byte and
                    // keep releasing other allocations; report the unreleased memory explicitly.
                    // A failed decommit is never a successful release and never omits slice payload.
                    unreleasedBytes += (ulong)length;
                    unreleasedChunks++;
                    if (releaseLimits.Count < 8)
                    {
                        var details = HostMemoryProtectionNative.QueryVirtualMemoryInformation(process, address, 0,
                            out var allocation, (nuint)Marshal.SizeOf<HostMemoryProtectionNative.MemoryRegionInformation>(), out _)
                            ? $"allocation=0x{allocation.AllocationBase:x}+0x{allocation.RegionSize:x} flags=0x{allocation.Flags:x} commit=0x{allocation.CommitSize:x}"
                            : $"allocation-query-failed-{Marshal.GetLastWin32Error()}";
                        releaseLimits.Add($"address=0x{address:x} bytes=0x{length:x} protect=0x{region.Protection:x} native-error={error} {details}");
                    }
                    decommitFailed?.Invoke(address, (nuint)length, error);
                }
                else
                {
                    released += (ulong)length;
                    chunkDecommitted?.Invoke(process, released, (ulong)file.Length);
                }
                offset += (nuint)length;
            }
        }
        file.Flush();
        return unreleasedChunks == 0 ? null
            : $"private-memory-decommit-limited chunks={unreleasedChunks} bytes={unreleasedBytes}; {string.Join("; ", releaseLimits)}";
    }

    private static unsafe void WriteMetadata(SafeProcessHandle process, int processId, Stream file)
    {
        Exception? callbackFailure = null;
        var alternateIoStarted = false;
        HostMemoryProtectionNative.MiniDumpCallbackRoutine callback = (_, inputPointer, outputPointer) =>
        {
            try
            {
                var input = Marshal.PtrToStructure<HostMemoryProtectionNative.MiniDumpCallbackInput>(inputPointer);
                switch (input.CallbackType)
                {
                    case 11: // IoStartCallback: S_FALSE requests callback I/O for every native write.
                        alternateIoStarted = true;
                        Marshal.WriteInt32(outputPointer, 1);
                        return true;
                    case 12: // IoWriteAllCallback: preserve native random offsets through the funded stream.
                        if (callbackFailure is not null) return false;
                        file.Position = checked((long)input.Io.Offset);
                        file.Write(new ReadOnlySpan<byte>((void*)input.Io.Buffer, checked((int)input.Io.BufferBytes)));
                        Marshal.WriteInt32(outputPointer, 0); // S_OK, only after all bytes have been written.
                        return true;
                    case 13: // IoFinishCallback.
                        if (callbackFailure is not null) return false;
                        file.Flush();
                        Marshal.WriteInt32(outputPointer, 0);
                        return true;
                    case 5: // No extra memory ranges beyond DbgHelp's standard thread stacks.
                    case 9:
                        return false;
                    default:
                        return true; // Preserve DbgHelp's prepopulated module/thread write flags.
                }
            }
            catch (Exception error)
            {
                // Managed exceptions must never unwind through DbgHelp's unmanaged stack.
                callbackFailure ??= error;
                Marshal.WriteInt32(outputPointer, unchecked((int)0x80004005));
                return false;
            }
        };
        var information = new HostMemoryProtectionNative.MiniDumpCallbackInformation { Routine = callback };
        bool succeeded;
        int nativeError;
        lock (DbgHelpGate)
        {
            // A null native file handle deliberately prevents any unfunded native-file fallback.
            // Normal dumps retain standard modules, threads, native contexts and original stack RVAs.
            succeeded = HostMemoryProtectionNative.MiniDumpWriteDump(process, processId, 0, 0, 0, 0, ref information);
            nativeError = Marshal.GetLastWin32Error();
        }
        GC.KeepAlive(callback);
        if (callbackFailure is not null) ExceptionDispatchInfo.Capture(callbackFailure).Throw();
        if (!succeeded) throw new Win32Exception(nativeError, "minidump-metadata-failed");
        if (!alternateIoStarted) throw new IOException("minidump-callback-io-unavailable");
    }

    private static List<(uint Type, uint Size, uint Rva)> ReadDirectory(Stream file)
    {
        using var reader = new BinaryReader(file, System.Text.Encoding.UTF8, leaveOpen: true);
        file.Position = 0;
        if (reader.ReadUInt32() != 0x504d444d) throw new InvalidDataException("invalid-minidump-header");
        file.Position = 8;
        var streamCount = reader.ReadUInt32();
        var oldDirectory = reader.ReadUInt32();
        if (streamCount > 1024) throw new InvalidDataException("invalid-minidump-stream-count");
        if ((ulong)oldDirectory + (ulong)streamCount * 12 > (ulong)file.Length)
            throw new InvalidDataException("invalid-minidump-directory");
        file.Position = oldDirectory;
        List<(uint Type, uint Size, uint Rva)> directory = [];
        for (var index = 0; index < streamCount; index++)
        {
            var entry = (Type: reader.ReadUInt32(), Size: reader.ReadUInt32(), Rva: reader.ReadUInt32());
            if (entry.Type is not MemoryListStream and not Memory64ListStream) directory.Add(entry);
        }
        return directory;
    }

    private static MemoryLayout CalculateLayout(ulong metadataBytes, int directoryCount, IReadOnlyList<PrivateMemoryRegion> regions)
    {
        var directoryRva = checked((uint)((metadataBytes + 7) & ~7UL));
        var memoryRva = checked(directoryRva + (uint)(directoryCount + 1) * 12);
        var memoryHeaderSize = checked(16u + (uint)regions.Count * 16u);
        var payloadRva = checked((ulong)memoryRva + memoryHeaderSize);
        var totalBytes = payloadRva;
        foreach (var region in regions) totalBytes = checked(totalBytes + (ulong)region.Size);
        return new(directoryRva, memoryRva, memoryHeaderSize, payloadRva, totalBytes);
    }

    private static void AppendMemoryLayout(Stream file, IReadOnlyList<PrivateMemoryRegion> regions)
    {
        var directory = ReadDirectory(file);
        var layout = CalculateLayout((ulong)file.Length, directory.Count, regions);

        // The old stack data stays in place. Replace only its top-level memory list with the complete
        // private-memory list; every other metadata RVA remains valid, including thread stack RVAs.
        file.Position = file.Length;
        while ((file.Position & 7) != 0) file.WriteByte(0);
        using var writer = new BinaryWriter(file, System.Text.Encoding.UTF8, leaveOpen: true);
        foreach (var entry in directory)
        {
            writer.Write(entry.Type);
            writer.Write(entry.Size);
            writer.Write(entry.Rva);
        }
        writer.Write(Memory64ListStream);
        writer.Write(layout.MemoryHeaderSize);
        writer.Write(layout.MemoryRva);
        writer.Write((ulong)regions.Count);
        writer.Write(layout.PayloadRva);
        foreach (var region in regions)
        {
            writer.Write((ulong)region.Address);
            writer.Write((ulong)region.Size);
        }
        writer.Flush();
        file.Position = 8;
        writer.Write((uint)directory.Count + 1);
        writer.Write(layout.DirectoryRva);
        file.Position = checked((long)layout.PayloadRva);
        writer.Flush();
    }

    private readonly record struct MemoryLayout(uint DirectoryRva, uint MemoryRva, uint MemoryHeaderSize, ulong PayloadRva, ulong TotalBytes);

    private sealed class MetadataSizingStream : Stream
    {
        private readonly byte[] prefix = new byte[64 * 1024];
        private long position;
        private long length;
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => true;
        public override long Length => length;
        public override long Position
        {
            get => position;
            set => position = value >= 0 ? value : throw new IOException("invalid-minidump-offset");
        }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            var available = (int)Math.Min(buffer.Length, Math.Max(0, length - position));
            if (position + available > prefix.Length) throw new IOException("minidump-preflight-directory-exceeds-64kib-prefix");
            prefix.AsSpan((int)position, available).CopyTo(buffer);
            position += available;
            return available;
        }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            var end = checked(position + buffer.Length);
            if (position < prefix.Length)
            {
                var kept = (int)Math.Min(buffer.Length, prefix.Length - position);
                buffer[..kept].CopyTo(prefix.AsSpan((int)position, kept));
            }
            position = end;
            length = Math.Max(length, end);
        }
        public override long Seek(long offset, SeekOrigin origin)
        {
            Position = checked((origin switch { SeekOrigin.Begin => 0, SeekOrigin.Current => position, SeekOrigin.End => length,
                _ => throw new ArgumentOutOfRangeException(nameof(origin)) }) + offset);
            return position;
        }
        public override void SetLength(long value)
            => throw new NotSupportedException("Preflight uses only DbgHelp callback writes.");
    }

    internal readonly record struct PrivateMemoryRegion(nuint Address, nuint Size, uint Protection, bool SharedSystemMemory = false);
}
