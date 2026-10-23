using System.Collections.Immutable;
using System.Text.Json;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.Control;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Dependencies;
using ResourceManager.ReadWrite.Helpers;
using ResourceManager.ReadWrite.Native;
using ResourceManager.ReadWrite.Vendors.Amd;
using ResourceManager.ReadWrite.Vendors.Intel;
using ResourceManager.ReadWrite.Windows.Cpu.Models;
using ResourceManager.ReadWrite.Windows.Gpu;
using ResourceManager.ReadWrite.Windows.Gpu.Control;
using ResourceManager.ReadWrite.Windows.Metrics;
using ResourceManager.ReadWrite.Windows.Metrics.Configuration;
using ResourceManager.ReadWrite.Windows.Metrics.Resources;
using ResourceManager.ReadWrite.Windows.Metrics.Topology;
using ResourceManager.ReadWrite.Windows.Metrics.Writes;
using Xunit;
namespace Resource_Manager_APP.Tests;

public sealed class CanonicalMetricControlBindingTests
{
    private const string IntelPl1 = "cpu_intel_requested_pl1_power_limit_watts";
    private const string AmdStapm = "cpu_amd_requested_stapm_power_limit_watts";

    [Theory]
    [InlineData("cpu_amd_requested_temperature_limit_celsius")]
    [InlineData("cpu_intel_requested_temperature_limit_reduction_celsius")]
    public void RequestedTemperatureFieldsUseCelsiusDisplayUnit(string name)
    {
        var binding = WindowsMetricControlBindings.FindByName(name);
        Assert.NotNull(binding);
        Assert.Equal("°C", binding.Definition.Unit);
    }

    [Fact]
    public void ExactManagedContractTableKeepsUnsupportedAndTelemetrySeparate()
    {
        Assert.Equal(30, WindowsMetricControlBindings.All.Count);
        Assert.Equal(25, WindowsMetricControlBindings.All.Count(entry => entry.Implemented));
        Assert.Equal(5, WindowsMetricControlBindings.All.Count(entry => !entry.Implemented));
        Assert.Equal(30, WindowsMetricControlBindings.All.Select(entry => entry.Definition.Code).Distinct().Count());
        foreach (var entry in WindowsMetricControlBindings.All)
        {
            var binding = new MetricDeviceFieldBinding("recorded-device", entry.Definition, "recorded-address", entry.SourceId);
            Assert.Same(entry, WindowsMetricControlBindings.TryFind(binding));
            Assert.Null(WindowsMetricControlBindings.TryFind(binding with { SourceId = "amd.smu" }));
            Assert.Null(WindowsMetricControlBindings.TryFind(binding with { Field = entry.Definition with { Unit = "wrong" } }));
            Assert.Null(WindowsMetricControlBindings.TryFind(binding with { Field = entry.Definition with { ValueKind = "string" } }));
            Assert.Null(WindowsMetricControlBindings.TryFind(binding with { Field = entry.Definition with { Vendor = MetricVendor.All } }));
            Assert.Null(WindowsMetricControlBindings.TryFind(binding with { Field = entry.Definition with { Code = MetricFieldCode.Unknown } }));
            if (entry.Family == WindowsMetricControlFamily.AmdDesktopGpu)
            { Assert.Null(entry.DeclaredRange.Minimum); Assert.Null(entry.DeclaredRange.DeclaredDefault); }
        }
        Assert.Null(WindowsMetricControlBindings.FindByName("cpu_amd_stapm_power_watts"));
        Assert.Null(WindowsMetricControlBindings.FindByName("gpu_all_graphics_clock_mhz"));
    }

