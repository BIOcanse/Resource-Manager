using System.Collections.Immutable;
using System.Text.Json;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Windows.Cpu;
using ResourceManager.ReadWrite.Windows.Cpu.Models;
using ResourceManager.ReadWrite.Windows.Cpu.Snapshots;

namespace Resource_Manager_APP.Tests;

public sealed class AmdHardwareTopologyTests
{
    [Fact]
    public void FourExplicitLevelsPreserveFullApicAndQueryEachRegisterOnce()
    {
        var reads = new Dictionary<(int, int), int>();
        var data = Cpuid(0x80000015, observe: (leaf, subleaf) =>
            reads[(leaf, subleaf)] = reads.GetValueOrDefault((leaf, subleaf)) + 1);
        var topology = Assert.IsType<CpuAmdExtendedTopology>(data.AmdExtendedTopology);
        Assert.Equal(0x80000015U, topology.ExtendedApicId);
        Assert.Equal(new CpuAmdTopologyLevel(1, 2), topology.Core);
        Assert.Equal(new CpuAmdTopologyLevel(2, 4), topology.Complex);
        Assert.Equal(new CpuAmdTopologyLevel(3, 8), topology.Ccd);
        Assert.Equal(new CpuAmdTopologyLevel(4, 16), topology.Socket);
        Assert.All(reads.Values, value => Assert.Equal(1, value));
        var raw = data.IdentityRegisters.Where(static row => row.Leaf == 0x80000026).ToArray();
        Assert.Equal(5, raw.Length);
        Assert.Equal(new uint[] { 0, 1, 2, 3, 4 }, raw.Select(static row => row.Subleaf));
        Assert.Equal(0xABCD0002U, raw[0].Ebx);
        Assert.Equal(0x80000015U, raw[0].Edx);
    }

    [Theory]
    [InlineData("GenuineIntel", 0x80000026U)]
    [InlineData("AuthenticAMD", 0x80000025U)]
    [InlineData("HygonGenuine", 0x80000026U)]
    public void VendorAndMaximumGatePreventAnyExtendedTopologyQuery(string vendor, uint maximum)
    {
        var calls = 0;
        var data = Cpuid(0, vendor, maximum, observe: (leaf, _) =>
        { if (leaf == unchecked((int)0x80000026)) calls++; });
        Assert.Null(data.AmdExtendedTopology);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData("unknown-type")]
    [InlineData("wrong-level")]
    [InlineData("apic-changed")]
    [InlineData("decreasing-shift")]
    [InlineData("decreasing-count")]
    [InlineData("count-exceeds-width")]
    [InlineData("missing-complex")]
    [InlineData("missing-end")]
    [InlineData("zero-count-active-type")]
    [InlineData("invalid-type-active-count")]
    public void MalformedHierarchyIsUnavailableWithoutRepairOrFallback(string fault)
    {
        var queries = 0;
        var data = Cpuid(0, mutate: (index, value) => (fault, index) switch
        {
            ("unknown-type", 1) => (value.Eax, value.Ebx, 0x501, value.Edx),
            ("wrong-level", 1) => (value.Eax, value.Ebx, 0x202, value.Edx),
            ("apic-changed", 2) => (value.Eax, value.Ebx, value.Ecx, 1),
            ("decreasing-shift", 2) => (1, 2, value.Ecx, value.Edx),
            ("decreasing-count", 2) => (3, 2, value.Ecx, value.Edx),
            ("count-exceeds-width", 0) => (0, 2, value.Ecx, value.Edx),
            ("missing-complex", 1) => (0, 0, 1, value.Edx),
            ("missing-end", 4) => (4, 16, 0x404, value.Edx),
            ("zero-count-active-type", 2) => (value.Eax, 0, value.Ecx, value.Edx),
            ("invalid-type-active-count", 2) => (value.Eax, value.Ebx, 2, value.Edx),
            _ => value
        }, observe: (leaf, _) => { if (leaf == unchecked((int)0x80000026)) queries++; });
        Assert.Null(data.AmdExtendedTopology);
        Assert.InRange(queries, 1, 5);
        Assert.Equal("AuthenticAMD", data.Identity.Vendor);
        Assert.NotNull(data.Signature);
    }

    [Fact]
    public void EqualShiftsAndSparseVisibleMembersDoNotRequireFillingHardwareCapacity()
    {
        var data = Cpuid(uint.MaxValue, mutate: (index, value) => index < 4
            ? (1, 1, value.Ecx, value.Edx) : value);
        var topology = Assert.IsType<CpuAmdExtendedTopology>(data.AmdExtendedTopology);
        Assert.Equal(uint.MaxValue, topology.ExtendedApicId);
        Assert.Equal((byte)1, topology.Socket.ApicShift);
        Assert.NotNull(Create(Information([0x80000000U, 0x80000001U])).Devices
            .Single(static device => device.ObjectType == "cpu_ccd"));
    }

