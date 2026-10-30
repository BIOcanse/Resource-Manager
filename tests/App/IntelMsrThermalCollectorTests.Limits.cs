using System.Text.Json;
using ResourceManager.App.Domain.CpuTopology;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Infrastructure.CpuTopology;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Vendors.Intel.Msr;
using ResourceManager.App.Infrastructure.NativeCore;

namespace Resource_Manager_APP.Tests;

public sealed partial class IntelMsrThermalCollectorTests
{
    private const IntelThermalStatusCapabilities AllStatus = IntelThermalStatusCapabilities.Raw | IntelThermalStatusCapabilities.Thermal
        | IntelThermalStatusCapabilities.Power | IntelThermalStatusCapabilities.Current | IntelThermalStatusCapabilities.CrossDomain;
    private static IntelMsrCatalogIdentity StatusCore(int index = 0) => Core(index) with { ThermalStatusCapabilities = AllStatus };
    private static Access StatusAccess() => new() { Leaf1Edx = 1 << 22, ThermalFlags = 1 | 16 | 64 | 128 };
    private static MetricFieldCode[] StatusCodes(bool package = false) => IntelThermalStatusCatalog.Fields
        .Where(field => field.IsPackage == package).Select(static field => field.Code).ToArray();

    internal static HardwareMetricSnapshot CommittedStatusSnapshot(bool flag)
    {
        using var state = new StatusWorkspace(Path.Combine(Path.GetTempPath(), $"boolean-http-{Guid.NewGuid():N}.bin"));
        state.Publish(flag);
        return NativeMetricSnapshotCommittedProjection.Create(frame: state.Workspace.ReadCommitted(), workspaceIdentity: 1);
    }

    [Theory]
    [InlineData(0, 1, 1, 0)]
    [InlineData(1 << 22, 1, 3, 2)]
    [InlineData(0, 1 | 16, 5, 2)]
    [InlineData(0, 1 | 128, 25, 4)]
    [InlineData(1 << 22, 1 | 16 | 128, 31, 8)]
    public void CpuidGatesAreIndependentAndPackageNeverInterpretsCurrentOrCrossDomainBits(int tm, int leaf6, int expected, int flags)
    {
        var access = new Access { Leaf1Edx = tm, ThermalFlags = leaf6 | 64 };
        var data = WindowsCpuThreadCpuidReader.ReadRegisters(access.Read);
        Assert.Equal((IntelThermalStatusCapabilities)expected, IntelThermalStatusCatalog.Capabilities(data, IntelMsrTargetKind.PhysicalCore));
        var core = Core() with { ThermalStatusCapabilities = (IntelThermalStatusCapabilities)expected };
        Assert.Equal(flags, StatusCodes().Count(code => core.AppliesTo(code) && IntelThermalStatusCatalog.Find(code)!.Value.Bit >= 0));
        var package = Package() with { ThermalStatusCapabilities = IntelThermalStatusCatalog.Capabilities(data, IntelMsrTargetKind.Package) };
        Assert.Equal(5, StatusCodes(true).Count(package.AppliesTo));
        Assert.DoesNotContain(StatusCodes(), package.AppliesTo);
        Assert.Equal(IntelThermalStatusCapabilities.None, IntelThermalStatusCatalog.Capabilities(data, IntelMsrTargetKind.PhysicalDie));
        access.Vendor = "AuthenticAMD";
        Assert.Equal(IntelThermalStatusCapabilities.None, IntelThermalStatusCatalog.Capabilities(WindowsCpuThreadCpuidReader.ReadRegisters(access.Read), IntelMsrTargetKind.PhysicalCore));
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(0xAA03UL)]
    [InlineData(ulong.MaxValue)]
    public void SelectedStatusBitsAreIndependentOfDtsValidAndFullRawIsPreserved(ulong raw)
    {
        var core = StatusCore(); var access = StatusAccess(); var hardware = new Hardware(access) { CoreStatus = raw };
        var read = Assert.Single(new IntelMsrReader(() => hardware, () => access).Read([core], Request(core, StatusCodes())));
        Assert.Equal(raw, read.UnsignedValues[MetricFieldCode.IntelCoreThermalStatus]);
        foreach (var field in IntelThermalStatusCatalog.Fields.Where(static field => !field.IsPackage && field.Bit >= 0))
            Assert.Equal((raw & (1UL << field.Bit)) != 0, read.BooleanValues[field.Code]);
        Assert.Empty(read.Values); Assert.Equal(8, read.BooleanValues.Count);
        Assert.Equal(new uint[] { 0x19C }, hardware.Reads.Select(static read => read.Register)); Assert.Equal(0, hardware.Writes);
    }

