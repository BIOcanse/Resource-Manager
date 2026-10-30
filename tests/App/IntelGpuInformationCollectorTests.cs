using System.Runtime.InteropServices;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Vendors.Intel;
using ResourceManager.ReadWrite.Windows.Metrics.Topology;

namespace Resource_Manager_APP.Tests;

public sealed partial class IntelGpuInformationCollectorTests
{
    private static readonly MetricFieldCode[] Codes = MetricFieldRegistry.Definitions
        .Where(field => field.Vendor == MetricVendor.Intel && IntelGclReader.IsRawTelemetryField(field.Code)).Select(field => field.Code).ToArray();
    private static readonly string[] Names = MetricFieldRegistry.Definitions
        .Where(field => field.Vendor == MetricVendor.Intel && IntelGclReader.IsRawTelemetryField(field.Code)).Select(field => field.Name).ToArray();
    private const ulong LuidA = 0xFEDCBA98000000A1;
    private const ulong LuidB = 0x00000002000000B1;

    [Fact]
    public void ManagedLayoutsMatchPinnedIntelX64Header()
    {
        Assert.Equal(36, Marshal.SizeOf<IntelGclInitArgs>());
        Assert.Equal(320, Marshal.SizeOf<IntelGclDeviceProperties>());
        Assert.Equal(24, Marshal.SizeOf<IntelGclTelemetryItem>());
        Assert.Equal(1024, Marshal.SizeOf<IntelGclPowerTelemetry>());
        Assert.Equal(8, Marshal.OffsetOf<IntelGclInitArgs>(nameof(IntelGclInitArgs.AppVersion)).ToInt32());
        Assert.Equal(20, Marshal.OffsetOf<IntelGclInitArgs>(nameof(IntelGclInitArgs.ApplicationUid)).ToInt32());
        Assert.Equal(64, Marshal.OffsetOf<IntelGclDeviceProperties>(nameof(IntelGclDeviceProperties.VendorId)).ToInt32());
        Assert.Equal(16, Marshal.OffsetOf<IntelGclTelemetryItem>(nameof(IntelGclTelemetryItem.Double)).ToInt32());
        Assert.Equal(176, Marshal.OffsetOf<IntelGclPowerTelemetry>(nameof(IntelGclPowerTelemetry.MediaActivity)).ToInt32());
        Assert.Equal(200, Marshal.OffsetOf<IntelGclPowerTelemetry>(nameof(IntelGclPowerTelemetry.PowerLimited)).ToInt32());
        Assert.Equal(204, Marshal.OffsetOf<IntelGclPowerTelemetry>(nameof(IntelGclPowerTelemetry.UtilizationLimited)).ToInt32());
        Assert.Equal(304, Marshal.OffsetOf<IntelGclPowerTelemetry>(nameof(IntelGclPowerTelemetry.MemoryReadBytes)).ToInt32());
        Assert.Equal(384, Marshal.OffsetOf<IntelGclPowerTelemetry>(nameof(IntelGclPowerTelemetry.BoardEnergy)).ToInt32());
    }

