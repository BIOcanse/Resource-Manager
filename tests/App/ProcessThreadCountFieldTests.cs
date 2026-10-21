using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.ResourceBreakdown;
using ResourceManager.App.Infrastructure.Telemetry.Etw;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.ProcessFields;
using ResourceManager.ReadWrite.Windows.Processes;
using Basic = Resource_Manager_APP.Tests.ProcessBasicFieldCollectorTests;

namespace Resource_Manager_APP.Tests;

public sealed class ProcessThreadCountFieldTests
{
    [Theory]
    [InlineData(0U)] [InlineData(7U)] [InlineData(uint.MaxValue)]
    public void SelectedThreadCountUsesVerifiedLiveHandleAndExactProcessSnapshotValue(uint count)
    {
        var api = new Api(); api.Calls.Count = count;
        var actual = new WindowsProcessBasicMetricReader(api).Read(Environment.ProcessId, (long)Basic.Birth, ProcessMetricReadMask.ThreadCount);
        Assert.Equal(count, actual.ThreadCount); Assert.Equal(2, api.Basic.TimeCalls); Assert.Equal(1, api.Basic.Closes);
        Assert.Equal(new[] { 0x101000U }, api.OpenRights); Assert.Equal(1, api.Calls.Snapshots); Assert.Equal(1, api.Calls.Closes);
        Assert.Equal(0, api.Basic.MemoryCalls + api.Basic.IoCalls + api.Basic.HandleCalls + api.Basic.ImageCalls + api.Basic.SidCalls + api.Basic.ParentCalls);
        var field = ProcessMetricFieldRegistry.Find("process_all_thread_count_actual")!;
        Assert.Equal(count.ToString(System.Globalization.CultureInfo.InvariantCulture), ProcessMetricFieldProjection.CreateBasicValue(field, actual).IntegerValue);
    }

    [Fact]
    public void ThreadSynchronizationPermissionFailurePreservesOtherSelectedFields()
    {
        var api = new Api { DenySynchronization = true };
        var actual = new WindowsProcessBasicMetricReader(api).Read(Environment.ProcessId, (long)Basic.Birth,
            ProcessMetricReadMask.ThreadCount | ProcessMetricReadMask.ImagePath);
        Assert.Null(actual.ThreadCount); Assert.Equal(api.Basic.Image, actual.ImagePath);
        Assert.Equal(new[] { 0x101000U, 0x1000U }, api.OpenRights);
        Assert.Equal(0, api.Calls.Snapshots); Assert.Equal(1, api.Basic.ImageCalls); Assert.Equal(1, api.Basic.Closes);
    }

    [Fact]
    public void NonThreadSelectionPreservesOriginalAccessAndMakesNoSnapshot()
    {
        var api = new Api();
        var actual = new WindowsProcessBasicMetricReader(api).Read(Environment.ProcessId, (long)Basic.Birth, ProcessMetricReadMask.ImagePath);
        Assert.Equal(api.Basic.Image, actual.ImagePath); Assert.Null(actual.ThreadCount);
        Assert.Equal(new[] { 0x1000U }, api.OpenRights); Assert.Equal(0, api.Calls.Snapshots);
        Assert.Equal(IntPtr.Size == 8 ? 568 : 556, Marshal.SizeOf<WindowsProcessEntry32>()); // Current x64 App target; pointer-sized heap member is retained.
        Assert.Equal(IntPtr.Size == 8 ? 28 : 20, Marshal.OffsetOf<WindowsProcessEntry32>(nameof(WindowsProcessEntry32.ThreadCount)).ToInt32());
    }

