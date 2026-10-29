using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using ResourceManager.ReadWrite.Windows.Gpu.Control;

namespace Resource_Manager_APP.Tests;

public sealed class IntelGraphicsControlSessionTests
{
    private const ulong Key = 0x800000001;
    private static readonly IntegratedGpuControlTarget Target = new("intel", IntegratedGpuControlRoute.IntelIntegrated, Key);
    private static readonly IntegratedGpuControlCapability Capability = new(IntegratedGpuControlField.CoreClockOffset, new(-200, 200, 1, "MHz", 0));
    private static IntegratedGpuControlResource Resource(Sdk sdk, Action<Exception?, IntegratedGpuControlMessage, IReadOnlyList<object?>>? warning = null)
        => new(null, new IntelGraphicsControlBridge(sdk.Functions),
            new(Path.Combine(Path.GetTempPath(), "unused-intel-baseline.json"), _ => Task.FromResult(true)), warning: warning);

    [Fact]
    public void IdleAndMissingIdentityNeverCallTheLibrary()
    {
        var sdk = new Sdk();
        var bridge = new IntelGraphicsControlBridge(sdk.Functions);
        using (Resource(sdk)) { }
        Assert.Equal(IntelGraphicsControlProbe.TargetMissing, bridge.Probe(null));
        Assert.Equal(IntelGraphicsControlUnreadable.TargetMissing, bridge.ReadFrequencyOffsetMhz(0).Unreadable);
        Assert.Equal(IntelGraphicsControlWriteFailure.TargetMissing, bridge.WriteFrequencyOffsetMhz(null, 20).Failure);
        Assert.Empty(sdk.Events);
        Assert.Equal(0, sdk.Active);
    }

    [Theory]
    [InlineData("probe")] [InlineData("read")] [InlineData("write")]
    public void EachOperationOpensAndClosesExactlyOneSession(string operation)
    {
        var sdk = new Sdk();
        var bridge = new IntelGraphicsControlBridge(sdk.Functions);
        switch (operation)
        {
            case "probe": Assert.Equal(IntelGraphicsControlProbe.Ready, bridge.Probe(Key)); break;
            case "read": Assert.Equal(new IntelGraphicsControlRead(15), bridge.ReadFrequencyOffsetMhz(Key)); break;
            case "write": Assert.Equal(new IntelGraphicsControlWrite(0, 45.5), bridge.WriteFrequencyOffsetMhz(Key, 45.5)); break;
        }
        Assert.Equal(1, sdk.Opens); Assert.Equal(sdk.Opens, sdk.Closes); Assert.Equal(0, sdk.Active);
        Assert.Equal("init", sdk.Events.First()); Assert.Equal("close", sdk.Events.Last());
        Assert.Equal(operation == "write" ? 1 : 0, sdk.Events.Count(e => e == "waiver"));
        Assert.Equal(operation == "probe" ? 0 : 1, sdk.Events.Count(e => e == "read"));
        if (operation == "write") Assert.Equal(new[] { "waiver", "set", "read", "close" }, sdk.Events.TakeLast(4));
    }

    [Fact]
    public async Task SequentialProbeReadWriteAndRestoreLeaveNoIdleSession()
    {
        var sdk = new Sdk(); using var resource = Resource(sdk);
        Assert.True(resource.Probe(Target, Capability).CanWrite); Assert.Equal(0, sdk.Active);
        Assert.Equal(15, (await resource.ReadAsync(Target, Capability))!.Number); Assert.Equal(0, sdk.Active);
        Assert.True((await resource.WriteAsync(Target, Capability, new(Capability.Field, 45.5))).ReadbackVerified); Assert.Equal(0, sdk.Active);
        Assert.True(await resource.RestoreAsync(Target, Capability)); Assert.Equal(0, sdk.Offset);
        Assert.Equal(4, sdk.Opens); Assert.Equal(4, sdk.Closes); Assert.Equal(0, sdk.Active);
    }

