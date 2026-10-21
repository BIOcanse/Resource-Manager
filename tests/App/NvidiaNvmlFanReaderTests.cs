using System.Reflection;
using System.Runtime.InteropServices;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.ReadWrite.Vendors.Nvidia;
using ResourceManager.ReadWrite.Windows.Gpu;

namespace Resource_Manager_APP.Tests;

public sealed class NvidiaNvmlFanReaderTests
{
    private const string PnpA = @"PCI\VEN_10DE&DEV_2684&SUBSYS_000010DE\A";
    private const string PnpB = @"PCI\VEN_10DE&DEV_2684&SUBSYS_000010DE\B";

    [Fact]
    public void CatalogUsesExactPhysicalParentsForIdenticalModelsAndOfficialSelectors()
    {
        var api = new Api();
        var fans = NvidiaNvmlReader.ReadFanIdentities(Inventory(Adapter(9, 1, PnpA), Adapter(3, 2, PnpB)), api)!;
        NvidiaNvmlFanIdentity[] expected = [new(PnpA, 0), new(PnpA, 1), new(PnpB, 0), new(PnpB, 1), new(PnpB, 2)];
        Assert.Equal(expected, fans);
        Assert.Equal([2, 1], api.Handles.Select(static device => (int)device).ToArray());
        Assert.Equal([1, 1, 2, 2], api.FanCountDevices.Select(static device => (int)device).ToArray());
        Assert.Empty(api.PercentQueries);
        Assert.Empty(api.RpmQueries);
        Assert.Equal("windows-nvml-fan|" + PnpA + "|fan:1", new NvidiaNvmlFanIdentity(" " + PnpA.ToLowerInvariant().Replace('\\', '/') + " ", 1).ExactIdentityKey);
        Assert.NotEqual(new NvidiaNvmlFanIdentity(PnpA, 0).ExactIdentityKey, new NvidiaNvmlFanIdentity(PnpB, 0).ExactIdentityKey);
    }

    [Fact]
    public void SuccessfulZeroAndNoNvidiaAreCompleteEmptyCatalogsWithoutGuessedFanZero()
    {
        var api = SingleApi();
        api.FanCounts[1] = 0;
        Assert.Empty(NvidiaNvmlReader.ReadFanIdentities(Inventory(Adapter(0, 1, PnpA)), api)!);
        Assert.Empty(api.PercentQueries);
        Assert.Empty(api.RpmQueries);
        var unused = new Api();
        Assert.Empty(NvidiaNvmlReader.ReadFanIdentities(Inventory(), unused)!);
        Assert.Empty(NvidiaNvmlReader.ReadFanIdentities(Inventory(Adapter(0, 1, PnpA, 0x1002)), unused)!);
        Assert.Equal(0, unused.InitCalls);
    }

    [Theory]
    [InlineData("init")]
    [InlineData("dll")]
    [InlineData("device-count")]
    [InlineData("device-count-export")]
    [InlineData("device-bound")]
    [InlineData("handle")]
    [InlineData("pci")]
    [InlineData("fan-count")]
    [InlineData("fan-count-export")]
    [InlineData("fan-bound")]
    public void UnavailableOrOverBoundCatalogNeverReturnsACompleteSubset(string failure)
    {
        var api = SingleApi();
        switch (failure)
        {
            case "init": api.Initialized = false; break;
            case "dll": api.MissingDll = true; break;
            case "device-count": api.CountStatus = 3; break;
            case "device-count-export": api.MissingDeviceCount = true; break;
            case "device-bound": api.DeviceCount = NvidiaNvmlReader.MaxFanDeviceCount + 1; break;
            case "handle": api.HandleStatus = 3; break;
            case "pci": api.PciStatus = 3; break;
            case "fan-count": api.FanCountStatus = 3; break;
            case "fan-count-export": api.MissingFanCount = true; break;
            case "fan-bound": api.FanCounts[1] = NvidiaNvmlReader.MaxFansPerDevice + 1; break;
        }
        Assert.Null(NvidiaNvmlReader.ReadFanIdentities(Inventory(Adapter(0, 1, PnpA)), api));
        var unknown = Assert.Single(Read([Target(PnpA, 0)], Inventory(Adapter(0, 1, PnpA)), api));
        Assert.Null(unknown.PercentOfMaxNoiseTolerance);
        Assert.Null(unknown.IntendedRpm);
        Assert.Empty(api.PercentQueries);
        Assert.Empty(api.RpmQueries);
        if (failure == "device-bound") Assert.Empty(api.Handles);
    }

