using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using ResourceManager.App.Application.Settings;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Infrastructure.Settings;

namespace Resource_Manager_APP.Tests;

public sealed class DeviceFieldDashboardBindingTests
{
    private readonly string testRoot = CreateTestRoot();

    [Fact]
    public void Normalize_PreservesOpaqueDeviceFieldSlotIdsAndBindings()
    {
        const string mainId = " custom_unregistered_field@NPU-A ";
        const string mainKey = "NPU-A ";
        const string secondDeviceId = "custom_unregistered_field@NPU-B";
        const string secondDeviceKey = "NPU-B";
        const string noDelimiterId = "opaque id from an unlisted directory";
        const string noDelimiterKey = "opaque unknown device identity";
        var settings = CreateSettings(new DashboardCardSettings(
            "npu-card",
            mainId,
            [secondDeviceId, noDelimiterId],
            new DashboardMetricBinding("device-field", mainKey),
            [
                new DashboardMetricBinding("device-field", secondDeviceKey),
                new DashboardMetricBinding("device-field", noDelimiterKey)
            ]));

        var normalized = DashboardSettingsNormalizer.Normalize(settings);
        var card = Assert.Single(normalized.Cards);

        Assert.Equal(mainId, card.Main);
        Assert.Equal(new DashboardMetricBinding("device-field", mainKey), card.MainBinding);
        Assert.Equal([secondDeviceId, noDelimiterId], card.Small);
        Assert.Equal(
            new DashboardMetricBinding?[]
            {
                new DashboardMetricBinding("device-field", secondDeviceKey),
                new DashboardMetricBinding("device-field", noDelimiterKey)
            },
            card.SmallBindings);
    }

    [Fact]
    public async Task JsonStore_SaveAndReadbackPreserveOpaqueDeviceFieldSlots()
    {
        const string mainId = "future_npu_field@device-A ";
        const string deviceBId = " future_npu_field@device-B";
        var settings = CreateSettings(new DashboardCardSettings(
            "npu-card",
            mainId,
            [deviceBId],
            new DashboardMetricBinding("device-field", "device-A "),
            [new DashboardMetricBinding("device-field", "device-B ")]));
        var appRoot = CreateAppRoot("store-roundtrip");
        var store = new JsonDashboardSettingsStore(new TestHostEnvironment(appRoot));

        await store.SaveAsync(settings, CancellationToken.None);
        var reloaded = await new JsonDashboardSettingsStore(
                new TestHostEnvironment(appRoot))
            .LoadReadOnlyAsync(CancellationToken.None);
        var card = Assert.Single(reloaded.Settings.Cards);

        Assert.Equal(mainId, card.Main);
        Assert.Equal(new DashboardMetricBinding("device-field", "device-A "), card.MainBinding);
        Assert.Equal([deviceBId], card.Small);
        Assert.Equal(
            new DashboardMetricBinding?[]
            {
                new DashboardMetricBinding("device-field", "device-B ")
            },
            card.SmallBindings);
    }

    [Fact]
    public async Task JsonStore_PreservesSixAlignedDetailSlotsAndTruncatesSeventh()
    {
        string[] slots = ["cpu.usage", "future_temperature@disk-A", "memory.used", "gpu.0.vram",
            "future_temperature@disk-B", "opaque offline field", "seventh@device-C"];
        DashboardMetricBinding?[] bindings = [null,
            new("device-field", "disk-A"), null, new("gpu", "gpu-stable"),
            new("device-field", "disk-B"), new("device-field", "offline-device"),
            new("device-field", "device-C")];
        var settings = CreateSettings(new DashboardCardSettings(
            "six-details", "cpu.usage", slots, null, bindings));
        var appRoot = CreateAppRoot("six-detail-roundtrip");
        await new JsonDashboardSettingsStore(new TestHostEnvironment(appRoot))
            .SaveAsync(settings, CancellationToken.None);

        var reloaded = await new JsonDashboardSettingsStore(new TestHostEnvironment(appRoot))
            .LoadReadOnlyAsync(CancellationToken.None);
        var card = Assert.Single(reloaded.Settings.Cards);

        Assert.Equal(slots.Take(6), card.Small);
        Assert.Equal(bindings.Take(6), card.SmallBindings);
        Assert.DoesNotContain(slots[6], card.Small);
    }

