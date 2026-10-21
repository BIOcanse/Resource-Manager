using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Vendors.Intel.Msr;
using ResourceManager.App.Infrastructure.NativeCore;

namespace Resource_Manager_APP.Tests;

public sealed partial class IntelMsrThermalCollectorTests
{
    private static MetricFieldCode[] PowerInformationCodes() => IntelRaplPowerInformation.Domains.Where(static domain => !domain.IsDie).SelectMany(static domain =>
        new[] { domain.Raw, domain.ThermalSpecification, domain.MinimumSetting, domain.MaximumSetting }).ToArray();

    [Theory]
    [InlineData(0x206A0, true, false, false)]
    [InlineData(0x206D0, true, true, true)]
    [InlineData(0x306E0, true, true, true)]
    [InlineData(0x306C0, true, false, false)]
    [InlineData(0x306F0, true, false, false)]
    [InlineData(0x50650, false, false, false)]
    [InlineData(0x606A0, true, true, false)]
    [InlineData(0x606C0, true, true, false)]
    [InlineData(0x806F0, true, false, false)]
    [InlineData(0xF06E0, false, false, false)]
    public void PowerInformationUsesIndependentModelAndUnitGates(uint signature, bool package, bool dramRaw, bool dramConverted)
    {
        var device = RaplPackage(signature, thermal: false);
        Assert.Equal(package, device.AppliesTo(MetricFieldCode.IntelPackagePowerInformation));
        Assert.Equal(package, device.AppliesTo(MetricFieldCode.IntelPackageMaximumPowerSetting));
        Assert.Equal(dramRaw, device.AppliesTo(MetricFieldCode.IntelDramPowerInformation));
        Assert.Equal(dramConverted, device.AppliesTo(MetricFieldCode.IntelDramMaximumPowerSetting));
        Assert.False((device with { Kind = IntelMsrTargetKind.PhysicalCore }).AppliesTo(MetricFieldCode.IntelPackagePowerInformation));
        Assert.False(FeedbackThread().AppliesTo(MetricFieldCode.IntelPackagePowerInformation));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(15)]
    public void PowerSpecificationUsesPowerUnitAndMasksReservedBits(int exponent)
    {
        var device = RaplPackage(0x206D0, thermal: false); var access = new Access { Signature = device.Signature, ThermalFlags = 0 };
        var hardware = new Hardware(access);
        hardware.RaplRegisters[0x606] = (ulong)exponent | 31UL << 8 | 15UL << 16;
        var packageRaw = 1UL << 63 | 1UL << 47 | 1UL << 31 | 1UL << 15 | 100UL | 20UL << 16 | 200UL << 32;
        hardware.RaplRegisters[0x614] = packageRaw;
        hardware.RaplRegisters[0x61C] = 40UL | 5UL << 16 | 60UL << 32;
        var result = Assert.Single(new IntelMsrReader(() => hardware, () => access).Read([device], Request(device, PowerInformationCodes())));
        var unit = Math.ScaleB(1d, -exponent);
        Assert.Equal(packageRaw, result.UnsignedValues[MetricFieldCode.IntelPackagePowerInformation]);
        Assert.Equal(100 * unit, result.Values[MetricFieldCode.IntelPackageThermalSpecPower]);
        Assert.Equal(20 * unit, result.Values[MetricFieldCode.IntelPackageMinimumPowerSetting]);
        Assert.Equal(200 * unit, result.Values[MetricFieldCode.IntelPackageMaximumPowerSetting]);
        Assert.Equal(40 * unit, result.Values[MetricFieldCode.IntelDramThermalSpecPower]);
        Assert.Equal(5 * unit, result.Values[MetricFieldCode.IntelDramMinimumPowerSetting]);
        Assert.Equal(60 * unit, result.Values[MetricFieldCode.IntelDramMaximumPowerSetting]);
        Assert.Equal(1, hardware.Reads.Count(static read => read.Register == 0x606));
        Assert.Equal(new uint[] { 0x614, 0x606, 0x61C }, hardware.Reads.Select(static read => read.Register));
        Assert.Equal(0, hardware.Writes);
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(18446744073709551615UL)]
    public void RawPowerInformationPreservesAllBitsWithoutReadingUnitsOrEnergy(ulong raw)
    {
        var device = RaplPackage(); var access = new Access { Signature = device.Signature };
        var hardware = new Hardware(access) { FailingRegister = 0x606 }; hardware.RaplRegisters[0x614] = raw;
        var result = Assert.Single(new IntelMsrReader(() => hardware, () => access).Read([device], Request(device, MetricFieldCode.IntelPackagePowerInformation)));
        Assert.Equal(raw, Assert.Single(result.UnsignedValues).Value); Assert.Empty(result.Values);
        Assert.Equal(0x614U, Assert.Single(hardware.Reads).Register);
    }