    [Fact]
    public void FailedLaterParentDoesNotReturnEarlierFanSubset()
    {
        var api = new Api();
        api.FanCountStatuses[2] = 3;
        Assert.Null(NvidiaNvmlReader.ReadFanIdentities(TwoGpuInventory(), api));
    }

    [Theory]
    [InlineData(4, true)]
    [InlineData(5, false)]
    public void TotalFanBoundAcceptsCompleteBoundaryAndRejectsOverBoundWithoutTruncation(int deviceCount, bool available)
    {
        var api = new Api
        {
            DeviceCount = (uint)deviceCount,
            DeviceHandles = Enumerable.Range(1, deviceCount).Reverse().ToArray(),
            FanCounts = Enumerable.Range(1, deviceCount).ToDictionary(static device => device, static _ => NvidiaNvmlReader.MaxFansPerDevice),
            PciBus = Enumerable.Range(1, deviceCount).ToDictionary(static device => device, static device => (uint)device)
        };
        var inventory = Inventory(Enumerable.Range(1, deviceCount).Select(device => Adapter(device, (uint)device, PnpA + device)).ToArray());
        var fans = NvidiaNvmlReader.ReadFanIdentities(inventory, api);
        if (available)
        {
            Assert.Equal(NvidiaNvmlReader.MaxFanCount, fans!.Count);
            Assert.Equal(NvidiaNvmlReader.MaxFansPerDevice - 1, fans[^1].FanIndex);
        }
        else Assert.Null(fans);
        Assert.Empty(api.PercentQueries);
        Assert.Empty(api.RpmQueries);
    }

    [Theory]
    [InlineData("missing-device")]
    [InlineData("duplicate-native-parent")]
    [InlineData("duplicate-handle")]
    [InlineData("wrong-pci")]
    [InlineData("conflicting-windows-pci")]
    [InlineData("duplicate-pnp")]
    [InlineData("missing-physical")]
    [InlineData("stale-inventory")]
    [InlineData("incomplete-inventory")]
    [InlineData("invalid-pnp")]
    public void MissingOrConflictingPhysicalEvidenceNeverFallsBackToModelOrDisplayOrder(string scenario)
    {
        var inventory = TwoGpuInventory();
        var api = new Api();
        switch (scenario)
        {
            case "missing-device": api.DeviceCount = 1; break;
            case "duplicate-native-parent": api.PciBus[2] = 1; break;
            case "duplicate-handle": api.DeviceHandles = [1, 1]; break;
            case "wrong-pci": api.PciBus[2] = 7; break;
            case "conflicting-windows-pci": inventory = Inventory(Adapter(9, 1, PnpA), Adapter(3, 1, PnpB)); break;
            case "duplicate-pnp": inventory = Inventory(Adapter(9, 1, PnpA), Adapter(3, 2, PnpA)); break;
            case "missing-physical": inventory = Inventory(Adapter(0, 1, PnpA) with { PhysicalIdentities = [] }); break;
            case "stale-inventory": inventory = inventory with { Status = SamplingObservationStatus.Unavailable }; break;
            case "incomplete-inventory": inventory = inventory with { SkippedCount = 1 }; break;
            case "invalid-pnp": inventory = Inventory(Adapter(0, 1, "model name")); break;
        }
        Assert.Null(NvidiaNvmlReader.ReadFanIdentities(inventory, api));
        var unknown = Assert.Single(Read([Target(PnpA, 0)], inventory, api));
        Assert.Equal(new NvidiaNvmlFanIdentity(PnpA, 0), unknown.Fan);
        Assert.Null(unknown.PercentOfMaxNoiseTolerance);
        Assert.Null(unknown.IntendedRpm);
        Assert.Empty(api.PercentQueries);
        Assert.Empty(api.RpmQueries);
    }

    [Fact]
    public void SelectedParentAndFanOnlyReadRequestedValuesAndKeepZeroAndOverHundred()
    {
        var api = new Api();
        api.PercentValues[(1, 1)] = 105;
        api.RpmValues[(1, 1)] = 0;
        var targets = new[] { Target(PnpA, 1), Target(PnpA, 0, rpm: false) };
        var result = Read(targets, TwoGpuInventory(), api);
        Assert.Equal(targets.Select(static target => target.Fan), result.Select(static reading => reading.Fan));
        Assert.Equal<uint?>(105, result[0].PercentOfMaxNoiseTolerance);
        Assert.Equal<uint?>(0, result[0].IntendedRpm);
        Assert.Equal<uint?>(0, result[1].PercentOfMaxNoiseTolerance);
        Assert.Null(result[1].IntendedRpm);
        Assert.Equal(new[] { (1, 1U), (1, 0U) }, api.PercentQueries);
        Assert.Equal(new[] { (1, 1U) }, api.RpmQueries);
        Assert.Equal([1, 1], api.FanCountDevices.Select(static device => (int)device).ToArray());
    }

