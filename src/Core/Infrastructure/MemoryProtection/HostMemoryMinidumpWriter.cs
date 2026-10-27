using System.ComponentModel;
using System.Runtime.InteropServices;
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

    internal static string? Write(SafeProcessHandle process, int processId, FileStream file,
        IReadOnlyList<PrivateMemoryRegion> regions, Action<SafeProcessHandle, ulong, ulong>? chunkDecommitted,
        Action<nuint, nuint, int>? decommitFailed)
    {
        lock (DbgHelpGate)
        {
            // Normal dumps contain stack memory plus modules, threads and their native contexts.
            // Keep those stack copies: the thread descriptors continue to reference their original RVAs.
            if (!HostMemoryProtectionNative.MiniDumpWriteDump(process, processId, file.SafeFileHandle, 0, 0, 0, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "minidump-metadata-failed");
        }
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
                // Unbuffered FileStream writes have reached the OS before the source is discarded.
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

    private static void AppendMemoryLayout(FileStream file, IReadOnlyList<PrivateMemoryRegion> regions)
    {
        using var reader = new BinaryReader(file, System.Text.Encoding.UTF8, leaveOpen: true);
        file.Position = 0;
        if (reader.ReadUInt32() != 0x504d444d) throw new InvalidDataException("invalid-minidump-header");
        file.Position = 8;
        var streamCount = reader.ReadUInt32();
        var oldDirectory = reader.ReadUInt32();
        if (streamCount > 1024) throw new InvalidDataException("invalid-minidump-stream-count");
        file.Position = oldDirectory;
        List<(uint Type, uint Size, uint Rva)> directory = [];
        for (var index = 0; index < streamCount; index++)
        {
            var entry = (Type: reader.ReadUInt32(), Size: reader.ReadUInt32(), Rva: reader.ReadUInt32());
            if (entry.Type is not MemoryListStream and not Memory64ListStream) directory.Add(entry);
        }

        // The old stack data stays in place. Replace only its top-level memory list with the complete
        // private-memory list; every other metadata RVA remains valid, including thread stack RVAs.
        file.Position = file.Length;
        while ((file.Position & 7) != 0) file.WriteByte(0);
        using var writer = new BinaryWriter(file, System.Text.Encoding.UTF8, leaveOpen: true);
        var directoryRva = checked((uint)file.Position);
        var memoryRva = checked(directoryRva + (uint)(directory.Count + 1) * 12);
        var memoryHeaderSize = checked(16u + (uint)regions.Count * 16u);
        var payloadRva = checked((ulong)memoryRva + memoryHeaderSize);
        foreach (var entry in directory)
        {
            writer.Write(entry.Type);
            writer.Write(entry.Size);
            writer.Write(entry.Rva);
        }
        writer.Write(Memory64ListStream);
        writer.Write(memoryHeaderSize);
        writer.Write(memoryRva);
        writer.Write((ulong)regions.Count);
        writer.Write(payloadRva);
        foreach (var region in regions)
        {
            writer.Write((ulong)region.Address);
            writer.Write((ulong)region.Size);
        }
        writer.Flush();
        file.Position = 8;
        writer.Write((uint)directory.Count + 1);
        writer.Write(directoryRva);
        file.Position = checked((long)payloadRva);
        writer.Flush();
    }

    internal readonly record struct PrivateMemoryRegion(nuint Address, nuint Size, uint Protection, bool SharedSystemMemory = false);
}