    [Fact]
    public void TwoCcdsAndFourComplexesLinkExactCoresAndGroupAddresses()
    {
        var information = Information(Enumerable.Range(0, 16).Select(static n => 0x80000000U + (uint)n).ToArray());
        var snapshot = Create(information);
        var ccds = snapshot.Devices.Where(static device => device.ObjectType == "cpu_ccd").ToArray();
        var complexes = snapshot.Devices.Where(static device => device.ObjectType == "cpu_ccx").ToArray();
        Assert.Equal(2, ccds.Length);
        Assert.Equal(4, complexes.Length);
        Assert.Contains("ccd:10000000/shift:3", ccds[0].DeviceIdentity);
        Assert.Contains("ccd:10000001/shift:3", ccds[1].DeviceIdentity);
        var rows = ccds.SelectMany(device => Field(device, "cpu_amd_ccd_core_membership").Value!.Value
            .Deserialize<CpuAmdCcdCoreMembership[]>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!).ToArray();
        Assert.Equal(8, rows.Length);
        foreach (var row in rows)
        {
            Assert.Contains(snapshot.Devices, device => device.ObjectType == "cpu_core" && device.DeviceIdentity == row.CoreDeviceIdentity);
            Assert.Contains(complexes, device => device.DeviceIdentity == row.ComplexDeviceIdentity);
            Assert.Equal(2, row.LogicalProcessorAddresses.Count);
            Assert.All(row.LogicalProcessorAddresses, address => Assert.Equal((ushort)2, address.ProcessorGroup));
        }
        Assert.Equal(16, rows.SelectMany(static row => row.LogicalProcessorAddresses).Distinct().Count());
        Assert.All(ccds.SelectMany(static device => device.Fields), field => Assert.Equal("amd.cpuid.80000026", field.Definition.SourceId));
        var reordered = information with { PhysicalCores = information.PhysicalCores!.Reverse().ToArray(),
            ThreadCpuid = information.ThreadCpuid!.Reverse().ToArray() };
        Assert.Equal(HardwareJson(snapshot), HardwareJson(Create(reordered)));
    }