    [Theory]
    [InlineData("percent-status")]
    [InlineData("percent-export")]
    [InlineData("rpm-not-supported")]
    [InlineData("rpm-version")]
    [InlineData("rpm-export")]
    [InlineData("rpm-other")]
    public void PercentAndRpmFailureRemainIndependentWithoutEstimation(string failure)
    {
        var api = SingleApi();
        api.PercentValues[(1, 1)] = 123;
        api.RpmValues[(1, 1)] = 1700;
        switch (failure)
        {
            case "percent-status": api.PercentStatus = 3; break;
            case "percent-export": api.MissingPercent = true; break;
            case "rpm-not-supported": api.RpmStatus = 3; break;
            case "rpm-version": api.RpmStatus = 25; break;
            case "rpm-export": api.MissingRpm = true; break;
            case "rpm-other": api.RpmStatus = 999; break;
        }
        var reading = Assert.Single(Read([Target(PnpA, 1)], Inventory(Adapter(0, 1, PnpA)), api));
        Assert.Equal(failure.StartsWith("percent", StringComparison.Ordinal) ? null : (uint?)123, reading.PercentOfMaxNoiseTolerance);
        Assert.Equal(failure.StartsWith("rpm", StringComparison.Ordinal) ? null : (uint?)1700, reading.IntendedRpm);
        Assert.Equal(new[] { (1, 1U) }, api.PercentQueries);
        Assert.Equal(new[] { (1, 1U) }, api.RpmQueries);
    }

    [Fact]
    public void RpmOnlyPreservesZeroAndNeverReadsPercent()
    {
        var api = SingleApi();
        var reading = Assert.Single(Read([Target(PnpA, 0, percent: false)], Inventory(Adapter(0, 1, PnpA)), api));
        Assert.Null(reading.PercentOfMaxNoiseTolerance);
        Assert.Equal<uint?>(0, reading.IntendedRpm);
        Assert.Empty(api.PercentQueries);
        Assert.Equal(new[] { (1, 0U) }, api.RpmQueries);
    }

    [Fact]
    public void MultiGpuReadKeepsIndependentFanValuesWithReversedNativeAndWindowsOrders()
    {
        var api = new Api();
        api.PercentValues[(1, 1)] = 105;
        api.RpmValues[(1, 1)] = 1900;
        api.PercentValues[(2, 1)] = 44;
        api.RpmValues[(2, 1)] = 2400;
        var readings = Read([Target(PnpB, 1), Target(PnpA, 1)], TwoGpuInventory(), api);
        Assert.Equal(new NvidiaNvmlFanReading(new(PnpB, 1), 44, 2400), readings[0]);
        Assert.Equal(new NvidiaNvmlFanReading(new(PnpA, 1), 105, 1900), readings[1]);
        Assert.Equal(new[] { (2, 1U), (1, 1U) }, api.PercentQueries);
        Assert.Equal(api.PercentQueries, api.RpmQueries);
    }

    [Fact]
    public void UnselectedEmptyDemandNeverInitializesAndUnknownTargetsAreRetained()
    {
        var api = SingleApi();
        Assert.Empty(Read([], Inventory(), api));
        var disabled = Target(PnpA, 0, false, false);
        Assert.Equal(new NvidiaNvmlFanReading(disabled.Fan, null, null), Assert.Single(Read([disabled], Inventory(), api)));
        Assert.Equal(0, api.InitCalls);
        var targets = new[] { Target(PnpA, 7), Target(PnpB, 0), Target("invalid", 0) };
        var result = Read(targets, Inventory(Adapter(0, 1, PnpA)), api);
        Assert.Equal(targets.Select(static target => target.Fan), result.Select(static reading => reading.Fan));
        Assert.All(result, static reading => { Assert.Null(reading.PercentOfMaxNoiseTolerance); Assert.Null(reading.IntendedRpm); });
        Assert.Empty(api.PercentQueries);
        Assert.Empty(api.RpmQueries);
    }