    [Fact]
    public void PreparedControlsRebaseOntoNewNativeCommitWithoutRestoringItsOldNumericFrame()
    {
        var nativeBinding = new MetricDeviceFieldBinding("recorded-platform",
            MetricFieldRegistry.FindByName("system_intel_psys_energy_counter_raw")!, "native-raw", "intel.msr");
        var declaration = WindowsMetricControlBindings.All.Single(static entry => entry.Definition.Name == IntelPl1);
        var control = new MetricDeviceFieldBinding("recorded-cpu", declaration.Definition, "managed-control", declaration.SourceId)
            { ControlTarget = new("recorded-cpu") };
        var withoutTarget = control with { RequestAddress = "unbound-control", ControlTarget = null };
        var nativeValue = new MetricValue(nativeBinding.RequestAddress, nativeBinding.Field.Name, "system", "",
            null, "count", null, null) { IntegerValue = "42" };
        var controlValue = new MetricValue(control.RequestAddress, control.Field.Name, "cpu", "",
            45, "W", null, null);
        var nativeObservation = new HardwareMetricDatasetObservation(nativeBinding.RequestAddress,
            SamplingObservationStatus.Current, 9, 100, 5, 6, 7, 8);
        var controlObservation = new HardwareMetricDatasetObservation(control.RequestAddress,
            SamplingObservationStatus.Current, 1, 200, 1, 2, 3, 4)
            { LastAttemptAtUtcTicks = 200, LastSuccessAtUtcTicks = 200 };
        var native = Fixture.Snapshot() with {
            WorkspaceIdentity = 5, ConfigurationGeneration = 6, CatalogGeneration = 7, CommittedGeneration = 8,
            FieldBindings = new Dictionary<string, MetricDeviceFieldBinding> { [nativeBinding.RequestAddress] = nativeBinding },
            Items = new Dictionary<string, MetricValue> { [nativeBinding.RequestAddress] = nativeValue },
            Datasets = new Dictionary<string, HardwareMetricDatasetObservation> { [nativeBinding.RequestAddress] = nativeObservation }
        };
        var prepared = Fixture.Snapshot() with {
            FieldBindings = new Dictionary<string, MetricDeviceFieldBinding> {
                [nativeBinding.RequestAddress] = nativeBinding, [control.RequestAddress] = control,
                [withoutTarget.RequestAddress] = withoutTarget },
            Items = new Dictionary<string, MetricValue> {
                [nativeBinding.RequestAddress] = nativeValue with { IntegerValue = "17" },
                [control.RequestAddress] = controlValue },
            Datasets = new Dictionary<string, HardwareMetricDatasetObservation> {
                [nativeBinding.RequestAddress] = nativeObservation with { ObservedAtUtcTicks = 1 },
                [control.RequestAddress] = controlObservation }
        };
        var merged = WindowsMetricControlFields.ApplyPreparedFields(native, prepared);
        Assert.Same(nativeValue, merged.Items[nativeBinding.RequestAddress]);
        Assert.Same(nativeObservation, merged.Datasets[nativeBinding.RequestAddress]);
        Assert.Equal(native.CommittedGeneration, merged.CommittedGeneration);
        Assert.Same(controlValue, merged.Items[control.RequestAddress]);
        Assert.DoesNotContain(withoutTarget.RequestAddress, merged.FieldBindings.Keys);
        Assert.Equal(controlObservation with { SourceGeneration = 8, WorkspaceIdentity = 5,
            ConfigurationGeneration = 6, CatalogGeneration = 7, CommittedGeneration = 8 }, merged.Datasets[control.RequestAddress]);
        var unavailable = WindowsMetricControlFields.ApplyPreparedFields(native, prepared with {
            Items = new Dictionary<string, MetricValue>(),
            Datasets = new Dictionary<string, HardwareMetricDatasetObservation> {
                [control.RequestAddress] = controlObservation with { Status = SamplingObservationStatus.Unavailable,
                    ObservedAtUtcTicks = 0, LastSuccessAtUtcTicks = 0 } }
        });
        Assert.False(unavailable.Items.ContainsKey(control.RequestAddress));
        Assert.Same(nativeValue, unavailable.Items[nativeBinding.RequestAddress]);
        Assert.Equal(SamplingObservationStatus.Unavailable, unavailable.Datasets[control.RequestAddress].Status);
        Assert.Equal(8UL, unavailable.Datasets[control.RequestAddress].CommittedGeneration);
    }

