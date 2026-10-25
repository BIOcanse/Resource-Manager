using ResourceManager.App.Domain.CpuTopology;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.CpuTopology;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Vendors.Intel.Msr;
using ResourceManager.App.Infrastructure.NativeCore;

namespace Resource_Manager_APP.Tests;

public sealed partial class IntelMsrThermalCollectorTests
{
    private static IntelMsrCatalogIdentity RaplDie(int index = 0, uint signature = 0x50650)
    {
        var data = WindowsCpuThreadCpuidReader.ReadRegisters(new Access { Signature = signature }.Read);
        return new(index, $"windows-cpu-die|{index}:0", new((ushort)index, 0), signature, data.IntelX2ApicId,
            IntelMsrTargetKind.PhysicalDie, false, false) { RaplProfile = IntelRaplModels.Get(data.Identity, IntelMsrTargetKind.PhysicalDie) };
    }
    private static MetricFieldCode[] DieCodes() => IntelRaplFieldCatalog.Domains.Where(static domain => domain.IsDie)
        .SelectMany(static domain => new[] { domain.Counter, domain.Joules, domain.Unit, domain.WrapRange })
        .Concat(IntelRaplPowerInformation.Domains.Where(static domain => domain.IsDie)
            .SelectMany(static domain => new[] { domain.Raw, domain.ThermalSpecification, domain.MinimumSetting, domain.MaximumSetting })).ToArray();

    [Fact]
    public void VerifiedDieIdentityRestoresXeonRaplWithoutPublishingAWholePackageOrOtherVendorDie()
    {
        var access = new Access { Signature = 0x50650 }; var data = WindowsCpuThreadCpuidReader.ReadRegisters(access.Read);
        var info = new CpuOsTopologyInformation([new("a", 0, true, [1, 3]), new("b", 0, false, [135])], []) {
            Packages = [new("windows-cpu-package|0:1,0:3,2:7", [1, 3, 135])],
            Dies = [new("windows-cpu-die|0:1,0:3", [1, 3], "windows-cpu-package|0:1,0:3,2:7"),
                new("windows-cpu-die|2:7", [135], "windows-cpu-package|0:1,0:3,2:7")],
            ThreadCpuid = [new(1, data), new(3, data), new(135, data)] };
        var read = IntelMsrTopologyProjection.Create(info);
        Assert.Equal(NativeMetricSnapshotCatalogTopologyStatus.Complete, read.DieStatus);
        var dies = read.Identities.Where(static target => target.IsDie).ToArray(); Assert.Equal(2, dies.Length);
        Assert.Equal(new CpuProcessorAddress(0, 1), dies[0].Processor); Assert.Equal(new CpuProcessorAddress(2, 7), dies[1].Processor);
        Assert.False(Assert.Single(read.Identities, static target => target.IsPackage).AppliesTo(MetricFieldCode.IntelPackageEnergyCounter));
        Assert.All(dies, static die => { Assert.True(die.AppliesTo(MetricFieldCode.IntelDiePackageEnergyCounter)); Assert.False(die.AppliesTo(MetricFieldCode.IntelPackageEnergyCounter)); });
        Assert.False(RaplPackage().AppliesTo(MetricFieldCode.IntelDiePackageEnergyCounter));
        Assert.False(RaplDie(signature: 0x306C0).AppliesTo(MetricFieldCode.IntelDiePackageEnergyCounter));
        var amd = data with { Identity = data.Identity with { Vendor = "AuthenticAMD" } };
        Assert.DoesNotContain(IntelMsrTopologyProjection.Create(info with { ThreadCpuid = [new(1, amd), new(3, amd), new(135, amd)] }).Identities, static target => target.IsDie);
        var failed = IntelMsrTopologyProjection.Create(info with { ThreadCpuid = [new(1, data)] });
        Assert.Equal(NativeMetricSnapshotCatalogTopologyStatus.Unavailable, failed.DieStatus); Assert.DoesNotContain(failed.Identities, static target => target.IsDie);
        Assert.Equal(NativeMetricSnapshotCatalogTopologyStatus.Complete, IntelMsrTopologyProjection.Create(info with { Dies = [] }).DieStatus);
    }