    [Theory]
    [InlineData("count-change")]
    [InlineData("count-fails-after")]
    [InlineData("pci-reset")]
    public void ParentResetOrCountChangeInvalidatesItsEntireStagedReading(string scenario)
    {
        var api = new Api();
        api.PercentValues[(1, 1)] = 105;
        var aCountCalls = 0;
        api.OnCall = call =>
        {
            if (call == "fan-count:1" && ++aCountCalls == 2)
            {
                if (scenario == "count-change") api.FanCounts[1] = 3;
                if (scenario == "count-fails-after") api.FanCountStatuses[1] = 3;
                if (scenario == "pci-reset") api.PciBus[1] = 2;
            }
        };
        var result = Read([Target(PnpA, 1), Target(PnpA, 0), Target(PnpB, 0)], TwoGpuInventory(), api);
        Assert.Null(result[0].PercentOfMaxNoiseTolerance);
        Assert.Null(result[0].IntendedRpm);
        Assert.Null(result[1].PercentOfMaxNoiseTolerance);
        Assert.Null(result[1].IntendedRpm);
        Assert.Equal<uint?>(0, result[2].PercentOfMaxNoiseTolerance);
        Assert.Equal<uint?>(0, result[2].IntendedRpm);
    }

    [Fact]
    public void ChangingCountDoesNotPublishACatalogAndNextReadUsesFreshHandles()
    {
        var api = SingleApi();
        var calls = 0;
        api.OnCall = call => { if (call == "fan-count:1" && ++calls == 2) api.FanCounts[1] = 3; };
        var inventory = Inventory(Adapter(0, 1, PnpA));
        Assert.Null(NvidiaNvmlReader.ReadFanIdentities(inventory, api));
        api.OnCall = null;
        Assert.Equal(3, NvidiaNvmlReader.ReadFanIdentities(inventory, api)!.Count);
        Assert.Equal(2, api.InitCalls);
        Assert.Equal([1, 1], api.Handles.Select(static device => (int)device).ToArray());
    }

    [Theory]
    [InlineData("before")]
    [InlineData("count")]
    [InlineData("percent")]
    [InlineData("rpm")]
    [InlineData("fan-count-after")]
    public void CancellationNeverReturnsStagedResults(string point)
    {
        using var cancellation = new CancellationTokenSource();
        var api = SingleApi();
        var counts = 0;
        if (point == "before") cancellation.Cancel();
        api.OnCall = call =>
        {
            if ((point == "count" && call == "count")
                || (point == "percent" && call == "percent:1:1")
                || (point == "rpm" && call == "rpm:1:1")
                || (point == "fan-count-after" && call == "fan-count:1" && ++counts == 2)) cancellation.Cancel();
        };
        Assert.Throws<OperationCanceledException>(() => NvidiaNvmlReader.ReadFans(
            [Target(PnpA, 1)], Inventory(Adapter(0, 1, PnpA)), api, cancellation.Token));
        if (point == "before") Assert.Equal(0, api.InitCalls);
    }

