using System.ComponentModel;
using System.Runtime.InteropServices;
using ResourceManager.App.Infrastructure.Windows;

namespace ResourceManager.App.Infrastructure.CpuTopology;

public sealed partial class WindowsCpuTopologyReader
{
    private IReadOnlyList<double?> ReadLogicalProcessorUsage(
        int logicalProcessorCount,
        ICollection<string> notes)
    {
        try
        {
            lock (usageGate)
            {
                var current = QueryProcessorTimes();
                var previous = previousTimes;
                previousTimes = current;
                if (previous is null || previous.Count != current.Count)
                {
                    notes.Add("每逻辑处理器占用需要两次采样后显示；当前为首次采样。");
                    return Enumerable.Repeat<double?>(null, Math.Max(logicalProcessorCount, current.Count)).ToArray();
                }

                var result = new double?[Math.Max(logicalProcessorCount, current.Count)];
                for (var index = 0; index < current.Count; index++)
                {
                    var totalDelta = (current[index].KernelTime - previous[index].KernelTime)
                        + (current[index].UserTime - previous[index].UserTime);
                    var idleDelta = current[index].IdleTime - previous[index].IdleTime;
                    if (totalDelta <= 0)
                    {
                        result[index] = null;
                        continue;
                    }

                    var busy = Math.Clamp(totalDelta - idleDelta, 0, totalDelta);
                    result[index] = Math.Round(busy * 100d / totalDelta, 2);
                }

                return result;
            }
        }
        catch (Exception ex) when (IsExpectedNativeException(ex))
        {
            notes.Add($"读取每逻辑处理器占用失败：{ex.Message}");
            return Enumerable.Repeat<double?>(null, logicalProcessorCount).ToArray();
        }
    }

    private static IReadOnlyList<ProcessorTimes> QueryProcessorTimes()
    {
        var processorCount = Math.Max(1, Environment.ProcessorCount);
        var size = Marshal.SizeOf<SystemProcessorPerformanceInformation>();
        var length = size * processorCount;
        var buffer = Marshal.AllocHGlobal(length);
        try
        {
            var status = NativeMethods.NtQuerySystemInformation(
                NativeMethods.SystemProcessorPerformanceInformation,
                buffer,
                length,
                out var returnLength);
            if (status < 0)
            {
                throw new Win32Exception(status);
            }

            var count = Math.Max(processorCount, returnLength > 0 ? returnLength / size : processorCount);
            var result = new List<ProcessorTimes>(count);
            for (var index = 0; index < count; index++)
            {
                var item = Marshal.PtrToStructure<SystemProcessorPerformanceInformation>(IntPtr.Add(buffer, index * size));
                result.Add(new ProcessorTimes(item.IdleTime, item.KernelTime, item.UserTime));
            }

            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static double? TryGetUsage(
        IReadOnlyList<double?> usageValues,
        int logicalProcessorId)
    {
        return logicalProcessorId >= 0 && logicalProcessorId < usageValues.Count
            ? usageValues[logicalProcessorId]
            : null;
    }
}