    [Fact]
    public void SeparateDieAddressesProduceIndependentValuesUnitsAndRegisterFailures()
    {
        var first = RaplDie(); var second = RaplDie(2); var access = new Access { Signature = first.Signature };
        var hardware = new Hardware(access); hardware.RaplRegisters[0x606] = 3 | 14UL << 8;
        var failSecond = false;
        hardware.AfterRead = () => {
            var read = hardware.Reads[^1]; var isSecond = read.Address.ProcessorGroup == 2;
            hardware.RaplRegisters[0x611] = isSecond ? 900UL : 100UL;
            hardware.RaplRegisters[0x619] = isSecond ? 600UL : 200UL;
            hardware.RaplRegisters[0x614] = (isSecond ? 240UL : 160UL) << 32;
            hardware.FailingRegister = failSecond && isSecond ? 0x611 : null;
        };
        var request = Request(first, DieCodes()).Concat(Request(second, DieCodes())).ToDictionary(static pair => pair.Key, static pair => pair.Value);
        var reader = new IntelMsrReader(() => hardware, () => access); var reads = reader.Read([first, second], request);
        Assert.Equal(100UL, reads[0].UnsignedValues[MetricFieldCode.IntelDiePackageEnergyCounter]);
        Assert.Equal(900UL, reads[1].UnsignedValues[MetricFieldCode.IntelDiePackageEnergyCounter]);
        Assert.Equal(200 * 15.3e-6, reads[0].Values[MetricFieldCode.IntelDieDramEnergyJoules]);
        Assert.Equal(600 * 15.3e-6, reads[1].Values[MetricFieldCode.IntelDieDramEnergyJoules]);
        Assert.Equal(20, reads[0].Values[MetricFieldCode.IntelDiePackageMaximumPowerSetting]); Assert.Equal(30, reads[1].Values[MetricFieldCode.IntelDiePackageMaximumPowerSetting]);
        Assert.Equal(2, hardware.Reads.Count(static read => read.Register == 0x606));
        Assert.All(hardware.Reads.GroupBy(static read => read.Address), group => Assert.Equal(new uint[] { 0x606, 0x611, 0x619, 0x614 }, group.Select(static read => read.Register)));
        failSecond = true; var missing = reader.Read([first, second], request);
        Assert.Equal(100UL, missing[0].UnsignedValues[MetricFieldCode.IntelDiePackageEnergyCounter]);
        Assert.DoesNotContain(MetricFieldCode.IntelDiePackageEnergyCounter, missing[1].UnsignedValues.Keys);
        Assert.Equal(600UL, missing[1].UnsignedValues[MetricFieldCode.IntelDieDramEnergyCounter]); Assert.Equal(0, hardware.Writes);
    }

    [Fact]
    public void OriginalNativeDieCommitKeepsIndependentScopeAndClearsOnlyFailedReading()
    {
        var compiled = HostManagerTestPlanFactory.CreatePlan().MetricSnapshot;
        using var workspace = NativeMetricSnapshotHostConfiguration.CreateWorkspace(compiled, Path.Combine(Path.GetTempPath(), $"die-native-{Guid.NewGuid():N}.bin"));
        var first = RaplDie(); var second = RaplDie(2);
        var catalog = NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(compiled, [], [], [], workspace.CreateCatalogHandleMap(), intelCpus: [first, second, RaplPackage(0x50650)]);
        var now = DateTimeOffset.UtcNow; workspace.ReplaceCatalog(catalog, now);
        var handles = catalog.MetricBindings.Values.Where(static binding => binding.FieldCode == MetricFieldCode.IntelDiePackageEnergyCounter).Select(static binding => binding.MetricHandle).ToArray();
        Assert.Equal(2, handles.Length); var modes = catalog.Sources.Select(static source => new NativeMetricSnapshotSourceRuntimeMode(source.SourceHandle, 1,
            NativeMetricSnapshotZoneMode.Normal, NativeMetricSnapshotSourceAvailability.Available)).ToArray();
        var snapshot = Publish(1, [new(first.ExactIdentityKey, new Dictionary<MetricFieldCode, double>()) { UnsignedValues = new Dictionary<MetricFieldCode, ulong> { [MetricFieldCode.IntelDiePackageEnergyCounter] = uint.MaxValue } },
            new(second.ExactIdentityKey, new Dictionary<MetricFieldCode, double>()) { UnsignedValues = new Dictionary<MetricFieldCode, ulong> { [MetricFieldCode.IntelDiePackageEnergyCounter] = 123 } }]);
        AssertDieCounter(snapshot, first.ExactIdentityKey, "4294967295"); AssertDieCounter(snapshot, second.ExactIdentityKey, "123");
        var failed = Publish(2, [new(first.ExactIdentityKey, new Dictionary<MetricFieldCode, double>()) { UnsignedValues = new Dictionary<MetricFieldCode, ulong> { [MetricFieldCode.IntelDiePackageEnergyCounter] = 0 } }]);
        AssertDieCounter(failed, first.ExactIdentityKey, "0"); AssertDieCounter(failed, second.ExactIdentityKey, null);
        HardwareMetricSnapshot Publish(ulong sequence, IReadOnlyList<IntelMsrMetricRead> values)
        {
            var at = now.AddSeconds(sequence); var plan = workspace.PlanCollection(handles, modes, false, at); var source = Assert.Single(plan.Sources);
            workspace.CompleteSource(plan, WindowsHardwareMetricSampler.CreateIntelMsrCompletion(plan, source, new(source.SourceHandle, 1, sequence), at, values), at);
            return NativeMetricSnapshotCommittedProjection.Create(frame: workspace.ReadCommitted(), workspaceIdentity: 1);
        }
    }

