using System.Diagnostics;
using System.Runtime.InteropServices;
using ResourceManager.App.Infrastructure.ResourceBreakdown;
using ResourceManager.ReadWrite.ProcessFields;
using ResourceManager.ReadWrite.Windows.Processes;

namespace Resource_Manager_APP.Tests;

/// <summary>Per-process dynamic counters come from the SystemProcessInformation record of the capture's own
/// table read; handles are opened only for fixed facts, once per process instance.</summary>
public sealed class ProcessTableCounterTests
{
    private const string WorkingSet = "process_all_working_set_actual";
    private const string Threads = "process_all_thread_count_actual";
    private const string Handles = "process_all_handle_count_actual";
    private const string ReadBytes = "process_all_io_read_bytes_actual";
    private const string ReadBytesRate = "process_all_io_read_bytes_rate_actual";
    private static readonly string[] FixedFields = ["process_all_image_path_actual", "process_all_user_sid_actual"];
    private static readonly string[] CounterFields = [.. ProcessBasicFieldCollectorTests.Names, Threads,
        "process_all_io_read_operations_actual", "process_all_io_write_operations_actual", "process_all_io_other_operations_actual"];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StableRoundsOpenNoHandleAndANewInstanceOpensOnceForItsFixedFacts(bool batch)
    {
        var table = new Table(); using var buffer = new WindowsProcessInventoryBuffer(api: table);
        var api = new Api(table);
        using var owner = new WindowsProcessFieldOwner(new ProcessFieldCurrentState()) { Inventory = new TableInventory(buffer), BasicReader = new(api) };
        // Thread and handle counts select the owner's batch path; without them each process is read on its own.
        string[] fields = batch ? [.. CounterFields, .. FixedFields]
            : [.. CounterFields.Where(static name => name is not (Threads or Handles)), .. FixedFields];
        await owner.CollectAsync(fields);
        Assert.Equal(3, api.Opens); Assert.Equal(3, api.TimeCalls); // One verified handle per new instance, for its fixed facts.
        for (var round = 0; round < 3; round++)
        {
            table.Advance();
            var snapshot = await owner.CollectAsync(fields);
            AssertPublishedEqualsTable(snapshot, table, batch);
        }
        Assert.Equal(3, api.Opens); Assert.Equal(3, api.TimeCalls);
        Assert.Equal(4, table.Queries); // Each round still reads one fresh table.
        table.Rows[1].Creation++; // Same PID, new instance.
        await owner.CollectAsync(fields);
        Assert.Equal(4, api.Opens);
        await owner.CollectAsync(fields);
        Assert.Equal(4, api.Opens); Assert.Equal(api.Opens, api.Closes);
        Assert.Equal(0, api.CounterCalls);
        Assert.Equal(new[] { 11, 22, 33, 22 }, api.OpenedPids);
    }

    [Fact]
    public void SingleAndBatchReadsEqualTheTableRecordAndFillOnlyRequestedCategories()
    {
        var table = new Table(); var clock = new Clock();
        using var buffer = new WindowsProcessInventoryBuffer(api: table, clock: clock);
        using var capture = WindowsProcessInventoryReader.Instance.Capture(buffer);
        var api = new Api(table);
        using var owner = new WindowsProcessFieldOwner(new ProcessFieldCurrentState()) { BasicReader = new(api) };
        const ProcessMetricReadMask all = WindowsProcessTableCounters.Fields | ProcessMetricReadMask.UserSid;
        var batch = owner.ReadBasicBatch(capture.Processes, static (_, _) => all);
        foreach (var entry in capture.Processes)
        {
            var row = table.Rows.Single(item => item.Pid == entry.Id);
            var single = owner.ReadBasic(entry, all);
            Assert.Equal(single, batch[entry].Basic);
            Assert.Equal((UInt128)row.Kernel + row.User, single.CpuTime100Ns);
            // Row 33 reports a private working set above its working set, which neither source accepts.
            Assert.Equal(new ProcessMemoryObservation(row.WorkingSet, row.Commit, row.PeakCommit, row.Pid == 33 ? null : row.PrivateWorkingSet), single.Memory);
            Assert.Equal(new ProcessIoObservation(row.ReadOps, row.WriteOps, row.OtherOps, row.ReadBytes, row.WriteBytes, row.OtherBytes)
                { Timing = new(1000, 1005, 1000) }, single.Io); // The table query bracket times every record.
            Assert.Equal(row.Handles, single.HandleCount); Assert.Equal(row.Threads, single.ThreadCount);
            Assert.Equal($"S-1-5-21-{row.Pid}", single.UserSid);
            var memoryOnly = owner.ReadBasic(entry, ProcessMetricReadMask.Memory);
            Assert.Equal(new ProcessMemoryObservation(row.WorkingSet, row.Commit, row.PeakCommit, null), memoryOnly.Memory);
            Assert.Null(memoryOnly.CpuTime100Ns); Assert.Null(memoryOnly.Io); Assert.Null(memoryOnly.HandleCount);
            Assert.Null(memoryOnly.ThreadCount); Assert.Null(memoryOnly.UserSid);
        }
        Assert.Equal(3, api.Opens); Assert.Equal(0, api.CounterCalls);
    }

