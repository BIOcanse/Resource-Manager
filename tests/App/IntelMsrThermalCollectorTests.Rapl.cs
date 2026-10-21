using ResourceManager.App.Domain.CpuTopology;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.CpuTopology;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Vendors.Intel.Msr;

namespace Resource_Manager_APP.Tests;

public sealed partial class IntelMsrThermalCollectorTests
{
    private static IntelMsrCatalogIdentity RaplPackage(uint signature = 0x306C0, bool thermal = true)
    {
        var data = WindowsCpuThreadCpuidReader.ReadRegisters(new Access { Signature = signature }.Read);
        return Package() with { Signature = signature, SupportsDts = thermal, RaplProfile = IntelRaplModels.Get(data.Identity) };
    }
    private static MetricFieldCode[] RaplCodes(IntelRaplDomains domains)
        => IntelRaplFieldCatalog.Domains.Where(domain => !domain.IsDie && (domain.Domain & domains) != 0)
            .SelectMany(static domain => new[] { domain.Counter, domain.Joules, domain.Unit, domain.WrapRange }).ToArray();

    [Theory]
    [InlineData(0x206A0, 7, null)]
    [InlineData(0x306A0, 7, null)]
    [InlineData(0x206D0, 11, null)]
    [InlineData(0x306E0, 11, null)]
    [InlineData(0x306C0, 15, null)]
    [InlineData(0x40650, 15, null)]
    [InlineData(0x40660, 15, null)]
    [InlineData(0x306D0, 15, null)]
    [InlineData(0x40670, 15, null)]
    [InlineData(0x306F0, 9, 0.0000153)]
    [InlineData(0x406F0, 9, 0.0000153)]
    [InlineData(0x50660, 9, 0.0000153)]
    [InlineData(0x50650, 0, null)]
    [InlineData(0x606A0, 9, 0.0000153)]
    [InlineData(0x606C0, 9, 0.0000153)]
    [InlineData(0x806F0, 9, 0.000061)]
    [InlineData(0xC06F0, 9, 0.000061)]
    [InlineData(0x906E0, 11, null)]
    [InlineData(0x90670, 11, null)]
    [InlineData(0xB0670, 11, null)]
    [InlineData(0x106A0, 0, null)]
    [InlineData(0xA0670, 0, null)]
    [InlineData(0x50670, 0, null)]
    [InlineData(0x80650, 0, null)]
    [InlineData(0xF06E0, 0, null)]
    public void ModelTablesKeepPowerPlanesAndServerUnitOverridesDistinct(uint signature, int domains, double? fixedUnit)
    {
        var data = WindowsCpuThreadCpuidReader.ReadRegisters(new Access { Signature = signature }.Read);
        var profile = IntelRaplModels.Get(data.Identity);
        Assert.Equal((IntelRaplDomains)domains, profile.Domains); Assert.Equal(fixedUnit, profile.FixedDramUnitJoules);
        Assert.Equal(default, IntelRaplModels.Get(data.Identity with { Vendor = "AuthenticAMD" }));
        Assert.Equal(default, IntelRaplModels.Get(data.Identity with { Family = 15 }));
    }

    [Fact]
    public void RaplPackageDeclarationDoesNotDependOnThermalSupportAndCannotLeakToCoreOrUnknownModel()
    {
        var data = WindowsCpuThreadCpuidReader.ReadRegisters(new Access { ThermalFlags = 0 }.Read);
        var info = new CpuOsTopologyInformation([new("0", 0, false, [0])], [])
        { ThreadCpuid = [new(0, data)], Packages = [new(Package().ExactIdentityKey, [0])] };
        var result = IntelMsrTopologyProjection.Create(info);
        var package = Assert.Single(result.Identities);
        Assert.True(package.IsPackage); Assert.False(package.SupportsDts);
        Assert.True(package.AppliesTo(MetricFieldCode.IntelPackageEnergyCounter));
        Assert.False(package.AppliesTo(MetricFieldCode.IntelPackageDtsDistance));
        Assert.False((package with { Kind = IntelMsrTargetKind.PhysicalCore }).AppliesTo(MetricFieldCode.IntelPackageEnergyCounter));
        Assert.False(RaplPackage(0xF06E0).AppliesTo(MetricFieldCode.IntelPackageEnergyCounter));
        var access = new Access { ThermalFlags = 0 }; var hardware = new Hardware(access);
        var captured = Assert.Single(new IntelMsrReader(() => hardware, () => access).Read([package], Request(package, MetricFieldCode.IntelPackageEnergyCounter)));
        Assert.Equal(4096UL, captured.UnsignedValues[MetricFieldCode.IntelPackageEnergyCounter]);
        Assert.Equal(0x611U, Assert.Single(hardware.Reads).Register);
    }

