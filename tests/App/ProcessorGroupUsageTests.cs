using System.Runtime.InteropServices;
using ResourceManager.App.Infrastructure.CpuTopology;
using ResourceManager.App.Infrastructure.Windows;

namespace Resource_Manager_APP.Tests;

public sealed class ProcessorGroupUsageTests
{
    [Fact]
    public void GroupQueriesPreserveSparseTopologyIdsAndOnlyReadReturnedRecords()
    {
        var calls = new List<ushort>();
        int Query(ushort group, IntPtr buffer, int length, out int returned)
        {
            calls.Add(group);
            var size = Marshal.SizeOf<SystemProcessorPerformanceInformation>();
            var count = group == 0 ? 48 : 1; // Group 1 was allocated for two, but returns only one.
            for (var index = 0; index < count; index++)
                Marshal.StructureToPtr(new SystemProcessorPerformanceInformation {
                    IdleTime = group == 0 ? 80 : 10, KernelTime = 100, UserTime = 0
                }, IntPtr.Add(buffer, size * index), false);
            returned = size * count; return 0;
        }
        var current = WindowsCpuOsTopologyReader.QueryProcessorTimes([48, 2], Query);
        Assert.Equal(new ushort[] { 0, 1 }, calls);
        Assert.Equal(49, current.Count); Assert.False(current.ContainsKey(48)); Assert.False(current.ContainsKey(65));
        var previous = current.Keys.ToDictionary(id => id, _ => new WindowsCpuOsTopologyReader.ProcessorTimes(0, 0, 0));
        var usage = WindowsCpuOsTopologyReader.CalculateProcessorUsage(previous, current, 66);
        Assert.Equal(20d, usage[47]); Assert.Null(usage[48]); Assert.Equal(90d, usage[64]); Assert.Null(usage[65]);
        var topologyIds = Enumerable.Range(0, 48).Concat(Enumerable.Range(64, 48)).ToArray();
        Assert.Equal(64, EtwCpuCoreResidencyReader.ResolveEventLogicalProcessorId(48, topologyIds));
        Assert.Equal(111, EtwCpuCoreResidencyReader.ResolveEventLogicalProcessorId(95, topologyIds));
        Assert.Null(EtwCpuCoreResidencyReader.ResolveEventLogicalProcessorId(96, topologyIds));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(int.MaxValue)]
    public void InvalidNativePayloadLengthCannotPublishUninitializedTimes(int returnedLength)
    {
        int Query(ushort group, IntPtr buffer, int length, out int returned) { returned = returnedLength; return 0; }
        Assert.Throws<InvalidOperationException>(() => WindowsCpuOsTopologyReader.QueryProcessorTimes([1], Query));
    }

    [Fact]
    public void CurrentMachineGroupQueriesReturnOnlyActualProcessorTimes()
    {
        var times = WindowsCpuOsTopologyReader.QueryProcessorTimes();
        Assert.NotEmpty(times);
        Assert.Equal(checked((int)NativeMethods.GetActiveProcessorCount(ushort.MaxValue)), times.Count);
        foreach (var item in times.Values)
        {
            Assert.True(item.IdleTime >= 0); Assert.True(item.KernelTime >= item.IdleTime); Assert.True(item.UserTime >= 0);
        }
    }
}