    [Fact]
    public void HostGpuMapperUsesFreshIdentityFactsBeforeFirstNativeInventoryCommitAndRejectsStaleCatalogs()
    {
        using var recording = new Fixture("AuthenticAMD");
        recording.Inventory = Fixture.GpuInventory();
        var originalInventory = recording.Inventory;
        var adapter = Assert.Single(originalInventory.Adapters);
        var key = WindowsGpuAdapterIdentity.Pack(adapter.Luid);
        var exact = WindowsHardwareMetricTopologySource.CreateExactGpuKey(adapter);
        var configuration = WindowsMetricConfiguration.CreateDefault();
        using var workspace = new NativeMetricSnapshotWorkspace(configuration.Configuration, configuration.CatalogConfiguration);
        var catalog = NativeMetricSnapshotCatalogProjection.Create(configuration.CatalogConfiguration,
            [new(adapter.Index, adapter.Name, exact, key, originalInventory.Generation, 2, true)], [], [], workspace.CreateCatalogHandleMap());
        workspace.ReplaceCatalog(catalog, DateTimeOffset.UtcNow);
        var firstNativeFrame = NativeMetricSnapshotCommittedProjection.Create(workspace.ReadCommitted(), "recorded", "recorded", 1);
        Assert.Equal(key, Assert.Single(firstNativeFrame.Gpus).AdapterKey);
        Assert.False(firstNativeFrame.GpuInventory.IsCurrentComplete());
        Assert.Null(WindowsControlObjectCatalog.ResolveAdapterKey(firstNativeFrame.GpuInventory, adapter.Index));
        using (var wire = JsonDocument.Parse(JsonSerializer.Serialize(firstNativeFrame.Gpus[0], new JsonSerializerOptions(JsonSerializerDefaults.Web))))
            Assert.False(wire.RootElement.TryGetProperty("adapterKey", out _));
        var gpuMappings = 0;
        var controls = recording.Controls with {
            ControlResolveObjectId = (frame, family, resource, _, adapterKey) =>
            {
                if (family is WindowsMetricControlFamily.IntelCpu or WindowsMetricControlFamily.AmdCpu) return "cpu:recorded";
                gpuMappings++;
                // This is the exact original Host mapper dependency. Do not call
                // ReadDeclaredObjects, whose legacy unique-id fallback probes NVML.
                Assert.Equal(adapterKey, WindowsControlObjectCatalog.ResolveAdapterKey(frame.GpuInventory, adapter.Index));
                Assert.Equal(recording.Inventory.Generation, frame.GpuInventory.Generation);
                Assert.Equal(recording.Inventory.ObservedAtUtcTicks, frame.GpuInventory.ObservedAtUtcTicks);
                Assert.Equal(recording.Inventory.ObservedCount, frame.GpuInventory.ObservedCount);
                Assert.Equal(recording.Inventory.SkippedCount, frame.GpuInventory.SkippedCount);
                Assert.Equal(recording.Inventory.OverflowCount, frame.GpuInventory.OverflowCount);
                Assert.Equal(recording.Inventory.TopologyFingerprint, frame.GpuInventory.TopologyFingerprint);
                Assert.All(frame.GpuInventory.Adapters, observed =>
                {
                    Assert.Equal(SchedulingGpuMetricMask.None, observed.ValidMetricMask);
                    Assert.Equal(SamplingObservationStatus.Unavailable, observed.UsageStatus);
                    Assert.Equal(SamplingObservationStatus.Unavailable, observed.CapacityStatus);
                });
                return resource == MetricResource.Fan ? "fan:recorded" : "gpu:recorded";
            }
        };
        var producer = new WindowsMetricControlFields(controls, () => recording.Inventory);
        var discovered = producer.Discover(firstNativeFrame);
        Assert.True(gpuMappings > 0);
        Assert.Contains(discovered.FieldBindings.Values, binding => binding.ControlTarget?.RawObjectId == "gpu:recorded");
        Assert.Same(firstNativeFrame.GpuInventory, discovered.GpuInventory);
        Assert.False(discovered.GpuInventory.IsCurrentComplete()); // Mapper identity facts never become current telemetry.
        var noNumericGpuBindings = firstNativeFrame with { FieldBindings = firstNativeFrame.FieldBindings
            .Where(static pair => pair.Value.Field.Resource != MetricResource.Gpu)
            .ToDictionary(static pair => pair.Key, static pair => pair.Value) };
        gpuMappings = 0;
        Assert.Contains(producer.Discover(noNumericGpuBindings).FieldBindings.Values,
            binding => binding.ControlTarget?.RawObjectId == "gpu:recorded");
        Assert.True(gpuMappings > 0);

        AssertRejected(firstNativeFrame, originalInventory with { Status = SamplingObservationStatus.Unavailable });
        AssertRejected(firstNativeFrame, originalInventory with { SkippedCount = 1 });
        AssertRejected(firstNativeFrame with { Gpus = [firstNativeFrame.Gpus[0] with { IdentityKey = "same-name-stale-identity" }] }, originalInventory);
        AssertRejected(firstNativeFrame with { Gpus = [firstNativeFrame.Gpus[0] with { Index = 9 }] }, originalInventory);
        AssertRejected(firstNativeFrame with { Gpus = [firstNativeFrame.Gpus[0] with { AdapterKey = null }] }, originalInventory);
        AssertRejected(firstNativeFrame, originalInventory with { Adapters = [adapter with { Luid = new() { LowPart = 99 } }] });
        recording.Inventory = originalInventory with { Generation = 2, ObservedAtUtcTicks = originalInventory.ObservedAtUtcTicks + 1 };
        gpuMappings = 0;
        Assert.Contains(producer.Discover(firstNativeFrame).FieldBindings.Values,
            binding => binding.ControlTarget?.RawObjectId == "gpu:recorded");
        Assert.True(gpuMappings > 0);

        void AssertRejected(HardwareMetricSnapshot frame, WindowsGpuAdapterInventoryRead inventory)
        {
            recording.Inventory = inventory;
            gpuMappings = 0;
            var rejected = producer.Discover(frame);
            Assert.Equal(0, gpuMappings);
            Assert.DoesNotContain(rejected.FieldBindings.Values, binding => binding.ControlTarget?.GpuDeviceIdentity is not null);
            Assert.Same(frame.GpuInventory, rejected.GpuInventory);
        }
    }