    [Fact]
    public void TemperatureSharesStatusReadAndFailedStatusDoesNotInvalidateOtherRegisters()
    {
        var core = StatusCore(); var access = StatusAccess(); var hardware = new Hardware(access) { CoreStatus = (1UL << 31) | (20UL << 16) | 3 };
        var codes = StatusCodes().Concat(Codes(false)).ToArray(); var reader = new IntelMsrReader(() => hardware, () => access);
        var result = Assert.Single(reader.Read([core], Request(core, codes)));
        Assert.Equal(80, result.Values[MetricFieldCode.IntelCoreDtsTemperature]); Assert.True(result.BooleanValues[MetricFieldCode.IntelCoreThermalActive]);
        Assert.Equal(new uint[] { 0x1A2, 0x19C }, hardware.Reads.Select(static read => read.Register));
        hardware.FailingRegister = 0x19C;
        var failed = Assert.Single(reader.Read([core], Request(core, codes)));
        Assert.Empty(failed.BooleanValues); Assert.Empty(failed.UnsignedValues); Assert.Equal(100, failed.Values[MetricFieldCode.IntelCoreTemperatureTarget]);
        hardware.FailingRegister = null; hardware.FailDispose = true;
        var closed = Assert.Single(reader.Read([core], Request(core, codes))); Assert.Empty(closed.BooleanValues); Assert.Empty(closed.Values); Assert.Empty(closed.UnsignedValues);
    }

    [Fact]
    public void CapabilityChangeAndUnsupportedRequestNeverOpenTheReadModule()
    {
        var core = StatusCore(); var access = StatusAccess(); access.ThermalFlags &= ~128;
        var opens = 0; var reader = new IntelMsrReader(() => { opens++; return new Hardware(access); }, () => access);
        var changed = Assert.Single(reader.Read([core], Request(core, [MetricFieldCode.IntelCoreCurrentActive])));
        Assert.True(changed.RequiresTopologyRefresh); Assert.Empty(changed.BooleanValues); Assert.Equal(0, opens);
        Assert.Empty(reader.Read([core with { ThermalStatusCapabilities = IntelThermalStatusCapabilities.Raw }], Request(core, [MetricFieldCode.IntelCoreCurrentActive])));
        var phi = WindowsCpuThreadCpuidReader.ReadRegisters(new Access { Signature = 0x50670, ThermalFlags = 209, Leaf1Edx = 1 << 22 }.Read);
        Assert.Equal(IntelThermalStatusCapabilities.None, IntelThermalStatusCatalog.Capabilities(phi, IntelMsrTargetKind.PhysicalCore));
        var info = new CpuOsTopologyInformation([new("core", 0, false, [0])], []) { ThreadCpuid = [new(0, WindowsCpuThreadCpuidReader.ReadRegisters(StatusAccess().Read))] };
        var projected = Assert.Single(IntelMsrTopologyProjection.Create(info).Identities);
        Assert.Equal(AllStatus, projected.ThermalStatusCapabilities);
        Assert.NotEqual(core.BindingKey, (core with { ThermalStatusCapabilities = IntelThermalStatusCapabilities.Raw }).BindingKey);
    }