    [Fact]
    public void AmbiguousXeonDieScopeCannotBePublishedAsPackageAndDoesNotDisableOtherDomains()
    {
        var access = new Access { Signature = 0x50650, FeedbackFlags = 1 };
        var data = WindowsCpuThreadCpuidReader.ReadRegisters(access.Read);
        var info = new CpuOsTopologyInformation([new("0", 0, false, [0])], [])
        { ThreadCpuid = [new(0, data)], Packages = [new(Package().ExactIdentityKey, [0])] };
        var targets = IntelMsrTopologyProjection.Create(info).Identities;
        var package = Assert.Single(targets, static target => target.IsPackage);
        var thread = Assert.Single(targets, static target => target.Kind == IntelMsrTargetKind.LogicalProcessor);
        var compiled = HostManagerTestPlanFactory.CreatePlan().MetricSnapshot;
        var catalog = NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(compiled, [], [], [], NativeMetricSnapshotCatalogHandleMap.Empty, intelCpus: targets);
        Assert.DoesNotContain(catalog.MetricBindings.Values, static binding => IntelRaplFieldCatalog.Find(binding.FieldCode) is not null
            || IntelRaplPowerInformation.Find(binding.FieldCode) is not null);
        Assert.True(package.AppliesTo(MetricFieldCode.IntelPackageDtsTemperature));
        Assert.True(thread.AppliesTo(MetricFieldCode.IntelPerformanceRatio));
        var hardware = new Hardware(access); var opens = 0;
        var reader = new IntelMsrReader(() => { opens++; return hardware; }, () => access);
        var forced = Request(package, MetricFieldCode.IntelPackageEnergyCounter, MetricFieldCode.IntelDramEnergyCounter,
            MetricFieldCode.IntelPackagePowerInformation, MetricFieldCode.IntelDramPowerInformation);
        Assert.Empty(reader.Read([package], forced)); Assert.Equal(0, opens);
        var stale = package with { RaplProfile = new(IntelRaplDomains.Package | IntelRaplDomains.Dram, 15.3e-6)
            { PowerInformationDomains = IntelRaplDomains.Package } };
        var prior = NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(compiled, [], [], [], catalog.HandleMap,
            intelCpus: targets.Select(target => target.IsPackage ? stale : target).ToArray());
        Assert.False(HostManagerMetricSnapshotOwner.CanRetainCatalog(prior, catalog));
        var outdated = Assert.Single(reader.Read([stale], forced));
        Assert.True(outdated.RequiresTopologyRefresh); Assert.Empty(outdated.Values); Assert.Empty(outdated.UnsignedValues);
        Assert.Equal(0, opens);
        var thermal = Assert.Single(reader.Read([package], Request(package, MetricFieldCode.IntelPackageDtsTemperature)));
        Assert.Equal(90, thermal.Values[MetricFieldCode.IntelPackageDtsTemperature]);
        Assert.Equal(new uint[] { 0x1A2, 0x1B1 }, hardware.Reads.Select(static read => read.Register));
    }