    [Fact]
    public async Task OwnerQueryIsPassiveAndCollectAlonePublishesConfiguredReadbackOrClearsFailure()
    {
        using var recording = new Fixture();
        using (var actual = WindowsHardwareMetricOwner.CreateDefault(new() { Controls = new(recording.Folder) }))
            Assert.False(Directory.Exists(recording.Folder)); // Factory only; never Discover on actual readers.
        using var owner = recording.Owner(); using var client = new HttpClient();
        var core = new ReadWriteCore(owner.CreateBackend(), new DependencyActions(client, []));
        Assert.Empty(core.Catalog.Fields); Assert.Equal(0, recording.Opens);
        var catalog = await core.DiscoverAsync();
        Assert.Equal(1, recording.Opens); Assert.True(recording.Intel.Reads > 0); Assert.Equal(0, recording.Intel.Writes);
        var field = catalog.Fields.Single(capability => capability.Field.FieldName == IntelPl1).Field;
        var reads = recording.Intel.Reads;
        Assert.True(core.Query(field).Write.Supported); Assert.NotNull(core.Query(field).WriteContract);
        Assert.Null(core.ReadCached(field).Value); Assert.Equal(reads, recording.Intel.Reads);
        var first = (await core.CollectAsync([field])).Single();
        Assert.Equal(FieldObservationStatus.Current, first.Status); Assert.Equal(45d, first.Value!.Number);
        // All/catalog inclusion is topology publication, not explicit control discovery.
        // The original recorded hardware counts CanWriteMsr, used by Probe but not ReadAsync.
        var probeChecks = recording.Intel.ProbeChecks;
        await owner.CaptureAsync(MetricSampleRequest.All);
        Assert.Equal(probeChecks, recording.Intel.ProbeChecks);
        var address = owner.ReadCachedSnapshot()!.FieldBindings.Values.Single(binding => binding.Field.Name == IntelPl1).RequestAddress;
        await owner.CaptureAsync(MetricSampleRequest.ForIds([address]).WithCatalog());
        Assert.Equal(probeChecks, recording.Intel.ProbeChecks);
        Assert.Equal(45d, core.ReadCached(field).Value!.Number);
        Assert.Equal(0, recording.Intel.Writes);
        var beforeWrite = owner.ReadCachedSnapshot();
        var applied = await core.WriteAsync(field, new(Number: 40));
        Assert.Equal("applied", applied.Status); Assert.Equal(FieldWriteEvidence.MaskedRegisterReadback, applied.Evidence!.Kind);
        Assert.Same(beforeWrite, owner.ReadCachedSnapshot()); Assert.Equal(45d, core.ReadCached(field).Value!.Number);
        Assert.Equal(40d, (await core.CollectAsync([field])).Single().Value!.Number);
        recording.Intel.IgnoreWrites = true;
        var failedWrite = await core.WriteAsync(field, new(Number: 50));
        Assert.Equal("failed", failedWrite.Status); Assert.Null(failedWrite.Evidence);
        Assert.Equal(40d, core.ReadCached(field).Value!.Number);
        recording.Intel.FailReads = true;
        var failedRead = (await core.CollectAsync([field])).Single();
        Assert.Equal(FieldObservationStatus.Unavailable, failedRead.Status); Assert.Null(failedRead.Value);
        Assert.Null(core.ReadCached(field).Value);
    }

    [Fact]
    public async Task InvalidPayloadRangeStepAndChangedOrAmbiguousCpuNeverReachSetter()
    {
        using var recording = new Fixture(); var producer = recording.Producer();
        var binding = Binding(producer.Discover(Fixture.Snapshot()), IntelPl1); var reference = Reference(binding);
        var reads = recording.Intel.Reads;
        Assert.True(producer.Query(binding).Supported); Assert.Equal(reads, recording.Intel.Reads);
        foreach (var value in new[] { new FieldValue(Integer: "40"), new FieldValue(Number: double.NaN), new FieldValue(Number: 201), new FieldValue(Number: 45.5) })
            Assert.Equal("failed", (await producer.WriteAsync(reference, binding, value)).Status);
        Assert.Equal(0, recording.Intel.Writes);
        recording.Identity = recording.Identity with { Stepping = 4 }; recording.Topology = Fixture.SingleTopology(recording.Identity);
        Assert.Equal("control-field-identity-changed-or-ambiguous", (await producer.WriteAsync(reference, binding, new(Number: 40))).Reason);
        Assert.Equal(0, recording.Intel.Writes);
        recording.Identity = recording.Identity with { Stepping = 3 };
        recording.Topology = Fixture.SingleTopology(recording.Identity) with { Packages = [new("package:0", [0]), new("package:1", [1])] };
        Assert.Equal("control-field-identity-changed-or-ambiguous", (await producer.WriteAsync(reference, binding, new(Number: 40))).Reason);
        Assert.Equal(0, recording.Intel.Writes);
        Assert.Empty(producer.Discover(Fixture.Snapshot()).FieldBindings);
    }