    [Theory]
    [InlineData("init")] [InlineData("target")] [InlineData("read")]
    public void ProbeAndReadDistinguishDriverTargetAndUnreadableFailures(string failure)
    {
        var sdk = new Sdk { Failure = failure }; var bridge = new IntelGraphicsControlBridge(sdk.Functions);
        Assert.Equal(failure switch
        {
            "init" => IntelGraphicsControlProbe.DriverMissing,
            "target" => IntelGraphicsControlProbe.TargetMissing,
            _ => IntelGraphicsControlProbe.Ready
        }, bridge.Probe(Key));
        Assert.Equal(failure switch
        {
            "init" => IntelGraphicsControlUnreadable.DriverMissing,
            "target" => IntelGraphicsControlUnreadable.TargetMissing,
            _ => IntelGraphicsControlUnreadable.ReadingMissing
        }, bridge.ReadFrequencyOffsetMhz(Key).Unreadable);
        Assert.DoesNotContain("waiver", sdk.Events);
        Assert.Equal(failure == "init" ? 0 : 2, sdk.Opens);
        Assert.Equal(sdk.Opens, sdk.Closes); Assert.Equal(0, sdk.Active);
    }

    [Fact]
    public void AlreadyCancelledOperationsDoNotOpenTheLibrary()
    {
        var sdk = new Sdk(); var bridge = new IntelGraphicsControlBridge(sdk.Functions);
        using var stop = new CancellationTokenSource(); stop.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => bridge.Probe(Key, stop.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => bridge.ReadFrequencyOffsetMhz(Key, stop.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => bridge.WriteFrequencyOffsetMhz(Key, 45, stop.Token));
        Assert.Empty(sdk.Events);
    }

    [Theory]
    [InlineData("init", IntegratedGpuControlMessage.IntelDriverMissing)]
    [InlineData("target", IntegratedGpuControlMessage.IntelTargetMissing)]
    [InlineData("waiver", IntegratedGpuControlMessage.WaiverRejected)]
    [InlineData("set", IntegratedGpuControlMessage.DriverRejected)]
    [InlineData("mismatch", IntegratedGpuControlMessage.NotEffective)]
    [InlineData("read", IntegratedGpuControlMessage.NotEffective)]
    public async Task FailuresCloseEveryAcquiredSession(string failure, IntegratedGpuControlMessage expected)
    {
        var sdk = new Sdk { Failure = failure }; using var resource = Resource(sdk);
        var result = await resource.WriteAsync(Target, Capability, new(Capability.Field, 45));
        Assert.Equal(IntegratedGpuControlStatus.Failed, result.Status);
        Assert.StartsWith("integrated-gpu-control-" + expected, result.Message);
        // Failed init creates no API session. All successful opens, including later failures, close once.
        Assert.Equal(1, sdk.InitAttempts); Assert.Equal(failure == "init" ? 0 : 1, sdk.Opens);
        Assert.Equal(sdk.Opens, sdk.Closes); Assert.Equal(0, sdk.Active);
        if (failure is "target" or "init" or "waiver") Assert.DoesNotContain("set", sdk.Events);
        if (failure == "set") Assert.DoesNotContain("read", sdk.Events);
    }

    [Fact]
    public async Task CancellationBetweenWaiverAndSetClosesWithoutSetting()
    {
        using var stop = new CancellationTokenSource();
        var sdk = new Sdk { AfterWaiver = stop.Cancel }; using var resource = Resource(sdk);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resource.WriteAsync(Target, Capability, new(Capability.Field, 45), stop.Token));
        Assert.Equal(new[] { "waiver", "close" }, sdk.Events.TakeLast(2));
        Assert.Equal(1, sdk.Opens); Assert.Equal(1, sdk.Closes); Assert.Equal(0, sdk.Active);
    }

    [Fact]
    public async Task CancellationDuringSetStillReadsBackAndCloses()
    {
        using var stop = new CancellationTokenSource();
        var sdk = new Sdk { AfterSet = stop.Cancel }; using var resource = Resource(sdk);
        var result = await resource.WriteAsync(Target, Capability, new(Capability.Field, 45), stop.Token);
        Assert.Equal(IntegratedGpuControlStatus.Applied, result.Status); Assert.True(result.ReadbackVerified);
        Assert.Equal(new[] { "set", "read", "close" }, sdk.Events.TakeLast(3));
        Assert.Equal(sdk.Opens, sdk.Closes); Assert.Equal(0, sdk.Active);
    }