    [Theory]
    [InlineData(0UL, 0UL)]
    [InlineData(4294967295UL, 4294967295UL)]
    [InlineData(18446744073709551615UL, 4294967295UL)]
    public void RawCounterPreservesAll32BitsAndDoesNotReadOrRequireUnits(ulong input, ulong expected)
    {
        var device = RaplPackage(); var access = new Access { Signature = device.Signature }; var hardware = new Hardware(access);
        hardware.RaplRegisters[0x611] = input; hardware.FailingRegister = 0x606;
        var result = Assert.Single(new IntelMsrReader(() => hardware, () => access).Read([device], Request(device, MetricFieldCode.IntelPackageEnergyCounter)));
        Assert.Empty(result.Values); Assert.Equal(expected, Assert.Single(result.UnsignedValues).Value);
        Assert.Equal(0x611U, Assert.Single(hardware.Reads).Register); Assert.Equal(0, hardware.Writes); Assert.Equal(1, hardware.Disposals);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(14)]
    [InlineData(16)]
    [InlineData(31)]
    public void EnergyUnitUsesExactlyFiveBitsAndWrapRangeIsAFullCycle(int exponent)
    {
        var device = RaplPackage(); var access = new Access { Signature = device.Signature }; var hardware = new Hardware(access);
        hardware.RaplRegisters[0x606] = 0xFFFF_FFFF_FFFF_E0FFUL | (ulong)exponent << 8;
        hardware.RaplRegisters[0x611] = uint.MaxValue;
        var result = Assert.Single(new IntelMsrReader(() => hardware, () => access).Read([device], Request(device,
            MetricFieldCode.IntelPackageEnergyJoules, MetricFieldCode.IntelPackageEnergyUnit, MetricFieldCode.IntelPackageEnergyWrapRange)));
        var unit = Math.ScaleB(1d, -exponent);
        Assert.Equal(unit, result.Values[MetricFieldCode.IntelPackageEnergyUnit]);
        Assert.Equal(unit * uint.MaxValue, result.Values[MetricFieldCode.IntelPackageEnergyJoules]);
        Assert.Equal(unit * 4294967296d, result.Values[MetricFieldCode.IntelPackageEnergyWrapRange]);
        Assert.Empty(result.UnsignedValues); Assert.Equal(new uint[] { 0x606, 0x611 }, hardware.Reads.Select(static read => read.Register));
    }

    [Fact]
    public void AllFourPlanesShareOneUnitReadAndOneSessionButNeverShareOrSumCounters()
    {
        var device = RaplPackage(); var access = new Access { Signature = device.Signature }; var hardware = new Hardware(access);
        var result = Assert.Single(new IntelMsrReader(() => { hardware.Opens++; return hardware; }, () => access)
            .Read([device], Request(device, RaplCodes(device.RaplProfile.Domains))));
        Assert.Equal(12, result.Values.Count); Assert.Equal(4, result.UnsignedValues.Count);
        Assert.Equal(new uint[] { 0x606, 0x611, 0x639, 0x641, 0x619 }, hardware.Reads.Select(static read => read.Register));
        Assert.Equal(0.25, result.Values[MetricFieldCode.IntelPackageEnergyJoules]);
        Assert.Equal(0.125, result.Values[MetricFieldCode.IntelPp0EnergyJoules]);
        Assert.Equal(0.0625, result.Values[MetricFieldCode.IntelPp1EnergyJoules]);
        Assert.Equal(0.03125, result.Values[MetricFieldCode.IntelDramEnergyJoules]);
        Assert.Equal(1, hardware.Opens); Assert.Equal(1, hardware.Disposals); Assert.Equal(0, hardware.Writes);
        Assert.All(hardware.Reads, read => Assert.Equal(device.Processor, read.Address));
    }

