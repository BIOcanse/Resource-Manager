using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using ResourceManager.App.Infrastructure.ResourceBreakdown;
using ResourceManager.ReadWrite.Windows.Processes;
using Xunit.Abstractions;

namespace Resource_Manager_APP.Tests;

public sealed class ProcessInventoryBufferTests(ITestOutputHelper output)
{
    [Fact]
    public void WorkspaceGrowsOnlyOnDemandAndReusesOneAllocationUntilRelease()
    {
        var api = new BufferApi { RequiredBytes = 1024 };
        using var buffer = new WindowsProcessInventoryBuffer(new(initialSystemInformationBufferBytes: 256,
            maximumSystemInformationBufferBytes: 4096), api);
        Assert.True(buffer.TryRead(out var data));
        Assert.Equal(1024, data.Length);
        Assert.Equal(2, api.Resizes);
        for (var i = 0; i < 20; i++) Assert.True(buffer.TryRead(out _));
        Assert.Equal(2, api.Resizes);
        api.RequiredBytes = 2048;
        Assert.True(buffer.TryRead(out _));
        Assert.Equal(3, api.Resizes);
        api.RequiredBytes = 128;
        Assert.True(buffer.TryRead(out data));
        Assert.Equal(128, data.Length);
        Assert.Equal(2048, buffer.CapacityBytes);
        buffer.Release();
        Assert.Equal(0, buffer.CapacityBytes);
        Assert.Equal(1, api.Frees);
        Assert.True(buffer.TryRead(out _));
        buffer.Dispose();
        buffer.Dispose();
        Assert.Equal(2, api.Frees);
        Assert.Throws<ObjectDisposedException>(() => buffer.TryRead(out _));
    }

    [Theory]
    [InlineData("ceiling")]
    [InlineData("denied")]
    [InlineData("zero")]
    [InlineData("oversized-success")]
    [InlineData("attempts")]
    public void FailedReadNeverReturnsThePreviousTableOrGrowsPastItsBound(string failure)
    {
        var api = new BufferApi { RequiredBytes = 128 };
        using var buffer = new WindowsProcessInventoryBuffer(new(initialSystemInformationBufferBytes: 256,
            maximumSystemInformationBufferBytes: 4096, maximumSystemInformationAttempts: 2), api);
        Assert.True(buffer.TryRead(out _));
        api.Failure = failure;
        api.RequiredBytes = failure == "ceiling" ? 4097 : 128;
        Assert.False(buffer.TryRead(out var data));
        Assert.True(data.IsEmpty);
        Assert.InRange(buffer.CapacityBytes, 256, 4096);
        if (failure == "ceiling") Assert.Equal(1, api.Resizes);
    }

