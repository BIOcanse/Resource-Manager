using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ResourceManager.App.Infrastructure.ResourceBreakdown;

internal readonly record struct PhysicalMemoryPoolTag(uint Tag, ulong NonPagedBytes);
internal sealed record PhysicalMemoryCounters(
    ulong? NonPagedPoolBytes, ulong? ResidentPagedPoolBytes,
    ulong? ResidentDriverBytes, ulong? ResidentCodeBytes,
    // Diagnostic-only readings: never sum these with the resident components.
    ulong? PagedPoolBytes = null, ulong? ResidentCacheBytes = null);
internal sealed record PhysicalMemoryResidualCapture(
    PhysicalMemoryCounters? Counters, IReadOnlyList<PhysicalMemoryPoolTag>? Tags,
    ulong OtherTagBytes, string State);

/// <summary>One synchronous capture window, owned by the existing demanded resource sampler.</summary>
internal sealed class WindowsPhysicalMemoryResidualReader
{
    internal const int TopTagCount = 8;
    internal const int MaximumTagBufferBytes = 4 * 1024 * 1024;

    internal unsafe PhysicalMemoryResidualCapture Capture(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
            return new(null, null, 0, "unsupported-platform");
        Span<byte> performance = stackalloc byte[512];
        int status;
        uint returned;
        fixed (byte* data = performance)
            status = NtQuerySystemInformation(2, data, (uint)performance.Length, out returned);
        if (status < 0 || returned < SystemPerformanceLayout.RequiredBytes)
            return new(null, null, 0, $"performance:0x{(uint)status:X8}:{returned}");
        var counters = DecodePerformance(performance);
        var tags = ReadTags(cancellationToken);
        return new(counters, tags.Tags, tags.OtherBytes, tags.State);
    }

    // SYSTEM_PERFORMANCE_INFORMATION is fixed-width on x86/x64. phnt ntexapi.h.
    internal static PhysicalMemoryCounters DecodePerformance(ReadOnlySpan<byte> data)
    {
        if (data.Length < SystemPerformanceLayout.RequiredBytes) throw new ArgumentException("Truncated performance information.", nameof(data));
        var layout = MemoryMarshal.Read<SystemPerformanceLayout>(data);
        return new(Bytes(layout.NonPagedPoolPages), Bytes(layout.ResidentPagedPoolPage),
            Bytes(layout.ResidentSystemDriverPage), Bytes(layout.ResidentSystemCodePage),
            Bytes(layout.PagedPoolPages), Bytes(layout.ResidentSystemCachePage));
    }
    private static ulong Bytes(uint pages) => pages * (ulong)Environment.SystemPageSize;
    internal static unsafe (IReadOnlyList<PhysicalMemoryPoolTag>? Tags, ulong OtherBytes, string State)
        ReadTags(CancellationToken cancellationToken)
    {
        var size = 64 * 1024;
        // The query grows only on an explicit required-length response, with a hard byte/attempt bound.
        for (var attempt = 0; attempt < 4; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var data = (byte*)NativeMemory.Alloc((nuint)size);
            try
            {
                var status = NtQuerySystemInformation(22, data, (uint)size, out var returned);
                if (status is unchecked((int)0xC0000004) or unchecked((int)0xC0000023))
                {
                    if (returned <= size || returned > MaximumTagBufferBytes)
                        return (null, 0, "pool-tags:buffer-limit");
                    size = checked((int)returned);
                    continue;
                }
                if (status < 0) return (null, 0, $"pool-tags:0x{(uint)status:X8}");
                if (returned < Unsafe.SizeOf<PoolTagHeader>() || returned > size) return (null, 0, "pool-tags:invalid-length");
                return DecodeTags(new ReadOnlySpan<byte>(data, (int)returned));
            }
            finally { NativeMemory.Free(data); }
        }
        return (null, 0, "pool-tags:attempt-limit");
    }

    internal static (IReadOnlyList<PhysicalMemoryPoolTag>? Tags, ulong OtherBytes, string State)
        DecodeTags(ReadOnlySpan<byte> data)
    {
        if (data.Length < Unsafe.SizeOf<PoolTagHeader>()) return (null, 0, "pool-tags:invalid-length");
        var count = MemoryMarshal.Read<PoolTagHeader>(data).Count;
        if (count > (data.Length - Unsafe.SizeOf<PoolTagHeader>()) / Unsafe.SizeOf<PoolTagLayout>()) return (null, 0, "pool-tags:invalid-count");
        var top = new List<PhysicalMemoryPoolTag>(TopTagCount + 1);
        ulong other = 0;
        for (var index = 0; index < count; index++)
        {
            var row = MemoryMarshal.Read<PoolTagLayout>(data[(Unsafe.SizeOf<PoolTagHeader>() + index * Unsafe.SizeOf<PoolTagLayout>())..]);
            var bytes = row.NonPagedUsed;
            if (bytes == 0) continue;
            var tag = new PhysicalMemoryPoolTag(row.Tag, bytes);
            var insert = 0;
            while (insert < top.Count && (top[insert].NonPagedBytes > bytes
                || top[insert].NonPagedBytes == bytes && top[insert].Tag < tag.Tag)) insert++;
            top.Insert(insert, tag);
            if (top.Count > TopTagCount)
            {
                if (ulong.MaxValue - other < top[^1].NonPagedBytes) return (null, 0, "pool-tags:overflow");
                other += top[^1].NonPagedBytes;
                top.RemoveAt(top.Count - 1);
            }
        }
        return (top.ToArray(), other, "ready");
    }

    [DllImport("ntdll.dll")]
    private static extern unsafe int NtQuerySystemInformation(int informationClass, void* buffer,
        uint length, out uint returnLength);
}