    [Fact]
    public async Task IoRateUsesTheTableQueryReturnAsItsReferencePoint()
    {
        var table = new Table(); var clock = new Clock();
        using var buffer = new WindowsProcessInventoryBuffer(api: table, clock: clock);
        var api = new Api(table);
        using var owner = new WindowsProcessFieldOwner(new ProcessFieldCurrentState()) { Inventory = new TableInventory(buffer), BasicReader = new(api) };
        var first = await owner.CollectAsync([ReadBytesRate, ReadBytes]);
        Assert.All(first.Processes, row => Assert.Null(row.Values.Single(value => value.Field == ReadBytesRate).NumericValue));
        table.Advance(); clock.Ticks = 3000;
        var second = await owner.CollectAsync([ReadBytesRate, ReadBytes]);
        foreach (var row in second.Processes)
        {
            Assert.Equal(100, row.Values.Single(value => value.Field == ReadBytesRate).NumericValue); // 200 B over 2000 ticks at 1000 Hz.
            var interval = Assert.IsType<ProcessIoInterval>(row.IoInterval);
            Assert.Equal(1005, interval.BeginCounter); Assert.Equal(3005, interval.EndCounter);
        }
        Assert.Equal(0, api.Opens + api.CounterCalls);
    }

    [Fact]
    public void NamedRecordFieldsSitAtTheirNativeOffsets()
    {
        if (IntPtr.Size != 8) return;
        static int Offset(string name) => Marshal.OffsetOf<WindowsProcessSystemThreadParser.SystemProcessInformationHeader>(name).ToInt32();
        Assert.Equal(0x08, Offset(nameof(WindowsProcessSystemThreadParser.SystemProcessInformationHeader.WorkingSetPrivateSize)));
        Assert.Equal(0x20, Offset(nameof(WindowsProcessSystemThreadParser.SystemProcessInformationHeader.CreationTime)));
        Assert.Equal(0x28, Offset(nameof(WindowsProcessSystemThreadParser.SystemProcessInformationHeader.UserTime)));
        Assert.Equal(0x30, Offset(nameof(WindowsProcessSystemThreadParser.SystemProcessInformationHeader.KernelTime)));
        Assert.Equal(0x60, Offset(nameof(WindowsProcessSystemThreadParser.SystemProcessInformationHeader.HandleCount)));
        Assert.Equal(0x90, Offset(nameof(WindowsProcessSystemThreadParser.SystemProcessInformationHeader.WorkingSetSize)));
        Assert.Equal(0xB8, Offset(nameof(WindowsProcessSystemThreadParser.SystemProcessInformationHeader.PagefileUsage)));
        Assert.Equal(0xC0, Offset(nameof(WindowsProcessSystemThreadParser.SystemProcessInformationHeader.PeakPagefileUsage)));
        Assert.Equal(0xD0, Offset(nameof(WindowsProcessSystemThreadParser.SystemProcessInformationHeader.ReadOperationCount)));
        Assert.Equal(0xF8, Offset(nameof(WindowsProcessSystemThreadParser.SystemProcessInformationHeader.OtherTransferCount)));
        Assert.Equal(0x100, WindowsProcessSystemThreadParser.ProcessHeaderSize);
    }

