using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Vendors.Intel.Msr;
using ResourceManager.ReadWrite.Windows.Cpu.Models;

namespace Resource_Manager_APP.Tests;

public sealed partial class IntelMsrThermalCollectorTests
{
    private static IntelMsrCatalogIdentity PlatformTarget(uint signature = 0x406E0, CpuProcessorAddress? address = null)
        => new(0, "windows-intel-psys-platform|windows-cpu-package|1:5,1:6", address ?? new(1, 5), signature,
            null, IntelMsrTargetKind.Platform, false, false);

    private static MetricFieldCode[] PsysCodes()
        => [MetricFieldCode.IntelPsysEnergyCounter, MetricFieldCode.IntelPsysEnergyUnit];

    private static CpuOsTopologyInformation PsysTopology(uint signature = 0x406E0)
    {
        var data = WindowsCpuThreadCpuidReader.ReadRegisters(new Access { Signature = signature, ThermalFlags = 0 }.Read);
        return new([new("core", 0, true, [69, 70])], [])
        {
            Packages = [new("windows-cpu-package|1:5,1:6", [70, 69])],
            ThreadCpuid = [new(70, data), new(69, data)]
        };
    }

    [Theory]
    [InlineData(0x406E0, true)]
    [InlineData(0x506E0, true)]
    [InlineData(0x50650, false)]
    [InlineData(0x806F0, false)]
    [InlineData(0x906E0, false)]
    [InlineData(0xF06E0, false)]
    public void PsysUsesOnlyVerifiedClientModelGate(uint signature, bool supported)
    {
        var data = WindowsCpuThreadCpuidReader.ReadRegisters(new Access { Signature = signature }.Read);
        Assert.Equal(supported, IntelPsysModels.Supports(data.Identity));
        Assert.False(IntelPsysModels.Supports(data.Identity with { Vendor = "AuthenticAMD" }));
        Assert.False(IntelPsysModels.Supports(data.Identity with { Family = 15 }));
    }

    [Theory]
    [InlineData(0x406E0)]
    [InlineData(0x506E0)]
    public void PsysTopologyCreatesOneIndependentPlatformOnActualNonzeroRepresentative(uint signature)
    {
        var result = IntelMsrTopologyProjection.Create(PsysTopology(signature));
        var platform = Assert.Single(result.Identities, static target => target.IsPlatform);

        Assert.Equal(NativeMetricSnapshotCatalogTopologyStatus.Complete, result.PlatformStatus);
        Assert.Equal(0, platform.DisplayIndex);
        Assert.Equal("windows-intel-psys-platform|windows-cpu-package|1:5,1:6", platform.ExactIdentityKey);
        Assert.Equal(new CpuProcessorAddress(1, 5), platform.Processor);
        Assert.Equal(signature, platform.Signature);
        Assert.False(platform.IsCore); Assert.False(platform.IsPackage); Assert.False(platform.IsDie);
        Assert.False(platform.SupportsDts); Assert.False(platform.SupportsTemperatureTarget);
        Assert.All(PsysCodes(), code => Assert.True(platform.AppliesTo(code)));
        Assert.False(platform.AppliesTo(MetricFieldCode.IntelPackageEnergyCounter));
        Assert.False(platform.AppliesTo(MetricFieldCode.IntelCoreDtsDistance));
        Assert.False(platform.AppliesTo(MetricFieldCode.IntelAperfCounter));
        Assert.All(result.Identities.Where(static target => !target.IsPlatform),
            target => Assert.All(PsysCodes(), code => Assert.False(target.AppliesTo(code))));
        Assert.Equal(NativeMetricSnapshotCatalogTopologyStatus.Unavailable, result.DieStatus);
    }