    [Fact]
    public void CoreAndPackageStatusReadOnlySelectedRegisterAndDoNotShareFlags()
    {
        var core = StatusCore(); var access = StatusAccess(); var hardware = new Hardware(access) { CoreStatus = 1, PackageStatus = 0xF000 };
        var package = Package() with { ThermalStatusCapabilities = IntelThermalStatusCapabilities.Raw | IntelThermalStatusCapabilities.Thermal | IntelThermalStatusCapabilities.Power };
        var requested = Request(core, [MetricFieldCode.IntelCoreThermalActive]); requested.Add(package.ExactIdentityKey, StatusCodes(true).ToHashSet());
        var values = new IntelMsrReader(() => hardware, () => access).Read([core, package], requested);
        Assert.True(values[0].BooleanValues[MetricFieldCode.IntelCoreThermalActive]); Assert.Single(values[0].BooleanValues);
        Assert.All(values[1].BooleanValues.Values, static flag => Assert.False(flag)); Assert.Equal(4, values[1].BooleanValues.Count);
        Assert.Equal(0xF000UL, values[1].UnsignedValues[MetricFieldCode.IntelPackageThermalStatus]);
        Assert.Equal(new uint[] { 0x19C, 0x1B1 }, hardware.Reads.Select(static read => read.Register)); Assert.Equal(0, hardware.Writes);
    }