    [Fact]
    public void DisjointDomainsAllowDifferentWidthsAndTheEndOfTheApicAddressSpace()
    {
        uint[] apics = [0xFFFFFFD0U, 0xFFFFFFD1U, 0xFFFFFFF0U, 0xFFFFFFF1U];
        var information = Information(apics) with
        {
            Packages = [new("package-A", [128,129]), new("package-B", [130,131])]
        };
        information = information with { ThreadCpuid = information.ThreadCpuid!.Select((capture, position) =>
            capture with { Data = Cpuid(apics[position], mutate: (index, value) => position < 2 && index < 4
                ? (index + 2, 1 << (index + 2), value.Ecx, value.Edx) : value) }).ToArray() };
        var snapshot = Create(information);
        Assert.Equal(2, snapshot.Devices.Count(static device => device.ObjectType == "cpu_ccd"));
        Assert.Equal(2, snapshot.Devices.Count(static device => device.ObjectType == "cpu_ccx"));
        Assert.All(snapshot.Devices.Where(static device => device.ObjectType is "cpu_ccd" or "cpu_ccx")
            .SelectMany(static device => device.Fields), field => Assert.NotNull(field.Value));
        Assert.Equal(HardwareJson(snapshot), HardwareJson(Create(information with
            { PhysicalCores = information.PhysicalCores!.Reverse().ToArray(),
              ThreadCpuid = information.ThreadCpuid!.Reverse().ToArray() })));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void OverlappingDomainsAtEveryLevelClearKnownHardware(int level)
    {
        var original = Information(level == 3
            ? [0x80000000U, 0x80000001U, 0x80000010U, 0x80000011U]
            : [0x80000000U, 0x80000001U, 0x80000002U, 0x80000003U]);
        if (level == 3) original = original with
            { Packages = [new("package-A", [128,129]), new("package-B", [130,131])] };
        var current = Create(original);
        var captures = original.ThreadCpuid!.Select((capture, position) => capture with
        {
            Data = Cpuid(0x80000000U + (uint)position, mutate: (index, value) =>
                index == level && (level < 2 ? position < 2 : position >= 2)
                    ? (level < 2 ? index + 2 : index, 1 << (level < 2 ? index + 2 : index), value.Ecx, value.Edx)
                    : value)
        }).ToArray();
        Assert.All(captures, capture => Assert.NotNull(capture.Data!.AmdExtendedTopology));
        var invalid = original with { ThreadCpuid = captures };
        Assert.DoesNotContain(Create(invalid).Devices, static device => device.ObjectType is "cpu_ccd" or "cpu_ccx");
        var cleared = Create(invalid, current);
        var known = cleared.Devices.Where(static device => device.ObjectType is "cpu_ccd" or "cpu_ccx").ToArray();
        Assert.Equal(current.Devices.Count(static device => device.ObjectType is "cpu_ccd" or "cpu_ccx"), known.Length);
        Assert.All(known.SelectMany(static device => device.Fields), field => Assert.Null(field.Value));
        Assert.NotNull(Field(cleared.Devices.Single(static device => device.DeviceIdentity == "windows-cpu-thread|2:0"),
            "cpu_all_cpuid_vendor").Value);
        Assert.Equal(HardwareJson(current), HardwareJson(Create(original, cleared)));
    }

    [Fact]
    public void HardwareSocketsMatchTheirExactWindowsPackages()
    {
        var information = Information([0x80000000U, 0x80000001U, 0x80000010U, 0x80000011U]) with
        { Packages = [new("package-A", [128,129]), new("package-B", [130,131])] };
        var ccds = Create(information).Devices.Where(static device => device.ObjectType == "cpu_ccd").ToArray();
        Assert.Equal(2, ccds.Length);
        Assert.Contains(ccds, static device => device.DeviceIdentity.Contains("socket:08000000/shift:4", StringComparison.Ordinal));
        Assert.Contains(ccds, static device => device.DeviceIdentity.Contains("socket:08000001/shift:4", StringComparison.Ordinal));
    }

    [Fact]
    public void MissingOsGroupsClearKnownHardwareAndSuccessfulNonAmdCaptureRemovesIt()
    {
        var information = Information([0x80000000U, 0x80000001U]);
        var current = Create(information);
        foreach (var failed in new[] { information with { PhysicalCores = null },
            information with { Packages = null }, information with { ThreadCpuid = null } })
            Assert.All(Create(failed, current).Devices.Where(static device => device.ObjectType is "cpu_ccd" or "cpu_ccx")
                .SelectMany(static device => device.Fields), field => Assert.Null(field.Value));
        var unsupported = information with { ThreadCpuid = information.ThreadCpuid!.Select(capture => capture with
            { Data = Cpuid((uint)capture.LogicalProcessorId, maximum: 0x80000025) }).ToArray() };
        Assert.All(Create(unsupported, current).Devices.Where(static device => device.ObjectType is "cpu_ccd" or "cpu_ccx")
            .SelectMany(static device => device.Fields), field => Assert.Null(field.Value));
        var intel = information with { ThreadCpuid = information.ThreadCpuid!.Select(capture => capture with
            { Data = Cpuid((uint)capture.LogicalProcessorId, vendor: "GenuineIntel") }).ToArray() };
        Assert.DoesNotContain(Create(intel, current).Devices, static device => device.ObjectType is "cpu_ccd" or "cpu_ccx");
    }

    [Theory]
    [InlineData("missing-capture")]
    [InlineData("duplicate-apic")]
    [InlineData("split-hardware-core")]
    [InlineData("split-os-core")]
    [InlineData("split-os-package")]
    [InlineData("legacy-apic-mismatch")]
    public void IncompleteOrConflictingDirectoryClearsKnownHardwareOnly(string fault)
    {
        var original = Information([0x80000000U, 0x80000001U, 0x80000002U, 0x80000003U]);
        var current = Create(original);
        var captures = original.ThreadCpuid!.ToArray();
        var cores = original.PhysicalCores!;
        var packages = original.Packages!;
        switch (fault)
        {
            case "missing-capture": captures[1] = captures[1] with { Data = null }; break;
            case "duplicate-apic": captures[1] = captures[1] with { Data = Cpuid(0x80000000) }; break;
            case "split-hardware-core":
                cores = [new("a", 0, false, [128]), new("b", 0, false, [129]), cores[1]]; break;
            case "split-os-core": captures[1] = captures[1] with { Data = Cpuid(0x80000005) }; break;
            case "split-os-package": packages = [new("p0", [128,129]), new("p1", [130,131])]; break;
            case "legacy-apic-mismatch": captures[0] = captures[0] with { Data = captures[0].Data! with { AmdExtendedApicId = 7 } }; break;
        }
        var failed = Create(original with { ThreadCpuid = captures, PhysicalCores = cores, Packages = packages }, current);
        var known = failed.Devices.Where(static device => device.ObjectType is "cpu_ccd" or "cpu_ccx").ToArray();
        Assert.Equal(current.Devices.Count(static device => device.ObjectType is "cpu_ccd" or "cpu_ccx"), known.Length);
        Assert.All(known.SelectMany(static device => device.Fields), field => Assert.Null(field.Value));
        Assert.NotNull(Field(failed.Devices.Single(static device => device.DeviceIdentity == "windows-cpu-thread|2:0"), "cpu_all_cpuid_vendor").Value);
        Assert.Equal(HardwareJson(current), HardwareJson(Create(original, failed)));
    }

    [Fact]
    public async Task OriginalOwnerAndReadWriteBackendPublishStructuredMembershipWithoutAnotherCapture()
    {
        var captures = 0;
        using var owner = new CpuTopologyCaptureOwner(() =>
        { captures++; return new(null, Information([0x80000000U, 0x80000001U]), DateTimeOffset.UnixEpoch); }, () => { });
        var backend = owner.CreateBackend(static _ => new OperationSupport(true, null, []));
        await backend.DiscoverAsync(CancellationToken.None);
        Assert.Equal(1, captures);
        var field = backend.Catalog.Fields.Single(static field => field.Field.FieldName == "cpu_amd_ccd_core_membership").Field;
        var cached = backend.ReadCached(field);
        Assert.Equal(FieldObservationStatus.Current, cached.Status);
        Assert.NotNull(cached.Value!.Structure);
        Assert.Equal(1, captures);
        var read = await backend.CollectAsync(ImmutableArray.Create(field), CancellationToken.None);
        Assert.Single(read);
        Assert.Equal(2, captures);
        var accepted = owner.ReadState();
        Assert.Equal(CpuTopologyCaptureCompletion.Skipped, await owner.CaptureAsync(CancellationToken.None,
            new CpuTopologyCapturePermission(() => CpuTopologyCaptureAdmission.Capture, _ => false)));
        Assert.Same(accepted, owner.ReadState());
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var before = owner.ReadState();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await owner.CaptureAsync(cancelled.Token));
        Assert.Same(before, owner.ReadState());
    }