    [Fact]
    public void DifferentDramPowerUnitInterfaceDoesNotReuseFixedEnergyUnitOrPackageUnit()
    {
        var device = RaplPackage(0x606A0); var access = new Access { Signature = device.Signature };
        var hardware = new Hardware(access); hardware.RaplRegisters[0x61C] = ulong.MaxValue;
        var result = Assert.Single(new IntelMsrReader(() => hardware, () => access).Read([device], Request(device,
            MetricFieldCode.IntelDramPowerInformation, MetricFieldCode.IntelDramThermalSpecPower, MetricFieldCode.IntelDramMaximumPowerSetting)));
        Assert.Equal(ulong.MaxValue, Assert.Single(result.UnsignedValues).Value); Assert.Empty(result.Values);
        Assert.Equal(0x61CU, Assert.Single(hardware.Reads).Register);
    }

    [Fact]
    public void UnitFailureLeavesRawInformationAndEnergyCounterIndependentAndNextReadRecovers()
    {
        var device = RaplPackage(0x206D0); var access = new Access { Signature = device.Signature };
        var hardware = new Hardware(access) { FailingRegister = 0x606 };
        hardware.RaplRegisters[0x614] = 100; hardware.RaplRegisters[0x61C] = 20;
        var fields = Request(device, [.. PowerInformationCodes(), MetricFieldCode.IntelPackageEnergyCounter, MetricFieldCode.IntelPackageEnergyJoules]);
        var reader = new IntelMsrReader(() => hardware, () => access);
        var unavailable = Assert.Single(reader.Read([device], fields));
        Assert.Equal(3, unavailable.UnsignedValues.Count); Assert.Empty(unavailable.Values);
        Assert.Equal(1, hardware.Reads.Count(static read => read.Register == 0x606));
        hardware.FailingRegister = null; hardware.Reads.Clear();
        var restored = Assert.Single(reader.Read([device], fields)); Assert.Equal(7, restored.Values.Count);
        Assert.Equal(1, hardware.Reads.Count(static read => read.Register == 0x606));
    }

    [Fact]
    public void FailedPowerInformationDoesNotClearOtherDomainsOrReadUnusedUnitDependency()
    {
        var device = RaplPackage(0x206D0); var access = new Access { Signature = device.Signature };
        var hardware = new Hardware(access) { FailingRegister = 0x614 }; hardware.RaplRegisters[0x61C] = 20;
        var result = Assert.Single(new IntelMsrReader(() => hardware, () => access).Read([device], Request(device,
            MetricFieldCode.IntelPackagePowerInformation, MetricFieldCode.IntelPackageMaximumPowerSetting,
            MetricFieldCode.IntelDramPowerInformation, MetricFieldCode.IntelPackageEnergyCounter)));
        Assert.DoesNotContain(MetricFieldCode.IntelPackagePowerInformation, result.UnsignedValues.Keys);
        Assert.Equal(20UL, result.UnsignedValues[MetricFieldCode.IntelDramPowerInformation]);
        Assert.Equal(4096UL, result.UnsignedValues[MetricFieldCode.IntelPackageEnergyCounter]);
        Assert.Empty(result.Values); Assert.DoesNotContain(hardware.Reads, read => read.Register == 0x606);
    }

    [Fact]
    public void PowerInformationFailedIdentityAndSessionCloseCannotPublishPartialData()
    {
        var device = RaplPackage(); var access = new Access { Signature = device.Signature, WrongCurrentFromCall = 3 };
        var hardware = new Hardware(access); hardware.RaplRegisters[0x614] = 100;
        var fields = Request(device, MetricFieldCode.IntelPackagePowerInformation, MetricFieldCode.IntelPackageThermalSpecPower);
        var changed = Assert.Single(new IntelMsrReader(() => hardware, () => access).Read([device], fields));
        Assert.True(changed.RequiresTopologyRefresh); Assert.Empty(changed.Values); Assert.Empty(changed.UnsignedValues);
        access.WrongCurrentFromCall = int.MaxValue; hardware.FailDispose = true;
        var failed = Assert.Single(new IntelMsrReader(() => hardware, () => access).Read([device], fields));
        Assert.Empty(failed.Values); Assert.Empty(failed.UnsignedValues);
    }