    [Theory]
    [InlineData("identity-before")] [InlineData("identity-after")] [InlineData("close")]
    public void OriginalCreationAndCloseFailuresDoNotPublishThreadCount(string failure)
    {
        var api = new Api(); api.Basic.Fault = failure;
        var actual = new WindowsProcessBasicMetricReader(api).Read(Environment.ProcessId, (long)Basic.Birth, ProcessMetricReadMask.ThreadCount);
        Assert.Null(actual.ThreadCount); Assert.Equal(1, api.Basic.Closes);
        if (failure == "identity-before") Assert.Equal(0, api.Calls.Snapshots);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void RetiredWrongPidPermissionSnapshotFailureOrCloseFailureReturnsNull(int failure)
    {
        var calls = new Calls();
        switch (failure)
        {
            case 0: calls.Pid++; break;
            case 1: calls.FirstState = 0; break;
            case 2: calls.SecondState = 0; break;
            case 3: calls.FirstState = uint.MaxValue; break;
            case 4: calls.InvalidSnapshot = true; break;
            case 5: calls.CloseSuccess = false; break;
        }
        Assert.Null(new WindowsProcessThreadCountQuery(calls).Read(new(42), Environment.ProcessId));
        Assert.Equal(failure is 2 or 5 ? 1 : 0, calls.Closes);
    }

    [Fact]
    public void NonTerminatingOrMissingPidEnumerationIsBoundedAndAlwaysCloses()
    {
        var calls = new Calls { EnumeratedPid = 1, Endless = true };
        Assert.Null(new WindowsProcessThreadCountQuery(calls).Read(new(42), Environment.ProcessId));
        Assert.Equal(WindowsProcessThreadCountQuery.MaximumEntries, calls.NextCalls); Assert.Equal(1, calls.Closes);
        calls = new() { EnumeratedPid = 1 };
        Assert.Null(new WindowsProcessThreadCountQuery(calls).Read(new(42), Environment.ProcessId)); Assert.Equal(1, calls.Closes);
    }

    [Fact]
    public async Task CanonicalDirectBorrowAndPublicationsUseOriginalCurrentWithoutCpuOrEtw()
    {
        var api = new Api(); using var owner = WindowsProcessFieldOwner.CreateDefault(new NoEtw());
        owner.Inventory = new Inventory(); owner.BasicReader = new(api);
        await owner.CollectAsync(["process_all_thread_count_actual"]);
        Assert.Equal(1, api.Calls.Snapshots);
        var catalog = owner.Backend.Catalog;
        var field = catalog.Fields.Single(item => item.Field.FieldName == "process_all_thread_count_actual").Field;
        using var borrow = owner.BorrowSubscriptionCapture(); borrow.Bind([field]); borrow.SynchronizeDemand([field]);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var signal = owner.Publications.Subscribe([ProcessMetricDatasetIds.BasicMetrics]);
        api.Calls.Count = 9;
        var reads = await owner.Backend.CollectAsync([field], timeout.Token); await signal.WaitAsync(timeout.Token);
        Assert.Equal("9", reads.Single().Value?.Integer); Assert.Equal(reads.Single(), owner.Backend.ReadCached(field));
        api.Calls.Count = 0;
        var scoped = await borrow.CollectAsync([field], new Permit(), timeout.Token); await signal.WaitAsync(timeout.Token);
        Assert.True(scoped.Committed); Assert.Equal("0", scoped.Readings.Single().Value?.Integer);
        Assert.Null(owner.Current.ReadPart(ProcessMetricDatasetIds.BasicMetrics)!.Sources.Single().WindowMilliseconds);
        var cpuFirst = await owner.CollectAsync(["process_all_cpu_usage_actual"], timeout.Token);
        Assert.Null(cpuFirst.Processes.Single().Values.Single().NumericValue);
    }

    [Fact]
    public void MultiPidBatchMakesOneSnapshotAndIsolatesIdentityAndSynchronizationFailures()
    {
        var api = new BatchApi();
        var reader = new WindowsProcessBasicMetricReader(api);
        var mask = ProcessMetricReadMask.ThreadCount | ProcessMetricReadMask.CpuTime | ProcessMetricReadMask.ImagePath;
        var rows = reader.ReadBatch(Enumerable.Range(1, 6).Select(pid => new ProcessBasicMetricRequest(pid, 100 + pid, mask)).ToArray());
        Assert.Equal(1, api.Calls.Snapshots); Assert.Equal(1, api.Calls.Closes);
        Assert.Equal(6, api.Opened.Count); Assert.Equal(6, api.Closed.Count);
        Assert.Equal(11U, rows[0].ThreadCount); Assert.Equal("image-1", rows[0].ImagePath);
        Assert.Null(rows[1].ThreadCount); Assert.Null(rows[1].CpuTime100Ns); Assert.Null(rows[1].ImagePath); // Incorrect initial birth.
        Assert.Null(rows[2].ThreadCount); Assert.Null(rows[2].CpuTime100Ns); Assert.Null(rows[2].ImagePath); // Incorrect final birth.
        Assert.Null(rows[3].ThreadCount); Assert.Equal((UInt128)12, rows[3].CpuTime100Ns); Assert.Equal("image-4", rows[3].ImagePath); // SYNCHRONIZE denied.
        Assert.Null(rows[4].ThreadCount); Assert.Equal((UInt128)12, rows[4].CpuTime100Ns); Assert.Equal("image-5", rows[4].ImagePath); // Retired after snapshot.
        Assert.Equal(16U, rows[5].ThreadCount); Assert.Equal("image-6", rows[5].ImagePath);
        Assert.All(api.TimeCalls, pair => Assert.Equal(pair.Key == 2 ? 1 : 2, pair.Value));
    }

    [Fact]
    public void BatchSnapshotSourceFailureRetainsOtherCategoriesAndReleasesEveryHandle()
    {
        var api = new BatchApi(); api.Calls.ThrowSnapshot = true;
        var rows = new WindowsProcessBasicMetricReader(api).ReadBatch([
            new(1, 101, ProcessMetricReadMask.ThreadCount | ProcessMetricReadMask.ImagePath),
            new(6, 106, ProcessMetricReadMask.ThreadCount | ProcessMetricReadMask.CpuTime)]);
        Assert.Equal(1, api.Calls.Snapshots); Assert.Equal(0, api.Calls.Closes);
        Assert.Equal(new[] { 1, 6 }, api.Closed.Order().ToArray());
        Assert.Null(rows[0].ThreadCount); Assert.Equal("image-1", rows[0].ImagePath);
        Assert.Null(rows[1].ThreadCount); Assert.Equal((UInt128)12, rows[1].CpuTime100Ns);
    }

    [Fact]
    public void UnselectedBatchNeverRequestsSnapshotOrSynchronization()
    {
        var api = new BatchApi();
        var rows = new WindowsProcessBasicMetricReader(api).ReadBatch([
            new(1, 101, ProcessMetricReadMask.ImagePath), new(6, 106, ProcessMetricReadMask.CpuTime)]);
        Assert.Equal(0, api.Calls.Snapshots); Assert.Equal(0, api.ThreadBatchCalls);
        Assert.Equal(2, api.Closed.Count); Assert.Equal("image-1", rows[0].ImagePath);
        Assert.Equal((UInt128)12, rows[1].CpuTime100Ns);
    }

    [Fact]
    public async Task OriginalOwnerCurrentClearsOnlyFailedThreadCategoryAndRetainsMixedImage()
    {
        var api = new Api(); using var owner = WindowsProcessFieldOwner.CreateDefault(new NoEtw());
        owner.Inventory = new Inventory(); owner.BasicReader = new(api);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var names = new[] { "process_all_thread_count_actual", "process_all_image_path_actual" };
        await owner.CollectAsync(names, timeout.Token);
        var fields = owner.Backend.Catalog.Fields.Where(item => names.Contains(item.Field.FieldName)).Select(item => item.Field).ToArray();
        Assert.Equal(1, api.Calls.Snapshots);
        api.Calls.SecondState = 0;
        await owner.CollectAsync(names, timeout.Token);
        Assert.Equal(1, api.Calls.Snapshots);
        var thread = fields.Single(field => field.FieldName == names[0]);
        var image = fields.Single(field => field.FieldName == names[1]);
        Assert.Null(owner.Backend.ReadCached(thread).Value);
        Assert.NotNull(owner.Backend.ReadCached(image).Value);
        Assert.Equal(2, api.Basic.ImageCalls);
        api.Basic.Fault = "identity-before";
        await owner.CollectAsync(names, timeout.Token);
        Assert.Equal(1, api.Calls.Snapshots);
        Assert.Null(owner.Backend.ReadCached(thread).Value); Assert.Null(owner.Backend.ReadCached(image).Value);
        Assert.Equal(3, api.Basic.Closes);
    }

    private sealed class BatchApi : IWindowsProcessBasicMetricApi
    {
        internal readonly HashSet<int> Opened = [], Closed = [];
        internal readonly Dictionary<int, int> TimeCalls = [];
        internal readonly BatchCalls Calls;
        internal int ThreadBatchCalls;
        internal BatchApi() => Calls = new(this);
        public IntPtr Open(int pid) { Opened.Add(pid); return new(pid); }
        public bool Times(IntPtr handle, out ulong creation, out ulong kernel, out ulong user)
        {
            var pid = handle.ToInt32(); var count = TimeCalls.GetValueOrDefault(pid) + 1; TimeCalls[pid] = count;
            creation = (ulong)(100 + pid + (pid == 2 || pid == 3 && count == 2 ? 1 : 0)); kernel = 5; user = 7; return true;
        }
        public ProcessMemoryObservation? Memory(IntPtr handle, bool privateWorkingSet) => throw new InvalidOperationException("Not selected");
        public ProcessIoObservation? Io(IntPtr handle) => throw new InvalidOperationException("Not selected");
        public uint? HandleCount(IntPtr handle) => throw new InvalidOperationException("Not selected");
        public string? ImagePath(IntPtr handle) => $"image-{handle.ToInt32()}";
        public IReadOnlyDictionary<int, uint?> ThreadCounts(IReadOnlyDictionary<int, IntPtr> handles)
        { ThreadBatchCalls++; return new WindowsProcessThreadCountQuery(Calls).ReadBatch(handles); }
        public bool ThreadCountLive(IntPtr handle, int pid) => Calls.ProcessId(handle) == (uint)pid && Calls.State(handle) == 0x102;
        public bool Close(IntPtr handle) { Assert.True(Closed.Add(handle.ToInt32())); return true; }
    }
    private sealed class BatchCalls(BatchApi api) : IWindowsProcessThreadCountCalls
    {
        internal int Snapshots, Closes; internal bool ThrowSnapshot; private uint row;
        public uint ProcessId(IntPtr process) => (uint)process.ToInt32();
        public uint State(IntPtr process) => process.ToInt32() == 4 ? uint.MaxValue : process.ToInt32() == 5 && Snapshots != 0 ? 0U : 0x102U;
        public IntPtr Snapshot()
        {
            Snapshots++;
            // Every opened instance has been creation-verified before the single snapshot.
            Assert.Equal(api.Opened.Count, api.TimeCalls.Count); Assert.All(api.TimeCalls.Values, count => Assert.Equal(1, count));
            Assert.Empty(api.Closed);
            if (ThrowSnapshot) throw new System.ComponentModel.Win32Exception(5);
            return new(84);
        }
        public bool First(IntPtr snapshot, ref WindowsProcessEntry32 entry) { row = 1; Set(ref entry); return true; }
        public bool Next(IntPtr snapshot, ref WindowsProcessEntry32 entry) { row++; Set(ref entry); return row <= 6; }
        private void Set(ref WindowsProcessEntry32 entry) { entry.ProcessId = row; entry.ThreadCount = 10 + row; }
        public bool Close(IntPtr snapshot) { Closes++; return true; }
    }

    private sealed class Api : IWindowsProcessBasicMetricApi
    {
        internal readonly Basic.Api Basic = new(); internal readonly Calls Calls = new();
        internal readonly List<uint> OpenRights = []; internal bool DenySynchronization;
        public IntPtr Open(int pid) => Basic.Open(pid);
        public IntPtr Open(int pid, ProcessMetricReadMask mask) => WindowsProcessBasicMetricNativeApi.OpenSelected(pid, mask,
            (rights, inherit, target) => { Assert.False(inherit); OpenRights.Add(rights);
                if (DenySynchronization && (rights & 0x100000) != 0) { Calls.FirstState = uint.MaxValue; return IntPtr.Zero; }
                return Basic.Open(target); });
        public bool Times(IntPtr handle, out ulong creation, out ulong kernel, out ulong user) => Basic.Times(handle, out creation, out kernel, out user);
        public ProcessMemoryObservation? Memory(IntPtr handle, bool privateWorkingSet) => Basic.Memory(handle, privateWorkingSet);
        public ProcessIoObservation? Io(IntPtr handle) => Basic.Io(handle);
        public uint? HandleCount(IntPtr handle) => Basic.HandleCount(handle);
        public string? ImagePath(IntPtr handle) => Basic.ImagePath(handle);
        public IReadOnlyDictionary<int, uint?> ThreadCounts(IReadOnlyDictionary<int, IntPtr> handles)
            => new WindowsProcessThreadCountQuery(Calls).ReadBatch(handles);
        public bool ThreadCountLive(IntPtr handle, int pid) => Calls.ProcessId(handle) == (uint)pid && Calls.State(handle) == 0x102;
        public uint? ThreadCount(IntPtr handle, int pid) => new WindowsProcessThreadCountQuery(Calls).Read(handle, pid);
        public bool Close(IntPtr handle) => Basic.Close(handle);
    }
    private sealed class Calls : IWindowsProcessThreadCountCalls
    {
        internal uint Pid = (uint)Environment.ProcessId, EnumeratedPid = (uint)Environment.ProcessId, Count = 7;
        internal uint FirstState = 0x102, SecondState = 0x102; internal bool InvalidSnapshot, CloseSuccess = true, Endless;
        internal int Snapshots, Closes, NextCalls;
        public uint ProcessId(IntPtr process) => Pid;
        public uint State(IntPtr process) => Snapshots == 0 ? FirstState : SecondState;
        public IntPtr Snapshot() { Snapshots++; return new(InvalidSnapshot ? -1 : 84); }
        public bool First(IntPtr snapshot, ref WindowsProcessEntry32 entry) { entry.ProcessId = EnumeratedPid; entry.ThreadCount = Count; return true; }
        public bool Next(IntPtr snapshot, ref WindowsProcessEntry32 entry) { NextCalls++; return Endless; }
        public bool Close(IntPtr snapshot) { Closes++; return CloseSuccess; }
    }
    private sealed class Inventory : IWindowsProcessInventoryReader { public Process[] GetProcesses() => [Process.GetCurrentProcess()]; }
    private sealed class NoEtw : IKernelEtwSessionBroker
    {
        public IKernelEtwSubscription Subscribe(KernelEtwSubscriptionRequest request) => throw new InvalidOperationException("Thread count must not start ETW.");
        public KernelEtwSessionSnapshot GetSnapshot() => throw new InvalidOperationException("Unexpected ETW query.");
    }
    private sealed class Permit : IMetricPublicationPermit
    {
        public ulong WorkspaceIncarnation => 1; public ulong ConfigurationGeneration => 1; public bool IsActive => true;
        public bool TryPublish(Action commit) { commit(); return true; }
    }
}
