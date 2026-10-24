using System.Text.Json;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.ReadWrite.Native;
using ResourceManager.ReadWrite.Native.Pdh;
using ResourceManager.ReadWrite.Vendors.Amd;
using ResourceManager.ReadWrite.Vendors.Intel.Msr;
using ResourceManager.ReadWrite.Vendors.Nvidia;
using ResourceManager.ReadWrite.Windows.Cpu.Models;
using ResourceManager.ReadWrite.Windows.Devices.Battery;
using ResourceManager.ReadWrite.Windows.Devices.Npu;
using ResourceManager.ReadWrite.Windows.Gpu;
using ResourceManager.ReadWrite.Windows.Metrics.Capture;
using ResourceManager.ReadWrite.Windows.Metrics.Resources;
using ResourceManager.ReadWrite.Windows.Metrics.Writes;
using ResourceManager.ReadWrite.Windows.Pdh;

namespace Resource_Manager_APP.Tests;

public sealed class MetricDeviceFieldAddressTests
{
    [Fact]
    public void EveryDescriptorKindSurvivesCatalogReadAndUsesItsTypedAddress()
    {
        const string pnp = @"PCI\VEN_10DE&DEV_2C05\GPU-RAW";
        const string secondPnp = @"PCI\VEN_10DE&DEV_2C05\GPU-RAW-2";
        var processor = new MetricProcessorAddress(3, 19);
        var gpu = new MetricDeviceDescriptor("gpu_adapter",
            MetricDeviceAddress.GpuAdapter(0xA1, [pnp]), "Fixture GPU", 0);
        var descriptors = new[]
        {
            new MetricDeviceDescriptor("host", null, null, 0),
            new MetricDeviceDescriptor("cpu", null, null, 0),
            new MetricDeviceDescriptor("memory", null, null, 0),
            new MetricDeviceDescriptor("virtual_memory", null, null, 0),
            new MetricDeviceDescriptor("cpu_logical_processor", MetricDeviceAddress.LogicalProcessor(3, 19), null, 0),
            new MetricDeviceDescriptor("cpu_core", MetricDeviceAddress.CpuCore("core:0", [processor]), null, 0),
            new MetricDeviceDescriptor("cpu_package", MetricDeviceAddress.CpuPackage("package:0", [processor]), null, 0),
            new MetricDeviceDescriptor("cpu_die", MetricDeviceAddress.CpuDie("die:0", [processor]), null, 0),
            new MetricDeviceDescriptor("cpu_platform", MetricDeviceAddress.CpuPlatform([processor]), null, 0),
            new MetricDeviceDescriptor("numa_node", MetricDeviceAddress.NumaNode(7), null, 0),
            gpu,
            new MetricDeviceDescriptor("gpu_adapter", MetricDeviceAddress.GpuAdapter(0xA2, [pnp, secondPnp]), "Composite GPU", 1),
            new MetricDeviceDescriptor("gpu_engine", MetricDeviceAddress.GpuEngine(gpu.Address!, 0xA1, 2, 3, 4), null, 0),
            new MetricDeviceDescriptor("npu", MetricDeviceAddress.Npu(@"ACPI\NPU_RAW"), null, 0),
            new MetricDeviceDescriptor("npu_engine", MetricDeviceAddress.NpuEngine(@"ACPI\NPU_RAW", 0, 4), "NPU compute", 0),
            new MetricDeviceDescriptor("storage", MetricDeviceAddress.StorageDevice(2, "unique-2", "serial-2"), "Fixture Disk", 0),
            new MetricDeviceDescriptor("fan", MetricDeviceAddress.SystemFan(@"root\LibreHardwareMonitor", "/lpc/fan/0"), "CPU Fan", 0),
            new MetricDeviceDescriptor("fan", MetricDeviceAddress.OemFan("cpu"), null, 1),
            new MetricDeviceDescriptor("fan", MetricDeviceAddress.GpuFan(pnp, 0), null, 2),
            new MetricDeviceDescriptor("gpu_fan_tuning_group", MetricDeviceAddress.GpuFanTuningGroup(0xA1, [pnp]), "Fixture GPU", 0),
            new MetricDeviceDescriptor("battery", MetricDeviceAddress.Battery(@"\\?\BATTERY#0", "battery-0"), null, 0),
            new MetricDeviceDescriptor("network_interface", MetricDeviceAddress.NetworkInterface(
                Guid.Parse("01234567-89ab-cdef-0123-456789abcdef"), 0x1122334455667788, 24), null, 0)
        };
        var field = MetricFieldRegistry.FindByName("cpu_all_usage_percent")!;
        var bindings = descriptors.Select((device, index) => new MetricDeviceFieldBinding(
            $"fixture-device-{index}", field, $"fixture-request-{index}", "fixture-source")
        { Device = device }).ToArray();
        var snapshot = WithCurrentReadings(CreateRequestSnapshot(bindings), bindings);

        var devices = MetricDeviceFields.Read(snapshot, fields: new HashSet<string>([field.Name], StringComparer.Ordinal));
        Assert.Equal(descriptors.Length, devices.Count);
        for (var index = 0; index < descriptors.Length; index++)
        {
            var row = Assert.Single(devices, device => device.DeviceIdentity == bindings[index].DeviceIdentity);
            Assert.Equal(descriptors[index].Kind, row.Device.Kind);
            AssertAddress(descriptors[index].Address, row.Device.Address);
            Assert.Equal(descriptors[index].Ordinal, row.Device.Ordinal);
            if (descriptors[index].Address is { } expectedAddress)
                Assert.Equal(expectedAddress.Kind, Assert.IsType<MetricDeviceAddress>(row.Device.Address).Kind);
            Assert.Equal(37.5, Assert.Single(row.Fields).NumericValue);
            var catalogDevice = Assert.Single(MetricDeviceFields.Catalog(snapshot),
                item => item.DeviceIdentity == row.DeviceIdentity).Device;
            Assert.Equal(row.Device.Kind, catalogDevice.Kind);
            AssertAddress(row.Device.Address, catalogDevice.Address);
            Assert.Equal(row.Device.Ordinal, catalogDevice.Ordinal);
        }

        var json = JsonSerializer.Serialize(devices,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("\"kind\":\"cpu_logical_processor\",\"address\":{\"kind\":\"cpu_logical_processor\",\"processorGroup\":3,\"groupRelativeIndex\":19}", json);
        Assert.Contains("\"kind\":\"host\",\"address\":null", json);
        Assert.Contains("\"displayName\":null", json);
        Assert.Contains("\"physicalDiskDeviceId\":2", json);
        using var document = JsonDocument.Parse(json);
        var gpuGroupAddress = document.RootElement.EnumerateArray()
            .Select(static device => device.GetProperty("device").GetProperty("address"))
            .Single(static address => address.ValueKind == JsonValueKind.Object
                && address.GetProperty("kind").GetString() == "gpu_adapter_group");
        Assert.Equal(new[] { pnp, secondPnp }, gpuGroupAddress.GetProperty("pnpInstanceIds")
            .EnumerateArray().Select(static member => member.GetString()));
        Assert.Contains("\"interfaceGuid\":\"01234567-89AB-CDEF-0123-456789ABCDEF\"", json);
        Assert.Contains("\"kind\":\"gpu_adapter_group\"", json);
        Assert.Equal("cpu_logical_processor", Assert.IsType<MetricDeviceAddress>(
            Assert.Single(devices, device => device.Device.Kind == "cpu_logical_processor").Device.Address).Kind);
        Assert.Equal("numa_node", Assert.IsType<MetricDeviceAddress>(
            Assert.Single(devices, device => device.Device.Kind == "numa_node").Device.Address).Kind);
        Assert.Equal("npu", Assert.IsType<MetricDeviceAddress>(
            Assert.Single(devices, device => device.Device.Kind == "npu").Device.Address).Kind);
        Assert.Equal("gpu_adapter", Assert.IsType<MetricDeviceAddress>(
            Assert.Single(devices, device => device.Device.Kind == "gpu_adapter"
                && device.Device.Address?.Kind == "gpu_adapter").Device.Address).Kind);
    }

    [Fact]
    public void NativeCatalogProjectionBindsAllProductionScopesWithoutChangingTypedValues()
    {
        const string gpuPnp = @"PCI\VEN_10DE&DEV_2C05&SUBSYS_00000000&REV_A1\GPU-RAW";
        const string multiGpuPnp = @"PCI\VEN_10DE&DEV_2C05\GPU-RAW-A";
        const string secondMultiGpuPnp = @"PCI\VEN_10DE&DEV_2C05\GPU-RAW-B";
        const string npuPnp = @"ACPI\NPU_RAW";
        var plan = HostManagerTestPlanFactory.CreatePlan().MetricSnapshot;
        const string packageId = "windows-cpu-package|typed-package-id";
        const string dieId = "windows-cpu-die|typed-die-id";
        var cpuIdentity = new CpuIdentityInformation("GenuineIntel", 6, 0x55, 0,
            "Fixture Intel CPU", [], []);
        var cpuidData = new CpuThreadCpuidData(cpuIdentity, 0x906EA, null, 0x319,
            null, null, null, [])
        {
            IntelDtsSupported = true,
            IntelPackageThermalSupported = true,
            IntelPerformanceFeedbackSupported = true,
            IntelProcessorBaseFrequencyDeclaredMhz = 2400
        };
        var intelTopology = new CpuOsTopologyInformation(
            [new CpuOsPhysicalCore("core:0", 0, false, [211])], [])
        {
            Packages = [new CpuOsPackage(packageId, [211])],
            Dies = [new CpuOsDie(dieId, [211], packageId)],
            ThreadCpuid = [new CpuThreadCpuidInformation(211, cpuidData)]
        };
        var projectedCpu = IntelMsrTopologyProjection.Create(intelTopology).Identities;
        var cpu = Assert.Single(projectedCpu, static identity => identity.Kind == IntelMsrTargetKind.LogicalProcessor);
        var core = Assert.Single(projectedCpu, static identity => identity.Kind == IntelMsrTargetKind.PhysicalCore);
        var package = Assert.Single(projectedCpu, static identity => identity.Kind == IntelMsrTargetKind.Package);
        var die = Assert.Single(projectedCpu, static identity => identity.Kind == IntelMsrTargetKind.PhysicalDie);
        Assert.Equal("core:0", core.CoreId);
        Assert.Equal(packageId, package.PackageId);
        Assert.Equal(dieId, die.DieId);
        Assert.Equal(new CpuProcessorAddress(3, 19), cpu.Processor);
        var gpu = new NativeMetricSnapshotGpuCatalogIdentity(0, "Fixture GPU",
            "opaque-gpu-identity", 0xA1, 1, 1, true)
        { PhysicalPnpInstanceIds = [gpuPnp] };
        var multiGpu = new NativeMetricSnapshotGpuCatalogIdentity(1, "Composite GPU",
            "opaque-multi-gpu-identity", 0xA2, 1, 1, true)
        { PhysicalPnpInstanceIds = [multiGpuPnp, secondMultiGpuPnp] };
        var npu = new NpuMemoryCatalogIdentity(npuPnp) { NameDeclared = "Fixture NPU" };
        using var engineDirectory = new FixtureNpuEngineDirectory(npuPnp);
        var engine = Assert.Single(engineDirectory.Identities);
        var disk = new NativeMetricSnapshotOrdinalCatalogIdentity(2, "storage|opaque", 1)
        {
            SupportsNvmeHealth = true,
            SupportsAtaSmart = true,
            Device = new("storage", MetricDeviceAddress.StorageDevice(2, "disk-unique", "disk-serial"), "Fixture Disk", 0)
        };
        var wmiFan = new NativeMetricSnapshotOrdinalCatalogIdentity(0, "fan|wmi-opaque", 1)
        {
            Device = new("fan", MetricDeviceAddress.SystemFan(@"root\LibreHardwareMonitor", "/fan/0"), "Fixture Fan", 0)
        };
        var nvmlFan = new NvidiaNvmlFanIdentity(gpuPnp, 0);
        var gpuFan = new NativeMetricSnapshotOrdinalCatalogIdentity(1, nvmlFan.ExactIdentityKey, 1)
        {
            NvmlFan = nvmlFan,
            Device = new("fan", MetricDeviceAddress.GpuFan(gpuPnp, 0), null, 1)
        };
        var oemFans = WindowsNotebookFanProjection.CreateCatalogIdentities(2, 1);
        var battery = new BatteryCatalogIdentity(0, @"\\?\BATTERY#0", "battery-unique",
            new BatteryInformation(0, "Li-ion", 50000, 45000, 12));
        var network = new NetworkInterfaceCatalogIdentity(0,
            Guid.Parse("01234567-89ab-cdef-0123-456789abcdef"), 0x1122334455667788, 24);
        using var workspace = NativeMetricSnapshotHostConfiguration.CreateWorkspace(plan,
            Path.Combine(Path.GetTempPath(), $"metric-device-descriptor-{Guid.NewGuid():N}.bin"));
        var catalog = NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(plan,
            [gpu, multiGpu], [disk], [wmiFan, gpuFan, .. oemFans], workspace.CreateCatalogHandleMap(),
            MetricVendor.Intel, [battery], [new NumaMemoryCatalogIdentity(7)],
            projectedCpu, [network], [npu], engineDirectory.Identities, engineDirectory);
        workspace.ReplaceCatalog(catalog, DateTimeOffset.UtcNow);

        var expectations = new (string Identity, MetricFieldCode Code, string Kind, MetricDeviceAddress Address, int Ordinal)[]
        {
            (cpu.ExactIdentityKey, MetricFieldCode.IntelEffectiveFrequencyMhz, "cpu_logical_processor", MetricDeviceAddress.LogicalProcessor(3, 19), 0),
            (core.ExactIdentityKey, MetricFieldCode.IntelCoreDtsDistance, "cpu_core", MetricDeviceAddress.CpuCore("core:0", [new(3, 19)]), 0),
            (package.ExactIdentityKey, MetricFieldCode.IntelPackageDtsDistance, "cpu_package", MetricDeviceAddress.CpuPackage(packageId, [new(3, 19)]), 0),
            (die.ExactIdentityKey, MetricFieldCode.IntelDiePackageEnergyCounter, "cpu_die", MetricDeviceAddress.CpuDie(dieId, [new(3, 19)]), 0),
            (new NumaMemoryCatalogIdentity(7).ExactIdentityKey, MetricFieldCode.NumaNodeCapacityMiB, "numa_node", MetricDeviceAddress.NumaNode(7), 0),
            (gpu.ExactIdentityKey, MetricFieldCode.GpuUsage, "gpu_adapter", MetricDeviceAddress.GpuAdapter(0xA1, [gpuPnp]), 0),
            (npu.ExactIdentityKey, MetricFieldCode.NpuDedicatedMemoryCommitted, "npu", MetricDeviceAddress.Npu(npuPnp), 0),
            (npu.ExactIdentityKey, MetricFieldCode.NpuDeviceEngineBusyPercent, "npu", MetricDeviceAddress.Npu(npuPnp), 0),
            (engine.ExactIdentityKey, MetricFieldCode.NpuEngineRunningTimeMicroseconds, "npu_engine", MetricDeviceAddress.NpuEngine(npuPnp, 0, 4), 0),
            (disk.ExactIdentityKey, MetricFieldCode.DiskTemperatureMaximum, "storage", MetricDeviceAddress.StorageDevice(2, "disk-unique", "disk-serial"), 0),
            (wmiFan.ExactIdentityKey, MetricFieldCode.FanRpm, "fan", MetricDeviceAddress.SystemFan(@"root\LibreHardwareMonitor", "/fan/0"), 0),
            (battery.ExactIdentityKey, MetricFieldCode.BatteryDesignCapacityAbsolute, "battery", MetricDeviceAddress.Battery(battery.DevicePath, battery.UniqueId), 0),
            (network.ExactIdentityKey, MetricFieldCode.NetworkRxRate, "network_interface", MetricDeviceAddress.NetworkInterface(network.Guid, network.Luid, network.Index), 0)
        };
        foreach (var expected in expectations)
        {
            var matches = catalog.RuleBindings.Values.Where(row => row.DeviceIdentity == expected.Identity
                && row.FieldCode == expected.Code
                && (expected.Identity != gpu.ExactIdentityKey || row.SourceId == NativeMetricSnapshotSources.NvidiaNvml))
                .ToArray();
            Assert.NotEmpty(matches);
            Assert.All(matches, binding =>
            {
                Assert.Equal(expected.Kind, binding.Device.Kind);
                AssertAddress(expected.Address, binding.Device.Address);
                Assert.Equal(expected.Ordinal, binding.Device.Ordinal);
            });
        }
        foreach (var kind in new[] { "host", "cpu", "memory", "virtual_memory" })
            Assert.Contains(catalog.RuleBindings.Values, row => row.Device.Kind == kind && row.Device.Address is null);
        Assert.DoesNotContain(catalog.RuleBindings.Values, row => row.Device.Kind == "unknown");
        var multiGpuBinding = Assert.Single(catalog.RuleBindings.Values,
            row => row.DeviceIdentity == multiGpu.ExactIdentityKey
                && row.FieldCode == MetricFieldCode.GpuUsage
                && row.SourceId == NativeMetricSnapshotSources.NvidiaNvml);
        Assert.Equal("gpu_adapter", multiGpuBinding.Device.Kind);
        var multiGpuAddress = Assert.IsType<MetricDeviceAddress>(multiGpuBinding.Device.Address);
        Assert.Equal("gpu_adapter_group", multiGpuAddress.Kind);
        Assert.Null(multiGpuAddress.PnpInstanceId);
        Assert.True(new[] { multiGpuPnp, secondMultiGpuPnp }.SequenceEqual(multiGpuAddress.PnpInstanceIds!));
        Assert.Equal("00000000000000A2", multiGpuAddress.AdapterLuid);

        var committed = NativeMetricSnapshotCommittedProjection.Create(
            workspace.ReadCommitted(), "fixture", "fixture", 1);
        var valuesToRead = new[]
        {
            Assert.Single(committed.FieldBindings.Values, binding => binding.DeviceIdentity == cpu.ExactIdentityKey
                && binding.Field.Code == MetricFieldCode.IntelEffectiveFrequencyMhz),
            Assert.Single(committed.FieldBindings.Values, binding => binding.DeviceIdentity == gpu.ExactIdentityKey
                && binding.Field.Code == MetricFieldCode.GpuUsage
                && binding.SourceId == NativeMetricSnapshotSources.NvidiaNvml),
            Assert.Single(committed.FieldBindings.Values, binding => binding.DeviceIdentity == npu.ExactIdentityKey
                && binding.Field.Code == MetricFieldCode.NpuDedicatedMemoryCommitted),
            Assert.Single(committed.FieldBindings.Values, binding => binding.DeviceIdentity == npu.ExactIdentityKey
                && binding.Field.Code == MetricFieldCode.NpuDeviceEngineBusyPercent)
        };
        committed = WithCurrentReadings(committed, valuesToRead);
        var read = MetricDeviceFields.Read(committed,
            fields: valuesToRead.Select(static binding => binding.Field.Name).ToHashSet(StringComparer.Ordinal));
        var cpuRead = Assert.Single(read, row => row.DeviceIdentity == cpu.ExactIdentityKey);
        Assert.Equal(1375.5, Assert.Single(cpuRead.Fields).NumericValue);
        Assert.Equal("Fixture Intel CPU", cpuRead.Device.DisplayName);
        var gpuRead = Assert.Single(read, row => row.DeviceIdentity == gpu.ExactIdentityKey);
        Assert.Equal(37.5, Assert.Single(gpuRead.Fields).NumericValue);
        var npuRead = Assert.Single(read, row => row.DeviceIdentity == npu.ExactIdentityKey);
        var memoryValue = Assert.Single(npuRead.Fields, value => value.Name == "npu_all_dedicated_memory_committed");
        Assert.Equal("unsigned64", memoryValue.ValueKind);
        Assert.Equal("9007199254740993", memoryValue.IntegerValue);
        Assert.Null(memoryValue.NumericValue);
        Assert.Contains(npuRead.Fields, value => value.Name == "npu_all_engine_busy_percent");
        Assert.Equal("Fixture GPU", gpuRead.Device.DisplayName);
        Assert.Equal("Fixture NPU", npuRead.Device.DisplayName);
    }

    [Fact]
    public void PnpAddressesUseTheDeviceTopologySpellingWithoutAnEndpointTopologyCache()
    {
        // Real Radeon 610M: DXGI/SetupAPI reports a lowercase instance part; device topology port DeviceId is upper invariant.
        const string reported = @" PCI\VEN_1002&DEV_164E&SUBSYS_50051D05&REV_D8\4&b799193&0&0041 ";
        const string topology = @"PCI\VEN_1002&DEV_164E&SUBSYS_50051D05&REV_D8\4&B799193&0&0041";
        const string secondReported = @"PCI\VEN_1002&DEV_164E\4&b799193&0&0042";
        var adapter = MetricDeviceAddress.GpuAdapter(0x10B0C, [reported]);
        Assert.Equal(topology, adapter.PnpInstanceId);
        Assert.Equal(topology, MetricDeviceAddress.GpuEngine(adapter, 0x10B0C, 0, 1, 0).PnpInstanceId);
        Assert.True(new[] { topology, secondReported.ToUpperInvariant() }.SequenceEqual(
            MetricDeviceAddress.GpuAdapter(0x10B0C, [reported, secondReported]).PnpInstanceIds!));
        Assert.Equal(topology, MetricDeviceAddress.GpuFanTuningGroup(0x10B0C, [reported]).PnpInstanceId);
        Assert.Equal(topology, MetricDeviceAddress.GpuFan(reported, 0).PnpInstanceId);
        Assert.Equal(@"ACPI\NPU_MIXED\4&B799193", MetricDeviceAddress.Npu(@"ACPI\Npu_Mixed\4&b799193").PnpInstanceId);
        Assert.Equal(@"ACPI\NPU_MIXED\4&B799193", MetricDeviceAddress.NpuEngine(@"ACPI\Npu_Mixed\4&b799193", 0, 1).PnpInstanceId);

        var plan = HostManagerTestPlanFactory.CreatePlan().MetricSnapshot;
        var gpu = new NativeMetricSnapshotGpuCatalogIdentity(0, "AMD Radeon(TM) 610M", "opaque-radeon", 0x10B0C, 1, 2, false)
        { PhysicalPnpInstanceIds = [reported] };
        var npu = new NpuMemoryCatalogIdentity(@"ACPI\Npu_Mixed\4&b799193");
        var catalog = NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(plan, [gpu], [], [],
            NativeMetricSnapshotCatalogHandleMap.Empty, npuMemoryIdentities: [npu]);
        var gpuBindings = catalog.RuleBindings.Values.Where(row => row.DeviceIdentity == gpu.ExactIdentityKey).ToArray();
        Assert.NotEmpty(gpuBindings);
        Assert.All(gpuBindings, row => Assert.Equal(topology, row.Device.Address!.PnpInstanceId));
        var npuBindings = catalog.RuleBindings.Values.Where(row => row.DeviceIdentity == npu.ExactIdentityKey).ToArray();
        Assert.NotEmpty(npuBindings);
        Assert.All(npuBindings, row => Assert.Equal(@"ACPI\NPU_MIXED\4&B799193", row.Device.Address!.PnpInstanceId));
    }

    [Fact]
    public void DiscoveredPdhGpuEngineCarriesParentDeviceAndItsOwnStableOrdinal()
    {
        const string gpuPnp = @"PCI\VEN_10DE&DEV_2C05\GPU-RAW";
        const ulong adapterLuid = 0xA1;
        var field = MetricFieldRegistry.FindByName("gpu_all_engine_busy_percent")!;
        var adapter = new MetricDeviceDescriptor("gpu_adapter",
            MetricDeviceAddress.GpuAdapter(adapterLuid, [gpuPnp]), "Fixture GPU", 0);
        var snapshot = CreateRequestSnapshot(
        [
            new MetricDeviceFieldBinding("gpu-adapter|fixture", field, "gpu.public", NativeMetricSnapshotSources.PdhGpuEngine)
            {
                AdapterKey = adapterLuid,
                Device = adapter
            }
        ]);
        var engineTwo = new NativePdhGpuEngineIdentity(adapterLuid, 0, 2, NativePdhEngineClass.Compute);
        var engineOne = new NativePdhGpuEngineIdentity(adapterLuid, 0, 1, NativePdhEngineClass.Other);
        var read = new PdhGpuEngineUsageRead(new Dictionary<int, double>(),
            NativePdhProviderAvailability.Available, DateTimeOffset.UtcNow)
        {
            UsageByEngine = new Dictionary<NativePdhGpuEngineIdentity, double>
            {
                [engineTwo] = 42.5,
                [engineOne] = 12.5
            }
        };

        var discovered = WindowsHardwareMetricCapture.ProjectGpuEngineFields(snapshot,
            MetricSampleRequest.CatalogProbe, null, read);
        var rows = discovered.FieldBindings.Values.OrderBy(static binding => binding.Device.Ordinal).ToArray();
        Assert.Equal(2, rows.Length);
        Assert.Equal(new uint?[] { 1, 2 }, rows.Select(static binding =>
            Assert.IsType<MetricDeviceAddress>(binding.Device.Address).EngineIndex).ToArray());
        Assert.Equal(new[] { 0, 1 }, rows.Select(static binding => binding.Device.Ordinal).ToArray());
        Assert.All(rows, binding =>
        {
            Assert.Equal("gpu_engine", binding.Device.Kind);
            var address = Assert.IsType<MetricDeviceAddress>(binding.Device.Address);
            Assert.Equal("gpu_engine", address.Kind);
            Assert.Equal(gpuPnp, address.PnpInstanceId);
            Assert.Equal("00000000000000A1", address.AdapterLuid);
        });
        Assert.Equal(rows[0].Device, Assert.Single(MetricDeviceFields.Read(discovered,
            rows[0].DeviceIdentity, new HashSet<string>([field.Name], StringComparer.Ordinal))).Device);
    }

    [Fact]
    public void DiscoveredGpuEngineFieldRequestUsesThePublicEngineAddressNotThePlannerTrigger()
    {
        // Real machine: /api/metrics/subscribe?fields=gpu_all_engine_busy_percent emitted no frame and
        // /api/metrics/fields/snapshot returned 24 NotRequested engines, because the field request carried the
        // planner trigger, which host publications and snapshot projections never contain. The host works on
        // public dataset ids; WindowsHardwareMetricCapture.CreateCollectionRequest maps them to the trigger.
        const ulong adapterLuid = 0xA1;
        var field = MetricFieldRegistry.FindByName("gpu_all_engine_busy_percent")!;
        var snapshot = CreateRequestSnapshot(
        [
            new MetricDeviceFieldBinding("gpu-adapter|fixture", field, "gpu.trigger", NativeMetricSnapshotSources.PdhGpuEngine)
            {
                AdapterKey = adapterLuid,
                Device = new("gpu_adapter", MetricDeviceAddress.GpuAdapter(adapterLuid, [@"PCI\VEN_10DE&DEV_2C05\GPU-RAW"]), "Fixture GPU", 0)
            }
        ]);
        var engine = new NativePdhGpuEngineIdentity(adapterLuid, 0, 1, NativePdhEngineClass.Other);
        var read = new PdhGpuEngineUsageRead(new Dictionary<int, double>(),
            NativePdhProviderAvailability.Available, DateTimeOffset.UtcNow)
        {
            UsageByEngine = new Dictionary<NativePdhGpuEngineIdentity, double> { [engine] = 12.5 }
        };
        var catalog = WindowsHardwareMetricCapture.ProjectGpuEngineFields(snapshot,
            MetricSampleRequest.CatalogProbe, null, read);
        var engineBinding = Assert.Single(catalog.FieldBindings.Values);
        Assert.DoesNotContain("gpu.trigger", catalog.FieldBindings.Keys);
        var state = new DashboardMonitoringCatalogState();
        state.PublishCatalogProbe(catalog);

        foreach (var device in new string?[] { null, engineBinding.DeviceIdentity })
        {
            var request = MetricDeviceFields.CreateRequest(catalog, device, [field.Name]);
            Assert.Equal(new[] { engineBinding.RequestAddress }, request.Ids.ToArray());
            Assert.Equal(request.Ids.ToArray(), state.SelectSubscribable(request).Ids.ToArray());
            // The completed sample is announced under the same public id the subscriber waits on.
            var published = MetricDatasetIds.ExpandBoundSystemDatasets(request.Ids, catalog.FieldBindings.Values);
            Assert.Contains(engineBinding.RequestAddress, published);
            Assert.DoesNotContain("gpu.trigger", published);
        }
        // The internal trigger is never a public subscription item.
        Assert.True(state.SelectSubscribable(MetricSampleRequest.ForIds(["gpu.trigger"])).IsEmpty);
    }

    [Fact]
    public void ControlBindingsDescribeCpuAndGpuFanTuningGroupWithoutProbingWrites()
    {
        const string gpuPnp = @"PCI\VEN_1002&DEV_73BF\RECORDED";
        var identity = new CpuIdentityInformation("AuthenticAMD", 25, 97, 2, "Recorded AMD CPU", [], []);
        var topology = new CpuOsTopologyInformation([new("core:0", 0, false, [0])], [])
        {
            Packages = [new("package:0", [0])],
            ThreadCpuid = [new(0, new(identity, null, null, null, null, null, null, []))]
        };
        var inventory = new WindowsGpuAdapterInventoryRead(SamplingObservationStatus.Current, 1,
            DateTimeOffset.UtcNow.UtcTicks, 1, 0, 0, 1,
            [new WindowsGpuAdapter(0, "Recorded AMD GPU", 0x1002, 0x73BF, 1,
                new AdapterLuid { LowPart = 1, HighPart = 8 }, false, 2UL * 1024 * 1024 * 1024,
                WindowsGpuAdapterKind.Dedicated)
            {
                PhysicalIdentityStatus = SamplingObservationStatus.Current,
                PhysicalIdentities =
                [
                    new(0, gpuPnp, WindowsGpuPnpKind.Pci, true, true, 0, 1, 0, 0,
                        0x1002, 0x73BF, 0x1002, 1, 1,
                        WindowsGpuIdentityEvidence.KmtHardwarePnpKey | WindowsGpuIdentityEvidence.CmExactDevNode,
                        WindowsGpuPhysicalIdentityStatus.Bound, 0)
                ]
            }]);
        var hardware = new FakeAdlx(gpuPnp);
        using var desktop = new AmdDesktopGpuControlResource(hardware,
            new(Path.Combine(Path.GetTempPath(), $"metric-control-{Guid.NewGuid():N}.json")));
        var resources = new WindowsMetricControlResources
        {
            AmdDesktopGpuControl = desktop,
            ControlReadCpuIdentity = () => identity,
            ControlReadCpuTopology = () => topology,
            ControlIsFixedChassis = static () => true
        };
        var discovered = new WindowsMetricControlFields(resources, () => inventory)
            .Discover(CreateRequestSnapshot([]));

        var cpu = Assert.Single(discovered.FieldBindings.Values,
            binding => binding.Field.Name == "cpu_amd_requested_stapm_power_limit_watts");
        Assert.Equal("cpu_platform", cpu.Device.Kind);
        var cpuAddress = Assert.IsType<MetricDeviceAddress>(cpu.Device.Address);
        Assert.Equal("cpu_platform", cpuAddress.Kind);
        Assert.True(new[] { new MetricProcessorAddress(0, 0) }.SequenceEqual(cpuAddress.ProcessorAddresses!));
        Assert.Equal("Recorded AMD CPU", cpu.Device.DisplayName);

        var fanGroup = Assert.Single(discovered.FieldBindings.Values,
            binding => binding.Field.Name == "fan_amd_requested_minimum_speed_rpm");
        Assert.Equal("gpu_fan_tuning_group", fanGroup.Device.Kind);
        var fanGroupAddress = Assert.IsType<MetricDeviceAddress>(fanGroup.Device.Address);
        Assert.Equal("gpu_adapter", fanGroupAddress.Kind);
        Assert.Equal(gpuPnp, fanGroupAddress.PnpInstanceId);
        Assert.Equal("0000000800000001", fanGroupAddress.AdapterLuid);
        Assert.Null(fanGroupAddress.FanIndex);
        var gpuControl = Assert.Single(discovered.FieldBindings.Values,
            binding => binding.Field.Name == "gpu_amd_requested_memory_clock_maximum_mhz");
        Assert.Equal("gpu_adapter", gpuControl.Device.Kind);
        Assert.Equal(gpuPnp, Assert.IsType<MetricDeviceAddress>(gpuControl.Device.Address).PnpInstanceId);
        Assert.Equal(0, hardware.Writes);
    }

    [Fact]
    public void NullableDeviceRequestsMergeActualBindingsAndPreserveSingleDeviceValidation()
    {
        var cpu = MetricFieldRegistry.FindByName("cpu_all_usage_percent")!;
        var memory = MetricFieldRegistry.FindByName("memory_all_numa_node_capacity_mib")!;
        var snapshot = CreateRequestSnapshot(
        [
            new MetricDeviceFieldBinding("cpu-0", cpu, "cpu.public", "cpu.source")
            { SampleRequestAddress = "sample.cpu.0" },
            new MetricDeviceFieldBinding("cpu-1", cpu, "cpu.public.1", "cpu.source")
            { SampleRequestAddress = "sample.cpu.1" },
            new MetricDeviceFieldBinding("numa-7", memory, "numa.public", "numa.source")
            { SampleRequestAddress = "sample.numa.7" }
        ]);
        var names = new[] { cpu.Name, memory.Name };

        // Requests name public addresses; internal sample addresses are mapped only at native planning.
        var merged = MetricDeviceFields.CreateRequest(snapshot, null, names);
        Assert.False(merged.IsAll);
        Assert.False(merged.IsCatalogProbe);
        Assert.True(new[] { "cpu.public", "cpu.public.1", "numa.public" }
            .ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(merged.Ids));

        var selected = MetricDeviceFields.Read(snapshot, fields: names.ToHashSet(StringComparer.Ordinal));
        Assert.Single(Assert.Single(selected, device => device.DeviceIdentity == "cpu-0").Fields);
        Assert.True(new[] { cpu.Name }.SequenceEqual(Assert.Single(selected,
            device => device.DeviceIdentity == "cpu-1").Fields.Select(static field => field.Name)));
        Assert.True(new[] { memory.Name }.SequenceEqual(Assert.Single(selected,
            device => device.DeviceIdentity == "numa-7").Fields.Select(static field => field.Name)));

        var knownButUnbound = MetricDeviceFields.CreateRequest(snapshot, null,
            ["gpu_nvidia_processor_busy_percent"]);
        Assert.True(knownButUnbound.IsEmpty);
        Assert.False(knownButUnbound.IsAll);
        Assert.Throws<ArgumentException>(() => MetricDeviceFields.CreateRequest(snapshot, null, []));
        Assert.Throws<ArgumentException>(() => MetricDeviceFields.CreateRequest(snapshot, null, ["metric_typo"]));

        var single = MetricDeviceFields.CreateRequest(snapshot, "cpu-0", [cpu.Name]);
        Assert.True(new[] { "cpu.public" }.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(single.Ids));
        Assert.Throws<ArgumentException>(() => MetricDeviceFields.CreateRequest(snapshot, "cpu-0", [memory.Name]));
        Assert.Throws<ArgumentException>(() => MetricDeviceFields.CreateRequest(snapshot, "cpu-0", ["metric_typo"]));
        Assert.Throws<ArgumentException>(() => MetricDeviceFields.CreateRequest(snapshot, "", [cpu.Name]));
    }

    private static HardwareMetricSnapshot WithCurrentReadings(
        HardwareMetricSnapshot snapshot,
        IReadOnlyList<MetricDeviceFieldBinding> bindings)
    {
        var now = DateTimeOffset.UtcNow;
        var items = new Dictionary<string, MetricValue>(snapshot.Items, StringComparer.OrdinalIgnoreCase);
        var datasets = new Dictionary<string, HardwareMetricDatasetObservation>(snapshot.Datasets,
            StringComparer.OrdinalIgnoreCase);
        foreach (var binding in bindings)
        {
            var integer = binding.Field.Code == MetricFieldCode.NpuDedicatedMemoryCommitted
                ? "9007199254740993"
                : binding.Field.ValueKind == "unsigned64" ? "8193" : null;
            double? numeric = binding.Field.Code == MetricFieldCode.IntelEffectiveFrequencyMhz ? 1375.5
                : binding.Field.ValueKind == "float64" ? 37.5
                : null;
            items[binding.RequestAddress] = new MetricValue(binding.RequestAddress,
                binding.Field.Name, binding.Field.Resource.ToString(), "fixture", numeric,
                binding.Field.Unit, null, null)
            {
                IntegerValue = integer,
                ValueKind = binding.Field.ValueKind,
                SourceId = binding.SourceId
            };
            datasets[binding.RequestAddress] = new HardwareMetricDatasetObservation(
                binding.RequestAddress, SamplingObservationStatus.Current, 1, now.UtcTicks,
                1, 1, 1, 1);
        }

        return snapshot with { Items = items, Datasets = datasets };
    }

    private static void AssertAddress(MetricDeviceAddress? expected, MetricDeviceAddress? actual)
    {
        if (expected is null)
        {
            Assert.Null(actual);
            return;
        }
        Assert.NotNull(actual);
        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(actual));
    }

