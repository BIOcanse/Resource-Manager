using System.Collections.Frozen;
using ResourceManager.ReadWrite.Windows.Processes;
using Resource_Manager_APP.Tests.KernelEtwFixtures;
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
        var config = NativeKernelEtwTestResource.SmallConfiguration(maximumProcessors: 96);
        config.Residency.MaximumCells = 8192;
        using var broker = new RecordingNativeKernelEtwBroker(config);
        var plan = new CpuResidencyCaptureConfiguration(1000, 128,
            topologyIds.ToFrozenDictionary(id => id, id => new CpuResidencyCoreAccounting($"core:{id}", "", 1, 1)),
            "native sparse group map", 50);
        using var owner = new EtwCpuResidencyCaptureOwner(broker, () => plan);
        owner.EnsureStarted();
        Assert.False(owner.IsWindowActive); // The run holds metadata only.
        owner.BeginWindow();
        Assert.True(owner.IsWindowActive);
        // The fixture delivers RAW indices through the actual shared trace
        // processor hook, so index48 becomes64 and index95 becomes111.
        // This controlled recording starts at native QPC origin0. Machine
        // uptime would add an unrelated retained window to every CPU pair.
        NativeCpuReaderTestFeed.ClosedWindow(owner, topologyIds, 1000, startQpc: 0);
        Assert.True(owner.Collect());
        var nativeIds = Assert.Single(owner.Read()!.Processes).LogicalProcessors
            .Select(row => row.LogicalProcessorId).Order().ToArray();
        Assert.Equal(topologyIds, nativeIds);
        var before = owner.Read();
        broker.Resource.CSwitchFromThreadIds(System.Diagnostics.Stopwatch.Frequency / 2,
            before!.MeasuredFrom.AddMilliseconds(500), 96, 0, 0);
        broker.Resource.Publish(broker.Resource.Tick);
        Assert.True(owner.Collect());
        Assert.Equal(before!.Processes.Single().SwitchCount, owner.Read()!.Processes.Single().SwitchCount);
        Assert.Equal(topologyIds, owner.Read()!.Processes.Single().LogicalProcessors.Select(row => row.LogicalProcessorId).Order());
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