    [Theory]
    [InlineData("missing-packages")]
    [InlineData("missing-cores")]
    [InlineData("missing-cpuid")]
    [InlineData("missing-member-cpuid")]
    [InlineData("failed-member-cpuid")]
    [InlineData("missing-signature")]
    [InlineData("extra-cpuid")]
    [InlineData("duplicate-member")]
    [InlineData("duplicate-core-member")]
    [InlineData("membership-mismatch")]
    [InlineData("empty-package-members")]
    [InlineData("empty-package-identity")]
    [InlineData("empty-packages-with-cores")]
    [InlineData("empty-packages-with-cpuid")]
    [InlineData("duplicate-cpuid-row")]
    [InlineData("member-vendor")]
    [InlineData("member-family")]
    [InlineData("member-model")]
    [InlineData("member-signature")]
    public void PsysIncompleteOrContradictoryDirectoryIsUnavailable(string failure)
    {
        var original = PsysTopology();
        var info = failure switch
        {
            "missing-packages" => original with { Packages = null },
            "missing-cores" => original with { PhysicalCores = null },
            "missing-cpuid" => original with { ThreadCpuid = null },
            "missing-member-cpuid" => original with { ThreadCpuid = original.ThreadCpuid!.Take(1).ToArray() },
            "failed-member-cpuid" => original with { ThreadCpuid = [original.ThreadCpuid![0], new(69, null)] },
            "missing-signature" => original with { ThreadCpuid = [original.ThreadCpuid![0],
                new(69, original.ThreadCpuid![1].Data! with { Signature = null })] },
            "extra-cpuid" => original with { ThreadCpuid = [.. original.ThreadCpuid!, new(71, original.ThreadCpuid![0].Data)] },
            "duplicate-member" => original with { Packages = [new("windows-cpu-package|1:5,1:6", [69, 70, 70])] },
            "duplicate-core-member" => original with { PhysicalCores = [new("core", 0, true, [69, 70, 70])] },
            "membership-mismatch" => original with { PhysicalCores = [new("core", 0, true, [69, 71])] },
            "empty-package-members" => original with { Packages = [new("windows-cpu-package|empty", [])] },
            "empty-packages-with-cores" => original with { Packages = [], ThreadCpuid = [] },
            "empty-packages-with-cpuid" => original with { Packages = [], PhysicalCores = [] },
            "duplicate-cpuid-row" => original with { ThreadCpuid = [.. original.ThreadCpuid!, original.ThreadCpuid![0]] },
            "member-vendor" or "member-family" or "member-model" or "member-signature" => original with
            {
                ThreadCpuid = [original.ThreadCpuid![1], original.ThreadCpuid![0] with
                {
                    Data = original.ThreadCpuid![0].Data! with
                    {
                        Identity = original.ThreadCpuid![0].Data!.Identity with
                        {
                            Vendor = failure == "member-vendor" ? "AuthenticAMD" : "GenuineIntel",
                            Family = failure == "member-family" ? 15 : 6,
                            Model = failure == "member-model" ? 0x5E : 0x4E
                        },
                        Signature = failure == "member-signature" ? 0x406E1U : 0x406E0U
                    }
                }]
            },
            _ => original with { Packages = [new("", [69, 70])] }
        };

        var result = IntelMsrTopologyProjection.Create(info);

        Assert.Equal(NativeMetricSnapshotCatalogTopologyStatus.Unavailable, result.PlatformStatus);
        Assert.DoesNotContain(result.Identities, static target => target.IsPlatform);
        Assert.Equal(NativeMetricSnapshotCatalogTopologyStatus.Unavailable, IntelMsrTopologyProjection.Create(null).PlatformStatus);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("multiple-packages")]
    [InlineData("non-intel")]
    [InlineData("unknown-model")]
    [InlineData("die-model")]
    [InlineData("spr-model")]
    public void PsysKnownInapplicableDirectoryIsSuccessfulEmpty(string scenario)
    {
        var info = PsysTopology(scenario switch { "unknown-model" => 0xF06E0U,
            "die-model" => 0x50650U, "spr-model" => 0x806F0U, _ => 0x406E0U });
        if (scenario == "empty") info = new([], []) { Packages = [] };
        if (scenario == "multiple-packages") info = info with
        {
            PhysicalCores = [new("one", 0, false, [69]), new("two", 0, false, [70])],
            Packages = [new("windows-cpu-package|1:5", [69]), new("windows-cpu-package|1:6", [70])]
        };
        if (scenario == "non-intel") info = info with { ThreadCpuid = info.ThreadCpuid!
            .Select(static row => row with { Data = row.Data! with { Identity = row.Data!.Identity with { Vendor = "AuthenticAMD" } } }).ToArray() };

        var result = IntelMsrTopologyProjection.Create(info);

        Assert.Equal(NativeMetricSnapshotCatalogTopologyStatus.Complete, result.PlatformStatus);
        Assert.DoesNotContain(result.Identities, static target => target.IsPlatform);
    }