    [Fact]
    public void RpmAbiUsesThreeUintFieldsVersionShift24AndUnsuffixedExport()
    {
        Assert.Equal(12, Marshal.SizeOf<NvmlFanSpeedInfoV1>());
        Assert.Equal(0, Marshal.OffsetOf<NvmlFanSpeedInfoV1>(nameof(NvmlFanSpeedInfoV1.Version)).ToInt32());
        Assert.Equal(4, Marshal.OffsetOf<NvmlFanSpeedInfoV1>(nameof(NvmlFanSpeedInfoV1.Fan)).ToInt32());
        Assert.Equal(8, Marshal.OffsetOf<NvmlFanSpeedInfoV1>(nameof(NvmlFanSpeedInfoV1.Speed)).ToInt32());
        Assert.Equal(0x0100000CU, NvmlFanSpeedInfoV1.StructVersion);
        var native = typeof(NvidiaNvmlReader).GetNestedType("NativeMethods", BindingFlags.NonPublic)!;
        var method = native.GetMethod("GetFanSpeedRpm", BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.Equal("nvmlDeviceGetFanSpeedRPM", method.GetCustomAttribute<DllImportAttribute>()!.EntryPoint);
        Assert.Equal(typeof(NvmlFanSpeedInfoV1).MakeByRefType(), method.GetParameters()[1].ParameterType);
    }

    private static IReadOnlyList<NvidiaNvmlFanReading> Read(IReadOnlyList<NvidiaNvmlFanReadTarget> targets,
        WindowsGpuAdapterInventoryRead inventory, Api api) => NvidiaNvmlReader.ReadFans(targets, inventory, api, CancellationToken.None);
    private static NvidiaNvmlFanReadTarget Target(string pnp, uint fan, bool percent = true, bool rpm = true) => new(new(pnp, fan), percent, rpm);
    private static Api SingleApi() => new() { DeviceHandles = [1], DeviceCount = 1 };
    private static WindowsGpuAdapterInventoryRead TwoGpuInventory() => Inventory(Adapter(9, 1, PnpA), Adapter(3, 2, PnpB));
    private static WindowsGpuAdapterInventoryRead Inventory(params WindowsGpuAdapter[] adapters) =>
        new(SamplingObservationStatus.Current, 1, DateTimeOffset.UtcNow.UtcTicks, (uint)adapters.Length, 0, 0, 1, adapters);
    private static WindowsGpuAdapter Adapter(int index, uint bus, string pnp, uint vendor = 0x10DE) =>
        new(index, "Identical GPU model", vendor, 0x2684, 0x10DE, new() { LowPart = (uint)index + 1 }, false, 8UL << 30, WindowsGpuAdapterKind.Dedicated)
        {
            PhysicalIdentityStatus = SamplingObservationStatus.Current,
            PhysicalIdentities = [new(0, pnp, WindowsGpuPnpKind.Pci, true, true, 0, bus, 0, 0, vendor, 0x2684, 0x10DE, 0, 1,
                WindowsGpuIdentityEvidence.CmPciAddress, WindowsGpuPhysicalIdentityStatus.Bound, 0)]
        };

    private sealed class Api : INvmlFanApi
    {
        internal bool Initialized = true, MissingDll, MissingDeviceCount, MissingFanCount, MissingPercent, MissingRpm;
        internal int CountStatus, HandleStatus, PciStatus, FanCountStatus, PercentStatus, RpmStatus, InitCalls;
        internal uint DeviceCount = 2;
        internal int[] DeviceHandles = [2, 1];
        internal Dictionary<int, uint> FanCounts = new() { [1] = 2, [2] = 3 };
        internal Dictionary<int, uint> PciBus = new() { [1] = 1, [2] = 2 };
        internal Dictionary<int, int> FanCountStatuses = [];
        internal Dictionary<(int, uint), uint> PercentValues = [], RpmValues = [];
        internal List<IntPtr> Handles = [], FanCountDevices = [];
        internal List<(int, uint)> PercentQueries = [], RpmQueries = [];
        internal Action<string>? OnCall;

        public bool Initialize()
        {
            InitCalls++;
            if (MissingDll) throw new DllNotFoundException();
            return Initialized;
        }
        public int Count(out uint count)
        {
            OnCall?.Invoke("count");
            if (MissingDeviceCount) throw new EntryPointNotFoundException();
            count = DeviceCount;
            return CountStatus;
        }
        public int Handle(uint index, out IntPtr device)
        {
            device = (IntPtr)DeviceHandles[index];
            Handles.Add(device);
            return HandleStatus;
        }
        public int Pci(IntPtr device, out NvmlPciInfo pci)
        {
            var bus = PciBus[(int)device];
            pci = new() { Domain = 0, Bus = bus, Device = 0, BusId = $"00000000:{bus:X2}:00.0", BusIdLegacy = $"0000:{bus:X2}:00.0",
                PciDeviceId = 0x268410DE, PciSubSystemId = 0x10DE };
            return PciStatus;
        }
        public int FanCount(IntPtr device, out uint count)
        {
            OnCall?.Invoke("fan-count:" + (int)device);
            FanCountDevices.Add(device);
            if (MissingFanCount) throw new EntryPointNotFoundException();
            count = FanCounts[(int)device];
            return FanCountStatuses.GetValueOrDefault((int)device, FanCountStatus);
        }
        public int Percent(IntPtr device, uint fan, out uint percent)
        {
            OnCall?.Invoke($"percent:{(int)device}:{fan}");
            PercentQueries.Add(((int)device, fan));
            if (MissingPercent) throw new EntryPointNotFoundException();
            percent = PercentValues.GetValueOrDefault(((int)device, fan));
            return PercentStatus;
        }
        public int Rpm(IntPtr device, ref NvmlFanSpeedInfoV1 speed)
        {
            OnCall?.Invoke($"rpm:{(int)device}:{speed.Fan}");
            RpmQueries.Add(((int)device, speed.Fan));
            Assert.Equal(0x0100000CU, speed.Version);
            Assert.Equal(0U, speed.Speed);
            if (MissingRpm) throw new EntryPointNotFoundException();
            speed.Speed = RpmValues.GetValueOrDefault(((int)device, speed.Fan));
            return RpmStatus;
        }
    }
}
