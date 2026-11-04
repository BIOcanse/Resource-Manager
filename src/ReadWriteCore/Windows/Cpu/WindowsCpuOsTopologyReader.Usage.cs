using System.ComponentModel;
using System.Runtime.InteropServices;
using NativeMethods = ResourceManager.ReadWrite.Windows.Cpu.WindowsCpuTopologyNativeMethods;

namespace ResourceManager.ReadWrite.Windows.Cpu;

public sealed partial class WindowsCpuOsTopologyReader
{
    public void ResetUsageBaseline()
    {
        lock (usageGate) previousTimes = null;
    }

    public IReadOnlyList<double?> ReadLogicalProcessorUsage(int logicalProcessorCount, ICollection<string> notes)
    {
        try
        {
            lock (usageGate)
            {
                var current = readProcessorTimes();
                var previous = previousTimes;
                previousTimes = current;
                if (previous is null || !previous.Keys.Order().SequenceEqual(current.Keys.Order()))
                {
                    return new double?[logicalProcessorCount];
                }
                return CalculateProcessorUsage(previous, current, logicalProcessorCount);
            }
        }
        catch (Exception ex) when (IsExpectedNativeException(ex))
        {
            ResetUsageBaseline();
            notes.Add($"读取每逻辑处理器占用失败：{ex.Message}");
            return Enumerable.Repeat<double?>(null, logicalProcessorCount).ToArray();
        }
    }

    internal static double?[] CalculateProcessorUsage(IReadOnlyDictionary<int, ProcessorTimes> previous,
        IReadOnlyDictionary<int, ProcessorTimes> current, int logicalProcessorCount)
    {
        var result = new double?[Math.Max(logicalProcessorCount, current.Keys.DefaultIfEmpty(-1).Max() + 1)];
        foreach (var (id, times) in current)
        {
            if (!previous.TryGetValue(id, out var old)) continue;
            // Kernel time includes idle. Reject resets and inconsistent cells instead of clamping them into plausible values.
            if (old.IdleTime < 0 || old.KernelTime < old.IdleTime || old.UserTime < 0
                || times.IdleTime < old.IdleTime || times.KernelTime < old.KernelTime
                || times.UserTime < old.UserTime || times.KernelTime < times.IdleTime) continue;
            var kernelDelta = times.KernelTime - old.KernelTime;
            var userDelta = times.UserTime - old.UserTime;
            var idleDelta = times.IdleTime - old.IdleTime;
            if (idleDelta > kernelDelta || userDelta > long.MaxValue - kernelDelta) continue;
            var totalDelta = kernelDelta + userDelta;
            if (totalDelta == 0) continue;
            result[id] = (totalDelta - idleDelta) * 100d / totalDelta;
        }
        return result;
    }

    internal delegate int ProcessorGroupTimesQuery(ushort group, IntPtr buffer, int length, out int returnedLength);

    public static IReadOnlyDictionary<int, ProcessorTimes> QueryProcessorTimes()
    {
        var groups = NativeMethods.GetActiveProcessorGroupCount();
        var counts = new int[groups];
        for (ushort group = 0; group < groups; group++) counts[group] = checked((int)NativeMethods.GetActiveProcessorCount(group));
        return QueryProcessorTimes(counts, static (ushort group, IntPtr buffer, int length, out int returned) =>
            NativeMethods.NtQuerySystemInformationEx(NativeMethods.SystemProcessorPerformanceInformation,
                ref group, sizeof(ushort), buffer, length, out returned));
    }

    internal static IReadOnlyDictionary<int, ProcessorTimes> QueryProcessorTimes(
        IReadOnlyList<int> activeCounts, ProcessorGroupTimesQuery query)
    {
        if (activeCounts.Count is <= 0 or > ushort.MaxValue) throw new InvalidOperationException("Invalid processor group count.");
        var result = new Dictionary<int, ProcessorTimes>();
        var size = Marshal.SizeOf<SystemProcessorPerformanceInformation>();
        for (var group = 0; group < activeCounts.Count; group++)
        {
            var count = activeCounts[group];
            if (count is <= 0 or > 64) throw new InvalidOperationException("Invalid active processor count.");
            var length = checked(size * count);
            var buffer = Marshal.AllocHGlobal(length);
            try
            {
                var status = query(checked((ushort)group), buffer, length, out var returned);
                if (status < 0) throw new Win32Exception(status);
                if (returned <= 0 || returned > length || returned % size != 0)
                    throw new InvalidOperationException("Invalid processor times buffer length.");
                // Never read allocated capacity beyond the native returned payload.
                for (var index = 0; index < returned / size; index++)
                {
                    var item = Marshal.PtrToStructure<SystemProcessorPerformanceInformation>(IntPtr.Add(buffer, index * size));
                    result.Add(group * 64 + index, new(item.IdleTime, item.KernelTime, item.UserTime));
                }
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return result;
    }

}
