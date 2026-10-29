using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Reflection;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Infrastructure.ResourceBreakdown;
using ResourceManager.ReadWrite.ProcessFields;
using ResourceManager.ReadWrite.Windows.Processes;

namespace Resource_Manager_APP.Tests;

public sealed class ProcessCaptureSharingTests
{
    [Fact]
    public async Task ProductionOwnerUsesOneKernelTableForProcessesBirthsParentsNamesAndThreadCounts()
    {
        var api = new TableApi();
        using var buffer = new WindowsProcessInventoryBuffer(api: api);
        using var owner = WindowsProcessFieldOwner.CreateDefault();
        owner.Inventory = new Inventory(buffer);
        var fields = new FieldsApi(); owner.BasicReader = new(fields);
        bool Capture(WindowsProcessInventoryEntry process, ProcessCreationRead creation, out string sample, out TimeSpan cpu, out ProcessInstanceKey? key)
        {
            sample = $"{process.Id}:{process.ProcessName}:{process.ParentProcessId}:{process.Counters?.ThreadCount}";
            cpu = default; key = new(process.Id, creation.Creation!.Value); return true;
        }
        var inventory = owner.CaptureBatch<string>(false, new(), Capture, static _ => false);
        Assert.Equal(new[] { "10:parent:1:3", "20:child:10:7" }, inventory.Samples);
        Assert.Equal(1, api.Queries);
        var snapshot = await owner.CollectAsync(["process_all_thread_count_actual", "process_all_user_sid_actual"]);
        Assert.Equal(2, api.Queries); // One additional table for this new round.
        Assert.Equal(0, fields.ThreadSnapshots);
        Assert.Equal(2, fields.Closes); // One handle per new instance, for its SID only.
        Assert.Equal(0, fields.LiveChecks + fields.MemoryCalls);
        Assert.Equal(new[] { "3", "7" }, snapshot.Processes.Select(row => row.Values.Single(value => value.Field == "process_all_thread_count_actual").IntegerValue!.Value.ToBigInteger().ToString()));
        Assert.Same(snapshot.Processes[0].Values.Single(value => value.Field == "process_all_user_sid_actual").StringValue,
            snapshot.Processes[1].Values.Single(value => value.Field == "process_all_user_sid_actual").StringValue);
    }

    [Theory]
    [InlineData("birth")]
    [InlineData("close")]
    [InlineData("retired")]
    public async Task TableThreadCountNeedsNoHandleWhileFixedSidStillRequiresVerifiedBirthAndReleasedHandle(string fault)
    {
        var api = new TableApi(); using var buffer = new WindowsProcessInventoryBuffer(api: api);
        using var owner = WindowsProcessFieldOwner.CreateDefault(); owner.Inventory = new Inventory(buffer);
        var fields = new FieldsApi { Fault = fault }; owner.BasicReader = new(fields);
        var snapshot = await owner.CollectAsync(["process_all_thread_count_actual", "process_all_user_sid_actual"]);
        var parent = snapshot.Processes.Single(row => row.ProcessId == 10);
        var child = snapshot.Processes.Single(row => row.ProcessId == 20);
        // The thread count is the child's own table record, read in the same kernel call as its birth.
        Assert.Equal(MetricInteger.Parse("3"), parent.Values.Single(value => value.Field == "process_all_thread_count_actual").IntegerValue);
        Assert.Equal(MetricInteger.Parse("7"), child.Values.Single(value => value.Field == "process_all_thread_count_actual").IntegerValue);
        // The SID is a handle fact: a wrong birth or a failed close still empties it. Liveness is never asked.
        Assert.Equal(fault == "retired", child.Values.Single(value => value.Field == "process_all_user_sid_actual").StringValue is not null);
        Assert.Equal(1, api.Queries); Assert.Equal(0, fields.ThreadSnapshots + fields.LiveChecks); Assert.Equal(2, fields.Closes);
    }

