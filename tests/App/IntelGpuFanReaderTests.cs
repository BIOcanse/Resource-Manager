using ResourceManager.App.Domain.Metrics;
using ResourceManager.ReadWrite.Vendors.Intel;

namespace Resource_Manager_APP.Tests;

public sealed class IntelGpuFanReaderTests
{
    [Fact]
    public void SameSessionReorderingReadsBoundHandlesAndHolderOutlivesInitialReference()
    {
        var sdk = new Session(); var opens = 0;
        var reader = new IntelGclReader(() => { opens++; return sdk; });
        var directory = Assert.IsType<IntelGclFanDirectoryLease>(reader.OpenFanDirectory(Inventory(), 2));
        var holder = directory.AcquireLease();
        directory.Dispose(); directory.Dispose();
        Assert.Equal(0, sdk.Closes);
        using (directory.AcquireLease())
        {
            sdk.Fans = [new(12), new(11)];
            var key = directory.Identities[0].ExactIdentityKey;
            Assert.True(directory.IsCurrent(Inventory(), 2, default));
            Assert.Equal(key, directory.Identities[0].ExactIdentityKey);
            var rows = reader.ReadFans(directory.Identities, Inventory(), 2, default);
            Assert.Equal(new int?[] { 0, 1200 }, rows.Select(row => row.Rpm));
            Assert.Equal(new IntPtr[] { new(11), new(12) }, sdk.Reads);
            Assert.Equal(1, opens);
        }
        holder.Dispose(); holder.Dispose();
        Assert.Equal(1, sdk.Closes);
        var operations = sdk.Operations;
        Assert.All(reader.ReadFans(directory.Identities, Inventory(), 2, default), row => Assert.Null(row.Rpm));
        Assert.Equal(operations, sdk.Operations);
        Assert.Throws<ObjectDisposedException>(() => directory.AcquireLease());
    }

    [Fact]
    public void NewSessionCannotReuseOldSelectorsAndForgedIdentityCannotRead()
    {
        var first = new Session(); var second = new Session { Fans = [new(12), new(11)] };
        var next = first;
        var reader = new IntelGclReader(() => next);
        using var a = Assert.IsType<IntelGclFanDirectoryLease>(reader.OpenFanDirectory(Inventory(), 2));
        next = second;
        using var b = Assert.IsType<IntelGclFanDirectoryLease>(reader.OpenFanDirectory(Inventory(), 2));
        Assert.NotEqual(a.Identities[0].ExactIdentityKey, b.Identities[0].ExactIdentityKey);
        var forged = new IntelGclFanIdentity(a.Identities[0].GpuPnpInstanceId, a.Identities[0].DirectoryId, 0);
        Assert.Null(Assert.Single(reader.ReadFans([forged], Inventory(), 2, default)).Rpm);
        Assert.All(reader.ReadFans([a.Identities[0], b.Identities[0]], Inventory(), 2, default), row => Assert.Null(row.Rpm));
        Assert.Empty(first.Reads); Assert.Empty(second.Reads);
    }

    [Fact]
    public void ZeroBudgetDoesNotOpenAndOverflowOrMissingFanEntryRejectsWholeDirectory()
    {
        var opens = 0; var sdk = new Session();
        var reader = new IntelGclReader(() => { opens++; return sdk; });
        using var empty = reader.OpenFanDirectory(Inventory(), 0);
        Assert.NotNull(empty); Assert.Empty(empty.Identities); Assert.Equal(0, opens);
        Assert.Null(reader.OpenFanDirectory(Inventory(), 1));
        Assert.Equal(1, sdk.Closes); Assert.Empty(sdk.Reads);
        var missing = new Session { Available = false };
        Assert.Null(new IntelGclReader(() => missing).OpenFanDirectory(Inventory(), 2));
        Assert.Equal(1, missing.Closes);
        var noFans = new Session { Fans = [] };
        using var successfulEmpty = new IntelGclReader(() => noFans).OpenFanDirectory(Inventory(), 2);
        Assert.NotNull(successfulEmpty); Assert.Empty(successfulEmpty.Identities);
    }

