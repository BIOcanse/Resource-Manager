using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using ResourceManager.App.Infrastructure.ResourceBreakdown;
using System.Runtime.InteropServices;
using System.Text;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.Telemetry.Etw;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.ProcessFields;
using ResourceManager.ReadWrite.Windows.Processes;
using Basic = Resource_Manager_APP.Tests.ProcessBasicFieldCollectorTests;

namespace Resource_Manager_APP.Tests;

public sealed class ProcessCommandLineFieldTests
{
    private const string Name = "process_all_command_line_actual";
    [Theory]
    [InlineData("")] [InlineData("  ")] [InlineData("\"C:\\Program Files\\app.exe\" --arg=\"quoted value\"")]
    [InlineData("app.exe 中文 😀")] [InlineData("a\0b")]
    public void OriginalRawQueryPreservesExactLengthUtf16AndReleasesBuffer(string text)
    {
        var calls = new Calls { Text = text };
        Assert.Equal(text, WindowsProcessCommandLineQuery.Read(new(42), calls));
        Assert.Equal(2, calls.Queries); Assert.Equal(1, calls.Allocations); Assert.Equal(1, calls.Frees);
        Assert.Equal((text.Length * 2) + Marshal.SizeOf<WindowsProcessCommandLineQuery.UnicodeString>() + 2, calls.AllocatedBytes);
    }
    [Theory]
    [InlineData("small")] [InlineData("oversized")] [InlineData("denied")]
    [InlineData("short-return")] [InlineData("large-return")] [InlineData("odd")]
    [InlineData("prefix-pointer")] [InlineData("outside-pointer")] [InlineData("negative-pointer")]
    [InlineData("maximum-short")] [InlineData("payload-long")] [InlineData("throw-query")]
    public void MalformedOrDeniedRawReadIsBoundedAndAlwaysReleasesOwnedBuffer(string fault)
    {
        var calls = new Calls { Fault = fault };
        if (fault == "throw-query") Assert.Throws<Win32Exception>(() => WindowsProcessCommandLineQuery.Read(new(42), calls));
        else
        {
            var result = WindowsProcessCommandLineQuery.ReadResult(new(42), calls);
            Assert.Equal(ProcessReadStatus.Unavailable, result.Status);
            Assert.Null(result.Value);
            Assert.NotNull(result.Reason);
        }
        Assert.Equal(fault is "small" or "oversized" ? 0 : 1, calls.Allocations);
        Assert.Equal(calls.Allocations, calls.Frees);
    }
    [Theory]
    [InlineData("exit-before")]
    [InlineData("exit-during")]
    [InlineData("exit-invalid-length")]
    public void ProcessExitIsTypedAbsenceWithoutThrowing(string fault)
    {
        var calls = new Calls { Fault = fault };
        var result = WindowsProcessCommandLineQuery.ReadResult(new(42), calls);
        Assert.Equal(ProcessReadStatus.Exited, result.Status);
        Assert.Null(result.Value);
        Assert.Equal("process-exited", result.Reason);
        Assert.Equal(calls.Allocations, calls.Frees);
    }
    [Fact]
    public void RawQueryPreservesIsolatedSurrogateCodeUnitWithoutValidationOrReplacement()
    {
        var calls = new Calls { Fault = "raw-surrogate" };
        var actual = WindowsProcessCommandLineQuery.Read(new(42), calls);
        Assert.Equal(1, actual.Length); Assert.Equal((char)0xD800, actual[0]);
        Assert.Equal(2, calls.Queries); Assert.Equal(1, calls.Allocations); Assert.Equal(1, calls.Frees);
    }
    [Theory]
    [InlineData("open")] [InlineData("identity-before")] [InlineData("close")]
    public void OriginalHandleIdentityOrCloseGateMakesCommandLineUnavailable(string fault)
    {
        var api = new Api(); api.Basic.Fault = fault;
        var value = new WindowsProcessBasicMetricReader(api).Read(Environment.ProcessId, (long)Basic.Birth, ProcessMetricReadMask.CommandLine);
        Assert.Null(value.CommandLine); Assert.Equal(fault == "open" ? 0 : 1, api.Basic.Closes);
        Assert.Equal(fault is "open" or "identity-before" ? 0 : 1, api.CommandCalls);
    }
    [Fact]
    public void SelectedOnlyAndMixedCategoryFailureKeepOriginalNonThreadPath()
    {
        var api = new Api(); var reader = new WindowsProcessBasicMetricReader(api);
        var value = reader.Read(Environment.ProcessId, (long)Basic.Birth, ProcessMetricReadMask.CommandLine);
        Assert.Equal(api.Raw.Text, value.CommandLine); Assert.Equal(1, api.Basic.TimeCalls); Assert.Equal(1, api.Basic.Closes);
        Assert.Equal(0, api.Basic.MemoryCalls + api.Basic.IoCalls + api.Basic.HandleCalls + api.Basic.ImageCalls + api.Basic.SidCalls + api.Basic.ParentCalls);
        Assert.Equal(1, api.CommandCalls);
        api.Raw.Fault = "denied";
        value = reader.Read(Environment.ProcessId, (long)Basic.Birth, ProcessMetricReadMask.CommandLine | ProcessMetricReadMask.ImagePath);
        Assert.Null(value.CommandLine); Assert.Equal(api.Basic.Image, value.ImagePath);
        reader.Read(Environment.ProcessId, (long)Basic.Birth, ProcessMetricReadMask.ImagePath);
        Assert.Equal(2, api.CommandCalls); Assert.Equal(3, api.Basic.Closes);
        Assert.Equal(2, api.Raw.Frees);
    }
    [Fact]
    public async Task HostCanonicalPathKeepsFixedEmptyTextWithoutRequeryingOrAttribution()
    {
        var api = new Api(); api.Raw.Text = "";
        using var fixture = new Resource_Manager_APP.Tests.ProcessNetworkFieldCollectorTests.Fixture(
            new Resource_Manager_APP.Tests.ProcessNetworkFieldCollectorTests.NetworkReader(_ => throw new InvalidOperationException("Unexpected ETW")),
            null, new WindowsProcessBasicMetricReader(api));
        var request = Basic.Request(Name);
        var first = await fixture.Sampler.CaptureSnapshotAsync(request, default);
        Assert.Equal("", Basic.Value(first, Name).StringValue);
        Assert.Null(first.ProcessFields!.WindowMilliseconds); Assert.Equal(1, api.CommandCalls);
        api.Raw.Fault = "denied";
        var failed = await fixture.Sampler.CaptureSnapshotAsync(request, default);
        Assert.Equal("", Basic.Value(failed, Name).StringValue);
        Assert.Equal(0, api.Basic.MemoryCalls + api.Basic.IoCalls + api.Basic.HandleCalls + api.Basic.ImageCalls + api.Basic.SidCalls + api.Basic.ParentCalls);
        Assert.Equal(1, api.Basic.Closes);
    }
    [Fact]
    public async Task DirectBorrowPublicationFailureAndRetirementUseSameOriginalCurrent()
    {
        var api = new Api(); var inventory = new Inventory(); using var owner = Owner(api, inventory);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await owner.CollectAsync([Name], timeout.Token);
        Assert.Equal(20, (int)ProcessMetricFieldCode.CommandLine); Assert.Equal(1024, (int)ProcessMetricReadMask.CommandLine);
        var field = owner.Backend.Catalog.Fields.Single(item => item.Field.FieldName == Name).Field;
        using var borrow = owner.BorrowSubscriptionCapture(); borrow.Bind([field]); borrow.SynchronizeDemand([field]);
        using var signal = owner.Publications.Subscribe([ProcessMetricDatasetIds.BasicMetrics]);
        api.Raw.Text = "";
        var direct = await owner.Backend.CollectAsync([field], timeout.Token); await signal.WaitAsync(timeout.Token);
        Assert.Equal("app.exe --arg", direct.Single().Value!.Text); Assert.Equal(direct.Single(), owner.Backend.ReadCached(field));
        api.Raw.Fault = "denied";
        var failure = await borrow.CollectAsync([field], new Permit(), timeout.Token); await signal.WaitAsync(timeout.Token);
        Assert.True(failure.Committed); Assert.Equal("app.exe --arg", failure.Readings.Single().Value!.Text); Assert.Equal("app.exe --arg", owner.Backend.ReadCached(field).Value!.Text);
        api.Raw.Fault = ""; inventory.Empty = true;
        var retired = await borrow.CollectAsync([field], new Permit(), timeout.Token); await signal.WaitAsync(timeout.Token);
        Assert.True(retired.Committed); Assert.Equal(field, retired.Readings.Single().Field); Assert.Null(retired.Readings.Single().Value);
        Assert.Empty(owner.Backend.Catalog.Devices); Assert.Equal(1, api.CommandCalls);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task RejectedPermitOrMidCaptureCancellationRetainsOriginalTextCurrent(bool cancel)
    {
        var api = new Api(); var inventory = new Inventory(); using var owner = Owner(api, inventory);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await owner.CollectAsync([Name], timeout.Token);
        var field = owner.Backend.Catalog.Fields.Single(item => item.Field.FieldName == Name).Field;
        using var borrow = owner.BorrowSubscriptionCapture(); borrow.Bind([field]);
        var before = owner.Backend.ReadCached(field); api.Raw.Text = "new";
        using var cancellation = new CancellationTokenSource();
        api.Creation = Basic.Birth + 1; inventory.Birth = (long)api.Creation.Value;
        if (cancel) api.AfterCommand = cancellation.Cancel;
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => borrow.CollectAsync([field], new Permit(), cancellation.Token).AsTask());
        else Assert.False((await borrow.CollectAsync([field], new Permit { Active = false }, timeout.Token)).Committed);
        Assert.Equal(before, owner.Backend.ReadCached(field)); Assert.Equal(2, api.Basic.Closes);
    }
    private static WindowsProcessFieldOwner Owner(Api api, Inventory inventory)
    { var owner = WindowsProcessFieldOwner.CreateDefault(new NoEtw()); owner.BasicReader = new(api); owner.Inventory = inventory; return owner; }
    [Theory]
    [InlineData("small")]
    [InlineData("denied")]
    public async Task UnreadableInstanceIsNotRetriedByHostAndNewInstanceCanRead(string fault)
    {
        var api = new Api(); api.Raw.Fault = fault;
        var reader = new WindowsProcessBasicMetricReader(api);
        using var fixture = new ProcessNetworkFieldCollectorTests.Fixture(
            new ProcessNetworkFieldCollectorTests.NetworkReader(_ => throw new InvalidOperationException("Unexpected ETW")), null, reader);
        for (var i = 0; i < 20; i++)
        {
            var sample = await fixture.Sampler.CaptureSnapshotAsync(Basic.Request(Name), default);
            Assert.Null(Basic.Value(sample, Name).StringValue);
        }
        Assert.Equal(1, api.CommandCalls);
        Assert.Equal(1, api.Basic.Opens);
        api.Raw.Fault = "";
        Assert.Equal(api.Raw.Text, reader.Read(Environment.ProcessId, (long)Basic.Birth, ProcessMetricReadMask.CommandLine).CommandLine);
        Assert.Equal(2, api.CommandCalls);
        api.Creation = Basic.Birth + 1;
        Assert.Equal(api.Raw.Text, reader.Read(Environment.ProcessId, (long)api.Creation.Value, ProcessMetricReadMask.CommandLine).CommandLine);
        Assert.Equal(3, api.CommandCalls);
    }

