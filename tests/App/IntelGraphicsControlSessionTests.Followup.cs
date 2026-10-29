using System.Runtime.InteropServices;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.ReadWrite.Windows.Gpu.Control;

namespace Resource_Manager_APP.Tests;

public sealed partial class IntelGraphicsControlSessionTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void DriverDllFactsPrecedeConsentWithoutOpeningASession(bool available)
    {
        var opens = 0; var consents = 0;
        var hardware = new IntelGraphicsControlBridge(() => { opens++; throw new InvalidOperationException(); }, () => available);
        using var resource = new IntegratedGpuControlResource(null, hardware,
            new("unused.json", _ => { consents++; return Task.FromResult(false); }));
        var result = resource.Probe(Target, Capability);
        Assert.Contains(available ? "OverclockNotAccepted" : "IntelDriverMissing", result.Reason);
        Assert.Equal(available ? IntegratedGpuControlUnavailableKind.Prerequisite : IntegratedGpuControlUnavailableKind.Component, result.UnavailableKind);
        Assert.Equal(available ? 1 : 0, consents); Assert.Equal(0, opens);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task MissingDllOrExportOnOpenIsAComponentFailure(bool missingExport)
    {
        var opens = 0;
        var hardware = new IntelGraphicsControlBridge(() =>
        {
            opens++;
            if (missingExport) throw new EntryPointNotFoundException();
            throw new DllNotFoundException();
        }, () => true);
        using var resource = new IntegratedGpuControlResource(null, hardware, new("unused.json", _ => Task.FromResult(true)));
        var probe = resource.Probe(Target, Capability);
        Assert.Contains("IntelDriverMissing", probe.Reason); Assert.Equal(IntegratedGpuControlUnavailableKind.Component, probe.UnavailableKind);
        Assert.Contains("IntelDriverMissing", (await resource.ReadAsync(Target, Capability))!.UnreadableReason);
        Assert.Contains("IntelDriverMissing", (await resource.WriteAsync(Target, Capability, new(Capability.Field, 20))).Message);
        Assert.Equal(3, opens);
    }

    [Fact]
    public void ControlDoesNotRequireTelemetryButTelemetrySessionStillDoes()
    {
        var sdk = new Sdk(); var functions = sdk.Functions with { Telemetry = null };
        var bridge = new IntelGraphicsControlBridge(functions);
        Assert.True(bridge.IsDriverAvailable);
        Assert.Equal(IntelGraphicsControlProbe.Ready, bridge.Probe(Key).Status);
        Assert.Equal(15, bridge.ReadFrequencyOffsetMhz(Key).OffsetMhz);
        Assert.Null(bridge.WriteFrequencyOffsetMhz(Key, 25).Failure);
        Assert.Null(IntelGclNativeSession.Open(functions, requireTelemetry: true));
        Assert.Equal(3, sdk.InitAttempts); Assert.Equal(3, sdk.Opens); Assert.Equal(3, sdk.Closes);
    }

    [Theory]
    [InlineData("waiver")] [InlineData("get")] [InlineData("set")]
    public void MissingOverclockExportIsUnsupportedAtProbe(string missing)
    {
        var sdk = new Sdk(); var functions = sdk.Functions;
        functions = missing switch
        {
            "waiver" => functions with { OverclockWaiver = null },
            "get" => functions with { FrequencyOffsetGet = null },
            _ => functions with { FrequencyOffsetSet = null }
        };
        using var resource = new IntegratedGpuControlResource(null, new IntelGraphicsControlBridge(functions), new("unused.json", _ => Task.FromResult(true)));
        var result = resource.Probe(Target, Capability);
        Assert.False(result.CanWrite); Assert.Contains("NotSupported", result.Reason);
        Assert.Equal(IntegratedGpuControlUnavailableKind.Platform, result.UnavailableKind);
        Assert.DoesNotContain("waiver", sdk.Events); Assert.DoesNotContain("set", sdk.Events);
        Assert.Equal(1, sdk.Opens); Assert.Equal(1, sdk.Closes);
    }

    [Fact]
    public void ReadReceiptKeepsValueAndReleaseCode()
    {
        var sdk = new Sdk { CloseResult = -17 };
        Assert.Equal(new IntelGraphicsControlRead(15, ReleaseErrorCode: -17), new IntelGraphicsControlBridge(sdk.Functions).ReadFrequencyOffsetMhz(Key));
        Assert.Equal(1, sdk.Opens); Assert.Equal(1, sdk.Closes);
    }

    [Theory]
    [InlineData("probe")] [InlineData("read")] [InlineData("write")]
    public async Task ThrownNativeCloseFailureAlsoPreservesTheCompletedOperation(string operation)
    {
        var sdk = new Sdk(); var original = sdk.Functions;
        var functions = original with { Close = handle => { original.Close(handle); throw new ExternalException("release", -17); } };
        var warnings = new List<Exception?>();
        using var resource = new IntegratedGpuControlResource(null, new IntelGraphicsControlBridge(functions), new("unused.json", _ => Task.FromResult(true)),
            warning: (error, code, _) => { Assert.Equal(IntegratedGpuControlMessage.IntelReleaseWarning, code); warnings.Add(error); });
        switch (operation)
        {
            case "probe": Assert.True(resource.Probe(Target, Capability).CanWrite); break;
            case "read": Assert.Equal(15, (await resource.ReadAsync(Target, Capability))!.Number); break;
            case "write": Assert.Equal(IntegratedGpuControlStatus.Applied, (await resource.WriteAsync(Target, Capability, new(Capability.Field, 25))).Status); break;
        }
        Assert.Equal(-17, Assert.IsType<ExternalException>(Assert.Single(warnings)).ErrorCode);
        Assert.Equal(1, sdk.Opens); Assert.Equal(1, sdk.Closes); Assert.Equal(0, sdk.Active);
    }

    [Fact]
    public void FailedCloseKeepsMissingTargetProbeConclusion()
    {
        var sdk = new Sdk { Failure = "target", CloseResult = -17 }; var warnings = new List<IntegratedGpuControlMessage>();
        using var resource = Resource(sdk, (_, code, _) => warnings.Add(code));
        var result = resource.Probe(Target, Capability);
        Assert.Contains("IntelTargetMissing", result.Reason);
        Assert.Equal(new[] { IntegratedGpuControlMessage.IntelReleaseWarning }, warnings);
        Assert.Equal(1, sdk.Opens); Assert.Equal(1, sdk.Closes);
    }

    [Theory]
    [InlineData("read")] [InlineData("probe")]
    public async Task ReadAndProbeCancellationSurviveFailedClose(string operation)
    {
        using var stop = new CancellationTokenSource();
        var sdk = new Sdk { AfterCount = stop.Cancel, CloseResult = -17 };
        var warnings = new List<Exception?>();
        using var resource = Resource(sdk, (error, code, _) =>
        { Assert.Equal(IntegratedGpuControlMessage.IntelReleaseWarning, code); warnings.Add(error); });
        var error = operation == "read"
            ? await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resource.ReadAsync(Target, Capability, stop.Token))
            : Assert.ThrowsAny<OperationCanceledException>(() => resource.Probe(Target, Capability, stop.Token));
        Assert.Equal(stop.Token, error.CancellationToken);
        Assert.Equal(-17, Assert.IsType<ExternalException>(Assert.Single(warnings)).ErrorCode);
        Assert.DoesNotContain("read", sdk.Events); Assert.Equal(1, sdk.Opens); Assert.Equal(1, sdk.Closes);
    }

    [Fact]
    public async Task SlowWriteConsentDoesNotBlockReadOrProbe()
    {
        var sdk = new Sdk(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var consents = 0;
        using var resource = new IntegratedGpuControlResource(null, new IntelGraphicsControlBridge(sdk.Functions), new("unused.json", async token =>
        {
            if (Interlocked.Increment(ref consents) == 1) { entered.SetResult(); await release.Task.WaitAsync(token); }
            return true;
        }));
        var write = resource.WriteAsync(Target, Capability, new(Capability.Field, 45));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, sdk.InitAttempts);
            Assert.Equal(15, (await resource.ReadAsync(Target, Capability).WaitAsync(TimeSpan.FromSeconds(5)))!.Number);
            Assert.True((await Task.Run(() => resource.Probe(Target, Capability)).WaitAsync(TimeSpan.FromSeconds(5))).CanWrite);
            Assert.False(write.IsCompleted); Assert.Equal(2, sdk.Opens); Assert.Equal(2, sdk.Closes);
        }
        finally { release.TrySetResult(); await write; }
        Assert.Equal(3, sdk.Opens); Assert.Equal(3, sdk.Closes);
    }

    [Fact]
    public async Task ProbeCanCancelWhileWaitingForWriteGate()
    {
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        using var probeConsented = new ManualResetEventSlim(); using var stop = new CancellationTokenSource(); var consents = 0;
        var sdk = new Sdk { AfterWaiver = () => { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(10))); } };
        using var resource = new IntegratedGpuControlResource(null, new IntelGraphicsControlBridge(sdk.Functions), new("unused.json", _ =>
        { if (Interlocked.Increment(ref consents) == 2) probeConsented.Set(); return Task.FromResult(true); }));
        var write = Task.Run(() => resource.WriteAsync(Target, Capability, new(Capability.Field, 45)));
        Task? probe = null;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            probe = Task.Run(() => resource.Probe(Target, Capability, stop.Token));
            Assert.True(probeConsented.Wait(TimeSpan.FromSeconds(5))); stop.Cancel();
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(stop.Token, error.CancellationToken); Assert.False(write.IsCompleted); Assert.Equal(1, sdk.Opens);
        }
        finally { release.Set(); await write; if (probe is not null) { try { await probe; } catch (OperationCanceledException) { } } }
        Assert.Equal(1, sdk.Closes);
    }

    [Fact]
    public async Task TwoBridgesTelemetryAndFanLeaseSerializeOnlyNativeInitAndClose()
    {
        var sdk = new ConcurrentSdk(); var functions = sdk.Functions;
        var first = new IntelGraphicsControlBridge(functions); var second = new IntelGraphicsControlBridge(functions);
        var reader = new IntelGclReader(() => IntelGclNativeSession.Open(functions, requireTelemetry: true));
        var inventory = ConcurrentInventory();
        IReadOnlyDictionary<int, IReadOnlySet<MetricFieldCode>> request = new Dictionary<int, IReadOnlySet<MetricFieldCode>>
        { [0] = new HashSet<MetricFieldCode> { MetricFieldCode.IgclGpuTemperature } };
        using var start = new Barrier(4);
        Task Run(Action operation) => Task.Factory.StartNew(() =>
        {
            Assert.True(start.SignalAndWait(TimeSpan.FromSeconds(5)));
            for (var index = 0; index < 12; index++) operation();
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        await Task.WhenAll(
            Run(() => Assert.Null(first.WriteFrequencyOffsetMhz(Key, 15).Failure)),
            Run(() => { Assert.Equal(IntelGraphicsControlProbe.Ready, second.Probe(Key).Status); Assert.Equal(15, second.ReadFrequencyOffsetMhz(Key).OffsetMhz); }),
            Run(() => Assert.Single(reader.Read(inventory, request, default))),
            Run(() => { using var fans = reader.OpenFanDirectory(inventory, 1); Assert.Single(Assert.IsType<IntelGclFanDirectoryLease>(fans).Identities); })
        ).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(0, sdk.LifecycleOverlaps); Assert.Equal(60, sdk.Opens); Assert.Equal(60, sdk.Closes);
        Assert.Equal(0, sdk.Active); Assert.Equal(12, sdk.TelemetryReads); Assert.True(sdk.FanEnumerations > 0);

        // A lifecycle lock must not serialize enumeration/identity/waiver/read/set work.
        using var identities = new CountdownEvent(2);
        sdk.OnIdentity = () => { identities.Signal(); Assert.True(identities.Wait(TimeSpan.FromSeconds(5))); };
        await Task.WhenAll(
            Task.Factory.StartNew(() => first.Probe(Key), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default),
            Task.Factory.StartNew(() => second.Probe(Key), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(62, sdk.Opens); Assert.Equal(62, sdk.Closes); Assert.Equal(0, sdk.LifecycleOverlaps);
    }

    private static WindowsGpuAdapterInventoryRead ConcurrentInventory()
    {
        var adapter = new WindowsGpuAdapter(0, "recorded", 0x8086, 0x56A0, 0,
            new AdapterLuid { LowPart = unchecked((uint)Key), HighPart = (int)(Key >> 32) }, false, 0, WindowsGpuAdapterKind.Integrated)
        {
            PhysicalIdentityStatus = SamplingObservationStatus.Current,
            PhysicalIdentities = [new(0, @"PCI\VEN_8086&DEV_56A0\0", WindowsGpuPnpKind.Pci, true, false, 0, 0, 0, 0,
                0x8086, 0x56A0, 0, 0, 1, WindowsGpuIdentityEvidence.KmtLuidOpen | WindowsGpuIdentityEvidence.KmtHardwarePnpKey
                | WindowsGpuIdentityEvidence.CmExactDevNode | WindowsGpuIdentityEvidence.CmCanonicalInstanceRoundTrip
                | WindowsGpuIdentityEvidence.KmtDeviceIds | WindowsGpuIdentityEvidence.CmPciAddress, WindowsGpuPhysicalIdentityStatus.Bound, 0)]
        };
        return new(SamplingObservationStatus.Current, 1, DateTimeOffset.UtcNow.UtcTicks, 1, 0, 0, 1, [adapter]);
    }

    private sealed class ConcurrentSdk
    {
        public int Opens, Closes, Active, LifecycleOverlaps, TelemetryReads, FanEnumerations;
        private int lifecycleCalls;
        public Action? OnIdentity;
        public IntelGclNativeFunctions Functions => new(Init, Close, Enumerate, Properties, Telemetry,
            Fans: Fans, OverclockWaiver: _ => 0, FrequencyOffsetGet: Read, FrequencyOffsetSet: (_, _) => 0);
        private void EnterLifecycle()
        {
            if (Interlocked.Increment(ref lifecycleCalls) != 1) Interlocked.Increment(ref LifecycleOverlaps);
            Thread.Sleep(2);
        }
        private int Init(ref IntelGclInitArgs args, out IntPtr api)
        {
            EnterLifecycle(); api = new(Interlocked.Increment(ref Opens)); Interlocked.Increment(ref Active);
            Interlocked.Decrement(ref lifecycleCalls); return 0;
        }
        private int Close(IntPtr api)
        {
            EnterLifecycle(); Interlocked.Increment(ref Closes); Interlocked.Decrement(ref Active);
            Interlocked.Decrement(ref lifecycleCalls); return 0;
        }
        private static int Enumerate(IntPtr api, ref uint count, IntPtr[]? devices)
        { count = 1; if (devices is not null) devices[0] = new(22); return 0; }
        private int Properties(IntPtr device, ref IntelGclDeviceProperties properties)
        {
            OnIdentity?.Invoke(); Marshal.WriteInt64(properties.DeviceId, (long)Key);
            properties.VendorId = 0x8086; properties.DevicePciId = 0x56A0; properties.DeviceType = 1; properties.GraphicsAdapterProperties = 1; return 0;
        }
        private int Telemetry(IntPtr device, ref IntelGclPowerTelemetry telemetry)
        {
            Interlocked.Increment(ref TelemetryReads);
            telemetry.GpuTemperature = new() { Supported = 1, Unit = IntelGclUnit.Celsius, Type = IntelGclDataType.Double, Double = 40 }; return 0;
        }
        private int Fans(IntPtr device, ref uint count, IntPtr[]? fans)
        { Interlocked.Increment(ref FanEnumerations); count = 1; if (fans is not null) fans[0] = new(33); return 0; }
        private static int Read(IntPtr device, out double value) { value = 15; return 0; }
    }
}