    [Fact]
    public void ParentChangeHandleChangeAndNegativeRpmNeverPublishAValue()
    {
        var sdk = new Session(); var reader = new IntelGclReader(() => sdk);
        using var directory = Assert.IsType<IntelGclFanDirectoryLease>(reader.OpenFanDirectory(Inventory(), 2));
        sdk.AfterRead = () => sdk.Identity = new(8, 0x8086, 0x56A0);
        Assert.All(reader.ReadFans(directory.Identities, Inventory(), 2, default), row => Assert.Null(row.Rpm));
        sdk.AfterRead = null; sdk.Identity = new(7, 0x8086, 0x56A0); sdk.Negative = true;
        Assert.All(reader.ReadFans(directory.Identities, Inventory(), 2, default), row => Assert.Null(row.Rpm));
        sdk.Reads.Clear(); sdk.Fans = [new(11), new(13)];
        Assert.False(directory.IsCurrent(Inventory(), 2, default));
        Assert.All(reader.ReadFans(directory.Identities, Inventory(), 2, default), row => Assert.Null(row.Rpm));
        Assert.Empty(sdk.Reads);
    }

    [Fact]
    public void CancellationDiscardsPendingReadAndFailedOpenClosesSession()
    {
        var sdk = new Session(); var reader = new IntelGclReader(() => sdk);
        using var directory = Assert.IsType<IntelGclFanDirectoryLease>(reader.OpenFanDirectory(Inventory(), 2));
        using var cancellation = new CancellationTokenSource();
        sdk.AfterRead = cancellation.Cancel;
        Assert.Throws<OperationCanceledException>(() => reader.ReadFans(directory.Identities, Inventory(), 2, cancellation.Token));
        Assert.Single(sdk.Reads);
        using var openCancellation = new CancellationTokenSource();
        var cancelled = new Session { AfterEnumerate = openCancellation.Cancel };
        Assert.Throws<OperationCanceledException>(() => new IntelGclReader(() => cancelled).OpenFanDirectory(Inventory(), 2, openCancellation.Token));
        Assert.Equal(1, cancelled.Closes);
    }

