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
    public async Task SteadyFrameReadsFixedFactsOnceAndTakesCountersFromTheTable(bool batch)
    {
        var api = new Api();
        var inventory = new Inventory();
        using var owner = new WindowsProcessFieldOwner(new ProcessFieldCurrentState()) { Inventory = inventory, BasicReader = new(api) };
        string[] fields = ["process_all_image_path_actual", "process_all_user_sid_actual", "process_all_command_line_actual", "process_all_parent_pid_actual", "process_all_working_set_actual", "process_all_cpu_time_actual"];
        if (batch) fields = [.. fields, "process_all_thread_count_actual"];
        var first = await owner.CollectAsync(fields);
        var initialFixed = api.FixedCalls;
        var initialOpens = api.Opens;
        inventory.Counter = 20;
        var timer = Stopwatch.StartNew();
        var second = await owner.CollectAsync(fields);
        timer.Stop();
        output.WriteLine($"processes=640 batch={batch} initial fixed={initialFixed}, initial opens={initialOpens}, steady fixed={api.FixedCalls - initialFixed}, steady opens={api.Opens - initialOpens}, frame ms={timer.Elapsed.TotalMilliseconds:F3}");
        Assert.Equal(640 * 4, initialFixed);
        Assert.Equal(640, initialOpens); // One handle per new instance, for its fixed facts only.
        Assert.Equal(initialFixed, api.FixedCalls);
        Assert.Equal(initialOpens, api.Opens); // A steady frame opens no handle at all.
        Assert.Equal(0, api.MemoryCalls + api.ThreadSnapshots);
        Assert.All(second.Processes, row => Assert.Equal(MetricInteger.Parse("20"), row.Values.Single(v => v.Field == "process_all_working_set_actual").IntegerValue));
        if (batch) Assert.All(second.Processes, row => Assert.Equal(MetricInteger.Parse("5"), row.Values.Single(v => v.Field == "process_all_thread_count_actual").IntegerValue));
        Assert.Equal(first.Processes.Select(row => row.Values.Single(v => v.Field == "process_all_image_path_actual").StringValue), second.Processes.Select(row => row.Values.Single(v => v.Field == "process_all_image_path_actual").StringValue));
        // Same PID, different birth, must acquire its immutable facts again.
        inventory.Birth = api.Birth = 101;
        await owner.CollectAsync(fields);
        Assert.Equal(initialFixed * 2, api.FixedCalls);
        Assert.Equal(initialOpens * 2, api.Opens);
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
        var opensBeforeSteady = cachedApi.Opens;
        timer.Restart();
        for (var frame = 0; frame < 3; frame++) await owner.CollectAsync(fields);
        var cachedMilliseconds = timer.Elapsed.TotalMilliseconds / 3;
        var cachedQueries = (cachedApi.FixedCalls - beforeSteady) / 3d;
        // Steady opens are only the fixed-fact retries of instances this account could not open.
        var steadyOpens = (cachedApi.Opens - opensBeforeSteady) / 3d;
        output.WriteLine($"native processes={rows.Length}, raw fixed reads/frame={rawQueries:F1}, cached fixed reads/frame={cachedQueries:F1}, steady opens/frame={steadyOpens:F1}, raw acquisition ms/frame={rawMilliseconds:F3}, cached full frame ms/frame={cachedMilliseconds:F3}");
        Assert.True(cachedApi.FixedCalls > 0);
        Assert.True(cachedQueries <= rawQueries);
        Assert.Equal(0, cachedApi.CounterCalls); // Working set and CPU time came from the kernel table records.
        Assert.True(steadyOpens < rows.Length);
    }

    private sealed class FrozenInventory(WindowsProcessInventoryEntry[] rows) : IWindowsProcessInventoryReader
    {
        public Process[] GetProcesses() => throw new InvalidOperationException();
        public WindowsProcessInventoryCapture Capture(WindowsProcessInventoryBuffer buffer) => new(rows);
    }
    private sealed class MeasuredNativeApi : IWindowsProcessBasicMetricApi
    {
        private readonly IWindowsProcessBasicMetricApi native = new WindowsProcessBasicMetricNativeApi();
        internal int FixedCalls, Opens, CounterCalls;
        public IntPtr Open(int pid) { Opens++; return native.Open(pid); }
        public IntPtr Open(int pid, ProcessMetricReadMask mask) { Opens++; return native.Open(pid, mask); }
        public bool Times(IntPtr handle, out ulong birth, out ulong kernel, out ulong user) => native.Times(handle, out birth, out kernel, out user);
        public bool Close(IntPtr handle) => native.Close(handle);
        public ProcessMemoryObservation? Memory(IntPtr handle, bool privateWorkingSet) { CounterCalls++; return native.Memory(handle, privateWorkingSet); }
        public ProcessIoObservation? Io(IntPtr handle) { CounterCalls++; return native.Io(handle); }
        public uint? HandleCount(IntPtr handle) { CounterCalls++; return native.HandleCount(handle); }
        public string? ImagePath(IntPtr handle) { FixedCalls++; return native.ImagePath(handle); }
        public string? UserSid(IntPtr handle) { FixedCalls++; return native.UserSid(handle); }
        public uint? ParentPid(IntPtr handle) { FixedCalls++; return native.ParentPid(handle); }
    }
    private sealed class Inventory : IWindowsProcessInventoryReader
    {
        internal long Birth = 100;
        internal ulong Counter = 10;
        internal bool Empty;
        public Process[] GetProcesses() => throw new InvalidOperationException();
        // Kernel-table rows, as the production inventory produces them: each carries its record counters.
        public WindowsProcessInventoryCapture Capture(WindowsProcessInventoryBuffer buffer)
            => new(Empty ? [] : Enumerable.Range(1000, 640).Select(pid => new WindowsProcessInventoryEntry(pid, new(ProcessReadStatus.Current, Birth), 0, "app",
                Counters: new(Counter, Counter, Counter, Counter, Counter, Counter, 0, 0, 0, 0, 0, 0, 1, 5, null))).ToArray());
    }
    private sealed class Api : IWindowsProcessBasicMetricApi
    {
        internal long Birth = 100;
        internal int FixedCalls, MemoryCalls, Opens, ThreadSnapshots;
        public IntPtr Open(int pid) { Opens++; return new(pid); }
        public bool Close(IntPtr handle) => true;
        public bool Times(IntPtr handle, out ulong birth, out ulong kernel, out ulong user) { birth = (ulong)Birth; kernel = user = 99; return true; }
        public ProcessMemoryObservation? Memory(IntPtr handle, bool privateWorkingSet) { MemoryCalls++; return new(99, 99, 99, 99); }
        public IReadOnlyDictionary<int, uint?> ThreadCounts(IReadOnlyDictionary<int, IntPtr> handles) { ThreadSnapshots++; return new Dictionary<int, uint?>(); }
        public ProcessIoObservation? Io(IntPtr handle) => null;
        public uint? HandleCount(IntPtr handle) => 1;
        public string? ImagePath(IntPtr handle) { FixedCalls++; return @"D:\Apps\App\app.exe"; }
        public string? UserSid(IntPtr handle) { FixedCalls++; return "S-1-5-21-1-2-3-1001"; }
        public string? CommandLine(IntPtr handle) { FixedCalls++; return "app.exe --fixture"; }
        public uint? ParentPid(IntPtr handle) { FixedCalls++; return 1; }
    }
}