    [Fact]
    public void SelectedGpuUsesFullLuidRatherThanSdkOrdinalAndClosesReadOnlySession()
    {
        var sdk = new Sdk();
        var row = Assert.Single(new IntelGclReader(sdk.Open).Read(Inventory(), Request(9), CancellationToken.None));
        Assert.Equal(9, row.Index); Assert.Equal(new IntPtr(1), Assert.Single(sdk.TelemetryDevices));
        Assert.Equal(1, sdk.Opens); Assert.Equal(1, sdk.Closes);
        Assert.Equal(3, sdk.PropertiesCalls); Assert.Equal(1, sdk.TelemetryCalls);
        Assert.Equal(18, row.Values.Count + row.Counters.Count + row.Flags.Count);
        Assert.Equal(-1, row.Values[MetricFieldCode.IgclGpuTemperature]);
        Assert.Equal(-3, row.Values[MetricFieldCode.IgclMemoryTemperature]);
        Assert.Equal(5, row.Values[MetricFieldCode.IgclGpuEnergy]);
        Assert.Equal(7, row.Values[MetricFieldCode.IgclMemoryEnergy]);
        Assert.Equal(11, row.Values[MetricFieldCode.IgclBoardEnergy]);
        Assert.Equal(1.1, row.Values[MetricFieldCode.IgclGpuVoltage]);
        Assert.Equal(1.2, row.Values[MetricFieldCode.IgclMemoryVoltage]);
        Assert.Equal(13, row.Values[MetricFieldCode.IgclSampleTimestamp]);
        Assert.Equal(17, row.Values[MetricFieldCode.IgclGlobalActivity]);
        Assert.Equal(19, row.Values[MetricFieldCode.IgclRenderComputeActivity]);
        Assert.Equal(23, row.Values[MetricFieldCode.IgclMediaActivity]);
        Assert.Equal(false, row.Flags[MetricFieldCode.IgclPowerLimited]);
        Assert.Equal(true, row.Flags[MetricFieldCode.IgclTemperatureLimited]);
        Assert.Equal(false, row.Flags[MetricFieldCode.IgclCurrentLimited]);
        Assert.Equal(true, row.Flags[MetricFieldCode.IgclVoltageLimited]);
        Assert.Equal(false, row.Flags[MetricFieldCode.IgclUtilizationLimited]);
        Assert.Equal(9007199254740993UL, row.Counters[MetricFieldCode.IgclMemoryReadBytes]);
        Assert.Equal(ulong.MaxValue, row.Counters[MetricFieldCode.IgclMemoryWriteBytes]);
    }

