using System.Diagnostics;
using ResourceManager.App.Infrastructure.ResourceBreakdown;
using ResourceManager.ReadWrite.Windows.Processes;
using Xunit.Abstractions;

namespace Resource_Manager_APP.Tests;

public sealed class TrayProcessFixedReadTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SteadyFrameReadsFixedFactsOnceAndKeepsCountersLive(bool batch)
    {
        var api = new Api();
        var inventory = new Inventory();
        using var owner = new WindowsProcessFieldOwner(new ProcessFieldCurrentState()) { Inventory = inventory, BasicReader = new(api) };
        string[] fields = ["process_all_image_path_actual", "process_all_user_sid_actual", "process_all_command_line_actual", "process_all_parent_pid_actual", "process_all_working_set_actual", "process_all_cpu_time_actual"];
        if (batch) fields = [.. fields, "process_all_thread_count_actual"];
        var first = await owner.CollectAsync(fields);
        var initialFixed = api.FixedCalls;
        var initialSampled = api.MemoryCalls;
        api.Counter = 20;
        var timer = Stopwatch.StartNew();
        var second = await owner.CollectAsync(fields);
        timer.Stop();
        output.WriteLine($"processes=640 batch={batch} initial fixed={initialFixed}, steady fixed={api.FixedCalls - initialFixed}, steady memory={api.MemoryCalls - initialSampled}, frame ms={timer.Elapsed.TotalMilliseconds:F3}");
        Assert.Equal(640 * 4, initialFixed);
        Assert.Equal(initialFixed, api.FixedCalls);
        Assert.Equal(640 * 2, api.MemoryCalls);
        Assert.All(second.Processes, row => Assert.Equal(MetricInteger.Parse("20"), row.Values.Single(v => v.Field == "process_all_working_set_actual").IntegerValue));
        Assert.Equal(first.Processes.Select(row => row.Values.Single(v => v.Field == "process_all_image_path_actual").StringValue), second.Processes.Select(row => row.Values.Single(v => v.Field == "process_all_image_path_actual").StringValue));
        // Same PID, different birth, must acquire its immutable facts again.
        inventory.Birth = api.Birth = 101;
        await owner.CollectAsync(fields);
        Assert.Equal(initialFixed * 2, api.FixedCalls);
        inventory.Empty = true;
        Assert.Empty((await owner.CollectAsync(fields)).Processes);
        // Re-introducing the old fixture key proves its entry was actually evicted.
        inventory.Empty = false;
        inventory.Birth = api.Birth = 100;
        await owner.CollectAsync(fields);
        Assert.Equal(initialFixed * 3, api.FixedCalls);
    }

    [Fact]
    public async Task NativeProcessFrameReportsPhysicalReadCountsAndElapsedCost()
    {
        using var buffer = new WindowsProcessInventoryBuffer();
        using var captured = WindowsProcessInventoryReader.Instance.Capture(buffer);
        var rows = captured.Processes.Where(row => row.Id > 0 && row.Creation.Creation is > 0).Take(2048).ToArray();
        Assert.NotEmpty(rows);
        const ProcessMetricReadMask mask = ProcessMetricReadMask.Memory | ProcessMetricReadMask.CpuTime
            | ProcessMetricReadMask.StartTime | ProcessMetricReadMask.ImagePath | ProcessMetricReadMask.ParentPid | ProcessMetricReadMask.UserSid;
        var requests = rows.Select(row => new ProcessBasicMetricRequest(row.Id, row.Creation.Creation!.Value, mask)).ToArray();
        var rawApi = new MeasuredNativeApi();
        var reader = new WindowsProcessBasicMetricReader(rawApi);
        reader.ReadBatch(requests); // warm the same raw acquisition path
        var rawBefore = rawApi.FixedCalls;
        var timer = Stopwatch.StartNew();
        for (var frame = 0; frame < 3; frame++) reader.ReadBatch(requests);
        var rawMilliseconds = timer.Elapsed.TotalMilliseconds / 3;
        var rawQueries = (rawApi.FixedCalls - rawBefore) / 3d;
        var cachedApi = new MeasuredNativeApi();
        using var owner = new WindowsProcessFieldOwner(new ProcessFieldCurrentState())
            { Inventory = new FrozenInventory(rows), BasicReader = new(cachedApi) };
        string[] fields = ["process_all_working_set_actual", "process_all_cpu_time_actual", "process_all_start_time_actual",
            "process_all_image_path_actual", "process_all_parent_pid_actual", "process_all_user_sid_actual"];
        await owner.CollectAsync(fields);
        var beforeSteady = cachedApi.FixedCalls;
        timer.Restart();
        for (var frame = 0; frame < 3; frame++) await owner.CollectAsync(fields);
        var cachedMilliseconds = timer.Elapsed.TotalMilliseconds / 3;
        var cachedQueries = (cachedApi.FixedCalls - beforeSteady) / 3d;
        output.WriteLine($"native processes={rows.Length}, raw fixed reads/frame={rawQueries:F1}, cached fixed reads/frame={cachedQueries:F1}, raw acquisition ms/frame={rawMilliseconds:F3}, cached full frame ms/frame={cachedMilliseconds:F3}");
        Assert.True(cachedApi.FixedCalls > 0);
        Assert.True(cachedQueries <= rawQueries);
    }

    private sealed class FrozenInventory(WindowsProcessInventoryEntry[] rows) : IWindowsProcessInventoryReader
    {
        public Process[] GetProcesses() => throw new InvalidOperationException();
        public WindowsProcessInventoryCapture Capture(WindowsProcessInventoryBuffer buffer) => new(rows);
    }
    private sealed class MeasuredNativeApi : IWindowsProcessBasicMetricApi
    {
        private readonly IWindowsProcessBasicMetricApi native = new WindowsProcessBasicMetricNativeApi();
        internal int FixedCalls;
        public IntPtr Open(int pid) => native.Open(pid);
        public IntPtr Open(int pid, ProcessMetricReadMask mask) => native.Open(pid, mask);
        public bool Times(IntPtr handle, out ulong birth, out ulong kernel, out ulong user) => native.Times(handle, out birth, out kernel, out user);
        public bool Close(IntPtr handle) => native.Close(handle);
        public ProcessMemoryObservation? Memory(IntPtr handle, bool privateWorkingSet) => native.Memory(handle, privateWorkingSet);
        public ProcessIoObservation? Io(IntPtr handle) => native.Io(handle);
        public uint? HandleCount(IntPtr handle) => native.HandleCount(handle);
        public string? ImagePath(IntPtr handle) { FixedCalls++; return native.ImagePath(handle); }
        public string? UserSid(IntPtr handle) { FixedCalls++; return native.UserSid(handle); }
        public uint? ParentPid(IntPtr handle) { FixedCalls++; return native.ParentPid(handle); }
    }
    private sealed class Inventory : IWindowsProcessInventoryReader
    {
        internal long Birth = 100;
        internal bool Empty;
        public Process[] GetProcesses() => throw new InvalidOperationException();
        public WindowsProcessInventoryCapture Capture(WindowsProcessInventoryBuffer buffer)
            => new(Empty ? [] : Enumerable.Range(1000, 640).Select(pid => new WindowsProcessInventoryEntry(pid, new(ProcessReadStatus.Current, Birth), 0, "app", ThreadCount: 1)).ToArray());
    }
    private sealed class Api : IWindowsProcessBasicMetricApi
    {
        internal long Birth = 100;
        internal ulong Counter = 10;
        internal int FixedCalls, MemoryCalls;
        public IntPtr Open(int pid) => new(pid);
        public bool Close(IntPtr handle) => true;
        public bool Times(IntPtr handle, out ulong birth, out ulong kernel, out ulong user) { birth = (ulong)Birth; kernel = user = Counter; return true; }
        public ProcessMemoryObservation? Memory(IntPtr handle, bool privateWorkingSet) { MemoryCalls++; return new(Counter, Counter, Counter, Counter); }
        public ProcessIoObservation? Io(IntPtr handle) => null;
        public uint? HandleCount(IntPtr handle) => 1;
        public string? ImagePath(IntPtr handle) { FixedCalls++; return @"D:\Apps\App\app.exe"; }
        public string? UserSid(IntPtr handle) { FixedCalls++; return "S-1-5-21-1-2-3-1001"; }
        public string? CommandLine(IntPtr handle) { FixedCalls++; return "app.exe --fixture"; }
        public uint? ParentPid(IntPtr handle) { FixedCalls++; return 1; }
    }
}