    [Fact]
    public void MaximumDieExpansionFitsOriginalBudgetAndUnavailabilityRetentionIsScoped()
    {
        var compiled = HostManagerTestPlanFactory.CreatePlan().MetricSnapshot;
        using var workspace = NativeMetricSnapshotHostConfiguration.CreateWorkspace(compiled, Path.Combine(Path.GetTempPath(), $"die-full-{Guid.NewGuid():N}.bin"));
        var dies = Enumerable.Range(0, 32).Select(static index => RaplDie(index)).ToArray();
        var cpus = Enumerable.Range(0, 256).Select(static index => Core(index) with { Signature = 0x50650 })
            .Concat(Enumerable.Range(0, 16).Select(static index => RaplPackage(0x50650) with { DisplayIndex = index, ExactIdentityKey = $"windows-cpu-package|{index}:0" }))
            .Concat(Enumerable.Range(0, 512).Select(static index => FeedbackThread(index) with { Signature = 0x50650 })).Concat(dies).ToArray();
        var catalog = NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(compiled, [], [], [], workspace.CreateCatalogHandleMap(), intelCpus: cpus);
        workspace.ReplaceCatalog(catalog, DateTimeOffset.UtcNow);
        Assert.Equal(2736, catalog.MetricBindings.Values.Count(static binding => binding.SourceId == "intel.msr"));
        // FeedbackThread fixtures have no declared CPUID.16H base MHz, so effective frequency adds no binding here.
        Assert.DoesNotContain(catalog.MetricBindings.Values,
            static binding => binding.FieldCode == MetricFieldCode.IntelEffectiveFrequencyMhz);
        Assert.InRange(workspace.Capacity.ResidentByteCount, 1UL, 64UL * 1024 * 1024);
        var retained = HostManagerMetricSnapshotOwner.RetainUnavailableOptionalIdentities(Topology([]) with { IntelCpuDieStatus = NativeMetricSnapshotCatalogTopologyStatus.Unavailable }, catalog);
        Assert.Equal(dies, retained.IntelCpuIdentities);
        Assert.Empty(HostManagerMetricSnapshotOwner.RetainUnavailableOptionalIdentities(Topology([]), catalog).IntelCpuIdentities);
        Assert.Throws<InvalidOperationException>(() => NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(compiled, [], [], [], catalog.HandleMap,
            intelCpus: Enumerable.Range(0, 33).Select(static index => RaplDie(index)).ToArray()));
    }

    private static void AssertDieCounter(HardwareMetricSnapshot snapshot, string identity, string? integer)
    {
        var field = Assert.Single(Assert.Single(MetricDeviceFields.Read(snapshot, identity)).Fields,
            static field => field.Name == "cpu_intel_die_package_energy_counter_raw");
        Assert.Equal(integer is null ? null : MetricInteger.Parse(integer), field.IntegerValue); Assert.Null(field.NumericValue); Assert.Equal("unsigned64", field.ValueKind); Assert.Equal("count", field.Unit);
    }
}