    [Theory]
    [InlineData(0x306F0, 0.0000153)]
    [InlineData(0x806F0, 0.000061)]
    public void FixedDramQuantumDoesNotUsePackageUnitOrReadTheUnitMsr(uint signature, double unit)
    {
        var device = RaplPackage(signature); var access = new Access { Signature = signature }; var hardware = new Hardware(access) { FailingRegister = 0x606 };
        var result = Assert.Single(new IntelMsrReader(() => hardware, () => access).Read([device], Request(device,
            MetricFieldCode.IntelDramEnergyCounter, MetricFieldCode.IntelDramEnergyJoules,
            MetricFieldCode.IntelDramEnergyUnit, MetricFieldCode.IntelDramEnergyWrapRange)));
        Assert.Equal(unit, result.Values[MetricFieldCode.IntelDramEnergyUnit]);
        Assert.Equal(unit * 512, result.Values[MetricFieldCode.IntelDramEnergyJoules]);
        Assert.Equal(unit * 4294967296d, result.Values[MetricFieldCode.IntelDramEnergyWrapRange]);
        Assert.Equal(0x619U, Assert.Single(hardware.Reads).Register);
        Assert.False(device.AppliesTo(MetricFieldCode.IntelPp0EnergyCounter));
        Assert.False(device.AppliesTo(MetricFieldCode.IntelPp1EnergyCounter));
    }

    [Theory]
    [InlineData(0x606)]
    [InlineData(0x639)]
    public void UnitAndDomainFailuresClearOnlyDependentFieldsAndNextBatchRecovers(int failing)
    {
        var device = RaplPackage(); var access = new Access { Signature = device.Signature }; var hardware = new Hardware(access) { FailingRegister = (uint)failing };
        var reader = new IntelMsrReader(() => hardware, () => access); var fields = Request(device, RaplCodes(device.RaplProfile.Domains));
        var result = Assert.Single(reader.Read([device], fields));
        Assert.Equal(failing == 0x606 ? 4 : 3, result.UnsignedValues.Count);
        Assert.Equal(failing == 0x606 ? 0 : 11, result.Values.Count);
        Assert.Equal(failing != 0x606, result.Values.ContainsKey(MetricFieldCode.IntelPackageEnergyJoules));
        Assert.False(result.Values.ContainsKey(MetricFieldCode.IntelPp0EnergyJoules));
        hardware.FailingRegister = null;
        Assert.Equal(12, Assert.Single(reader.Read([device], fields)).Values.Count);
    }

    [Fact]
    public void UnitOnlyDemandNeverReadsCountersAndThermalAndRaplDemandsShareTheSession()
    {
        var device = RaplPackage(); var access = new Access { Signature = device.Signature }; var hardware = new Hardware(access);
        var reader = new IntelMsrReader(() => { hardware.Opens++; return hardware; }, () => access);
        var result = Assert.Single(reader.Read([device], Request(device, MetricFieldCode.IntelPackageEnergyUnit)));
        Assert.Single(result.Values); Assert.Empty(result.UnsignedValues); Assert.Equal(0x606U, Assert.Single(hardware.Reads).Register);
        hardware.Reads.Clear();
        result = Assert.Single(reader.Read([device], Request(device, MetricFieldCode.IntelPackageEnergyCounter, MetricFieldCode.IntelPackageDtsTemperature)));
        Assert.Single(result.Values); Assert.Single(result.UnsignedValues);
        Assert.Equal(new uint[] { 0x1A2, 0x1B1, 0x611 }, hardware.Reads.Select(static read => read.Register));
        Assert.Equal(2, hardware.Opens); Assert.Equal(2, hardware.Disposals);
    }

    [Fact]
    public void ChangedProfileOrProcessorClearsBothNumericKindsAndRequestsOriginalTopologyRefresh()
    {
        var device = RaplPackage(); var access = new Access { Signature = device.Signature }; var opens = 0;
        var wrong = device with { RaplProfile = new(IntelRaplDomains.Package) };
        var result = Assert.Single(new IntelMsrReader(() => { opens++; throw new IOException(); }, () => access)
            .Read([wrong], Request(wrong, MetricFieldCode.IntelPackageEnergyCounter)));
        Assert.True(result.RequiresTopologyRefresh); Assert.Empty(result.UnsignedValues); Assert.Equal(0, opens);
        var hardware = new Hardware(access) { AfterRead = () => access.WrongCurrentFromCall = 1 };
        result = Assert.Single(new IntelMsrReader(() => hardware, () => access).Read([device], Request(device, RaplCodes(device.RaplProfile.Domains))));
        Assert.True(result.RequiresTopologyRefresh); Assert.Empty(result.Values); Assert.Empty(result.UnsignedValues);
    }
}
