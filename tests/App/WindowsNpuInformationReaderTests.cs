using System.Runtime.InteropServices;
using System.Text;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.ReadWrite.Windows.Devices.Models;
using ResourceManager.ReadWrite.Windows.Devices;
using ResourceManager.ReadWrite.Windows.Devices.Npu;

namespace Resource_Manager_APP.Tests;

public sealed class WindowsNpuInformationReaderTests
{
    private const string FirstId = @"PCI\VEN_8086&DEV_1234\ONE";
    private const string SecondId = @"PCI\VEN_8086&DEV_1234\TWO";

    [Fact]
    public void ReadsOnlyNpuHardwareAttributeAndUsesOfficialPropertyPayloads()
    {
        var api = new RecordedApi([new()]);
        var reader = new WindowsNpuInformationReader(api, (luid, vendor, device, maximum) =>
        {
            Assert.Equal(7U, luid.LowPart);
            Assert.Equal(0x8086U, vendor);
            Assert.Equal(0x1234U, device);
            Assert.Equal(64U, maximum);
            return Bound(Physical(0, FirstId));
        });

        var result = Assert.Single(reader.Read(Snapshot(Device(FirstId)))!);

        Assert.Equal(new Guid("D46140C4-ADD7-451B-9E56-06FE8C3B58ED"), api.Filter);
        Assert.Equal(new[] { api.Filter }, api.AttributeChecks);
        Assert.Equal(new[] { (11U, (nuint)1), (0U, (nuint)8), (3U, (nuint)16) }, api.PropertyReads);
        Assert.Equal("windows-npu|" + FirstId, result.DeviceIdentity);
        Assert.Equal(FirstId, result.PnpInstanceId);
        Assert.Equal(new nint[] { 10, 2, 1 }, api.Released);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void NameAndSetupClassCannotReplaceHardwareNpuEvidence(bool npu, bool hardware)
    {
        var api = new RecordedApi([new() { Npu = npu, Hardware = hardware }]);
        var reader = new WindowsNpuInformationReader(api, (_, _, _, _) => throw new InvalidOperationException("Must not bridge rejected adapters."));
        var native = Device(FirstId) with { FriendlyName = "NPU AI Generic ML", ClassName = "ComputeAccelerator" };

        Assert.Empty(reader.Read(Snapshot(native))!);
        Assert.Equal(new nint[] { 10, 2, 1 }, api.Released);
    }

    [Fact]
    public void OrdinaryNameAndNonDisplayClassDoNotRejectDeclaredNpu()
    {
        var result = Assert.Single(Reader(new RecordedApi([new()]), Bound(Physical(0, FirstId)))
            .Read(Snapshot(Device(FirstId) with { FriendlyName = "Device", ClassName = "System" }))!);
        Assert.Equal("Device", result.NameDeclared);
    }

    [Fact]
    public void SuccessfulEmptyEnumerationIsDistinctFromUnavailable()
    {
        var api = new RecordedApi([]);
        Assert.Empty(Reader(api, WindowsGpuIdentityRead.Invalid).Read(Snapshot())!);
        Assert.Equal(new nint[] { 2, 1 }, api.Released);
    }

    [Theory]
    [InlineData("factory")]
    [InlineData("list")]
    [InlineData("unsupported-attribute")]
    [InlineData("count")]
    [InlineData("stale")]
    [InlineData("stale-after-read")]
    [InlineData("adapter")]
    [InlineData("invalid")]
    [InlineData("removed-after-properties")]
    [InlineData("zero-luid")]
    [InlineData("missing-property")]
    [InlineData("failed-property")]
    [InlineData("missing-luid")]
    [InlineData("missing-hardware-id")]
    [InlineData("failed-hardware-id")]
    [InlineData("api-exception")]
    public void IncompleteNativeDirectoryReturnsNullAndReleasesAcquiredObjects(string failure)
    {
        var api = new RecordedApi([new()]) { Failure = failure };
        var result = Reader(api, Bound(Physical(0, FirstId))).Read(Snapshot(Device(FirstId)));

        Assert.Null(result);
        if (failure == "factory") Assert.Equal(new nint[] { 1 }, api.Released);
        else if (failure is "list" or "unsupported-attribute" or "count" or "stale" or "api-exception")
            Assert.Equal(new nint[] { 2, 1 }, api.Released);
        else Assert.Equal(new nint[] { 10, 2, 1 }, api.Released);
    }

    [Fact]
    public void NativeSnapshotErrorPreventsAnyEnumeration()
    {
        var api = new RecordedApi([new()]);
        Assert.Null(Reader(api, WindowsGpuIdentityRead.Invalid).Read(Snapshot() with { Error = "partial PnP" }));
        Assert.Equal(0, api.FactoryCalls);
    }

    [Theory]
    [InlineData(WindowsGpuPhysicalIdentityStatus.Unavailable)]
    [InlineData(WindowsGpuPhysicalIdentityStatus.Conflict)]
    [InlineData(WindowsGpuPhysicalIdentityStatus.Removed)]
    public void AnyFailedPhysicalMemberMakesWholeDirectoryUnavailable(WindowsGpuPhysicalIdentityStatus status)
    {
        var read = Bound(Physical(0, FirstId), Physical(1, SecondId) with { Status = status });
        Assert.Null(Reader(new RecordedApi([new()]), read).Read(Snapshot(Device(FirstId), Device(SecondId))));
    }

    [Fact]
    public void BridgeFailureAndIncompleteBridgeCannotBecomeSuccessfulEmpty()
    {
        foreach (var read in new[] { WindowsGpuIdentityRead.Invalid,
            Bound(), Bound(Physical(0, FirstId)) with { SkippedCount = 1 },
            Bound(Physical(0, FirstId)) with { Status = SamplingObservationStatus.Partial } })
            Assert.Null(Reader(new RecordedApi([new()]), read).Read(Snapshot(Device(FirstId))));
    }

    [Fact]
    public void MissingCurrentPnpMappingRejectsWholeDirectory()
    {
        Assert.Null(Reader(new RecordedApi([new()]), Bound(Physical(0, FirstId), Physical(1, SecondId)))
            .Read(Snapshot(Device(FirstId))));
    }

    [Fact]
    public void DuplicatePhysicalInstanceOrIndexCannotPublishConflictingObjects()
    {
        foreach (var read in new[] { Bound(Physical(0, FirstId), Physical(1, FirstId.ToLowerInvariant())),
            Bound(Physical(0, FirstId), Physical(0, SecondId)), Bound(Physical(5, FirstId)) })
            Assert.Null(Reader(new RecordedApi([new()]), read).Read(Snapshot(Device(FirstId), Device(SecondId))));
    }

    [Fact]
    public void DuplicateLuidCannotHideAmbiguousNativeEnumeration()
    {
        var api = new RecordedApi([new(), new()]);
        Assert.Null(Reader(api, Bound(Physical(0, FirstId))).Read(Snapshot(Device(FirstId))));
        Assert.Equal(new nint[] { 10, 11, 2, 1 }, api.Released);
    }

    [Fact]
    public void SeparateLogicalAdaptersCannotPublishSamePhysicalInstanceTwice()
    {
        var api = new RecordedApi([new(), new() { Luid = new AdapterLuid { LowPart = 8 } }]);
        Assert.Null(Reader(api, Bound(Physical(0, FirstId))).Read(Snapshot(Device(FirstId))));
    }

    [Fact]
    public void DuplicatePnpValuesAreCheckedEvenWhenSnapshotDictionaryKeysDiffer()
    {
        var native = new DeviceTopologyNativeSnapshot(new Dictionary<string, DeviceTopologyNativeDevice>
        {
            ["record-one"] = Device(FirstId), ["record-two"] = Device(FirstId.ToLowerInvariant())
        }, null);
        Assert.Null(Reader(new RecordedApi([new()]), Bound(Physical(0, FirstId))).Read(native));
    }

    [Fact]
    public void SameNamesAndMultiplePhysicalMembersKeepEachExactPnpIdentity()
    {
        var api = new RecordedApi([new()]);
        var read = Bound(Physical(0, FirstId), Physical(1, SecondId));

        var result = Reader(api, read).Read(Snapshot(Device(FirstId), Device(SecondId)))!;

        Assert.Equal(new[] { FirstId, SecondId }, result.Select(static row => row.PnpInstanceId));
        Assert.All(result, static row => Assert.Equal("same declared name", row.NameDeclared));
        Assert.Equal(2, result.Select(static row => row.DeviceIdentity).Distinct().Count());
    }

    [Fact]
    public void NonPciPhysicalIdentityUsesSameExactBridgeAndCurrentPnpFacts()
    {
        const string id = @"ACPI\VENDOR1234\0";
        var physical = Physical(0, id) with { Status = WindowsGpuPhysicalIdentityStatus.NonPci,
            PnpKind = WindowsGpuPnpKind.Other, PciAddressValid = false };
        var result = Assert.Single(Reader(new RecordedApi([new()]), Bound(physical)).Read(Snapshot(Device(id)))!);
        Assert.Equal(id, result.PnpInstanceId);
    }

    [Fact]
    public void DriverNameAndLocationsComeOnlyFromSamePnpSnapshotAndPathsAreCopied()
    {
        var paths = new[] { "ACPI(_SB_)#ACPI(NPU0)", "reported second path" };
        var native = Device(FirstId.ToLowerInvariant()) with
        {
            FriendlyName = null, DeviceDescription = "driver description", LocationInfo = "reported location", LocationPaths = paths,
            DriverInformation = new DeviceTopologyDriverInformation(FirstId) { VersionDeclared = "31.0.1" }
        };
        var read = Bound(Physical(0, FirstId) with { DriverVersion = "must not borrow bridge version" });
        var result = Assert.Single(Reader(new RecordedApi([new()]), read).Read(Snapshot(native))!);
        paths[0] = "modified after read";

        Assert.Equal("driver description", result.NameDeclared);
        Assert.Equal("31.0.1", result.DriverVersionDeclared);
        Assert.Equal("reported location", result.LocationInformationReported);
        Assert.Equal("ACPI(_SB_)#ACPI(NPU0)", result.LocationPathsDeclared![0]);
        Assert.Equal(FirstId, result.PnpInstanceId);
    }

    [Fact]
    public void MismatchedDriverDevnodeDoesNotBorrowVersion()
    {
        var native = Device(FirstId) with { DriverInformation = new DeviceTopologyDriverInformation(SecondId) { VersionDeclared = "wrong" } };
        var result = Assert.Single(Reader(new RecordedApi([new()]), Bound(Physical(0, FirstId))).Read(Snapshot(native))!);
        Assert.Null(result.DriverVersionDeclared);
    }

    [Fact]
    public void AbsentDeclarationsAndSuccessfulEmptyLocationPathsRemainEmpty()
    {
        var native = Device(FirstId) with { FriendlyName = null, DeviceDescription = null, DriverInformation = null,
            LocationInfo = null, LocationPaths = [] };
        var result = Assert.Single(Reader(new RecordedApi([new()]), Bound(Physical(0, FirstId))).Read(Snapshot(native))!);
        Assert.Null(result.NameDeclared);
        Assert.Null(result.DriverVersionDeclared);
        Assert.Null(result.LocationInformationReported);
        Assert.Empty(result.LocationPathsDeclared!);
    }

    [Fact]
    public void NativeAbiMatchesOfficialHeader()
    {
        Assert.Equal(8, Marshal.SizeOf<AdapterLuid>());
        Assert.Equal(16, Marshal.SizeOf<WindowsNpuDxCoreHardwareId>());
        Assert.Equal(4, Marshal.OffsetOf<WindowsNpuDxCoreHardwareId>(nameof(WindowsNpuDxCoreHardwareId.DeviceId)).ToInt32());
        Assert.Equal(3U, (uint)WindowsNpuDxCoreProperty.HardwareId);
        Assert.Equal(11U, (uint)WindowsNpuDxCoreProperty.IsHardware);
        Assert.Equal(3, (int)WindowsNpuDxCoreFactorySlot.CreateAdapterList);
        Assert.Equal(3, (int)WindowsNpuDxCoreListSlot.GetAdapter);
        Assert.Equal(4, (int)WindowsNpuDxCoreListSlot.GetAdapterCount);
        Assert.Equal(5, (int)WindowsNpuDxCoreListSlot.IsStale);
        Assert.Equal(4, (int)WindowsNpuDxCoreAdapterSlot.IsAttributeSupported);
        Assert.Equal(6, (int)WindowsNpuDxCoreAdapterSlot.GetProperty);
        Assert.Equal(12, (int)WindowsNpuDxCoreAdapterSlot.GetFactory);
    }

    [Fact]
    public void UnavailableLocationPathsCannotBecomeAnAvailableEmptyList()
    {
        var device = Device(FirstId) with { LocationPathsAvailable = false, LocationPaths = [] };
        var result = Assert.Single(Reader(new RecordedApi([new()]), Bound(Physical(0, FirstId)))
            .Read(Snapshot(device))!);
        Assert.Null(result.LocationPathsDeclared);
        Assert.Equal(FirstId, result.PnpInstanceId);
        Assert.Equal("same declared name", result.NameDeclared);
    }

    [Fact]
    public void NativeLocationPathsValidateTheActualMultiStringDeclaration()
    {
        Assert.Equal(new[] { "PCIROOT(0)#PCI(0100)", "ACPI(_SB_)#NPU0" },
            WindowsDeviceTopologyNativeReader.DecodeLocationPaths(
                Encoding.Unicode.GetBytes("PCIROOT(0)#PCI(0100)\0ACPI(_SB_)#NPU0\0\0"), 7));
        Assert.Empty(WindowsDeviceTopologyNativeReader.DecodeLocationPaths([0, 0], 7)!);
        Assert.Empty(WindowsDeviceTopologyNativeReader.DecodeLocationPaths([0, 0, 0, 0], 7)!);
        Assert.Null(WindowsDeviceTopologyNativeReader.DecodeLocationPaths([], 7));
        Assert.Null(WindowsDeviceTopologyNativeReader.DecodeLocationPaths([0, 0, 0], 7));
        Assert.Null(WindowsDeviceTopologyNativeReader.DecodeLocationPaths([0, 0, 0, 0], 1));
        Assert.Null(WindowsDeviceTopologyNativeReader.DecodeLocationPaths(Encoding.Unicode.GetBytes("unterminated\0"), 7));
        Assert.Null(WindowsDeviceTopologyNativeReader.DecodeLocationPaths([0, 0xD8, 0, 0, 0, 0], 7));
        Assert.Null(WindowsDeviceTopologyNativeReader.DecodeLocationPaths(Encoding.Unicode.GetBytes("bad\npath\0\0"), 7));
    }

    private static WindowsNpuInformationReader Reader(RecordedApi api, WindowsGpuIdentityRead result)
        => new(api, (_, _, _, _) => result);

    private static DeviceTopologyNativeSnapshot Snapshot(params DeviceTopologyNativeDevice[] devices)
        => new(devices.ToDictionary(static value => value.DeviceId, StringComparer.OrdinalIgnoreCase), null);

    private static DeviceTopologyNativeDevice Device(string id)
        => new(id, "same declared name", "description", "vendor", "service", "driver", "ComputeAccelerator", null,
            "PCI", "location", [], [], [], null, 0, 0) { LocationPathsAvailable = true };

    private static WindowsGpuIdentityRead Bound(params WindowsGpuPhysicalIdentity[] identities)
        => new(SamplingObservationStatus.Current, 0, identities, 0);

    private static WindowsGpuPhysicalIdentity Physical(uint index, string id)
        => new(index, id, WindowsGpuPnpKind.Pci, true, false, 0, 0, index, 0, 0x8086, 0x1234, 0, 0, 0,
            WindowsGpuIdentityEvidence.KmtLuidOpen | WindowsGpuIdentityEvidence.KmtHardwarePnpKey
            | WindowsGpuIdentityEvidence.CmExactDevNode | WindowsGpuIdentityEvidence.CmCanonicalInstanceRoundTrip
            | WindowsGpuIdentityEvidence.KmtDeviceIds, WindowsGpuPhysicalIdentityStatus.Bound, 0);

    private sealed record RecordedAdapter
    {
        internal bool Npu { get; init; } = true;
        internal bool Hardware { get; init; } = true;
        internal AdapterLuid Luid { get; init; } = new() { LowPart = 7 };
    }

    private sealed class RecordedApi(RecordedAdapter[] adapters) : IWindowsNpuDxCoreApi
    {
        internal string? Failure { get; init; }
        internal Guid Filter { get; private set; }
        internal List<Guid> AttributeChecks { get; } = [];
        internal List<(uint Property, nuint Size)> PropertyReads { get; } = [];
        internal List<nint> Released { get; } = [];
        internal int FactoryCalls { get; private set; }
        private int _staleChecks;
        private int _validChecks;

        public int CreateFactory(out nint factory)
        {
            FactoryCalls++;
            factory = 1;
            return Failure == "factory" ? -1 : 0;
        }

        public int CreateAdapterList(nint factory, Guid attribute, out nint list)
        {
            Filter = attribute;
            list = 2;
            return Failure is "list" or "unsupported-attribute" ? unchecked((int)0x887A0004) : 0;
        }

        public uint GetAdapterCount(nint list)
        {
            if (Failure == "api-exception") throw new COMException("recorded unavailable API");
            return Failure == "count" ? 257U : checked((uint)adapters.Length);
        }

        public bool IsStale(nint list) => Failure == "stale" || (Failure == "stale-after-read" && ++_staleChecks > 1);
        public int GetAdapter(nint list, uint index, out nint adapter)
        {
            adapter = checked((nint)(10 + index));
            return Failure == "adapter" ? -1 : 0;
        }

        public bool IsValid(nint adapter)
            => Failure != "invalid" && !(Failure == "removed-after-properties" && ++_validChecks > 1);

        public bool IsAttributeSupported(nint adapter, Guid attribute)
        {
            AttributeChecks.Add(attribute);
            return adapters[checked((int)adapter - 10)].Npu;
        }

        public bool IsPropertySupported(nint adapter, WindowsNpuDxCoreProperty property)
            => Failure != "missing-property"
                && !(Failure == "missing-luid" && property == WindowsNpuDxCoreProperty.InstanceLuid)
                && !(Failure == "missing-hardware-id" && property == WindowsNpuDxCoreProperty.HardwareId);

        public int GetProperty(nint adapter, WindowsNpuDxCoreProperty property, nuint size, nint data)
        {
            PropertyReads.Add(((uint)property, size));
            if (Failure == "failed-property" || (Failure == "failed-hardware-id" && property == WindowsNpuDxCoreProperty.HardwareId)) return -1;
            var row = adapters[checked((int)adapter - 10)];
            switch (property)
            {
                case WindowsNpuDxCoreProperty.IsHardware:
                    Assert.Equal((nuint)1, size);
                    Marshal.WriteByte(data, row.Hardware ? (byte)1 : (byte)0);
                    break;
                case WindowsNpuDxCoreProperty.InstanceLuid:
                    Assert.Equal((nuint)8, size);
                    Marshal.StructureToPtr(Failure == "zero-luid" ? default(AdapterLuid) : row.Luid, data, false);
                    break;
                case WindowsNpuDxCoreProperty.HardwareId:
                    Assert.Equal((nuint)16, size);
                    Marshal.StructureToPtr(new WindowsNpuDxCoreHardwareId { VendorId = 0x8086, DeviceId = 0x1234 }, data, false);
                    break;
                default: throw new InvalidOperationException("Unexpected property.");
            }
            return 0;
        }

        public void Release(nint instance) => Released.Add(instance);
    }
}
