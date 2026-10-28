using System.Diagnostics;
using ResourceManager.App.Domain.ProcessAttribution;
using ResourceManager.App.Infrastructure.ResourceBreakdown;
using ResourceManager.ReadWrite.ProcessFields;
using ResourceManager.ReadWrite.Windows.Processes;

namespace Resource_Manager_APP.Tests;

public sealed class ProcessImmutableTextSharingTests
{
    private const string Command = "process_all_command_line_actual", SidField = "process_all_user_sid_actual";
    private const string Sid = "S-1-5-21-101-102-103-1001";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OriginalCurrentAndAttributionOwnersShareExactStringsInEitherCaptureOrder(bool fieldsFirst)
    {
        var api = new Api(); using var owner = WindowsProcessFieldOwner.CreateDefault(); owner.BasicReader = new(api);
        var inventory = new Inventory(); owner.Inventory = inventory;
        var cache = new ProcessInstanceCache<WindowsResourceBreakdownSampler.CachedProcessAttribution>();
        owner.ReadAttributionText = () => cache.Values.Select(entry => new ProcessInstanceText(
            new(entry.Identity.ProcessId, entry.Identity.StartKey!.Value), entry.Identity.CommandLine, entry.Identity.UserSid));
        ProcessMetricFieldSnapshot? fields = null;
        if (fieldsFirst) fields = await owner.CollectAsync([Command, SidField]);
        RuntimeProcessIdentity Attribute()
        {
            var sharing = owner.CreateTextSharing();
            return WindowsResourceBreakdownSampler.ReadRuntimeCommandLine(cache,
                new(10, null, "child", null, false, StartKey: inventory.Birth, UserSid: new string(Sid.ToCharArray())),
                () => owner.BasicReader.Read(10, inventory.Birth, ProcessMetricReadMask.CommandLine).CommandLine, sharing);
        }
        var identity = Attribute();
        if (!fieldsFirst) fields = await owner.CollectAsync([Command, SidField]);
        var row = Assert.Single(fields!.Processes);
        Assert.Same(identity.CommandLine, row.Values.Single(value => value.Field == Command).StringValue);
        Assert.Same(identity.UserSid, row.Values.Single(value => value.Field == SidField).StringValue);
        var repeated = await owner.CollectAsync([Command, SidField]);
        Assert.Same(identity.CommandLine, repeated.Processes[0].Values.Single(value => value.Field == Command).StringValue);
        Assert.Same(identity.UserSid, repeated.Processes[0].Values.Single(value => value.Field == SidField).StringValue);

        var fixedReads = (api.CommandReads, api.SidReads);
        api.Fail = true;
        var failed = await owner.CollectAsync([Command, SidField]);
        // fb6b23df: a verified instance keeps its fixed facts; no new read can fail this frame.
        Assert.Same(identity.CommandLine, failed.Processes[0].Values.Single(value => value.Field == Command).StringValue);
        Assert.Same(identity.UserSid, failed.Processes[0].Values.Single(value => value.Field == SidField).StringValue);
        Assert.Equal(fixedReads, (api.CommandReads, api.SidReads));
        api.Fail = false;
        inventory.Birth++; api.Birth++;
        var next = await owner.CollectAsync([Command, SidField]);
        Assert.NotSame(identity.CommandLine, next.Processes[0].Values.Single(value => value.Field == Command).StringValue);
        Assert.Equal(identity.CommandLine, next.Processes[0].Values.Single(value => value.Field == Command).StringValue);
        // SID equality is independent of PID reuse; command lines remain instance-bound.
        Assert.Same(identity.UserSid, next.Processes[0].Values.Single(value => value.Field == SidField).StringValue);
        cache.Prune(new HashSet<ProcessInstanceKey>());
        inventory.Empty = true; await owner.CollectAsync([Command, SidField]);
        inventory.Empty = false;
        var retired = await owner.CollectAsync([Command, SidField]);
        Assert.NotSame(identity.UserSid, retired.Processes[0].Values.Single(value => value.Field == SidField).StringValue);
    }

    private sealed class Inventory : IWindowsProcessInventoryReader
    {
        internal long Birth = 100; internal bool Empty;
        public Process[] GetProcesses() => throw new InvalidOperationException("Native rows only.");
        public WindowsProcessInventoryCapture Capture(WindowsProcessInventoryBuffer buffer)
            => new(Empty ? [] : [new(10, new(ProcessReadStatus.Current, Birth), 0, "child")]);
    }

    private sealed class Api : IWindowsProcessBasicMetricApi
    {
        internal ulong Birth = 100; internal bool Fail; internal int CommandReads, SidReads;
        public IntPtr Open(int pid) => new(pid);
        public bool Times(IntPtr handle, out ulong creation, out ulong kernel, out ulong user)
        { creation = Birth; kernel = user = 0; return true; }
        public ProcessMemoryObservation? Memory(IntPtr handle, bool privateWorkingSet) => null;
        public ProcessIoObservation? Io(IntPtr handle) => null;
        public uint? HandleCount(IntPtr handle) => null;
        public string? CommandLine(IntPtr handle) { CommandReads++; return Fail ? null : new string("child.exe --type=utility".ToCharArray()); }
        public string? UserSid(IntPtr handle) { SidReads++; return Fail ? null : new string(Sid.ToCharArray()); }
        public bool Close(IntPtr handle) => true;
    }
}