    [Fact]
    public void FullCompatibleStatusAndDieExpansionFitsTheActualOriginalBudget()
    {
        var compiled = HostManagerTestPlanFactory.CreatePlan().MetricSnapshot;
        using var workspace = NativeMetricSnapshotHostConfiguration.CreateWorkspace(compiled, Path.Combine(Path.GetTempPath(), $"intel-status-budget-{Guid.NewGuid():N}.bin"));
        var cpus = Enumerable.Range(0, 256).Select(index => StatusCore(index) with { Signature = 0x50650 })
            .Concat(Enumerable.Range(0, 16).Select(index => RaplPackage(0x50650) with { DisplayIndex = index, ExactIdentityKey = $"windows-cpu-package|{index}:0",
                ThermalStatusCapabilities = IntelThermalStatusCapabilities.Raw | IntelThermalStatusCapabilities.Thermal | IntelThermalStatusCapabilities.Power }))
            .Concat(Enumerable.Range(0, 512).Select(index => FeedbackThread(index) with { Signature = 0x50650 }))
            .Concat(Enumerable.Range(0, 32).Select(index => RaplDie(index))).ToArray();
        var catalog = NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(compiled, [], [], [], workspace.CreateCatalogHandleMap(), intelCpus: cpus);
        workspace.ReplaceCatalog(catalog, DateTimeOffset.UtcNow);
        Assert.Equal(5120, catalog.MetricBindings.Values.Count(static binding => binding.SourceId == "intel.msr"));
        // FeedbackThread fixtures have no declared CPUID.16H base MHz, so effective frequency adds no binding here.
        Assert.DoesNotContain(catalog.MetricBindings.Values,
            static binding => binding.FieldCode == MetricFieldCode.IntelEffectiveFrequencyMhz);
        Assert.InRange(workspace.Capacity.ResidentByteCount, 1UL, 64UL * 1024 * 1024);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BooleanCommitAndWirePreserveFalseWhileRestartStartsEmptyAndFailureClears(bool initial)
    {
        var path = Path.Combine(Path.GetTempPath(), $"boolean-recovery-{Guid.NewGuid():N}.bin");
        try
        {
            using (var active = new StatusWorkspace(path))
            {
                Assert.Equal(initial, active.Publish(initial).BooleanValue);
                var wire = JsonSerializer.Serialize(active.Read(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
                Assert.Contains(initial ? "\"booleanValue\":true" : "\"booleanValue\":false", wire);
                Assert.Null(active.Read().NumericValue); Assert.Null(active.Read().IntegerValue); Assert.Null(active.Read().BytesValue);
                Assert.True(active.Workspace.PersistRecoveryCheckpointIfAdvanced(active.NextTime()));
            }
            using var recovered = new StatusWorkspace(path);
            Assert.Null(recovered.Read().BooleanValue);
            Assert.Equal(SamplingObservationStatus.Unavailable, recovered.Read().Status);
            Assert.Null(recovered.Publish(null).BooleanValue); Assert.Equal(SamplingObservationStatus.Unavailable, recovered.Read().Status);
            Assert.Equal(!initial, recovered.Publish(!initial).BooleanValue);
            Assert.Equal("boolean", recovered.Read().ValueKind); Assert.Equal("flag", recovered.Read().Unit);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void NativeBooleanRejectsInvalidValueAndExtraPayloadWithoutReplacingCommittedFalse()
    {
        using var state = new StatusWorkspace(Path.Combine(Path.GetTempPath(), $"boolean-invalid-{Guid.NewGuid():N}.bin"));
        Assert.False(state.Publish(false).BooleanValue);
        foreach (var corruption in new[] { "value", "high", "bytes" })
        {
            Assert.Throws<InvalidOperationException>(() => state.Publish(true, corruption));
            Assert.False(state.Read().BooleanValue);
        }
    }

    private sealed class StatusWorkspace : IDisposable
    {
        private readonly IntelMsrCatalogIdentity target = StatusCore();
        private readonly DateTimeOffset start = DateTimeOffset.UtcNow;
        private readonly NativeMetricSnapshotCatalogProjection catalog;
        private ulong sequence;
        internal NativeMetricSnapshotWorkspace Workspace { get; }
        internal StatusWorkspace(string path, ulong sequence = 0)
        {
            this.sequence = sequence; var compiled = HostManagerTestPlanFactory.CreatePlan().MetricSnapshot;
            Workspace = NativeMetricSnapshotHostConfiguration.CreateWorkspace(compiled, path);
            catalog = NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(compiled, [], [], [], Workspace.CreateCatalogHandleMap(), intelCpus: [target]);
            Workspace.ReplaceCatalog(catalog, start);
        }
        internal DateTimeOffset NextTime() => start.AddSeconds(++sequence);
        internal DeviceMetricFieldValue Read() => Assert.Single(Assert.Single(MetricDeviceFields.Read(
            NativeMetricSnapshotCommittedProjection.Create(frame: Workspace.ReadCommitted(), workspaceIdentity: 1), target.ExactIdentityKey)).Fields,
            static field => field.Name == "cpu_intel_core_thermal_throttling_active");
        internal DeviceMetricFieldValue Publish(bool? value, string? corruption = null)
        {
            var binding = Assert.Single(catalog.MetricBindings.Values, static binding => binding.FieldCode == MetricFieldCode.IntelCoreThermalActive);
            var modes = catalog.Sources.Select(static source => new NativeMetricSnapshotSourceRuntimeState(source.SourceHandle, 1,
                NativeMetricSnapshotSourceAvailability.Available)).ToArray();
            var at = NextTime(); var plan = Workspace.PlanCollection([binding.MetricHandle], modes, false, at); var source = Assert.Single(plan.Sources);
            var completion = WindowsHardwareMetricSampler.CreateIntelMsrCompletion(plan, source, new(source.SourceHandle, 1, sequence), at,
                [new(target.ExactIdentityKey, new Dictionary<MetricFieldCode, double>()) { BooleanValues = value.HasValue
                    ? new Dictionary<MetricFieldCode, bool> { [MetricFieldCode.IntelCoreThermalActive] = value.Value } : new Dictionary<MetricFieldCode, bool>() }]);
            if (corruption is not null)
            {
                var observation = Assert.Single(completion.Observations);
                if (corruption == "value") observation.ValueBits = 2;
                if (corruption == "high") observation.ValueHighBits = 1;
                if (corruption == "bytes") observation.ByteCount = 1;
                completion = completion with { Observations = [observation] };
            }
            Workspace.CompleteSource(plan, completion, at); return Read();
        }
        public void Dispose() => Workspace.Dispose();
    }
}