    [Fact]
    public void MalformedOptionalImageNameDoesNotSuppressOtherProcessesOrCreationIdentities()
    {
        var api = new TableApi { BadName = true }; using var buffer = new WindowsProcessInventoryBuffer(api: api);
        using var inventory = WindowsProcessInventoryReader.Instance.Capture(buffer);
        Assert.Equal("parent", inventory.Processes[0].ProcessName);
        Assert.Equal(120, inventory.Processes[1].Creation.Creation);
        Assert.Equal(10, inventory.Processes[1].ParentProcessId);
        Assert.Null(inventory.Processes[1].Name);
        Assert.Equal(string.Empty, inventory.Processes[1].ProcessName);
        Assert.Equal(1, api.Queries);
    }

    [Fact]
    public void SamplerRetainsIndependentMetricsAndParentWhenTheNativeImageNameIsMalformed()
    {
        var api = new TableApi { BadName = true }; using var buffer = new WindowsProcessInventoryBuffer(api: api);
        var fields = new FieldsApi();
        using var subscriptions = new HostManagerSamplingSubscriptionTestFixture();
        using var sampler = new WindowsResourceBreakdownSampler(null!, null!, new StaticResourceResidualBreakdownProvider(),
            null!, null!, subscriptions.Provider, subscriptions.Owner, new PdhProcessGpuReader(),
            new Inventory(buffer), new WindowsProcessBasicMetricReader(fields));
        // Exercise the sampler's actual production capture, without unrelated hardware/catalog projection.
        var capture = typeof(WindowsResourceBreakdownSampler).GetMethod("CaptureProcessSamples", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var batch = capture.Invoke(sampler, [true, ProcessSampleDetailLevel.SmartSchedulingLite, new ProcessCpuDeltaTracker(),
            ProcessMetricReadMask.Memory | ProcessMetricReadMask.PrivateWorkingSet | ProcessMetricReadMask.ThreadCount, true])!;
        var samples = (IReadOnlyList<WindowsResourceBreakdownSampler.ProcessResourceSample>)batch.GetType().GetProperty("Samples")!.GetValue(batch)!;
        var child = Assert.Single(samples, sample => sample.ProcessId == 20);
        Assert.Equal(string.Empty, child.Name);
        Assert.Equal(120L, child.StartKey);
        Assert.Equal(10, child.ParentProcessId);
        Assert.True(child.HasWorkingSetBytes);
        Assert.True(child.HasPrivateMemoryBytes);
        Assert.True(child.HasProcessorTime);
        var basic = child.BasicMetrics;
        // Dynamic counters equal the child's table record; the handle only supplied the fixed SID.
        Assert.Equal(1000UL, basic.Memory!.WorkingSet);
        Assert.Equal(500UL, basic.Memory.PrivateCommit);
        Assert.Equal(600UL, basic.Memory.PeakPrivateCommit);
        Assert.Equal(250UL, basic.Memory.PrivateWorkingSet);
        Assert.Equal((UInt128)18, basic.CpuTime100Ns);
        Assert.Equal("S-1-5-21-1001", basic.UserSid);
        Assert.Equal(7U, basic.ThreadCount);
        Assert.Null(basic.Io); Assert.Null(basic.HandleCount); // Not requested.
        Assert.Equal(1, api.Queries); Assert.Equal(0, fields.ThreadSnapshots); Assert.Equal(2, fields.Closes);
        Assert.Equal(0, fields.MemoryCalls + fields.LiveChecks);
    }

    private sealed class Inventory(WindowsProcessInventoryBuffer buffer) : IWindowsProcessInventoryReader
    {
        public Process[] GetProcesses() => throw new InvalidOperationException("A second inventory is forbidden.");
        public IReadOnlyDictionary<int, ProcessCreationRead> ReadCreations(Process[] processes, WindowsProcessInventoryBuffer workspace)
            => throw new InvalidOperationException("A second inventory is forbidden.");
        public WindowsProcessInventoryCapture Capture(WindowsProcessInventoryBuffer workspace) => WindowsProcessInventoryReader.Instance.Capture(buffer);
    }

    private sealed class TableApi : IWindowsProcessInventoryBufferApi
    {
        internal int Queries; internal bool BadName;
        public int Query(IntPtr buffer, uint length, out uint returned)
        {
            Queries++;
            var headerSize = WindowsProcessSystemThreadParser.ProcessHeaderSize;
            var rowSize = headerSize + 32;
            var bytes = new byte[rowSize * 2];
            for (var i = 0; i < 2; i++)
            {
                var name = Encoding.Unicode.GetBytes(i == 0 ? "parent.exe" : "child.exe");
                var offset = i * rowSize;
                var header = new WindowsProcessSystemThreadParser.SystemProcessInformationHeader
                {
                    NextEntryOffset = i == 0 ? (uint)rowSize : 0,
                    UniqueProcessId = new(i == 0 ? 10 : 20), InheritedFromUniqueProcessId = new(i == 0 ? 1 : 10),
                    CreationTime = i == 0 ? 110 : 120, NumberOfThreads = i == 0 ? 3U : 7U,
                    // Child counters; the parent's differ so a row mix-up is visible.
                    WorkingSetSize = new(i == 0 ? 2000U : 1000U), PagefileUsage = new(i == 0 ? 1500U : 500U),
                    PeakPagefileUsage = new(i == 0 ? 1600U : 600U), WorkingSetPrivateSize = i == 0 ? 1250 : 250,
                    KernelTime = i == 0 ? 70 : 7, UserTime = i == 0 ? 110 : 11, HandleCount = i == 0 ? 30U : 40U,
                    ImageName = new() { Length = (ushort)name.Length, MaximumLength = (ushort)name.Length,
                        Buffer = BadName && i == 1 ? new(1) : IntPtr.Add(buffer, offset + headerSize) }
                };
                MemoryMarshal.Write(bytes.AsSpan(offset), in header);
                name.CopyTo(bytes.AsSpan(offset + headerSize));
            }
            Marshal.Copy(bytes, 0, buffer, bytes.Length); returned = (uint)bytes.Length; return 0;
        }
        public IntPtr Resize(IntPtr buffer, int length) => Marshal.ReAllocHGlobal(buffer, new(length));
        public void Free(IntPtr buffer) => Marshal.FreeHGlobal(buffer);
    }

    private sealed class FieldsApi : IWindowsProcessBasicMetricApi
    {
        internal int ThreadSnapshots, Closes, MemoryCalls, LiveChecks; internal string? Fault;
        public IntPtr Open(int pid) => new(pid);
        public bool Times(IntPtr handle, out ulong creation, out ulong kernel, out ulong user)
        { creation = (ulong)(handle.ToInt32() + 100 + (handle.ToInt32() == 20 && Fault == "birth" ? 1 : 0)); kernel = 7; user = 11; return true; }
        public ProcessMemoryObservation? Memory(IntPtr handle, bool privateWorkingSet) { MemoryCalls++; return new(9, 9, 9, privateWorkingSet ? 9UL : null); }
        public ProcessIoObservation? Io(IntPtr handle) => null;
        public uint? HandleCount(IntPtr handle) => null;
        public string? UserSid(IntPtr handle) => new string("S-1-5-21-1001".ToCharArray());
        public IReadOnlyDictionary<int, uint?> ThreadCounts(IReadOnlyDictionary<int, IntPtr> handles)
        { ThreadSnapshots++; throw new InvalidOperationException("A second inventory is forbidden."); }
        public bool ThreadCountLive(IntPtr handle, int pid) { LiveChecks++; return pid != 20 || Fault != "retired"; }
        public bool Close(IntPtr handle) { Closes++; return handle.ToInt32() != 20 || Fault != "close"; }
    }
}
