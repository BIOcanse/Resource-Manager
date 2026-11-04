using System.Runtime.InteropServices;
using ResourceManager.ReadWrite.Windows.Cpu.Models;
using NativeMethods = ResourceManager.ReadWrite.Windows.Cpu.WindowsCpuTopologyNativeMethods;

namespace ResourceManager.ReadWrite.Windows.Cpu;

public sealed partial class WindowsCpuOsTopologyReader
{
    private const int MaximumTopologyBufferBytes = 4 * 1024 * 1024;
    private const int MaximumTopologyRecords = 65536;
    private const int MaximumProcessorAddresses = 65536;

    internal static void ValidateNativeBufferLength(int allocatedLength, int returnedLength)
    {
        if (allocatedLength < 0 || allocatedLength > MaximumTopologyBufferBytes ||
            returnedLength < 0 || returnedLength > allocatedLength)
            throw new InvalidOperationException("Invalid CPU topology buffer length.");
    }

    private static IReadOnlyList<T> ParseLogicalProcessorBuffer<T>(
        IntPtr buffer, int length, int relationship, Func<LogicalProcessorInfoRecord, T> parse)
    {
        var records = new List<T>();
        var offset = 0;
        while (offset < length)
        {
            if (length - offset < 8 || records.Count >= MaximumTopologyRecords)
                throw new InvalidOperationException("Truncated or oversized CPU topology record list.");
            var pointer = IntPtr.Add(buffer, offset);
            var size = Marshal.ReadInt32(pointer, 4);
            if (size < 8 || size > length - offset || Marshal.ReadInt32(pointer) != relationship)
                throw new InvalidOperationException("Invalid CPU topology relationship header.");
            records.Add(parse(new(relationship, pointer, size)));
            offset += size;
        }
        return records;
    }

    private static IReadOnlyList<CpuSetRecord> ParseCpuSetBuffer(IntPtr buffer, int length)
    {
        var records = new List<CpuSetRecord>();
        var offset = 0;
        var count = 0;
        while (offset < length)
        {
            if (length - offset < 8 || count++ >= MaximumTopologyRecords)
                throw new InvalidOperationException("Truncated or oversized CPU set record list.");
            var pointer = IntPtr.Add(buffer, offset);
            var size = Marshal.ReadInt32(pointer);
            var type = Marshal.ReadInt32(pointer, 4);
            if (size < 8 || size > length - offset)
                throw new InvalidOperationException("Invalid CPU set header.");
            // The API explicitly permits future types, skipped using their Size.
            if (type == NativeMethods.CpuSetInformation)
            {
                if (size < 32) throw new InvalidOperationException("Truncated CPU set information.");
                var record = ParseCpuSetRecord(pointer);
                if (record.LogicalProcessorIndex >= 64)
                    throw new InvalidOperationException("Invalid group-relative CPU set index.");
                records.Add(record);
            }
            offset += size;
        }
        return records;
    }

    private static IReadOnlyList<LogicalProcessorRelationshipRecord> ValidateProcessorRelationships(
        IReadOnlyList<LogicalProcessorRelationshipRecord> records)
    {
        var addresses = new HashSet<int>();
        foreach (var record in records)
            foreach (var id in record.LogicalProcessorIds)
                if (!addresses.Add(id) || addresses.Count > MaximumProcessorAddresses)
                    throw new InvalidOperationException("Overlapping or oversized processor relationships.");
        return records;
    }

    private static IReadOnlyList<CacheRelationshipRecord> ValidateCacheRelationships(IReadOnlyList<CacheRelationshipRecord> records)
    {
        var identities = new HashSet<string>(StringComparer.Ordinal);
        var count = 0;
        foreach (var record in records)
        {
            count += record.LogicalProcessorIds.Count;
            if (count > MaximumProcessorAddresses || !identities.Add(CacheIdentity(record)))
                throw new InvalidOperationException("Repeated or oversized cache relationships.");
        }
        return records;
    }

    private static string CacheIdentity(CacheRelationshipRecord cache)
        => $"windows-cpu-cache|{cache.Level}|{cache.CacheType}|{string.Join(',', cache.LogicalProcessorIds)}";

    internal static CpuCacheInformation ToCacheInformation(CacheRelationshipRecord cache)
        => new(CacheIdentity(cache), cache.Level, unchecked((uint)cache.CacheSizeBytes), cache.LineSizeBytes,
            cache.Associativity, cache.CacheType, cache.LogicalProcessorIds);

    private static void Require64BitLayout()
    {
        if (IntPtr.Size != 8) throw new NotSupportedException("The CPU topology reader requires the 64-bit Windows layout.");
    }

    // Raw-buffer seams exercise the production walkers without querying the test machine.
    public static IReadOnlyList<CpuOsPhysicalCore> ParsePhysicalCoreInformation(byte[] data)
        => WithPinnedTopologyBuffer(data, (pointer, length) => ValidateProcessorRelationships(
            ParseLogicalProcessorBuffer(pointer, length, NativeMethods.RelationProcessorCore, ParseProcessorRelationship))
            .Select((record, index) => new CpuOsPhysicalCore($"core:{index}", (byte)record.EfficiencyClass,
                record.SmtRelationshipReported, record.LogicalProcessorIds)).ToArray());

    public static IReadOnlyList<CpuCacheInformation> ParseCacheInformation(byte[] data)
        => WithPinnedTopologyBuffer(data, (pointer, length) => ValidateCacheRelationships(
            ParseLogicalProcessorBuffer(pointer, length, NativeMethods.RelationCache, ParseCacheRelationship))
            .Select(ToCacheInformation).ToArray());

    public static IReadOnlyList<int[]> ParseProcessorGroupingInformation(byte[] data, int relationship)
        => WithPinnedTopologyBuffer(data, (pointer, length) =>
            ParseLogicalProcessorBuffer(pointer, length, relationship, ParseProcessorRelationship)
                .Select(static record => record.LogicalProcessorIds.ToArray()).ToArray());

    public static IReadOnlyList<CpuSetRecord> ParseCpuSetInformation(byte[] data)
        => WithPinnedTopologyBuffer(data, ParseCpuSetBuffer);

    private static T WithPinnedTopologyBuffer<T>(byte[] data, Func<IntPtr, int, T> parse)
    {
        Require64BitLayout();
        ValidateNativeBufferLength(data.Length, data.Length);
        var pinned = GCHandle.Alloc(data, GCHandleType.Pinned);
        try { return parse(pinned.AddrOfPinnedObject(), data.Length); }
        finally { pinned.Free(); }
    }
}
