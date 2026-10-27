using System.ComponentModel;
using System.Runtime.InteropServices;
using NativeMethods = ResourceManager.ReadWrite.Windows.Cpu.WindowsCpuTopologyNativeMethods;

namespace ResourceManager.ReadWrite.Windows.Cpu;

public sealed partial class WindowsCpuOsTopologyReader
{
    internal static IReadOnlyList<LogicalProcessorRelationshipRecord> QueryLogicalProcessorRecords(int relationship)
    {
        return ValidateProcessorRelationships(QueryLogicalProcessorInformation(relationship, ParseProcessorRelationship));
    }

    internal static IReadOnlyList<CacheRelationshipRecord> QueryCacheRecords()
    {
        return ValidateCacheRelationships(QueryLogicalProcessorInformation(NativeMethods.RelationCache, ParseCacheRelationship));
    }

    // GetSystemCpuSetInformation reports current OS CPU-set state, not the fixed processor specification.
    [RuntimeRead(ReadVolatilityKind.Sampled)]
    internal static IReadOnlyList<CpuSetRecord> QueryCpuSetRecords()
    {
        Require64BitLayout();
        if (!NativeMethods.GetSystemCpuSetInformation(IntPtr.Zero, 0, out var returnedLength, IntPtr.Zero, 0))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != NativeMethods.ErrorInsufficientBuffer || returnedLength <= 0)
            {
                throw new Win32Exception(error);
            }
        }

        ValidateNativeBufferLength(returnedLength, returnedLength);
        if (returnedLength == 0) return [];
        var allocatedLength = returnedLength;
        var buffer = Marshal.AllocHGlobal(allocatedLength);
        try
        {
            if (!NativeMethods.GetSystemCpuSetInformation(buffer, allocatedLength, out returnedLength, IntPtr.Zero, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            ValidateNativeBufferLength(allocatedLength, returnedLength);
            return ParseCpuSetBuffer(buffer, returnedLength);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [RuntimeRead(ReadVolatilityKind.EventDriven, "Windows processor topology changed")]
    private static IReadOnlyList<T> QueryLogicalProcessorInformation<T>(
        int relationship,
        Func<LogicalProcessorInfoRecord, T> parse,
        int? responseRelationship = null)
    {
        Require64BitLayout();
        var returnedLength = 0;
        if (!NativeMethods.GetLogicalProcessorInformationEx(relationship, IntPtr.Zero, ref returnedLength))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != NativeMethods.ErrorInsufficientBuffer || returnedLength <= 0)
            {
                throw new Win32Exception(error);
            }
        }

        ValidateNativeBufferLength(returnedLength, returnedLength);
        if (returnedLength == 0) return [];
        var allocatedLength = returnedLength;
        var buffer = Marshal.AllocHGlobal(allocatedLength);
        try
        {
            if (!NativeMethods.GetLogicalProcessorInformationEx(relationship, buffer, ref returnedLength))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            ValidateNativeBufferLength(allocatedLength, returnedLength);
            return ParseLogicalProcessorBuffer(buffer, returnedLength, responseRelationship ?? relationship, parse);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static LogicalProcessorRelationshipRecord ParseProcessorRelationship(LogicalProcessorInfoRecord record)
    {
        if (record.Size < 32) throw new InvalidOperationException("Truncated processor relationship.");
        var efficiencyClass = Marshal.ReadByte(record.Pointer, 9);
        var groupCount = unchecked((ushort)Marshal.ReadInt16(record.Pointer, 30));
        // A core belongs to one group; package/die queries may span several groups.
        if (groupCount == 0 ||
            (record.Relationship == NativeMethods.RelationProcessorCore && groupCount != 1) ||
            record.Size < 32 + groupCount * 16)
            throw new InvalidOperationException("Invalid processor group affinity.");
        var logicalProcessors = ReadGroupAffinities(record.Pointer, 32, groupCount);
        return new LogicalProcessorRelationshipRecord(efficiencyClass, logicalProcessors)
        {
            SmtRelationshipReported = (Marshal.ReadByte(record.Pointer, 8) & 1) != 0
        };
    }

    private static CacheRelationshipRecord ParseCacheRelationship(LogicalProcessorInfoRecord record)
    {
        if (record.Size < 40) throw new InvalidOperationException("Truncated cache relationship.");
        var level = Marshal.ReadByte(record.Pointer, 8);
        var cacheSize = Marshal.ReadInt32(record.Pointer, 12);
        var groupCount = unchecked((ushort)Marshal.ReadInt16(record.Pointer, 38));
        // Older Windows stores one GroupMask with the same offset and a reserved zero GroupCount.
        if (groupCount == 0 && record.Size == 56) groupCount = 1;
        if (groupCount == 0 || record.Size < 40 + groupCount * 16)
            throw new InvalidOperationException("Truncated cache group masks.");
        var logicalProcessors = ReadGroupAffinities(record.Pointer, 40, groupCount);
        return new CacheRelationshipRecord(level, cacheSize, logicalProcessors)
        {
            Associativity = Marshal.ReadByte(record.Pointer, 9),
            LineSizeBytes = unchecked((ushort)Marshal.ReadInt16(record.Pointer, 10)),
            CacheType = Marshal.ReadInt32(record.Pointer, 16)
        };
    }

    private static CpuSetRecord ParseCpuSetRecord(IntPtr pointer)
    {
        return new CpuSetRecord(
            unchecked((uint)Marshal.ReadInt32(pointer, 8)),
            unchecked((ushort)Marshal.ReadInt16(pointer, 12)),
            Marshal.ReadByte(pointer, 14),
            Marshal.ReadByte(pointer, 15),
            Marshal.ReadByte(pointer, 16),
            Marshal.ReadByte(pointer, 17),
            Marshal.ReadByte(pointer, 18),
            Marshal.ReadByte(pointer, 20));
    }

    private static IReadOnlyList<int> ReadGroupAffinities(
        IntPtr pointer,
        int offset,
        int groupCount,
        bool allowEmptyMask = false)
    {
        var result = new List<int>();
        var groups = new HashSet<ushort>();
        for (var groupIndex = 0; groupIndex < groupCount; groupIndex++)
        {
            var groupOffset = offset + groupIndex * 16;
            var mask = unchecked((ulong)Marshal.ReadInt64(pointer, groupOffset));
            var group = unchecked((ushort)Marshal.ReadInt16(pointer, groupOffset + 8));
            if ((!allowEmptyMask && mask == 0) || !groups.Add(group))
                throw new InvalidOperationException("Empty or repeated CPU group affinity.");
            for (var bit = 0; bit < 64; bit++)
            {
                if ((mask & (1UL << bit)) != 0)
                {
                    if (result.Count >= MaximumProcessorAddresses)
                        throw new InvalidOperationException("Oversized CPU affinity list.");
                    result.Add(group * 64 + bit);
                }
            }
        }

        return result.Order().ToArray();
    }
}
