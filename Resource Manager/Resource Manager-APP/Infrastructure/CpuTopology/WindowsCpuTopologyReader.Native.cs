using System.ComponentModel;
using System.Runtime.InteropServices;
using ResourceManager.App.Infrastructure.Windows;

namespace ResourceManager.App.Infrastructure.CpuTopology;

public sealed partial class WindowsCpuTopologyReader
{
    private static IReadOnlyList<LogicalProcessorRelationshipRecord> QueryLogicalProcessorRecords(int relationship)
    {
        return QueryLogicalProcessorInformation(relationship, ParseProcessorRelationship)
            .Where(static record => record.LogicalProcessorIds.Count > 0)
            .ToArray();
    }

    private static IReadOnlyList<CacheRelationshipRecord> QueryCacheRecords()
    {
        return QueryLogicalProcessorInformation(NativeMethods.RelationCache, ParseCacheRelationship)
            .Where(static record => record.LogicalProcessorIds.Count > 0)
            .ToArray();
    }

    private static IReadOnlyList<CpuSetRecord> QueryCpuSetRecords()
    {
        if (!NativeMethods.GetSystemCpuSetInformation(IntPtr.Zero, 0, out var returnedLength, IntPtr.Zero, 0))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != NativeMethods.ErrorInsufficientBuffer || returnedLength <= 0)
            {
                throw new Win32Exception(error);
            }
        }

        var buffer = Marshal.AllocHGlobal(returnedLength);
        try
        {
            if (!NativeMethods.GetSystemCpuSetInformation(buffer, returnedLength, out returnedLength, IntPtr.Zero, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var parsed = new List<CpuSetRecord>();
            var offset = 0;
            while (offset + 24 <= returnedLength)
            {
                var current = IntPtr.Add(buffer, offset);
                var size = Marshal.ReadInt32(current);
                var type = Marshal.ReadInt32(current, 4);
                if (size <= 0 || offset + size > returnedLength)
                {
                    break;
                }

                if (type == NativeMethods.CpuSetInformation)
                {
                    parsed.Add(ParseCpuSetRecord(current));
                }

                offset += size;
            }

            return parsed;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static IReadOnlyList<T> QueryLogicalProcessorInformation<T>(
        int relationship,
        Func<LogicalProcessorInfoRecord, T> parse)
    {
        var returnedLength = 0;
        if (!NativeMethods.GetLogicalProcessorInformationEx(relationship, IntPtr.Zero, ref returnedLength))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != NativeMethods.ErrorInsufficientBuffer || returnedLength <= 0)
            {
                throw new Win32Exception(error);
            }
        }

        var buffer = Marshal.AllocHGlobal(returnedLength);
        try
        {
            if (!NativeMethods.GetLogicalProcessorInformationEx(relationship, buffer, ref returnedLength))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var parsed = new List<T>();
            var offset = 0;
            while (offset + 8 <= returnedLength)
            {
                var current = IntPtr.Add(buffer, offset);
                var currentRelationship = Marshal.ReadInt32(current);
                var size = Marshal.ReadInt32(current, 4);
                if (size <= 0 || offset + size > returnedLength)
                {
                    break;
                }

                parsed.Add(parse(new LogicalProcessorInfoRecord(currentRelationship, current, size)));
                offset += size;
            }

            return parsed;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static LogicalProcessorRelationshipRecord ParseProcessorRelationship(LogicalProcessorInfoRecord record)
    {
        var efficiencyClass = Marshal.ReadByte(record.Pointer, 9);
        var groupCount = Marshal.ReadInt16(record.Pointer, 30);
        var logicalProcessors = ReadGroupAffinities(record.Pointer, 32, groupCount);
        return new LogicalProcessorRelationshipRecord(efficiencyClass, logicalProcessors);
    }

    private static CacheRelationshipRecord ParseCacheRelationship(LogicalProcessorInfoRecord record)
    {
        var level = Marshal.ReadByte(record.Pointer, 8);
        var cacheSize = Marshal.ReadInt32(record.Pointer, 12);
        var logicalProcessors = ReadGroupAffinities(record.Pointer, 40, 1);
        return new CacheRelationshipRecord(level, cacheSize, logicalProcessors);
    }

    private static CpuSetRecord ParseCpuSetRecord(IntPtr pointer)
    {
        return new CpuSetRecord(
            unchecked((uint)Marshal.ReadInt32(pointer, 8)),
            Marshal.ReadInt16(pointer, 12),
            Marshal.ReadByte(pointer, 14),
            Marshal.ReadByte(pointer, 15),
            Marshal.ReadByte(pointer, 16),
            Marshal.ReadByte(pointer, 17),
            Marshal.ReadByte(pointer, 18),
            Marshal.ReadInt32(pointer, 20));
    }

    private static IReadOnlyList<int> ReadGroupAffinities(
        IntPtr pointer,
        int offset,
        int groupCount)
    {
        var result = new List<int>();
        for (var groupIndex = 0; groupIndex < groupCount; groupIndex++)
        {
            var groupOffset = offset + groupIndex * 16;
            var mask = unchecked((ulong)Marshal.ReadInt64(pointer, groupOffset));
            var group = Marshal.ReadInt16(pointer, groupOffset + 8);
            for (var bit = 0; bit < 64; bit++)
            {
                if ((mask & (1UL << bit)) != 0)
                {
                    result.Add(group * 64 + bit);
                }
            }
        }

        return result;
    }
}