    [Fact]
    public void RealKernelTableRecordDescribesTheCurrentProcessWithoutAHandle()
    {
        using var buffer = new WindowsProcessInventoryBuffer();
        using var capture = WindowsProcessInventoryReader.Instance.Capture(buffer);
        var self = Assert.Single(capture.Processes, row => row.Id == Environment.ProcessId);
        var counters = self.Counters!.Value;
        using var process = Process.GetCurrentProcess();
        Assert.Equal(WindowsProcessFieldOwner.ReadCreation(process).Creation, self.Creation.Creation);
        var handleRead = new WindowsProcessBasicMetricReader().Read(Environment.ProcessId, self.Creation.Creation!.Value,
            ProcessMetricReadMask.CpuTime | ProcessMetricReadMask.Memory);
        // Read after the table: cumulative counters can only have grown since the record was taken.
        Assert.True((UInt128)counters.KernelTime100Ns + counters.UserTime100Ns <= handleRead.CpuTime100Ns);
        Assert.True(counters.PeakPrivateCommit <= handleRead.Memory!.PeakPrivateCommit);
        Assert.True(counters.WorkingSet > 0 && counters.PrivateCommit > 0 && counters.PrivateWorkingSet <= counters.WorkingSet);
        Assert.True(counters.ThreadCount >= 1); Assert.True(counters.HandleCount > 0);
        Assert.NotNull(counters.IoTiming);
    }

    private static void AssertPublishedEqualsTable(ProcessMetricFieldSnapshot snapshot, Table table, bool batch)
    {
        foreach (var row in table.Rows)
        {
            var values = snapshot.Processes.Single(process => process.ProcessId == row.Pid).Values;
            MetricInteger? Integer(string field) => values.Single(value => value.Field == field).IntegerValue;
            Assert.Equal(MetricInteger.FromUnsigned128(row.WorkingSet), Integer(WorkingSet));
            Assert.Equal(MetricInteger.FromUnsigned128(row.ReadBytes), Integer(ReadBytes));
            if (!batch) continue;
            Assert.Equal(MetricInteger.FromUnsigned128(row.Threads), Integer(Threads));
            Assert.Equal(MetricInteger.FromUnsigned128(row.Handles), Integer(Handles));
        }
    }