    [Fact]
    public async Task ReleaseFailureDuringCancellationIsReportedRatherThanHiddenByCancellation()
    {
        using var stop = new CancellationTokenSource();
        var sdk = new Sdk { AfterWaiver = stop.Cancel, CloseResult = -17 };
        Exception? warning = null;
        using var resource = Resource(sdk, (error, _, _) => warning = error);
        var outcome = await resource.WriteAsync(Target, Capability, new(Capability.Field, 45), stop.Token);
        Assert.Equal(IntegratedGpuControlStatus.Failed, outcome.Status);
        Assert.Equal(-17, Assert.IsType<ExternalException>(warning).ErrorCode);
        Assert.DoesNotContain("set", sdk.Events); Assert.Equal(1, sdk.Closes);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)]
    public async Task BothSuccessfulCloseCodesAcceptTheReceipt(int code)
    {
        var sdk = new Sdk { CloseResult = code }; using var resource = Resource(sdk);
        Assert.True(resource.Probe(Target, Capability).CanWrite);
        Assert.Equal(15, (await resource.ReadAsync(Target, Capability))!.Number);
        Assert.Equal(IntegratedGpuControlStatus.Applied, (await resource.WriteAsync(Target, Capability, new(Capability.Field, 45))).Status);
        Assert.Equal(3, sdk.Closes); Assert.Equal(sdk.Opens, sdk.Closes); Assert.Equal(0, sdk.Active);
    }

    [Theory]
    [InlineData("probe")] [InlineData("read")] [InlineData("write")]
    public async Task ReleaseFailureWarnsWithoutHidingASuccessfulWrite(string operation)
    {
        var sdk = new Sdk { CloseResult = -17 };
        var warnings = new List<Exception?>();
        using var resource = Resource(sdk, (error, message, _) =>
        { Assert.Equal(IntegratedGpuControlMessage.IntelRejectedWarning, message); warnings.Add(error); });
        switch (operation)
        {
            case "probe":
                var probe = resource.Probe(Target, Capability); Assert.False(probe.CanWrite);
                Assert.Contains("DriverRejected", probe.Reason); break;
            case "read":
                var read = await resource.ReadAsync(Target, Capability); Assert.Null(read!.Number);
                Assert.Contains("ReadingMissing", read.UnreadableReason); break;
            case "write":
                var write = await resource.WriteAsync(Target, Capability, new(Capability.Field, 45));
                Assert.Equal(IntegratedGpuControlStatus.Applied, write.Status); Assert.True(write.ReadbackVerified);
                Assert.Null(write.Message); Assert.Equal(45, sdk.Offset); break;
        }
        Assert.Equal(-17, Assert.IsType<ExternalException>(Assert.Single(warnings)).ErrorCode);
        Assert.Equal(1, sdk.Closes); Assert.Equal(1, sdk.Opens); Assert.Equal(0, sdk.Active);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void BridgeKeepsWorkAndReleaseResultsSeparate(bool rejectSet)
    {
        var sdk = new Sdk { CloseResult = -17, Failure = rejectSet ? "set" : "" };
        var result = new IntelGraphicsControlBridge(sdk.Functions).WriteFrequencyOffsetMhz(Key, 45);
        Assert.Equal(-17, result.ReleaseErrorCode);
        Assert.Equal(rejectSet ? -3 : 0, result.DriverResult);
        Assert.Equal(rejectSet ? IntelGraphicsControlWriteFailure.DriverRejected : (IntelGraphicsControlWriteFailure?)null, result.Failure);
        Assert.Equal(rejectSet ? (double?)null : 45, result.ReadbackMhz);
        Assert.Equal(1, sdk.Opens); Assert.Equal(sdk.Opens, sdk.Closes); Assert.Equal(0, sdk.Active);
    }

    [Fact]
    public async Task RejectedSetWithFailedReleaseStaysFailed()
    {
        var sdk = new Sdk { Failure = "set", CloseResult = -17 };
        var warnings = new List<Exception?>(); using var resource = Resource(sdk, (error, _, _) => warnings.Add(error));
        var result = await resource.WriteAsync(Target, Capability, new(Capability.Field, 45));
        Assert.Equal(IntegratedGpuControlStatus.Failed, result.Status); Assert.False(result.ReadbackVerified);
        Assert.Contains("DriverRejected", result.Message);
        Assert.Equal(-17, Assert.IsType<ExternalException>(Assert.Single(warnings.Where(error => error is not null))).ErrorCode);
        Assert.Equal(1, sdk.Opens); Assert.Equal(sdk.Opens, sdk.Closes); Assert.Equal(0, sdk.Active);
    }

    [Fact]
    public async Task AppliedWriteWithReleaseFailureStillAllowsRestore()
    {
        var sdk = new Sdk { CloseResult = -17 }; var warnings = new List<Exception?>();
        using var resource = Resource(sdk, (error, _, _) => warnings.Add(error));
        Assert.Equal(IntegratedGpuControlStatus.Applied, (await resource.WriteAsync(Target, Capability, new(Capability.Field, 45))).Status);
        Assert.True(await resource.RestoreAsync(Target, Capability)); Assert.Equal(0, sdk.Offset);
        Assert.Equal(2, warnings.Count); Assert.All(warnings, error => Assert.Equal(-17, Assert.IsType<ExternalException>(error).ErrorCode));
        Assert.Equal(2, sdk.Opens); Assert.Equal(sdk.Opens, sdk.Closes); Assert.Equal(0, sdk.Active);
    }

    [Fact]
    public async Task SlowProbeConsentDoesNotHoldTheIgclGateOrBlockAWrite()
    {
        var sdk = new Sdk(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var consentCalls = 0;
        using var resource = new IntegratedGpuControlResource(null, new IntelGraphicsControlBridge(sdk.Functions),
            new(Path.Combine(Path.GetTempPath(), "unused-intel-baseline.json"), async _ =>
            {
                if (Interlocked.Increment(ref consentCalls) == 1) { entered.TrySetResult(); await release.Task; }
                return true;
            }));
        var probe = Task.Run(() => resource.Probe(Target, Capability));
        Task<IntegratedGpuControlOutcome>? write = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, sdk.Active); Assert.Equal(sdk.Opens, sdk.Closes);
            write = resource.WriteAsync(Target, Capability, new(Capability.Field, 45));
            Assert.Equal(IntegratedGpuControlStatus.Applied, (await write.WaitAsync(TimeSpan.FromSeconds(5))).Status);
            Assert.False(probe.IsCompleted); Assert.Equal(2, sdk.Opens); Assert.Equal(sdk.Opens, sdk.Closes);
            release.TrySetResult(); Assert.True((await probe.WaitAsync(TimeSpan.FromSeconds(5))).CanWrite);
        }
        finally { release.TrySetResult(); await probe; if (write is not null) await write; }
    }

    [Theory]
    [InlineData("init", false, IntegratedGpuControlMessage.IntelDriverMissing, 0)]
    [InlineData("target", false, IntegratedGpuControlMessage.OverclockNotAccepted, 1)]
    [InlineData("target", true, IntegratedGpuControlMessage.IntelTargetMissing, 1)]
    public void ProbeKeepsDriverConsentTargetOrderAfterClosingTheSession(string failure, bool accepted, IntegratedGpuControlMessage message, int expectedConsentCalls)
    {
        var sdk = new Sdk { Failure = failure }; var consentCalls = 0;
        using var resource = new IntegratedGpuControlResource(null, new IntelGraphicsControlBridge(sdk.Functions),
            new(Path.Combine(Path.GetTempPath(), "unused-intel-baseline.json"), _ =>
            {
                consentCalls++; Assert.Equal(0, sdk.Active); Assert.Equal(sdk.Opens, sdk.Closes);
                return Task.FromResult(accepted);
            }));
        var result = resource.Probe(Target, Capability);
        Assert.False(result.CanWrite); Assert.Contains(message.ToString(), result.Reason);
        Assert.Equal(expectedConsentCalls, consentCalls);
    }

    [Fact]
    public async Task ReadWaitsForOverlappingWriteToFinishAndClose()
    {
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var sdk = new Sdk { AfterWaiver = () => { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(10))); } };
        using var resource = Resource(sdk);
        var write = Task.Run(() => resource.WriteAsync(Target, Capability, new(Capability.Field, 45)));
        Task<IntegratedGpuControlValue?>? read = null;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            read = resource.ReadAsync(Target, Capability);
            Assert.False(read.IsCompleted); Assert.Equal(1, sdk.Opens); Assert.Equal(1, sdk.Active);
            release.Set();
            Assert.Equal(IntegratedGpuControlStatus.Applied, (await write.WaitAsync(TimeSpan.FromSeconds(5))).Status);
            Assert.Equal(45, (await read.WaitAsync(TimeSpan.FromSeconds(5)))!.Number);
            Assert.Equal(new[] { "waiver", "set", "read", "close", "init", "count", "devices", "identity", "read", "close" }, sdk.Events.Skip(4));
            Assert.Equal(2, sdk.Opens); Assert.Equal(2, sdk.Closes); Assert.Equal(0, sdk.Active);
        }
        finally { release.Set(); await write; if (read is not null) await read; }
    }

    private sealed class Sdk
    {
        public readonly ConcurrentQueue<string> Events = new();
        public int InitAttempts, Opens, Closes, Active, CloseResult;
        public string Failure = "";
        public double Offset = 15;
        public Action? AfterWaiver, AfterSet;
        public IntelGclNativeFunctions Functions => new(Init, Close, Enumerate, Properties, Telemetry,
            OverclockWaiver: Waiver, FrequencyOffsetGet: Read, FrequencyOffsetSet: Set);
        private int Init(ref IntelGclInitArgs args, out IntPtr api)
        {
            Events.Enqueue("init"); InitAttempts++;
            Assert.Equal(36U, args.Size); Assert.Equal(65537U, args.AppVersion); Assert.Equal(1U, args.Flags);
            api = IntPtr.Zero;
            if (Failure == "init") return -1;
            Opens++; Active++; api = new(Opens); return 0;
        }
        private int Close(IntPtr api)
        { Assert.True(api != IntPtr.Zero); Events.Enqueue("close"); Closes++; Active--; return CloseResult; }
        private int Enumerate(IntPtr api, ref uint count, IntPtr[]? devices)
        {
            Assert.Equal(1, Active);
            Events.Enqueue(devices is null ? "count" : "devices"); count = 1;
            if (devices is not null) devices[0] = new(22);
            return 0;
        }
        private int Properties(IntPtr device, ref IntelGclDeviceProperties properties)
        {
            Events.Enqueue("identity"); Assert.Equal(new IntPtr(22), device); Assert.Equal(1, Active);
            Marshal.WriteInt64(properties.DeviceId, unchecked((long)(Failure == "target" ? Key + 1 : Key)));
            properties.VendorId = 0x8086; properties.DeviceType = 1; properties.GraphicsAdapterProperties = 1; return 0;
        }
        private int Waiver(IntPtr device)
        { Assert.Equal(1, Active); Events.Enqueue("waiver"); AfterWaiver?.Invoke(); return Failure == "waiver" ? -2 : 0; }
        private int Set(IntPtr device, double value)
        { Assert.Equal(1, Active); Events.Enqueue("set"); Offset = value; AfterSet?.Invoke(); return Failure == "set" ? -3 : 0; }
        private int Read(IntPtr device, out double value)
        { Assert.Equal(1, Active); Events.Enqueue("read"); value = Failure == "mismatch" ? Offset + 5 : Offset; return Failure == "read" ? -4 : 0; }
        private static int Telemetry(IntPtr device, ref IntelGclPowerTelemetry telemetry) => throw new InvalidOperationException("control must not sample telemetry");
    }
}