    [Fact]
    public void OnlyRequestedFieldsPublishedAndUnselectedGpuNeverReadsTelemetry()
    {
        var sdk = new Sdk();
        var request = new Dictionary<int, IReadOnlySet<MetricFieldCode>> { [3] = new HashSet<MetricFieldCode> { MetricFieldCode.IgclMemoryEnergy } };
        var row = Assert.Single(new IntelGclReader(sdk.Open).Read(Inventory(), request, CancellationToken.None));
        Assert.Equal(3, row.Index); Assert.Equal(MetricFieldCode.IgclMemoryEnergy, Assert.Single(row.Values).Key);
        Assert.Empty(row.Counters); Assert.Equal(new IntPtr(2), Assert.Single(sdk.TelemetryDevices));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)]
    public void NoDemandIncompleteOrAmbiguousWindowsIdentityNeverOpensSdk(int invalid)
    {
        var sdk = new Sdk(); var inventory = Inventory(); var requested = Request(9);
        switch (invalid)
        {
            case 0: requested.Clear(); break;
            case 1: requested[9] = new HashSet<MetricFieldCode> { MetricFieldCode.GpuPower }; break;
            case 2: inventory = inventory with { Status = SamplingObservationStatus.RetainedLastGood }; break;
            case 3: inventory = inventory with { SkippedCount = 1 }; break;
            case 4: inventory = inventory with { Adapters = [Adapter(9, LuidA) with { PhysicalIdentities = [] }, Adapter(3, LuidB)] }; break;
            case 5: var a = Adapter(9, LuidA); inventory = inventory with { Adapters = [a with { PhysicalIdentities = [a.PhysicalIdentities[0], a.PhysicalIdentities[0]] }, Adapter(3, LuidB)] }; break;
            case 6: inventory = inventory with { Adapters = [Adapter(9, LuidA, 0x1002), Adapter(3, LuidB, 0x1002)] }; break;
        }
        Assert.Empty(new IntelGclReader(sdk.Open).Read(inventory, requested, CancellationToken.None));
        Assert.Equal(0, sdk.Opens);
    }

    [Theory]
    [InlineData("duplicate-luid")] [InlineData("wrong-pci")] [InlineData("changed-luid")]
    [InlineData("changed-size")] [InlineData("changed-pointer")] [InlineData("wrong-type")]
    public void BadSdkIdentityDoesNotPublishToAnotherDevice(string mode)
    {
        var sdk = new Sdk { IdentityMode = mode };
        Assert.Empty(new IntelGclReader(sdk.Open).Read(Inventory(), Request(9), CancellationToken.None));
        Assert.Equal(mode == "changed-luid" ? 1 : 0, sdk.TelemetryCalls);
        Assert.Equal(1, sdk.Closes);
    }

    [Theory]
    [InlineData("init")] [InlineData("count")] [InlineData("enum-count-change")]
    [InlineData("duplicate-handle")] [InlineData("null-handle")] [InlineData("telemetry")]
    [InlineData("telemetry-size")] [InlineData("telemetry-version")]
    public void SdkFailureDiscardsDirtyOutputsAndClosesTheSession(string mode)
    {
        var sdk = new Sdk { FailureMode = mode };
        Assert.Empty(new IntelGclReader(sdk.Open).Read(Inventory(), Request(9), CancellationToken.None));
        Assert.Equal(mode == "init" ? 0 : 1, sdk.Closes);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void MissingRuntimeAndExportsAreUnavailable(int kind)
    {
        IIntelGclSession? Open() => throw kind switch { 0 => new DllNotFoundException(), 1 => new EntryPointNotFoundException(), _ => new BadImageFormatException() };
        Assert.Empty(new IntelGclReader(Open).Read(Inventory(), Request(9), CancellationToken.None));
    }

    [Fact]
    public void CancellationStopsFutureSdkCallsAndClosesSession()
    {
        var sdk = new Sdk(); using var stop = new CancellationTokenSource(); stop.Cancel();
        Assert.Throws<OperationCanceledException>(() => new IntelGclReader(sdk.Open).Read(Inventory(), Request(9), stop.Token));
        Assert.Equal(0, sdk.Opens);
        using var between = new CancellationTokenSource(); sdk.AfterCount = between.Cancel;
        Assert.Throws<OperationCanceledException>(() => new IntelGclReader(sdk.Open).Read(Inventory(), Request(9), between.Token));
        Assert.Equal(1, sdk.EnumerateCalls); Assert.Equal(0, sdk.PropertiesCalls); Assert.Equal(1, sdk.Closes);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void UnsupportedWrongUnitTypeAndNonFiniteItemsAreNull(int invalid)
    {
        var item = Number(12, IntelGclUnit.Joules);
        switch (invalid) { case 0: item.Supported = 0; break; case 1: item.Supported = 2; break;
            case 2: item.Unit = IntelGclUnit.Seconds; break; case 3: item.Type = (IntelGclDataType)10; break;
            case 4: item.Double = double.NaN; break; case 5: item.Double = double.PositiveInfinity; break; }
        Assert.Null(IntelGclReader.ReadNumber(item, IntelGclUnit.Joules));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void NegativeCountersAndInexactNumericIntegersAreNull(int invalid)
    {
        var item = Number(-1, IntelGclUnit.Joules);
        switch (invalid) { case 1: item.Type = IntelGclDataType.UInt64; item.UInt64 = 9007199254740993; break;
            case 2: item.Type = IntelGclDataType.Int64; item.Int64 = 9007199254740993; break;
            case 3: item.Unit = IntelGclUnit.Celsius; item.Double = -274; break; }
        Assert.Null(IntelGclReader.ReadNumber(item, item.Unit, invalid == 3));
    }

    [Fact]
    public void ValidZeroFalseAndNegativeCelsiusArePreserved()
    {
        var telemetry = Sample(); telemetry.GpuEnergy = Number(0, IntelGclUnit.Joules);
        telemetry.MemoryReadBytes.UInt64 = 0; telemetry.PowerLimited = 0; telemetry.CurrentLimited = 2;
        var row = IntelGclReader.Decode(9, telemetry, Codes.ToHashSet());
        Assert.Equal(0, row.Values[MetricFieldCode.IgclGpuEnergy]); Assert.Equal(0UL, row.Counters[MetricFieldCode.IgclMemoryReadBytes]);
        Assert.Equal(false, row.Flags[MetricFieldCode.IgclPowerLimited]); Assert.Null(row.Flags[MetricFieldCode.IgclCurrentLimited]);
        Assert.Equal(-1, row.Values[MetricFieldCode.IgclGpuTemperature]);
        var floating = Number(12, IntelGclUnit.Bytes); Assert.Null(IntelGclReader.ReadCounter(floating));
        floating.Type = IntelGclDataType.Int64; floating.Int64 = -1; Assert.Null(IntelGclReader.ReadCounter(floating));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)] [InlineData(9)]
    public void SupportedNativeNumericTypesUseTheDeclaredUnionMember(int type)
    {
        var item = new IntelGclTelemetryItem { Supported = 1, Unit = IntelGclUnit.Joules, Type = (IntelGclDataType)type };
        switch (item.Type)
        {
            case IntelGclDataType.Int8: item.Int8 = 42; break; case IntelGclDataType.UInt8: item.UInt8 = 42; break;
            case IntelGclDataType.Int16: item.Int16 = 42; break; case IntelGclDataType.UInt16: item.UInt16 = 42; break;
            case IntelGclDataType.Int32: item.Int32 = 42; break; case IntelGclDataType.UInt32: item.UInt32 = 42; break;
            case IntelGclDataType.Int64: item.Int64 = 42; break; case IntelGclDataType.UInt64: item.UInt64 = 42; break;
            case IntelGclDataType.Float: item.Float = 42.25f; break; case IntelGclDataType.Double: item.Double = 42.25; break;
        }
        Assert.Equal(type >= 8 ? 42.25 : 42, IntelGclReader.ReadNumber(item, IntelGclUnit.Joules));
    }

    [Fact]
    public void CancellationAfterTelemetryDoesNotReadIdentityAgain()
    {
        var sdk = new Sdk(); using var stop = new CancellationTokenSource(); sdk.AfterTelemetry = stop.Cancel;
        Assert.Throws<OperationCanceledException>(() => new IntelGclReader(sdk.Open).Read(Inventory(), Request(9), stop.Token));
        Assert.Equal(1, sdk.TelemetryCalls); Assert.Equal(2, sdk.PropertiesCalls); Assert.Equal(1, sdk.Closes);
    }

    [Fact]
    public void FailedInitExceptionCreatesNoApiSession()
    {
        int Init(ref IntelGclInitArgs args, out IntPtr api) { api = IntPtr.Zero; throw new EntryPointNotFoundException(); }
        var sdk = new Sdk();
        Assert.Throws<EntryPointNotFoundException>(() => IntelGclNativeSession.Open(new(Init, sdk.Close, sdk.Enumerate, sdk.Properties, sdk.Telemetry)));
        Assert.Equal(0, sdk.Closes);
    }

    [Theory]
    [InlineData("close-still-open", true)] [InlineData("close-failure", false)]
    public void CloseStatusKeepsSharedSdkModuleSafeAndDiscardsCleanupFailures(string mode, bool accepted)
    {
        var sdk = new Sdk { FailureMode = mode };
        var result = new IntelGclReader(sdk.Open).Read(Inventory(), Request(9), CancellationToken.None);
        Assert.Equal(accepted ? 1 : 0, result.Count); Assert.Equal(1, sdk.Closes);
    }

    [Fact]
    public void FailedGpuDoesNotSuppressSuccessfulOtherGpu()
    {
        var sdk = new Sdk { FailureMode = "telemetry-a" }; var request = Request(9); request.Add(3, Codes.ToHashSet());
        var row = Assert.Single(new IntelGclReader(sdk.Open).Read(Inventory(), request, CancellationToken.None));
        Assert.Equal(3, row.Index); Assert.Equal(7, row.Values[MetricFieldCode.IgclMemoryEnergy]);
        Assert.Equal(2, sdk.TelemetryCalls); Assert.Equal(1, sdk.Closes);
    }

    [Fact]
    public void ZoneReadsThroughOriginalSession()
    {
        var sdk = new Sdk(); var zone = new IntelGclMonitoringZone(new IntelGclReader(sdk.Open));
        Assert.Single(zone.Read(Inventory(), Request(9), CancellationToken.None)); Assert.Equal(1, sdk.Opens);
    }

    private static IntelGclTelemetryItem Number(double value, IntelGclUnit unit)
        => new() { Supported = 1, Unit = unit, Type = IntelGclDataType.Double, Double = value };
    private static IntelGclTelemetryItem Counter(ulong value)
        => new() { Supported = 1, Unit = IntelGclUnit.Bytes, Type = IntelGclDataType.UInt64, UInt64 = value };
    private static IntelGclPowerTelemetry Sample() => new() { Size = 1024,
        GpuTemperature = Number(-1, IntelGclUnit.Celsius), MemoryTemperature = Number(-3, IntelGclUnit.Celsius),
        GpuEnergy = Number(5, IntelGclUnit.Joules), MemoryEnergy = Number(7, IntelGclUnit.Joules), BoardEnergy = Number(11, IntelGclUnit.Joules),
        GpuVoltage = Number(1.1, IntelGclUnit.Volts), MemoryVoltage = Number(1.2, IntelGclUnit.Volts), Timestamp = Number(13, IntelGclUnit.Seconds),
        GlobalActivity = Number(17, IntelGclUnit.Seconds), RenderComputeActivity = Number(19, IntelGclUnit.Seconds), MediaActivity = Number(23, IntelGclUnit.Seconds),
        MemoryReadBytes = Counter(9007199254740993), MemoryWriteBytes = Counter(ulong.MaxValue), TemperatureLimited = 1, VoltageLimited = 1,
        GraphicsClock = Number(1222.5, IntelGclUnit.Megahertz), MemoryClock = Number(900.75, IntelGclUnit.Megahertz) };
    private static Dictionary<int, IReadOnlySet<MetricFieldCode>> Request(int index)
        => new() { [index] = Codes.ToHashSet() };
    private static WindowsGpuAdapterInventoryRead Inventory() => Inventory(secondHasDedicatedMemory: true);
    private static WindowsGpuAdapterInventoryRead Inventory(bool secondHasDedicatedMemory) => new(SamplingObservationStatus.Current, 1, DateTimeOffset.UtcNow.UtcTicks, 2, 0, 0, 1,
        [Adapter(9, LuidA), Adapter(3, LuidB) with
        {
            Kind = secondHasDedicatedMemory ? WindowsGpuAdapterKind.Dedicated : WindowsGpuAdapterKind.Integrated,
            DedicatedVideoMemoryBytes = secondHasDedicatedMemory ? 8UL << 30 : 0
        }]);

    // The catalog and live inventory must use the topology owner's same physical identity projection.
    private static IReadOnlyList<NativeMetricSnapshotGpuCatalogIdentity> RecordedGpuIdentities(bool secondHasDedicatedMemory = true)
    {
        var captured = WindowsHardwareMetricTopologySource.CaptureGpuIdentities(Inventory(secondHasDedicatedMemory));
        Assert.Null(captured.FailureCode);
        return captured.Rows;
    }
    private static WindowsGpuAdapter Adapter(int index, ulong luid, uint vendor = 0x8086) => new(index, "same-name", vendor, 0x56A0, 0,
        new AdapterLuid { LowPart = (uint)luid, HighPart = unchecked((int)(luid >> 32)) }, false, 8UL << 30, WindowsGpuAdapterKind.Dedicated)
    { PhysicalIdentityStatus = SamplingObservationStatus.Current,
      PhysicalIdentities = [new(0, $@"PCI\VEN_{vendor:X4}&DEV_56A0\{index}", WindowsGpuPnpKind.Pci, true, false, 0, (uint)index, 0, 0,
        vendor, 0x56A0, 0, 0, 1, WindowsGpuIdentityEvidence.KmtLuidOpen | WindowsGpuIdentityEvidence.KmtHardwarePnpKey | WindowsGpuIdentityEvidence.CmExactDevNode |
        WindowsGpuIdentityEvidence.CmCanonicalInstanceRoundTrip | WindowsGpuIdentityEvidence.KmtDeviceIds | WindowsGpuIdentityEvidence.CmPciAddress,
        WindowsGpuPhysicalIdentityStatus.Bound, 0)] };

    private sealed class Sdk
    {
        public int Opens, Closes, EnumerateCalls, PropertiesCalls, TelemetryCalls, Mode;
        public string IdentityMode = "", FailureMode = ""; public int ClockMode, PciMode, PciCalls; public bool MissingPciEntry; public Action? AfterPci;
        public readonly List<IntPtr> PciDevices = [];
        public int PciPropertiesMode, PciPropertiesCalls; public bool MissingPciPropertiesEntry; public Action? AfterPciProperties;
        public readonly List<IntPtr> PciPropertiesDevices = [];
        public Action? AfterCount, AfterTelemetry;
        public Func<IntelGclPowerTelemetry>? SampleFactory;
        public readonly List<IntPtr> TelemetryDevices = [];
        public IIntelGclSession? Open()
        {
            Interlocked.Increment(ref Opens);
            return IntelGclNativeSession.Open(new(Init, Close, Enumerate, Properties, Telemetry, MissingPciEntry ? null : PciState, MissingPciPropertiesEntry ? null : PciProperties));
        }
        private int Init(ref IntelGclInitArgs args, out IntPtr api)
        { Assert.Equal(36U, args.Size); Assert.Equal(0, args.Version); Assert.Equal(65536U, args.AppVersion); Assert.Equal(1U, args.Flags);
          Assert.Equal(Guid.Empty, args.ApplicationUid); api = new(42); return FailureMode == "init" ? 1 : 0; }
        public int Close(IntPtr api)
        { Assert.Equal(new IntPtr(42), api); Interlocked.Increment(ref Closes); return FailureMode switch { "close-still-open" => 1, "close-failure" => 2, _ => 0 }; }
        public int Enumerate(IntPtr api, ref uint count, IntPtr[]? devices)
        {
            Interlocked.Increment(ref EnumerateCalls);
            if (devices is null) { count = FailureMode == "count" ? 257U : 2U; AfterCount?.Invoke(); return 0; }
            devices[0] = new(2); devices[1] = new(1);
            if (FailureMode == "enum-count-change") count = 1;
            if (FailureMode == "duplicate-handle") devices[1] = devices[0];
            if (FailureMode == "null-handle") devices[0] = IntPtr.Zero;
            return 0;
        }
        public int Properties(IntPtr device, ref IntelGclDeviceProperties props)
        {
            var call = Interlocked.Increment(ref PropertiesCalls); Assert.Equal(320U, props.Size); Assert.Equal(8U, props.DeviceIdSize);
            var luid = device == new IntPtr(1) || IdentityMode == "duplicate-luid" ? LuidA : LuidB;
            if (IdentityMode == "changed-luid" && call > 2) luid = LuidB;
            Marshal.WriteInt64(props.DeviceId, unchecked((long)luid)); props.DeviceType = 1; props.VendorId = 0x8086; props.DevicePciId = 0x56A0;
            if (IdentityMode == "wrong-pci") props.DevicePciId = 0x56A1;
            if (IdentityMode == "changed-size") props.DeviceIdSize = 4;
            if (IdentityMode == "changed-pointer") props.DeviceId = IntPtr.Zero;
            if (IdentityMode == "wrong-type") props.DeviceType = 2;
            return 0;
        }
        public int PciProperties(IntPtr device, ref IntelGclPciProperties properties)
        {
            Interlocked.Increment(ref PciPropertiesCalls); lock (PciPropertiesDevices) PciPropertiesDevices.Add(device);
            Assert.Equal(64U, properties.Size); Assert.Equal(0, properties.Version);
            Assert.Equal(24U, properties.Address.Size); Assert.Equal(0, properties.Address.Version);
            Assert.Equal(24U, properties.MaximumSpeed.Size); Assert.Equal(0, properties.MaximumSpeed.Version);
            var mode = Volatile.Read(ref PciPropertiesMode); if (mode == 11) return 0;
            properties.MaximumSpeed.Generation = 5; properties.MaximumSpeed.Width = 16; properties.MaximumSpeed.MaximumBandwidth = -1;
            if (mode == 2) properties.MaximumSpeed.Generation = -1;
            if (mode == 3) properties.MaximumSpeed.Width = -1;
            if (mode == 4) properties.Size = 56;
            if (mode == 5) properties.Version = 1;
            if (mode == 6) properties.MaximumSpeed.Size = 16;
            if (mode == 7) properties.MaximumSpeed.Version = 1;
            if (mode == 8) { properties.MaximumSpeed.Generation = 0; properties.MaximumSpeed.Width = 0; }
            if (mode == 9) { properties.MaximumSpeed.Generation = int.MaxValue; properties.MaximumSpeed.Width = int.MaxValue; }
            if (mode == 10) { properties.MaximumSpeed.Generation = int.MinValue; properties.MaximumSpeed.Width = int.MinValue; }
            AfterPciProperties?.Invoke(); return mode == 1 ? 1 : 0;
        }

        public int PciState(IntPtr device, ref IntelGclPciState state)
        {
            Interlocked.Increment(ref PciCalls); lock (PciDevices) PciDevices.Add(device);
            Assert.Equal(32U, state.Size); Assert.Equal(0, state.Version); Assert.Equal(24U, state.Speed.Size); Assert.Equal(0, state.Speed.Version);
            if (Volatile.Read(ref PciMode) == 11) return 0; // Successful query with no known properties.
            state.Speed.Generation = 4; state.Speed.Width = 8; state.Speed.MaximumBandwidth = -1;
            var mode = Volatile.Read(ref PciMode);
            if (mode == 2) state.Speed.Generation = -1;
            if (mode == 3) state.Speed.Width = -1;
            if (mode == 4) state.Size = 24;
            if (mode == 5) state.Version = 1;
            if (mode == 6) state.Speed.Size = 16;
            if (mode == 7) state.Speed.Version = 1;
            if (mode == 8) { state.Speed.Generation = 0; state.Speed.Width = 0; }
            if (mode == 9) { state.Speed.Generation = int.MaxValue; state.Speed.Width = int.MaxValue; }
            if (mode == 10) { state.Speed.Generation = int.MinValue; state.Speed.Width = int.MinValue; }
            AfterPci?.Invoke(); return mode == 1 ? 1 : 0;
        }

        public int Telemetry(IntPtr device, ref IntelGclPowerTelemetry value)
        {
            Assert.Equal(1024U, value.Size); Assert.Equal(0, value.Version);
            Interlocked.Increment(ref TelemetryCalls); lock (TelemetryDevices) TelemetryDevices.Add(device);
            value = SampleFactory?.Invoke() ?? Sample(); AfterTelemetry?.Invoke();
            if (Volatile.Read(ref Mode) == 1) return 1;
            if (FailureMode == "telemetry-a" && device == new IntPtr(1)) return 1;
            if (Volatile.Read(ref Mode) == 2) { value.GpuEnergy.Double = 0; value.MemoryReadBytes.UInt64 = 0; value.GraphicsClock.Double = 0; value.MemoryClock.Double = 0; }
            if (Volatile.Read(ref ClockMode) == 1) value.GraphicsClock.Supported = 0;
            if (Volatile.Read(ref ClockMode) == 2) value.GraphicsClock.Unit = IntelGclUnit.Volts;
            if (Volatile.Read(ref ClockMode) == 3) value.GraphicsClock.Double = -1;
            if (Volatile.Read(ref ClockMode) == 4) value.GraphicsClock.Double = double.NaN;
            if (FailureMode == "telemetry-size") value.Size = 1000;
            if (FailureMode == "telemetry-version") value.Version = 1;
            return FailureMode == "telemetry" ? 1 : 0;
        }
    }
}