    [Fact]
    public void SnapshotResolution_PreservesOfflineDeviceFieldsAndLegacyGpuRebinding()
    {
        const string mainId = "same_field@NPU-A";
        const string deviceBId = "same_field@NPU-B";
        const string offlineDeviceId = "same_field@temporarily-offline";
        var gpuBinding = new DashboardMetricBinding("gpu", "gpu-stable");
        var settings = CreateSettings(new DashboardCardSettings(
            "mixed-card",
            mainId,
            [deviceBId, offlineDeviceId, "gpu.1.vram"],
            new DashboardMetricBinding("device-field", "NPU-A"),
            [
                new DashboardMetricBinding("device-field", "NPU-B"),
                new DashboardMetricBinding("device-field", "temporarily-offline"),
                gpuBinding
            ]));
        var snapshot = CreateSnapshot(CreateGpu(0, "gpu-stable"));
        var migrator = new DashboardSettingsMigrator();

        var readSettings = migrator.MigrateForRead(settings, snapshot);
        AssertCardBindings(readSettings, mainId, deviceBId, offlineDeviceId, gpuBinding, "gpu.0.vram");

        var savedSettings = migrator.SanitizeForSave(settings, snapshot);
        AssertCardBindings(savedSettings, mainId, deviceBId, offlineDeviceId, gpuBinding, "gpu.0.vram");
    }

    private static DashboardSettings CreateSettings(DashboardCardSettings card)
    {
        return DashboardSettingsDefaults.Create() with
        {
            Cards = [card]
        };
    }

    private static HardwareMetricSnapshot CreateSnapshot(params GpuMetrics[] gpus)
    {
        return new HardwareMetricSnapshot(
            DateTimeOffset.UtcNow,
            new CpuMetrics(
                "test CPU",
                0,
                true,
                CpuMetricObservationStatus.Complete,
                1,
                100,
                1000,
                4000,
                25,
                "test",
                new CpuSensorMetrics(
                    new HardwareSensorProviderState("test", HardwareSensorProviderStates.Active, null),
                    null,
                    null,
                    null,
                    null)),
            new MemoryMetrics(1, 2, 50, true, "test"),
            new VirtualMemoryMetrics(0, 0, 0, "test", IsSelectable: false),
            gpus,
            new SchedulingGpuInventorySnapshot(
                SamplingObservationStatus.NotRequested,
                0,
                0,
                0,
                0,
                0,
                0,
                []),
            new Dictionary<string, MetricValue>());
    }

    private static GpuMetrics CreateGpu(int index, string identityKey)
    {
        return new GpuMetrics(
            index,
            $"GPU{index}",
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            new GpuSensorMetrics(
                new HardwareSensorProviderState("test", HardwareSensorProviderStates.Active, null),
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null),
            IdentityKey: identityKey);
    }

    private static void AssertCardBindings(
        DashboardSettings settings,
        string mainId,
        string deviceBId,
        string offlineDeviceId,
        DashboardMetricBinding gpuBinding,
        string expectedGpuSlot)
    {
        var card = Assert.Single(settings.Cards);
        Assert.Equal(mainId, card.Main);
        Assert.Equal(new DashboardMetricBinding("device-field", "NPU-A"), card.MainBinding);
        Assert.Equal([deviceBId, offlineDeviceId, expectedGpuSlot], card.Small);
        Assert.Equal(
            new DashboardMetricBinding?[]
            {
                new DashboardMetricBinding("device-field", "NPU-B"),
                new DashboardMetricBinding("device-field", "temporarily-offline"),
                gpuBinding
            },
            card.SmallBindings);
    }

    private string CreateAppRoot(string caseName)
        => Directory.CreateDirectory(
            Path.Combine(testRoot, caseName, "Resource Manager-APP")).FullName;

    private static string CreateTestRoot()
    {
        var scratchRoot = Environment.GetEnvironmentVariable("RM_REVIEW_SCRATCH");
        if (string.IsNullOrWhiteSpace(scratchRoot))
        {
            throw new InvalidOperationException(
                "Device-field dashboard tests require the managed RM_REVIEW_SCRATCH root.");
        }

        var testRoot = Path.GetFullPath(Path.Combine(
            scratchRoot,
            "TEMP",
            "resource-manager-device-field-dashboard-tests",
            Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(testRoot);
        return testRoot;
    }

    private sealed class TestHostEnvironment(string contentRootPath) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "ResourceManager.Tests";
        public string ContentRootPath { get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