    [Fact]
    public void PowerInformationActualNativeCommitIsLosslessAndFailureClearsConvertedValues()
    {
        var compiled = HostManagerTestPlanFactory.CreatePlan().MetricSnapshot;
        using var workspace = NativeMetricSnapshotHostConfiguration.CreateWorkspace(compiled, Path.Combine(Path.GetTempPath(), $"power-info-{Guid.NewGuid():N}.bin"));
        var device = RaplPackage(0x206D0); var access = new Access { Signature = device.Signature }; var hardware = new Hardware(access);
        hardware.RaplRegisters[0x614] = ulong.MaxValue; hardware.RaplRegisters[0x61C] = 0;
        var reader = new IntelMsrReader(() => hardware, () => access);
        var catalog = NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(compiled, [], [], [], workspace.CreateCatalogHandleMap(), intelCpus: [device]);
        var now = DateTimeOffset.UtcNow; workspace.ReplaceCatalog(catalog, now);
        var handles = catalog.MetricBindings.Values.Where(binding => IntelRaplPowerInformation.Find(binding.FieldCode) is not null)
            .Select(static binding => binding.MetricHandle).ToArray(); Assert.Equal(8, handles.Length);
        var modes = catalog.Sources.Select(static source => new NativeMetricSnapshotSourceRuntimeMode(source.SourceHandle, 1,
            NativeMetricSnapshotZoneMode.Normal, NativeMetricSnapshotSourceAvailability.Available)).ToArray();
        var first = Publish(1);
        AssertPowerInformation(first, device.ExactIdentityKey, "18446744073709551615");
        var watt = MetricDeviceFields.Read(first, device.ExactIdentityKey).Single().Fields.Single(field => field.Name == "cpu_intel_package_maximum_power_setting_declared_watts");
        Assert.Equal(32767, watt.NumericValue); Assert.Equal("W", watt.Unit);
        Assert.Equal(0, MetricDeviceFields.Read(first, device.ExactIdentityKey).Single().Fields.Single(field => field.Name == "cpu_intel_dram_thermal_spec_power_declared_watts").NumericValue);
        hardware.FailingRegister = 0x606;
        var missing = Publish(2); AssertPowerInformation(missing, device.ExactIdentityKey, "18446744073709551615");
        Assert.Null(MetricDeviceFields.Read(missing, device.ExactIdentityKey).Single().Fields.Single(field => field.Name == watt.Name).NumericValue);
        hardware.FailingRegister = 0x614; AssertPowerInformation(Publish(3), device.ExactIdentityKey, null);
        hardware.FailingRegister = null; AssertPowerInformation(Publish(4), device.ExactIdentityKey, "18446744073709551615");
        HardwareMetricSnapshot Publish(ulong sequence)
        {
            var at = now.AddSeconds(sequence); var plan = workspace.PlanCollection(handles, modes, false, at); var source = Assert.Single(plan.Sources);
            var reads = reader.Read([device], Request(device, PowerInformationCodes()));
            workspace.CompleteSource(plan, WindowsHardwareMetricSampler.CreateIntelMsrCompletion(plan, source, new(source.SourceHandle, 1, sequence), at, reads), at);
            return NativeMetricSnapshotCommittedProjection.Create(workspace.ReadCommitted(), "CPU", "RAM", 1);
        }
    }

    private static void AssertPowerInformation(HardwareMetricSnapshot snapshot, string identity, string? raw)
    {
        var field = Assert.Single(Assert.Single(MetricDeviceFields.Read(snapshot, identity)).Fields,
            field => field.Name == "cpu_intel_package_power_information_raw");
        Assert.Equal(raw, field.IntegerValue); Assert.Null(field.NumericValue); Assert.Equal("unsigned64", field.ValueKind); Assert.Equal("bitmask64", field.Unit);
    }
}