    [Fact]
    public async Task AmdConfiguredLimitIsNotActualPowerAndPboSuccessRemainsAcceptedOnly()
    {
        using var recording = new Fixture("AuthenticAMD"); using var owner = recording.Owner(); using var client = new HttpClient();
        var core = new ReadWriteCore(owner.CreateBackend(), new DependencyActions(client, []));
        Assert.Equal(0, recording.Bridge.Calls); var catalog = await core.DiscoverAsync();
        Assert.True(recording.Bridge.Calls > 0); Assert.Equal(0, recording.Bridge.Settings);
        var power = catalog.Fields.Single(capability => capability.Field.FieldName == AmdStapm).Field;
        var pbo = catalog.Fields.Single(capability => capability.Field.FieldName == "cpu_amd_requested_pbo_scalar_multiplier").Field;
        var count = recording.Bridge.Calls;
        Assert.True(core.Query(power).Write.Supported); Assert.False(core.Query(pbo).Read.Supported);
        Assert.Null(core.ReadCached(power).Value); Assert.Equal(count, recording.Bridge.Calls);
        var read = (await core.CollectAsync([power])).Single();
        Assert.Equal(45d, read.Value!.Number); Assert.NotEqual(Helper.ActualPower, read.Value.Number);
        var verified = await core.WriteAsync(power, new(Number: 35));
        Assert.Equal(FieldWriteEvidence.SettingReadbackTolerance, verified.Evidence!.Kind);
        var accepted = await core.WriteAsync(pbo, new(Number: 2));
        Assert.Equal("applied", accepted.Status); Assert.Equal(FieldWriteEvidence.AcceptedOnly, accepted.Evidence!.Kind);
        Assert.Null(accepted.Evidence!.ObservedValue);
        var unreadable = (await core.CollectAsync([pbo])).Single();
        Assert.Equal(FieldObservationStatus.Unsupported, unreadable.Status); Assert.Null(unreadable.Value);
        // PBO's documented default is not a sampled current setting.
        Assert.Equal(1d, core.Query(pbo).WriteContract!.DeclaredDefault);
    }

    [Fact]
    public async Task AmdIntegratedMissingOptionalReadbackReportsAcceptedNotVerified()
    {
        using var recording = new Fixture("AuthenticAMD");
        recording.Inventory = Fixture.GpuInventory(WindowsGpuAdapterKind.Integrated);
        recording.Bridge.OmitGpuWriteReadback = true;
        var producer = recording.Producer(); var discovered = producer.Discover(Fixture.Snapshot());
        var binding = Binding(discovered, "gpu_amd_requested_curve_optimizer_counts");
        var result = await producer.WriteAsync(Reference(binding), binding, new(Integer: "-10"));
        Assert.Equal("applied", result.Status); Assert.Equal(FieldWriteEvidence.AcceptedOnly, result.Evidence!.Kind);
        Assert.Null(result.Evidence!.ObservedValue);
        var selected = await producer.ReadSelectedAsync(discovered, MetricSampleRequest.ForIds([binding.RequestAddress]));
        Assert.Equal("-10", selected.Items[binding.RequestAddress].IntegerValue);
        recording.Inventory = recording.Inventory with { Adapters = [recording.Inventory.Adapters[0], recording.Inventory.Adapters[0] with { Index = 1 }], ObservedCount = 2 };
        var settings = recording.Bridge.Settings;
        Assert.Equal("failed", (await producer.WriteAsync(Reference(binding), binding, new(Integer: "-15"))).Status);
        Assert.Equal(settings, recording.Bridge.Settings);
    }