    private static HardwareMetricSnapshot CreateRequestSnapshot(
        IEnumerable<MetricDeviceFieldBinding> bindings)
    {
        var now = DateTimeOffset.UtcNow;
        return new HardwareMetricSnapshot(now,
            new("fixture", 0, false, CpuMetricObservationStatus.NotRequested, 0, 0, 0, 0, 0,
                "fixture", new(new("fixture", HardwareSensorProviderStates.NotRequested, null), null, null, null, null)),
            new(0, 0, 0, false, "fixture"),
            new(0, 0, 0, "fixture", false), [],
            new(SamplingObservationStatus.NotRequested, 0, 0, 0, 0, 0, 0, []),
            new Dictionary<string, MetricValue>(StringComparer.Ordinal))
        {
            FieldBindings = bindings.ToDictionary(static binding => binding.RequestAddress,
                StringComparer.Ordinal),
            Datasets = new Dictionary<string, HardwareMetricDatasetObservation>(StringComparer.Ordinal),
            WorkspaceIdentity = 1,
            ConfigurationGeneration = 1,
            CatalogGeneration = 1,
            CommittedGeneration = 1
        };
    }

    private sealed class FixtureNpuEngineDirectory : IWindowsNpuEngineDirectory
    {
        public FixtureNpuEngineDirectory(string pnp)
        {
            Identities =
            [
                new NpuEngineCatalogIdentity(pnp, 4, "Fixture NPU engine", Guid.NewGuid(),
                    0xA1, 0x8086, 1, 8, null, null, this)
            ];
        }

        public IReadOnlyList<NpuEngineCatalogIdentity> Identities { get; }
        public bool IsCurrent(uint effectiveMaximumInstanceCount, CancellationToken cancellationToken = default)
            => !cancellationToken.IsCancellationRequested && Identities.Count <= effectiveMaximumInstanceCount;
        public IDisposable AcquireLease() => new FixtureLease();
        public void Dispose() { }

        private sealed class FixtureLease : IDisposable
        {
            public void Dispose() { }
        }
    }

    private sealed class FakeAdlx(string pnp) : IAdlxTuningHardware
    {
        public int Writes { get; private set; }
        public AdlxTuningValue Read(int? adapterIndex, int field)
            => new(pnp, 1000, 0, 5000, 1);
        public void Write(string devicePnp, int field, int value) => Writes++;
    }
}