    private sealed class Api : IWindowsProcessBasicMetricApi
    {
        internal readonly Basic.Api Basic = new(); internal readonly Calls Raw = new(); internal int CommandCalls; internal ulong? Creation; internal Action? AfterCommand;
        public IntPtr Open(int pid) => Basic.Open(pid);
        public bool Times(IntPtr handle, out ulong creation, out ulong kernel, out ulong user)
        { var result = Basic.Times(handle, out creation, out kernel, out user); creation = Creation ?? creation; return result; }
        public ProcessMemoryObservation? Memory(IntPtr handle, bool privateWorkingSet) => Basic.Memory(handle, privateWorkingSet);
        public ProcessIoObservation? Io(IntPtr handle) => Basic.Io(handle);
        public uint? HandleCount(IntPtr handle) => Basic.HandleCount(handle);
        public string? ImagePath(IntPtr handle) => Basic.ImagePath(handle);
        public string? CommandLine(IntPtr handle) { CommandCalls++; try { return WindowsProcessCommandLineQuery.Read(handle, Raw); } finally { AfterCommand?.Invoke(); } }
        public IReadOnlyDictionary<int, uint?> ThreadCounts(IReadOnlyDictionary<int, IntPtr> handles) => throw new Xunit.Sdk.XunitException("Command line must not request thread snapshot");
        public bool Close(IntPtr handle) => Basic.Close(handle);
    }
    private sealed class Calls : IWindowsProcessCommandLineCalls
    {
        internal string Text = "app.exe --arg"; internal string Fault = ""; internal int Queries, Allocations, Frees, AllocatedBytes;
        public bool IsExited(IntPtr process) => Fault == "exit-invalid-length";
        public int Query(IntPtr process, int kind, IntPtr buffer, int length, out int returned)
        {
            Assert.Equal(new IntPtr(42), process); Assert.Equal(60, kind); Queries++;
            var header = Marshal.SizeOf<WindowsProcessCommandLineQuery.UnicodeString>();
            var bytes = Fault == "raw-surrogate" ? new byte[] { 0, 0xD8 } : Encoding.Unicode.GetBytes(Text);
            returned = header + bytes.Length + 2;
            if (Fault == "exit-before" || Fault == "exit-during" && buffer != IntPtr.Zero)
                return unchecked((int)0xC000010A);
            if (buffer == IntPtr.Zero) { if (Fault is "small" or "exit-invalid-length") returned = header - 1; if (Fault == "oversized") returned = 65536; return unchecked((int)0xC0000004); }
            Assert.InRange(length, header, ushort.MaxValue);
            if (Fault == "throw-query") throw new Win32Exception(5);
            if (Fault == "denied") return unchecked((int)0xC0000022);
            var value = new WindowsProcessCommandLineQuery.UnicodeString { Length = (ushort)bytes.Length, MaximumLength = (ushort)(bytes.Length + 2), Buffer = IntPtr.Add(buffer, header) };
            if (Fault == "odd") value.Length = 1;
            if (Fault == "maximum-short") value.MaximumLength = 0;
            if (Fault == "payload-long") value.Length = value.MaximumLength = ushort.MaxValue - 1;
            if (Fault == "prefix-pointer") value.Buffer = buffer;
            if (Fault == "outside-pointer") value.Buffer = IntPtr.Add(buffer, length + 2);
            if (Fault == "negative-pointer") value.Buffer = IntPtr.Subtract(buffer, 2);
            if (Fault == "short-return") returned = header - 1;
            if (Fault == "large-return") returned = length + 1;
            Marshal.StructureToPtr(value, buffer, false); Marshal.Copy(bytes, 0, IntPtr.Add(buffer, header), bytes.Length);
            return 0;
        }
        public IntPtr Allocate(int length) { Allocations++; AllocatedBytes = length; return Marshal.AllocHGlobal(length); }
        public void Free(IntPtr buffer) { Frees++; Marshal.FreeHGlobal(buffer); }
    }
    private sealed class Inventory : IWindowsProcessInventoryReader
    {
        internal bool Empty;
        internal long Birth = (long)Basic.Birth;
        public Process[] GetProcesses() => throw new InvalidOperationException();
        public WindowsProcessInventoryCapture Capture(WindowsProcessInventoryBuffer buffer)
            => new(Empty ? [] : [new(Environment.ProcessId, new(ProcessReadStatus.Current, Birth), 0, "fixture")]);
    }
    private sealed class NoEtw : IKernelEtwSessionBroker
    { public IKernelEtwSubscription Acquire(KernelEtwSubscriptionRequest request) => throw new Xunit.Sdk.XunitException("Unexpected ETW"); public KernelEtwSessionSnapshot GetSnapshot() => throw new Xunit.Sdk.XunitException("Unexpected ETW"); }
    private sealed class Permit : IMetricPublicationPermit
    { internal bool Active = true; public ulong WorkspaceIncarnation => 1; public ulong ConfigurationGeneration => 1; public bool IsActive => Active; public bool TryPublish(Action commit) { if (!Active) return false; commit(); return true; } }
}