    [Fact]
    public async Task SamplerStopReleasesWorkspaceEvenWithoutABackgroundWorkerAndCanRestart()
    {
        using var sampler = new WindowsResourceBreakdownSampler(null!, null!, null!, null!, null!, null!, null!, null!);
        var owner = (WindowsProcessFieldOwner)typeof(WindowsResourceBreakdownSampler)
            .GetProperty("processFieldOwner", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(sampler)!;
        sampler.CaptureSchedulingProcessSampleAccounting();
        Assert.True(owner.InventoryBufferCapacityBytes > 0);
        await sampler.StopAsync(default);
        Assert.Equal(0, owner.InventoryBufferCapacityBytes);
        sampler.CaptureSchedulingProcessSampleAccounting();
        Assert.True(owner.InventoryBufferCapacityBytes > 0);
        sampler.Dispose();
        Assert.Equal(0, owner.InventoryBufferCapacityBytes);
    }

    [Fact]
    public async Task LastBorrowDemandReleasesOnlyItsOwnersWorkspace()
    {
        using var owner = WindowsProcessFieldOwner.CreateDefault();
        await owner.CollectAsync(["process_all_command_line_actual"]);
        var field = owner.Backend.Catalog.Fields.First(
            value => value.Field.FieldName == "process_all_command_line_actual").Field;
        using var first = owner.BorrowSubscriptionCapture();
        using var second = owner.BorrowSubscriptionCapture();
        first.Bind([field]); second.Bind([field]);
        first.SynchronizeDemand([field]); second.SynchronizeDemand([field]);
        Assert.True(owner.InventoryBufferCapacityBytes > 0);
        first.SynchronizeDemand([]);
        Assert.True(owner.InventoryBufferCapacityBytes > 0);
        second.SynchronizeDemand([]);
        Assert.Equal(0, owner.InventoryBufferCapacityBytes);
    }

    [Fact]
    public void MeasureCurrentKernelTableAndIdentityBatches()
    {
        const int samples = 40;
        var processes = WindowsProcessInventoryReader.Instance.GetProcesses();
        try
        {
            using var buffer = new WindowsProcessInventoryBuffer();
            var oldApi = new WindowsProcessDetailsNativeApi(WindowsProcessDetailsOptions.Default);
            for (var i = 0; i < 5; i++)
            {
                Assert.True(buffer.TryRead(out _));
                _ = WindowsProcessInventoryReader.Instance.ReadCreations(processes, buffer);
            }
            var queryTimes = new double[samples];
            var batchTimes = new double[samples];
            var oldTimes = new double[samples];
            var bytes = 0;
            var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < samples; i++)
            {
                var started = Stopwatch.GetTimestamp();
                if (!buffer.TryRead(out var data)) throw new InvalidDataException("Kernel table query failed.");
                bytes = data.Length;
                queryTimes[i] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            }
            var queryBytes = (GC.GetAllocatedBytesForCurrentThread() - allocatedBefore) / (double)samples;
            allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < samples; i++)
            {
                var started = Stopwatch.GetTimestamp();
                _ = WindowsProcessInventoryReader.Instance.ReadCreations(processes, buffer);
                batchTimes[i] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            }
            var batchBytes = (GC.GetAllocatedBytesForCurrentThread() - allocatedBefore) / (double)samples;
            allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < samples; i++)
            {
                var started = Stopwatch.GetTimestamp();
                if (!oldApi.TryReadSystemProcessInformation(out _)) throw new InvalidDataException("Baseline table query failed.");
                oldTimes[i] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            }
            var oldBytes = (GC.GetAllocatedBytesForCurrentThread() - allocatedBefore) / (double)samples;
            Array.Sort(queryTimes); Array.Sort(batchTimes); Array.Sort(oldTimes);
            output.WriteLine("PROCESS_INVENTORY_BUFFER_PROBE " + JsonSerializer.Serialize(new
            {
                processes = processes.Length, samples, tableBytes = bytes, retainedBytes = buffer.CapacityBytes,
                queryManagedBytesPerRead = queryBytes, identityBatchManagedBytes = batchBytes,
                baselineCopiedTableManagedBytesPerRead = oldBytes,
                queryMedianMs = queryTimes[samples / 2], identityBatchMedianMs = batchTimes[samples / 2],
                baselineMedianMs = oldTimes[samples / 2], queryP95Ms = queryTimes[(int)(samples * .95) - 1],
                identityBatchP95Ms = batchTimes[(int)(samples * .95) - 1]
            }));
            Assert.Equal(0, queryBytes);
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    private sealed class BufferApi : IWindowsProcessInventoryBufferApi
    {
        internal int RequiredBytes, Resizes, Frees;
        internal string? Failure;
        public int Query(IntPtr buffer, uint length, out uint returned)
        {
            returned = (uint)RequiredBytes;
            if (Failure == "denied") return unchecked((int)0xC0000022);
            if (Failure == "zero") { returned = 0; return 0; }
            if (Failure == "oversized-success") { returned = length + 1; return 0; }
            if (Failure == "attempts") { returned = length + 1; return unchecked((int)0xC0000004); }
            return returned > length ? unchecked((int)0xC0000004) : 0;
        }
        public IntPtr Resize(IntPtr buffer, int length) { Resizes++; return Marshal.ReAllocHGlobal(buffer, new(length)); }
        public void Free(IntPtr buffer) { Frees++; Marshal.FreeHGlobal(buffer); }
    }
}