    private static CpuThreadCpuidData Cpuid(uint apic, string vendor = "AuthenticAMD", uint maximum = 0x80000026,
        Func<int, (int Eax, int Ebx, int Ecx, int Edx), (int Eax, int Ebx, int Ecx, int Edx)>? mutate = null,
        Action<int, int>? observe = null)
    {
        var words = Enumerable.Range(0, 3).Select(index => BitConverter.ToInt32(System.Text.Encoding.ASCII.GetBytes(vendor), index*4)).ToArray();
        return WindowsCpuThreadCpuidReader.ReadRegisters((leaf, subleaf) =>
        {
            observe?.Invoke(leaf, subleaf);
            if (leaf == 0) return (1, words[0], words[2], words[1]);
            if (leaf == 1) return (0x00B00F12, 0, 0, 0);
            if (unchecked((uint)leaf) == 0x80000000) return (unchecked((int)maximum), 0, 0, 0);
            if (unchecked((uint)leaf) != 0x80000026) return (0, 0, 0, 0);
            var value = subleaf < 4
                ? (Eax: subleaf + 1, Ebx: unchecked((int)(0xABCD0000U | (2U << subleaf))), Ecx: ((subleaf+1)<<8)|subleaf, Edx: unchecked((int)apic))
                : (Eax: 0, Ebx: 0, Ecx: subleaf, Edx: unchecked((int)apic));
            return mutate?.Invoke(subleaf, value) ?? value;
        });
    }

    internal static CpuOsTopologyInformation Information(uint[] apics)
    {
        var ids = Enumerable.Range(128, apics.Length).ToArray();
        return new(Enumerable.Range(0, apics.Length/2).Select(index => new CpuOsPhysicalCore("core:"+index,
            0, true, ids.Skip(index*2).Take(2).ToArray())).ToArray(), [])
        {
            Packages = [new("package-A", ids)],
            ThreadCpuid = apics.Select((apic,index) => new CpuThreadCpuidInformation(ids[index], Cpuid(apic))).ToArray()
        };
    }

    private static DeviceInformationSnapshot Create(CpuOsTopologyInformation information, DeviceInformationSnapshot? current = null)
        => CpuInformationFieldProjection.Create(information, DateTimeOffset.UnixEpoch, current ?? DeviceInformationSnapshot.Empty);
    private static DeviceInformationField Field(DeviceInformationObject device, string name)
        => device.Fields.Single(field => field.Definition.Name == name);
    private static string HardwareJson(DeviceInformationSnapshot snapshot)
        => JsonSerializer.Serialize(snapshot.Devices.Where(static device => device.ObjectType is "cpu_ccd" or "cpu_ccx"));
}