    [Fact]
    public async Task DesktopModeFanBooleanAndPnpContractsRejectBeforeRecordedSetter()
    {
        using var recording = new Fixture(); recording.Inventory = Fixture.GpuInventory();
        var producer = recording.Producer(); var discovered = producer.Discover(Fixture.Snapshot());
        var absolute = Binding(discovered, "gpu_amd_requested_graphics_clock_maximum_mhz");
        var offset = Binding(discovered, "gpu_amd_requested_graphics_clock_offset_mhz");
        var fan = Binding(discovered, "fan_amd_requested_zero_rpm_enabled");
        Assert.True(producer.Query(absolute).Supported); Assert.False(producer.Query(offset).Supported);
        Assert.EndsWith("|fan-tuning-group", fan.DeviceIdentity); Assert.Equal(absolute.AdapterKey, fan.AdapterKey);
        Assert.Equal("unsupported", (await producer.WriteAsync(Reference(offset), offset, new(Integer: "5"))).Status);
        Assert.Equal("failed", (await producer.WriteAsync(Reference(fan), fan, new(Number: 0))).Status);
        Assert.Equal(0, recording.Adlx.Writes);
        var toggled = await producer.WriteAsync(Reference(fan), fan, new(Boolean: false));
        Assert.Equal("applied", toggled.Status); Assert.Equal(FieldWriteEvidence.ExactIntegerAndPnpReadback, toggled.Evidence!.Kind);
        var collected = await producer.ReadSelectedAsync(discovered, MetricSampleRequest.ForIds([fan.RequestAddress]));
        Assert.False(collected.Items[fan.RequestAddress].BooleanValue); Assert.Null(collected.Items[fan.RequestAddress].NumericValue);
        var absoluteCurrent = await producer.ReadSelectedAsync(discovered, MetricSampleRequest.ForIds([absolute.RequestAddress]));
        Assert.Equal("2400", absoluteCurrent.Items[absolute.RequestAddress].IntegerValue);
        recording.Adlx.Mode = AdlxGfxMode.Offset;
        var readsBeforeModeCheck = recording.Adlx.Reads;
        var changedModeRead = await producer.ReadSelectedAsync(absoluteCurrent, MetricSampleRequest.ForIds([absolute.RequestAddress]));
        Assert.Equal(SamplingObservationStatus.Unavailable, changedModeRead.Datasets[absolute.RequestAddress].Status);
        Assert.False(changedModeRead.Items.ContainsKey(absolute.RequestAddress));
        Assert.Equal(readsBeforeModeCheck + 1, recording.Adlx.Reads); // Original Read only; no extra Probe.
        discovered = producer.Discover(discovered);
        absolute = Binding(discovered, absolute.Field.Name); offset = Binding(discovered, offset.Field.Name);
        Assert.False(producer.Query(absolute).Supported); Assert.True(producer.Query(offset).Supported);
        var settings = recording.Adlx.Writes;
        Assert.Equal("unsupported", (await producer.WriteAsync(Reference(absolute), absolute, new(Integer: "2000"))).Status);
        Assert.Equal(settings, recording.Adlx.Writes);
        recording.Adlx.Pnp = "PCI\\OTHER"; // Inventory still says original PNP: raw expected-PNP check must reject.
        Assert.NotEqual("applied", (await producer.WriteAsync(Reference(offset), offset, new(Integer: "5"))).Status);
        Assert.Equal(settings, recording.Adlx.Writes);
    }
    // Only original lower reader/transport seams are recorded. The producer,
    // native workspace, capture, publication/current and raw baseline algorithms remain real.
    private sealed class Fixture : IDisposable
    {
        internal readonly string Folder = Path.Combine(Path.GetTempPath(), "rm-canonical-controls-" + Guid.NewGuid().ToString("N"));
        internal readonly IntelHardware Intel = new();
        internal readonly Helper Bridge = new();
        internal readonly AdlxHardware Adlx = new();
        internal CpuIdentityInformation Identity;
        internal CpuOsTopologyInformation Topology;
        internal WindowsGpuAdapterInventoryRead Inventory = EmptyInventory();
        internal readonly IntelCpuControlResource IntelResource;
        internal readonly AmdCpuControlResource AmdResource;
        internal readonly AmdDesktopGpuControlResource DesktopResource;
        internal readonly IntegratedGpuControlResource IntegratedResource;
        internal int Opens;
        internal Fixture(string vendor = "GenuineIntel")
        {
            Identity = new(vendor, 6, 154, 3, "recorded CPU", [], []);
            Topology = SingleTopology(Identity);
            IntelResource = new(() => { Opens++; return Intel; }, Path.Combine(Folder, "intel.json"));
            AmdResource = new(Bridge, AmdCpuControlOptions.InDirectory(Folder));
            DesktopResource = new(Adlx, new(Path.Combine(Folder, "desktop.json")));
            IntegratedResource = new(Bridge, new IntelGraphicsHardware(), new(Path.Combine(Folder, "integrated.json"), _ => Task.FromResult(true)));
        }
        internal WindowsMetricControlResources Controls => new()
        {
            IntelCpuControl = IntelResource, AmdCpuControl = AmdResource,
            AmdDesktopGpuControl = DesktopResource, IntegratedGpuControl = IntegratedResource,
            ControlReadCpuIdentity = () => Identity, ControlReadCpuTopology = () => Topology,
            ControlIsFixedChassis = () => true
        };
        internal WindowsMetricControlFields Producer() => new(Controls, () => Inventory);
        internal WindowsHardwareMetricOwner Owner() => new(WindowsMetricConfiguration.CreateDefault(), Readers(), new());
        internal static HardwareMetricSnapshot Snapshot() => new(DateTimeOffset.UtcNow, null!, null!, null!, [], null!, new Dictionary<string, MetricValue>())
            { WorkspaceIdentity = 1, ConfigurationGeneration = 1, CatalogGeneration = 1, CommittedGeneration = 1 };
        internal static CpuOsTopologyInformation SingleTopology(CpuIdentityInformation identity) => new([new("core:0", 0, false, [0])], [])
        {
            Packages = [new("package:0", [0])],
            ThreadCpuid = [new(0, new(identity, null, null, null, null, null, null, []))]
        };
        internal static WindowsGpuAdapterInventoryRead EmptyInventory() => new(SamplingObservationStatus.Current, 1, DateTimeOffset.UtcNow.UtcTicks, 0, 0, 0, 1, []);
        internal static WindowsGpuAdapterInventoryRead GpuInventory(WindowsGpuAdapterKind kind = WindowsGpuAdapterKind.Dedicated,
            string pnp = "PCI\\VEN_1002&DEV_73BF\\RECORDED", int index = 0) => new(SamplingObservationStatus.Current, 1, DateTimeOffset.UtcNow.UtcTicks, 1, 0, 0, 1,
            [new(index, "recorded AMD GPU", 0x1002, 0x73BF, 1, new AdapterLuid { LowPart = 1, HighPart = 8 }, false, 2UL * 1024 * 1024 * 1024, kind)
            {
                PhysicalIdentityStatus = SamplingObservationStatus.Current,
                PhysicalIdentities = [new(0, pnp, WindowsGpuPnpKind.Pci, true, true, 0, 1, 0, 0, 0x1002, 0x73BF, 0x1002, 1, 1,
                    WindowsGpuIdentityEvidence.KmtHardwarePnpKey | WindowsGpuIdentityEvidence.CmExactDevNode,
                    WindowsGpuPhysicalIdentityStatus.Bound, 0)]
            }]);
        // Copy of the existing callback fixture shape; no second sampler or metric projection.
        private WindowsHardwareMetricResources Readers() => new()
        {
            CpuName = "recorded", MemoryDescription = "recorded", Controls = Controls,
            ReadCpuCounters = () => throw new InvalidOperationException("unselected CPU telemetry"),
            ReadMemory = () => throw new InvalidOperationException("unselected memory telemetry"),
            ReadVirtualMemory = () => throw new InvalidOperationException(),
            ReadNumaAvailable = (_, selected) => selected.Count == 0 ? new Dictionary<string, ulong>() : throw new InvalidOperationException(),
            ReadNumaIdentities = () => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, []),
            ReadGpuInventory = () => Inventory,
            ReadCpuFrequency = () => throw new InvalidOperationException(), ReadGpuUsage = _ => throw new InvalidOperationException(),
            ReadNvmlAsync = (_, _, _) => throw new InvalidOperationException(), ReadNvapiAsync = (_, _, _) => throw new InvalidOperationException(),
            ReadAdlxAsync = (_, _, _) => throw new InvalidOperationException(), ReadAmdSmu = _ => throw new InvalidOperationException(),
            ReadIntelGpu = (_, _, _) => throw new InvalidOperationException(), ReadIntelMsr = (_, _) => throw new InvalidOperationException(),
            ReadBatteryIdentities = () => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, []),
            ReadBattery = (_, _) => throw new InvalidOperationException(),
            ReadNetworkIdentities = () => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, []),
            ReadNetworkInterfaces = (_, _) => throw new InvalidOperationException(), ReadSystemIo = _ => throw new InvalidOperationException(),
            ReadStorageIdentities = _ => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, []),
            ReadFanIdentities = _ => new(NativeMetricSnapshotCatalogTopologyStatus.Complete, []),
            ReadStorageSensors = () => throw new InvalidOperationException(), ReadNvmeSensors = _ => throw new InvalidOperationException(),
            ReadAtaSensors = _ => throw new InvalidOperationException(), ReadHardwareMonitor = () => throw new InvalidOperationException(),
            ReadNotebookFans = () => throw new InvalidOperationException(), SynchronizeNotebookDemand = _ => { }
        };
        public void Dispose()
        {
            IntegratedResource.Dispose(); DesktopResource.Dispose(); AmdResource.Dispose(); IntelResource.Dispose(); Bridge.Dispose();
            if (!Directory.Exists(Folder)) return;
            // All fixtures create only immediate baseline files in this unique directory.
            foreach (var file in Directory.EnumerateFiles(Folder)) File.Delete(file);
            Directory.Delete(Folder);
        }
    }
    private sealed class IntelHardware : IIntelCpuHardware
    {
        internal ulong Limits = 360UL | (1UL << 15) | (640UL << 32);
        internal int Reads, Writes, ProbeChecks;
        internal bool FailReads = false, IgnoreWrites = false;
        public ulong ReadMsr(uint register)
        {
            Reads++; if (FailReads) throw new IOException("recorded unavailable");
            return register switch { 0x606 => 3UL | (10UL << 16), 0x610 => Limits, _ => throw new IOException() };
        }
        public bool CanWriteMsr(uint register) { ProbeChecks++; return register == 0x610; }
        public void WriteMsr(uint register, ulong value)
        { if (register != 0x610) throw new IOException(); Writes++; if (!IgnoreWrites) Limits = value; }
        public void Dispose() { }
    }
    private sealed class Helper : IHardwareBridgeProcessClient
    {
        public bool IsInstalled => true;
        public string? UnavailableReason => null;
        internal int Calls, Settings;
        internal double Stapm = 45, Slow = 55, Fast = 65, CpuCounts = -5, GpuCounts = 8;
        internal bool OmitGpuWriteReadback = false;
        internal const double ActualPower = 7;
        public Task<JsonElement?> SendAsync(string operation, IReadOnlyDictionary<string, object?>? arguments, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Calls++;
            if (operation.StartsWith("set-", StringComparison.Ordinal)) Settings++;
            if (operation == "set-power-limit")
            {
                var value = Convert.ToDouble(arguments!["value"]);
                if ((string?)arguments["which"] == "stapm") Stapm = value;
                else if ((string?)arguments["which"] == "slow") Slow = value; else Fast = value;
            }
            if (operation == "set-curve-optimizer") CpuCounts = Convert.ToDouble(arguments!["value"]);
            if (operation == "set-igpu-curve-optimizer")
            {
                GpuCounts = Convert.ToDouble(arguments!["value"]);
                if (OmitGpuWriteReadback) return Task.FromResult<JsonElement?>(JsonSerializer.SerializeToElement(new { ok = true, accepted = true }));
            }
            return Task.FromResult<JsonElement?>(JsonSerializer.SerializeToElement(new
            {
                ok = true, available = true, powerTable = true, curveOptimizer = true, integratedGpuCurveOptimizer = true,
                accepted = true, status = "OK", stapmLimitWatts = Stapm, slowLimitWatts = Slow, fastLimitWatts = Fast,
                curveOptimizerCounts = CpuCounts, integratedGpuCurveOptimizerCounts = GpuCounts, actualPowerWatts = ActualPower
            }));
        }
        public void Dispose() { }
    }
    private sealed class IntelGraphicsHardware : IIntelGraphicsControlHardware
    {
        public bool IsAvailable => false;
        public IntPtr? FindIntegratedDevice(ulong? key) => null;
        public bool TryAcceptOverclockWaiver(IntPtr device) => throw new InvalidOperationException("unselected waiver");
        public double? ReadFrequencyOffsetMhz(IntPtr device) => throw new InvalidOperationException();
        public int WriteFrequencyOffsetMhz(IntPtr device, double value) => throw new InvalidOperationException("unselected setter");
    }
    private sealed class AdlxHardware : IAdlxTuningHardware
    {
        internal string Pnp = "PCI\\VEN_1002&DEV_73BF\\RECORDED";
        internal AdlxGfxMode Mode = AdlxGfxMode.Absolute;
        internal int Reads, Writes;
        internal readonly Dictionary<int, int> Values = new() { [0] = 0, [1] = 600, [2] = 2400, [3] = 1000, [4] = 2000, [5] = 500, [6] = 1500, [7] = 1, [8] = 0, [9] = 0 };
        public AdlxTuningValue Read(int? adapterIndex, int field)
        {
            Reads++;
            var (min, max, step) = field switch { 0 => (-10, 14, 2), 7 => (0, 1, 1), 8 or 9 => (-500, 500, 5), _ => (0, 4000, 1) };
            // Return actual mode metadata, allowing the producer itself to refuse
            // incompatible absolute/offset declarations before the recorded setter.
            return new(Pnp, Values[field], min, max, step, Mode);
        }
        public void Write(string pnp, int field, int value)
        { if (pnp != Pnp) throw new IOException(); Writes++; Values[field] = value; }
    }
    private static MetricDeviceFieldBinding Binding(HardwareMetricSnapshot snapshot, string name)
        => snapshot.FieldBindings.Values.Single(binding => binding.Field.Name == name);
    private static FieldReference Reference(MetricDeviceFieldBinding binding) => new(binding.DeviceIdentity, binding.Field.Name);
}