    [Fact]
    public void NativeFanAbiRequestsRpmAndRejectsNegativeAndCountGrowth()
    {
        var requestedUnits = uint.MaxValue; var rpm = 0; var grew = false; var closed = 0;
        IntPtr[] nativeFans = [new(11)];
        int EnumerateFans(IntPtr parent, ref uint count, IntPtr[]? handles)
        { if (handles is null) count = (uint)nativeFans.Length; else { nativeFans.CopyTo(handles, 0); if (grew) count++; } return 0; }
        int Init(ref IntelGclInitArgs args, out IntPtr api) { api = new(1); return 0; }
        int Enumerate(IntPtr parent, ref uint count, IntPtr[]? handles)
        { if (handles is null) count = 1; else { handles[0] = new(11); if (grew) count = 2; } return 0; }
        int Properties(IntPtr device, ref IntelGclDeviceProperties properties) => 0;
        int Telemetry(IntPtr device, ref IntelGclPowerTelemetry telemetry) => 0;
        int State(IntPtr fan, uint units, out int value) { requestedUnits = units; value = rpm; return 0; }
        using (var native = IntelGclNativeSession.Open(new(Init, _ => { closed++; return 0; }, Enumerate, Properties, Telemetry,
            Fans: EnumerateFans, FanState: State)))
        {
            Assert.NotNull(native);
            Assert.True(native.TryGetFans(new(9), 1, out var fans, default)); Assert.Equal(new IntPtr(11), Assert.Single(fans));
            Assert.True(native.TryReadFanRpm(new(11), out var zero)); Assert.Equal(0, zero); Assert.Equal(0u, requestedUnits);
            rpm = -1; Assert.False(native.TryReadFanRpm(new(11), out _));
            grew = true; Assert.False(native.TryGetFans(new(9), 1, out _, default));
            grew = false; nativeFans = [];
            Assert.True(native.TryGetFans(new(9), 2, out var noFans, default)); Assert.Empty(noFans);
            nativeFans = [new(11), new(11)]; Assert.False(native.TryGetFans(new(9), 2, out _, default));
            nativeFans = [IntPtr.Zero]; Assert.False(native.TryGetFans(new(9), 2, out _, default));
        }
        Assert.Equal(1, closed);
        grew = false;
        using var missing = IntelGclNativeSession.Open(new(Init, _ => 0, Enumerate, Properties, Telemetry));
        Assert.NotNull(missing); Assert.False(missing.TryGetFans(new(9), 1, out _, default));
        Assert.False(missing.TryReadFanRpm(new(11), out _));
        Assert.True(missing.TryGetDevices(out _, default)); // optional fan absence leaves original entry usable
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SameNamedGpusUseFullParentIdentityAndOneParentsPostReadFailureIsIsolated(bool changeIdentity)
    {
        var sdk = new DualSession(); var opens = 0;
        var reader = new IntelGclReader(() => { opens++; return sdk; });
        using var directory = Assert.IsType<IntelGclFanDirectoryLease>(reader.OpenFanDirectory(DualInventory(), 4));
        var a = directory.Identities.Where(fan => fan.GpuPnpInstanceId.EndsWith(@"\9", StringComparison.Ordinal)).ToArray();
        var b = directory.Identities.Where(fan => fan.GpuPnpInstanceId.EndsWith(@"\3", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, a.Length); Assert.Equal(2, b.Length);
        // SDK device order was B,A at Open, then A,B. Both fan arrays reverse within the original session.
        sdk.Devices = [new(1), new(2)]; sdk.FansA = [new(12), new(11)]; sdk.FansB = [new(22), new(21)];
        Assert.True(directory.IsCurrent(DualInventory(), 4, default));
        var selected = reader.ReadFans([b[0], a[1]], DualInventory(), 4, default);
        Assert.Equal(new int?[] { 2100, 1200 }, selected.Select(row => row.Rpm));
        Assert.Equal(new IntPtr[] { new(21), new(12) }, sdk.Reads); Assert.Equal(1, opens);
        sdk.Reads.Clear();
        sdk.AfterRead = handle =>
        {
            if (handle != new IntPtr(11)) return;
            if (changeIdentity) sdk.IdentityA = new(8, 0x8086, 0x56A0);
            else sdk.FansA = [new(11), new(13)];
        };
        // B completes first; A's post-read identity or handle-set failure must not erase B's result.
        var isolated = reader.ReadFans([b[1], a[0]], DualInventory(), 4, default);
        Assert.Same(b[1], isolated[0].Fan); Assert.Equal(2200, isolated[0].Rpm);
        Assert.Same(a[0], isolated[1].Fan); Assert.Null(isolated[1].Rpm);
        Assert.Equal(new IntPtr[] { new(22), new(11) }, sdk.Reads);
    }

    [Fact]
    public void TotalFanBudgetIsSharedAcrossGpusRatherThanMultipliedPerDevice()
    {
        var sdk = new DualSession();
        Assert.Null(new IntelGclReader(() => sdk).OpenFanDirectory(DualInventory(), 3));
        Assert.Equal(new uint[] { 3, 3 }, sdk.RequestedMaxima);
        Assert.Equal(1, sdk.Closes); Assert.Empty(sdk.Reads);
    }
    private static WindowsGpuAdapterInventoryRead Inventory() => new(SamplingObservationStatus.Current, 1, DateTimeOffset.UtcNow.UtcTicks,
        1, 0, 0, 1, [new WindowsGpuAdapter(9, "Intel", 0x8086, 0x56A0, 0, new AdapterLuid { LowPart = 7 }, false,
            8UL << 30, WindowsGpuAdapterKind.Dedicated)
        { PhysicalIdentityStatus = SamplingObservationStatus.Current,
          PhysicalIdentities = [new(0, @"PCI\VEN_8086&DEV_56A0\9", WindowsGpuPnpKind.Pci, true, false, 0, 9, 0, 0,
            0x8086, 0x56A0, 0, 0, 1, WindowsGpuIdentityEvidence.KmtLuidOpen | WindowsGpuIdentityEvidence.KmtHardwarePnpKey |
            WindowsGpuIdentityEvidence.CmExactDevNode | WindowsGpuIdentityEvidence.CmCanonicalInstanceRoundTrip |
            WindowsGpuIdentityEvidence.KmtDeviceIds | WindowsGpuIdentityEvidence.CmPciAddress, WindowsGpuPhysicalIdentityStatus.Bound, 0)] }]);

    private static WindowsGpuAdapterInventoryRead DualInventory()
    {
        WindowsGpuAdapter Adapter(int index, ulong luid) => new(index, "Intel", 0x8086, 0x56A0, 0,
            new AdapterLuid { LowPart = (uint)luid, HighPart = unchecked((int)(luid >> 32)) }, false, 8UL << 30, WindowsGpuAdapterKind.Dedicated)
        { PhysicalIdentityStatus = SamplingObservationStatus.Current,
          PhysicalIdentities = [new(0, $@"PCI\VEN_8086&DEV_56A0\{index}", WindowsGpuPnpKind.Pci, true, false, 0, (uint)index, 0, 0,
            0x8086, 0x56A0, 0, 0, 1, WindowsGpuIdentityEvidence.KmtLuidOpen | WindowsGpuIdentityEvidence.KmtHardwarePnpKey |
            WindowsGpuIdentityEvidence.CmExactDevNode | WindowsGpuIdentityEvidence.CmCanonicalInstanceRoundTrip |
            WindowsGpuIdentityEvidence.KmtDeviceIds | WindowsGpuIdentityEvidence.CmPciAddress, WindowsGpuPhysicalIdentityStatus.Bound, 0)] };
        return new(SamplingObservationStatus.Current, 1, DateTimeOffset.UtcNow.UtcTicks, 2, 0, 0, 1,
            [Adapter(9, 7), Adapter(3, 0x100000007)]);
    }

    private sealed class DualSession : IIntelGclSession
    {
        public IntPtr[] Devices = [new(2), new(1)], FansA = [new(11), new(12)], FansB = [new(21), new(22)];
        public IntelGclIdentity IdentityA = new(7, 0x8086, 0x56A0);
        public List<IntPtr> Reads { get; } = [];
        public List<uint> RequestedMaxima { get; } = [];
        public Action<IntPtr>? AfterRead;
        public int Closes;
        public bool TryGetDevices(out IntPtr[] devices, CancellationToken token) { devices = Devices.ToArray(); return true; }
        public bool TryGetIdentity(IntPtr device, out IntelGclIdentity identity)
        { identity = device == new IntPtr(1) ? IdentityA : new(0x100000007, 0x8086, 0x56A0); return true; }
        public bool TryGetFans(IntPtr device, uint maximum, out IntPtr[] fans, CancellationToken token)
        { RequestedMaxima.Add(maximum); fans = (device == new IntPtr(1) ? FansA : FansB).ToArray(); return fans.Length <= maximum; }
        public bool TryReadFanRpm(IntPtr fan, out int rpm)
        { Reads.Add(fan); rpm = checked((int)fan.ToInt64() * 100); AfterRead?.Invoke(fan); return true; }
        public bool TryReadTelemetry(IntPtr device, out IntelGclPowerTelemetry telemetry) { telemetry = default; return false; }
        public bool TryReadPciState(IntPtr device, out IntelGclPciState state) { state = default; return false; }
        public bool TryReadPciProperties(IntPtr device, out IntelGclPciProperties properties) { properties = default; return false; }
        public void Dispose() => Closes++;
    }
    private sealed class Session : IIntelGclSession
    {
        public IntPtr[] Fans = [new(11), new(12)];
        public IntelGclIdentity Identity = new(7, 0x8086, 0x56A0);
        public bool Available = true, Negative;
        public int Closes, Operations;
        public Action? AfterRead, AfterEnumerate;
        public List<IntPtr> Reads { get; } = [];
        public bool TryGetDevices(out IntPtr[] devices, CancellationToken token)
        { Operations++; devices = [new(1)]; AfterEnumerate?.Invoke(); return true; }
        public bool TryGetIdentity(IntPtr device, out IntelGclIdentity identity)
        { Operations++; identity = Identity; return true; }
        public bool TryGetFans(IntPtr device, uint maximum, out IntPtr[] fans, CancellationToken token)
        { Operations++; fans = Fans.ToArray(); return Available && fans.Length <= maximum; }
        public bool TryReadFanRpm(IntPtr fan, out int rpm)
        { Operations++; Reads.Add(fan); rpm = Negative ? -1 : fan == new IntPtr(11) ? 0 : 1200; AfterRead?.Invoke(); return true; }
        public bool TryReadTelemetry(IntPtr device, out IntelGclPowerTelemetry telemetry) { telemetry = default; return false; }
        public bool TryReadPciState(IntPtr device, out IntelGclPciState state) { state = default; return false; }
        public bool TryReadPciProperties(IntPtr device, out IntelGclPciProperties properties) { properties = default; return false; }
        public void Dispose() => Closes++;
    }
}