    [Theory]
    [InlineData(0UL, 0UL)]
    [InlineData(4294967295UL, 4294967295UL)]
    [InlineData(18446744073709551615UL, 4294967295UL)]
    public void PsysRawPreservesZeroAndAllLow32BitsWithoutReadingUnits(ulong input, ulong expected)
    {
        var target = PlatformTarget(); var access = new Access { Signature = target.Signature, ThermalFlags = 0 };
        var hardware = new Hardware(access) { FailingRegister = 0x606 };
        hardware.RaplRegisters[0x64D] = input;
        var caller = Environment.CurrentManagedThreadId;

        var result = Assert.Single(new IntelMsrReader(() => { hardware.Opens++; return hardware; }, () => access)
            .Read([target], Request(target, MetricFieldCode.IntelPsysEnergyCounter)));

        Assert.Equal(expected, result.UnsignedValues[MetricFieldCode.IntelPsysEnergyCounter]);
        Assert.Empty(result.Values); Assert.Empty(result.CounterPairs);
        var read = Assert.Single(hardware.Reads);
        Assert.Equal(0x64DU, read.Register); Assert.Equal(target.Processor, read.Address); Assert.NotEqual(caller, read.Thread);
        Assert.Equal(1, hardware.Opens); Assert.Equal(1, hardware.Disposals); Assert.Equal(0, hardware.Writes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    public void PsysUnitReadsOnlyEnergyUnitBitsAndDoesNotRequireCounter(int esu)
    {
        var target = PlatformTarget(0x506E0); var access = new Access { Signature = target.Signature, ThermalFlags = 0 };
        var hardware = new Hardware(access) { FailingRegister = 0x64D };
        hardware.RaplRegisters[0x606] = (ulong)esu << 8 | 0xFFFF_FFFF_FFFF_E0FFUL;

        var result = Assert.Single(new IntelMsrReader(() => hardware, () => access)
            .Read([target], Request(target, MetricFieldCode.IntelPsysEnergyUnit)));

        Assert.Equal(Math.ScaleB(1d, -esu), result.Values[MetricFieldCode.IntelPsysEnergyUnit]);
        Assert.Empty(result.UnsignedValues);
        Assert.Equal(0x606U, Assert.Single(hardware.Reads).Register);
        Assert.Equal(1, hardware.Disposals); Assert.Equal(0, hardware.Writes);
    }

    [Theory]
    [InlineData(0x606)]
    [InlineData(0x64D)]
    public void PsysRegisterFailuresAreIndependentAndNextBatchRecovers(int failingRegister)
    {
        var target = PlatformTarget(); var access = new Access { Signature = target.Signature, ThermalFlags = 0 };
        var hardware = new Hardware(access) { FailingRegister = (uint)failingRegister };
        hardware.RaplRegisters[0x64D] = 0;
        var reader = new IntelMsrReader(() => hardware, () => access);
        var failed = Assert.Single(reader.Read([target], Request(target, PsysCodes())));

        Assert.Equal(failingRegister != 0x64D, failed.UnsignedValues.ContainsKey(MetricFieldCode.IntelPsysEnergyCounter));
        Assert.Equal(failingRegister != 0x606, failed.Values.ContainsKey(MetricFieldCode.IntelPsysEnergyUnit));
        Assert.Equal(new uint[] { 0x64D, 0x606 }, hardware.Reads.Select(static value => value.Register));
        hardware.FailingRegister = null; hardware.Reads.Clear();
        var recovered = Assert.Single(reader.Read([target], Request(target, PsysCodes())));
        Assert.Equal(0UL, recovered.UnsignedValues[MetricFieldCode.IntelPsysEnergyCounter]);
        Assert.Single(recovered.Values);
        Assert.Equal(new uint[] { 0x64D, 0x606 }, hardware.Reads.Select(static value => value.Register));
        Assert.Equal(2, hardware.Disposals); Assert.Equal(0, hardware.Writes);
    }

    [Theory]
    [InlineData("unknown-model")]
    [InlineData("vendor")]
    [InlineData("signature")]
    [InlineData("apic")]
    [InlineData("address")]
    public void PsysStaleTargetIsRejectedBeforeOpeningModule(string change)
    {
        var target = PlatformTarget(); var access = new Access { Signature = target.Signature, ThermalFlags = 0 };
        if (change == "unknown-model") { access.Signature = 0x906E0; target = target with { Signature = access.Signature }; }
        if (change == "vendor") access.Vendor = "AuthenticAMD";
        if (change == "signature") access.Signature++;
        if (change == "apic") target = target with { X2ApicId = 9 };
        if (change == "address") access.WrongCurrentFromCall = 2;
        var opens = 0;

        var result = Assert.Single(new IntelMsrReader(() => { opens++; throw new IOException(); }, () => access)
            .Read([target], Request(target, PsysCodes())));

        Assert.True(result.RequiresTopologyRefresh); Assert.Empty(result.Values); Assert.Empty(result.UnsignedValues);
        Assert.Equal(0, opens);
    }

    [Fact]
    public void PsysFrozenEmptyAndWrongTargetDemandNeverCreatesAccess()
    {
        var target = PlatformTarget(); var creates = 0;
        var reader = new IntelMsrReader(() => throw new IOException(), () => { creates++; throw new IOException(); });
        Assert.Empty(reader.Read([target], new Dictionary<string, IReadOnlySet<MetricFieldCode>>()));
        Assert.Empty(reader.Read([Package()], Request(Package(), PsysCodes())));
        Assert.Empty(reader.Read([target], Request(target, MetricFieldCode.IntelPackageEnergyCounter)));
        var zone = new IntelMsrMonitoringZone(reader);
        zone.ApplyMode(ResourceManagerComputeZoneMode.Freeze);
        Assert.Empty(zone.Read([target], Request(target, PsysCodes())));
        Assert.Equal(0, creates);
    }

    [Fact]
    public void PsysAndPackageTargetsShareOriginalOneBatchSessionWithoutWrites()
    {
        var platform = PlatformTarget(); var package = RaplPackage(platform.Signature, false);
        var access = new Access { Signature = platform.Signature, ThermalFlags = 0 }; var hardware = new Hardware(access);
        hardware.RaplRegisters[0x64D] = 123;
        var requests = Request(platform, PsysCodes()); requests.Add(package.ExactIdentityKey,
            new HashSet<MetricFieldCode> { MetricFieldCode.IntelPackageEnergyCounter });
        var results = new IntelMsrReader(() => { hardware.Opens++; return hardware; }, () => access)
            .Read([platform, package], requests);

        Assert.Equal(2, results.Count);
        Assert.Equal(new uint[] { 0x64D, 0x606, 0x611 }, hardware.Reads.Select(static value => value.Register));
        Assert.Equal(1, hardware.Opens); Assert.Equal(1, hardware.Disposals); Assert.Equal(0, hardware.Writes);
    }

    [Fact]
    public void PsysAddressChangingDuringReadClearsBothTypedValuesAndDisposes()
    {
        var target = PlatformTarget(); var access = new Access { Signature = target.Signature, ThermalFlags = 0 };
        var hardware = new Hardware(access) { AfterRead = () => access.WrongCurrentFromCall = 1 };
        hardware.RaplRegisters[0x64D] = 123;
        var result = Assert.Single(new IntelMsrReader(() => hardware, () => access).Read([target], Request(target, PsysCodes())));

        Assert.True(result.RequiresTopologyRefresh); Assert.Empty(result.Values); Assert.Empty(result.UnsignedValues);
        Assert.Equal(1, hardware.Disposals); Assert.Equal(0, hardware.Writes);
    }

    [Fact]
    public void PsysDisposeFailureClearsBothResultKinds()
    {
        var target = PlatformTarget(); var access = new Access { Signature = target.Signature, ThermalFlags = 0 };
        var hardware = new Hardware(access) { FailDispose = true }; hardware.RaplRegisters[0x64D] = 123;
        var result = Assert.Single(new IntelMsrReader(() => hardware, () => access).Read([target], Request(target, PsysCodes())));

        Assert.Empty(result.Values); Assert.Empty(result.UnsignedValues);
        Assert.Equal(1, hardware.Disposals); Assert.Equal(0, hardware.Writes);
    }
}