    private sealed class Table : IWindowsProcessInventoryBufferApi
    {
        internal sealed class Row
        {
            internal int Pid; internal long Creation; internal uint Handles, Threads;
            internal ulong Kernel, User, WorkingSet, Commit, PeakCommit, PrivateWorkingSet;
            internal ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes;
        }
        internal readonly List<Row> Rows =
        [
            new() { Pid = 11, Creation = 1100, Handles = 40, Threads = 5, Kernel = 70, User = 30, WorkingSet = 8192, Commit = 4096,
                PeakCommit = 6000, PrivateWorkingSet = 2048, ReadOps = 1, WriteOps = 2, OtherOps = 3, ReadBytes = 400, WriteBytes = 500, OtherBytes = 600 },
            new() { Pid = 22, Creation = 2200, Handles = 41, Threads = 9, Kernel = 71, User = 31, WorkingSet = 9000, Commit = 5000,
                PeakCommit = 7000, PrivateWorkingSet = 3000, ReadOps = 4, WriteOps = 5, OtherOps = 6, ReadBytes = 700, WriteBytes = 800, OtherBytes = 900 },
            new() { Pid = 33, Creation = 3300, Handles = 42, Threads = 12, Kernel = 72, User = 32, WorkingSet = 1000, Commit = 6000,
                PeakCommit = 8000, PrivateWorkingSet = 1001, ReadOps = 7, WriteOps = 8, OtherOps = 9, ReadBytes = 1000, WriteBytes = 1100, OtherBytes = 1200 }
        ];
        internal int Queries;
        internal void Advance()
        {
            foreach (var row in Rows) { row.Kernel += 10; row.User += 5; row.WorkingSet += 4096; row.ReadBytes += 200; row.Handles++; row.Threads++; }
        }
        public int Query(IntPtr buffer, uint length, out uint returned)
        {
            Queries++;
            var size = WindowsProcessSystemThreadParser.ProcessHeaderSize;
            var bytes = new byte[size * Rows.Count];
            for (var i = 0; i < Rows.Count; i++)
            {
                var row = Rows[i];
                var header = new WindowsProcessSystemThreadParser.SystemProcessInformationHeader
                {
                    NextEntryOffset = i + 1 < Rows.Count ? (uint)size : 0, NumberOfThreads = row.Threads,
                    UniqueProcessId = new(row.Pid), CreationTime = row.Creation, HandleCount = row.Handles,
                    KernelTime = (long)row.Kernel, UserTime = (long)row.User, WorkingSetPrivateSize = (long)row.PrivateWorkingSet,
                    WorkingSetSize = new(row.WorkingSet), PagefileUsage = new(row.Commit), PeakPagefileUsage = new(row.PeakCommit),
                    ReadOperationCount = (long)row.ReadOps, WriteOperationCount = (long)row.WriteOps, OtherOperationCount = (long)row.OtherOps,
                    ReadTransferCount = (long)row.ReadBytes, WriteTransferCount = (long)row.WriteBytes, OtherTransferCount = (long)row.OtherBytes
                };
                MemoryMarshal.Write(bytes.AsSpan(i * size), in header);
            }
            returned = (uint)bytes.Length;
            if (bytes.Length > length) return unchecked((int)0xC0000004);
            Marshal.Copy(bytes, 0, buffer, bytes.Length);
            return 0;
        }
        public IntPtr Resize(IntPtr buffer, int length) => Marshal.ReAllocHGlobal(buffer, new(length));
        public void Free(IntPtr buffer) => Marshal.FreeHGlobal(buffer);
    }

    private sealed class TableInventory(WindowsProcessInventoryBuffer buffer) : IWindowsProcessInventoryReader
    {
        public Process[] GetProcesses() => throw new InvalidOperationException("Kernel table rows only.");
        public WindowsProcessInventoryCapture Capture(WindowsProcessInventoryBuffer workspace) => WindowsProcessInventoryReader.Instance.Capture(buffer);
    }

    private sealed class Api(Table table) : IWindowsProcessBasicMetricApi
    {
        internal int Opens, TimeCalls, Closes, CounterCalls;
        internal readonly List<int> OpenedPids = [];
        public IntPtr Open(int processId) { Opens++; OpenedPids.Add(processId); return new(processId); }
        public bool Times(IntPtr handle, out ulong creation, out ulong kernel, out ulong user)
        {
            TimeCalls++; creation = (ulong)table.Rows.Single(row => row.Pid == handle.ToInt32()).Creation; kernel = user = 0; return true;
        }
        public ProcessMemoryObservation? Memory(IntPtr handle, bool privateWorkingSet) { CounterCalls++; return null; }
        public ProcessIoObservation? Io(IntPtr handle) { CounterCalls++; return null; }
        public uint? HandleCount(IntPtr handle) { CounterCalls++; return null; }
        public uint? ThreadCount(IntPtr handle, int processId) { CounterCalls++; return null; }
        public IReadOnlyDictionary<int, uint?> ThreadCounts(IReadOnlyDictionary<int, IntPtr> handles) { CounterCalls++; return new Dictionary<int, uint?>(); }
        public bool ThreadCountLive(IntPtr handle, int pid) { CounterCalls++; return true; }
        public string? ImagePath(IntPtr handle) => $@"C:\Apps\app{handle.ToInt32()}.exe";
        public string? UserSid(IntPtr handle) => $"S-1-5-21-{handle.ToInt32()}";
        public bool Close(IntPtr handle) { Closes++; return true; }
    }

    private sealed class Clock : TimeProvider
    {
        internal long Ticks = 1000;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() { var value = Ticks; Ticks += 5; return value; }
    }
}
