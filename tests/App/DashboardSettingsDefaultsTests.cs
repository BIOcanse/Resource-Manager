using ResourceManager.App.Application.Settings;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Domain.ResourceTable;
using ResourceManager.App.Domain.Settings;

namespace Resource_Manager_APP.Tests;

public sealed class DashboardSettingsDefaultsTests
{
    [Fact]
    public void ResolveForRead_BundledFirstRunUsesTopologyAwareDefaults()
    {
        var snapshot = CreateSnapshot(
            CreateSelectableMetrics(),
            CreateGpu(0, "gpu-integrated"),
            CreateGpu(1, "gpu-dedicated"));
        var stored = new DashboardSettingsUpdateResult(
            DashboardSettingsDefaults.Create(),
            DateTimeOffset.UtcNow,
            "dashboard-settings.json",
            new DashboardSettingsSourceMetadata(
                DashboardSettingsSourceKind.BundledFirstRun,
                DashboardSettingsDefaults.CurrentVersion,
                "input",
                "effective",
                RewritePerformed: false));
        var migrator = new DashboardSettingsMigrator();

        var resolved = migrator.ResolveForRead(stored, snapshot);
        var expected = DashboardSettingsDefaults.Create(snapshot);

        Assert.True(migrator.AreEquivalent(expected, resolved));
        // 8 = 原来的 7 加上 gpu0 的风扇卡：那块 GPU 有自己的风扇、没有独立显存，
        // 先前被"要有显存才出传感卡"那条判据吞掉了。
        Assert.Equal(8, resolved.Cards.Count);
        Assert.Contains(resolved.Cards, static card => card.Id == "gpu0-sensors");
        Assert.Contains(resolved.Cards, static card => card.Id == "gpu1-sensors");
    }

    [Fact]
    public void Create_FromSnapshotOnlyReferencesSelectableCatalogMetrics()
    {
        var items = CreateSelectableMetrics();
        items["cpu.actualPower"] = EmptyMetric("cpu.actualPower");
        items["cpu.coreVoltage"] = EmptyMetric("cpu.coreVoltage");
        items["cpu.packageCurrent"] = EmptyMetric("cpu.packageCurrent");
        items["cpu.temperature"] = EmptyMetric("cpu.temperature");
        items["gpu.0.graphicsClock"] = EmptyMetric("gpu.0.graphicsClock");
        items["gpu.0.temperature"] = EmptyMetric("gpu.0.temperature");
        items["gpu.1.current"] = EmptyMetric("gpu.1.current");
        items["memory.temperature"] = EmptyMetric("memory.temperature");
        var snapshot = CreateSnapshot(
            items,
            CreateGpu(0, "gpu-integrated"),
            CreateGpu(1, "gpu-dedicated"));

        var settings = DashboardSettingsDefaults.Create(snapshot);
        var selectableIds = MetricCatalog.FromSnapshot(snapshot)
            .Where(static definition => definition.Selectable)
            .Select(static definition => definition.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var configuredIds = settings.Cards
            .SelectMany(static card => new[] { card.Main }.Concat(card.Small))
            .Where(static id => !string.IsNullOrWhiteSpace(id));

        Assert.All(configuredIds, id => Assert.Contains(id!, selectableIds));
        Assert.DoesNotContain(
            settings.Cards.SelectMany(static card => card.Small),
            static id => id is "cpu.actualPower" or "gpu.1.current" or "memory.temperature");
    }

    [Fact]
    public void Create_IdleKnownGpuKeepsUsageSlotWithoutInventingAReading()
    {
        var items = CreateSelectableMetrics();
        foreach (var metricId in items.Keys
                     .Where(static id => id.StartsWith(
                         "gpu.0.",
                         StringComparison.OrdinalIgnoreCase))
                     .ToArray())
        {
            items.Remove(metricId);
        }
        var snapshot = CreateSnapshot(
            items,
            CreateGpu(0, "gpu-integrated"),
            CreateGpu(1, "gpu-dedicated"));

        var catalog = MetricCatalog.FromSnapshot(snapshot);
        var settings = DashboardSettingsDefaults.Create(snapshot);
        var gpu0Usage = catalog.Single(
            static definition => definition.Id == "gpu.0.usage");
        var gpu0Card = settings.Cards.Single(static card => card.Id == "gpu0");

        Assert.True(gpu0Usage.Selectable);
        Assert.Equal("gpu-integrated", gpu0Usage.ScopeKey);
        Assert.Equal("gpu.0.usage", gpu0Card.Main);
        Assert.Empty(gpu0Card.Small);
        Assert.DoesNotContain("gpu.0.usage", snapshot.Items.Keys);
    }

    [Fact]
    public void Create_UsesConfirmedHardwareDashboardLayout()
    {
        var settings = DashboardSettingsDefaults.Create(CreateAvailableMetrics());

        Assert.Equal(DashboardSettingsDefaults.CurrentVersion, settings.Version);
        Assert.Contains(
            settings.ResourceBars,
            static bar => bar.MetricId == ResourceBreakdownMetricIds.VirtualMemoryUsage);
        Assert.Collection(
            settings.Cards,
            card => AssertCard(card, "cpu", "cpu.usage", "cpu.frequency", "cpu.temperature", "cpu.actualPower"),
            card => AssertCard(card, "memory", "memory.usage", "memory.percent", "memory.temperature"),
            card => AssertCard(card, "gpu0", "gpu.0.usage", "gpu.0.graphicsClock", "gpu.0.temperature"),
            card => AssertCard(card, "gpu1", "gpu.1.usage", "gpu.1.graphicsClock", "gpu.1.power", "gpu.1.temperature"),
            card => AssertCard(card, "vram1", "gpu.1.vram", "gpu.1.vramPercent", "gpu.1.memoryClock", "gpu.1.graphicsClockPercent"),
            card => AssertCard(card, "cpu-sensors", "cpu.fanRpm", "cpu.coreVoltage", "cpu.packageCurrent"),
            // gpu.0 有自己的风扇但没有独立显存。先前"要有显存才出传感卡"的判据
            // 会把这张风扇卡整张吞掉 —— 显存和风扇没有关系。
            card => AssertCard(card, "gpu0-sensors", "gpu.0.fanRpm", "gpu.0.coreVoltage", "gpu.0.current"),
            card => AssertCard(card, "gpu1-sensors", "gpu.1.fanRpm", "gpu.1.coreVoltage", "gpu.1.current"));
    }

    [Fact]
    public void Create_KeepsAllGpuUsageCardsTogetherAndUsesCurrentVramOwner()
    {
        var items = CreateAvailableMetrics()
            .Where(static pair => !pair.Key.StartsWith("gpu.", StringComparison.Ordinal))
            .ToDictionary(static pair => pair.Key, static pair => pair.Value);
        AddMetrics(
            items,
            "gpu.0.usage",
            "gpu.0.graphicsClock",
            "gpu.0.graphicsClockPercent",
            "gpu.0.power",
            "gpu.0.temperature",
            "gpu.0.vram",
            "gpu.0.vramPercent",
            "gpu.0.memoryClock",
            "gpu.0.fanRpm",
            "gpu.0.coreVoltage",
            "gpu.0.current",
            "gpu.1.usage",
            "gpu.1.graphicsClock",
            "gpu.1.temperature");

        var settings = DashboardSettingsDefaults.Create(items);

        Assert.Collection(
            settings.Cards,
            card => AssertCard(card, "cpu", "cpu.usage", "cpu.frequency", "cpu.temperature", "cpu.actualPower"),
            card => AssertCard(card, "memory", "memory.usage", "memory.percent", "memory.temperature"),
            card => AssertCard(card, "gpu0", "gpu.0.usage", "gpu.0.graphicsClock", "gpu.0.power", "gpu.0.temperature"),
            card => AssertCard(card, "gpu1", "gpu.1.usage", "gpu.1.graphicsClock", "gpu.1.temperature"),
            card => AssertCard(card, "vram0", "gpu.0.vram", "gpu.0.vramPercent", "gpu.0.memoryClock", "gpu.0.graphicsClockPercent"),
            card => AssertCard(card, "cpu-sensors", "cpu.fanRpm", "cpu.coreVoltage", "cpu.packageCurrent"),
            card => AssertCard(card, "gpu0-sensors", "gpu.0.fanRpm", "gpu.0.coreVoltage", "gpu.0.current"));
    }

    [Fact]
    public void MigrateForRead_UpgradesV13SlotsWithStableGpuBindings()
    {
        var oldSettings = new DashboardSettings(
            DashboardSettingsDefaults.CurrentVersion - 1,
            [
                new DashboardCardSettings("cpu", "cpu.usage", ["cpu.frequency", "cpu.frequencyPercent"]),
                new DashboardCardSettings("memory", "memory.usage", ["memory.percent"]),
                new DashboardCardSettings("gpu0", "gpu.0.usage", ["gpu.0.graphicsClock", "gpu.0.graphicsClockPercent"]),
                new DashboardCardSettings("gpu1", "gpu.1.usage", ["gpu.1.graphicsClock", "gpu.1.graphicsClockPercent"]),
                new DashboardCardSettings("vram1", "gpu.1.vram", ["gpu.1.vramPercent", "gpu.1.memoryClock", "gpu.1.graphicsClockPercent"])
            ],
            [new ResourceBarSettings("resource-cpu", ResourceBreakdownMetricIds.CpuUsage, ResourceBreakdownScaleModes.Capacity)],
            [new ResourceTableColumnSettings(ResourceTableColumnIds.Name, true, 240)],
            [new ResourceTableColumnSettings(ResourceTableColumnIds.Name, true, 260)]);
        var snapshot = CreateSnapshot(
            CreateAvailableMetrics(),
            CreateGpu(0, "gpu-integrated"),
            CreateGpu(1, "gpu-dedicated"));

        var migrated = new DashboardSettingsMigrator().MigrateForRead(
            oldSettings,
            snapshot);

        Assert.Equal(DashboardSettingsDefaults.CurrentVersion, migrated.Version);
        Assert.Equal(
            "gpu-integrated",
            migrated.Cards.Single(static card => card.Id == "gpu0")
                .MainBinding?.ScopeKey);
        Assert.Equal(
            "gpu-dedicated",
            migrated.Cards.Single(static card => card.Id == "vram1")
                .MainBinding?.ScopeKey);
    }

    [Fact]
    public void MigrateForRead_ReconcilesLegacyGpuColumnsAndBarsWithCurrentCatalog()
    {
        var settings = new DashboardSettings(
            DashboardSettingsDefaults.CurrentVersion - 1,
            [new DashboardCardSettings("cpu", "cpu.usage", [])],
            [new ResourceBarSettings("legacy-vram", "gpu.0.vram", ResourceBreakdownScaleModes.Capacity)],
            [
                new ResourceTableColumnSettings(ResourceTableColumnIds.Name, true, 260),
                new ResourceTableColumnSettings("gpu.0.vram", false, 177),
                new ResourceTableColumnSettings(ResourceTableColumnIds.Disk, true, 112)
            ],
            [
                new ResourceTableColumnSettings(ResourceTableColumnIds.Name, true, 260),
                new ResourceTableColumnSettings("gpu.0.vram", true, 188),
                new ResourceTableColumnSettings(ResourceTableColumnIds.Disk, true, 112)
            ]);
        var snapshot = CreateSnapshot(
            CreateAvailableMetrics(),
            CreateGpu(0, "gpu-integrated"),
            CreateGpu(1, "gpu-dedicated"));

        var migrated = new DashboardSettingsMigrator().MigrateForRead(
            settings,
            snapshot);

        var bar = Assert.Single(migrated.ResourceBars, static bar => bar.Id == "legacy-vram");
        Assert.Equal("gpu.1.vram", bar.MetricId);
        Assert.Equal("gpu-dedicated", bar.Binding?.ScopeKey);
        var softwareColumn = Assert.Single(
            migrated.ResourceTableColumns,
            static column => column.Id == "gpu.1.vram");
        Assert.False(softwareColumn.Visible);
        Assert.Equal(177, softwareColumn.Width);
        Assert.Equal("gpu-dedicated", softwareColumn.Binding?.ScopeKey);
        var processColumn = Assert.Single(
            migrated.ResourceTableProcessColumns!,
            static column => column.Id == "gpu.1.vram");
        Assert.True(processColumn.Visible);
        Assert.Equal(188, processColumn.Width);
        Assert.Equal("gpu-dedicated", processColumn.Binding?.ScopeKey);
        Assert.DoesNotContain(
            migrated.ResourceTableColumns,
            static column => column.Id == "gpu.0.vram");
        var softwareColumnIds = migrated.ResourceTableColumns
            .Select(static column => column.Id)
            .ToArray();
        Assert.Equal(ResourceTableColumnIds.Name, softwareColumnIds[0]);
        var reboundGpuIndex = Array.IndexOf(softwareColumnIds, "gpu.1.vram");
        var diskIndex = Array.IndexOf(
            softwareColumnIds,
            ResourceTableColumnIds.Disk);
        var appendedNetworkIndex = Array.IndexOf(
            softwareColumnIds,
            ResourceTableColumnIds.Network);
        Assert.True(reboundGpuIndex > 0);
        Assert.True(reboundGpuIndex < diskIndex);
        Assert.True(diskIndex < appendedNetworkIndex);
    }

    [Fact]
    public void MigrateForRead_RebindsGpuColumnAndBarToStableAdapterAfterOrdinalChange()
    {
        var binding = new DashboardMetricBinding("gpu", "gpu-dedicated");
        var settings = new DashboardSettings(
            DashboardSettingsDefaults.CurrentVersion,
            [new DashboardCardSettings("cpu", "cpu.usage", [])],
            [new ResourceBarSettings("vram", "gpu.1.vram", ResourceBreakdownScaleModes.Capacity, binding)],
            [
                new ResourceTableColumnSettings(ResourceTableColumnIds.Name, true, 260),
                new ResourceTableColumnSettings("gpu.1.vram", false, 179, binding)
            ],
            [
                new ResourceTableColumnSettings(ResourceTableColumnIds.Name, true, 260),
                new ResourceTableColumnSettings("gpu.1.vram", true, 189, binding)
            ]);
        var reboundItems = CreateAvailableMetrics()
            .Where(static pair => !pair.Key.StartsWith("gpu.", StringComparison.Ordinal))
            .ToDictionary(static pair => pair.Key, static pair => pair.Value);
        AddMetrics(reboundItems, "gpu.0.usage", "gpu.0.vram", "gpu.1.usage");
        var snapshot = CreateSnapshot(
            reboundItems,
            CreateGpu(0, "gpu-dedicated"),
            CreateGpu(1, "gpu-integrated"));

        var migrated = new DashboardSettingsMigrator().MigrateForRead(settings, snapshot);

        var reboundBar = Assert.Single(migrated.ResourceBars, static bar => bar.Id == "vram");
        Assert.Equal("gpu.0.vram", reboundBar.MetricId);
        Assert.Equal("gpu-dedicated", reboundBar.Binding?.ScopeKey);
        var softwareColumn = Assert.Single(
            migrated.ResourceTableColumns,
            static column => column.Id == "gpu.0.vram");
        Assert.False(softwareColumn.Visible);
        Assert.Equal(179, softwareColumn.Width);
        Assert.Equal("gpu-dedicated", softwareColumn.Binding?.ScopeKey);
    }

    [Fact]
    public void MigrateForRead_RebindsVramSlotToSameGpuAfterOrdinalChanges()
    {
        var settings = new DashboardSettings(
            DashboardSettingsDefaults.CurrentVersion,
            [
                new DashboardCardSettings(
                    "vram",
                    "gpu.1.vram",
                    ["gpu.1.vramPercent", "gpu.1.memoryClock"],
                    new DashboardMetricBinding("gpu", "gpu-dedicated"),
                    [
                        new DashboardMetricBinding("gpu", "gpu-dedicated"),
                        new DashboardMetricBinding("gpu", "gpu-dedicated")
                    ])
            ],
            DashboardSettingsDefaults.CreateResourceBars(CreateAvailableMetrics()),
            DashboardSettingsDefaults.CreateResourceTableColumns(),
            DashboardSettingsDefaults.CreateResourceTableProcessColumns());
        var reboundItems = CreateAvailableMetrics()
            .Where(static pair => !pair.Key.StartsWith("gpu.", StringComparison.Ordinal))
            .ToDictionary(static pair => pair.Key, static pair => pair.Value);
        AddMetrics(
            reboundItems,
            "gpu.0.usage",
            "gpu.0.vram",
            "gpu.0.vramPercent",
            "gpu.0.memoryClock",
            "gpu.1.usage");
        var snapshot = CreateSnapshot(
            reboundItems,
            CreateGpu(0, "gpu-dedicated"),
            CreateGpu(1, "gpu-integrated"));

        var migrated = new DashboardSettingsMigrator().MigrateForRead(
            settings,
            snapshot);
        var card = Assert.Single(migrated.Cards);

        Assert.Equal("gpu.0.vram", card.Main);
        Assert.Equal(
            ["gpu.0.vramPercent", "gpu.0.memoryClock"],
            card.Small);
        Assert.All(
            new[] { card.MainBinding }.Concat(card.SmallBindings ?? []),
            binding => Assert.Equal("gpu-dedicated", binding?.ScopeKey));
    }

    [Fact]
    public void MigrateForRead_MissingBoundGpuDoesNotAttachSlotToReplacementOrdinal()
    {
        var settings = new DashboardSettings(
            DashboardSettingsDefaults.CurrentVersion,
            [
                new DashboardCardSettings(
                    "vram",
                    "gpu.1.vram",
                    [],
                    new DashboardMetricBinding("gpu", "gpu-dedicated"))
            ],
            DashboardSettingsDefaults.CreateResourceBars(CreateAvailableMetrics()),
            DashboardSettingsDefaults.CreateResourceTableColumns(),
            DashboardSettingsDefaults.CreateResourceTableProcessColumns());
        var snapshot = CreateSnapshot(
            CreateAvailableMetrics(),
            CreateGpu(1, "gpu-replacement"));

        var migrated = new DashboardSettingsMigrator().MigrateForRead(
            settings,
            snapshot);
        var card = Assert.Single(migrated.Cards);

        Assert.Equal("gpu.1.vram", card.Main);
        Assert.Equal("gpu-dedicated", card.MainBinding?.ScopeKey);
    }

    [Fact]
    public void MigrateForRead_PreservesConfiguredMetricsWhenCurrentProbeIsIncomplete()
    {
        var settings = DashboardSettingsDefaults.Create(CreateAvailableMetrics());
        var incompleteProbe = CreateAvailableMetrics()
            .Where(static pair => pair.Key is "cpu.usage" or "gpu.1.vram")
            .ToDictionary(static pair => pair.Key, static pair => pair.Value);

        var migrated = new DashboardSettingsMigrator().MigrateForRead(
            settings,
            CreateSnapshot(incompleteProbe));

        Assert.True(new DashboardSettingsMigrator().AreEquivalent(settings, migrated));
        Assert.Contains(migrated.Cards, static card => card.Id == "memory");
        Assert.Contains(migrated.Cards, static card => card.Id == "cpu-sensors");
        Assert.Contains(migrated.Cards, static card => card.Id == "gpu1-sensors");
    }

    [Fact]
    public void SanitizeForSave_DoesNotTreatMissingSamplesAsDeletedConfiguration()
    {
        var settings = DashboardSettingsDefaults.Create(CreateAvailableMetrics());

        var sanitized = new DashboardSettingsMigrator().SanitizeForSave(
            settings,
            CreateSnapshot(CreateAvailableMetrics()));

        Assert.True(new DashboardSettingsMigrator().AreEquivalent(settings, sanitized));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SanitizeForSave_PartialGpuCatalogPreservesUserPreferences(bool adapterPresent)
    {
        var binding = new DashboardMetricBinding("gpu", "gpu-dedicated");
        var settings = DashboardSettingsDefaults.Create(CreateAvailableMetrics()) with
        {
            ResourceBars = [new ResourceBarSettings("my-vram", "gpu.1.vram", ResourceBreakdownScaleModes.Capacity, binding)],
            ResourceTableColumns = [new ResourceTableColumnSettings("gpu.1.vram", false, 179, binding)],
            ResourceTableProcessColumns = [new ResourceTableColumnSettings("gpu.1.vram", true, 189, binding)]
        };
        var metrics = CreateAvailableMetrics().Where(pair => !pair.Key.StartsWith("gpu.", StringComparison.Ordinal))
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        var snapshot = CreateSnapshot(metrics, CreateGpu(0, "gpu-integrated"),
            CreateGpu(1, adapterPresent ? "gpu-dedicated" : "gpu-replacement"));
        var saved = new DashboardSettingsMigrator().SanitizeForSave(settings, snapshot);
        Assert.Equal(binding, Assert.Single(saved.ResourceBars, bar => bar.Id == "my-vram").Binding);
        var software = Assert.Single(saved.ResourceTableColumns, column => column.Id == "gpu.1.vram");
        Assert.False(software.Visible);
        Assert.Equal(179, software.Width);
        Assert.Equal(binding, software.Binding);
        Assert.Equal(189, Assert.Single(saved.ResourceTableProcessColumns!, column => column.Id == "gpu.1.vram").Width);
    }

    [Fact]
    public void SnapshotIndependentNormalizationPreservesConfiguredDashboard()
    {
        var settings = DashboardSettingsDefaults.Create(CreateAvailableMetrics());
        var migrator = new DashboardSettingsMigrator();

        var migrated = migrator.MigrateForRead(settings);
        var sanitized = migrator.SanitizeForSave(settings);

        Assert.True(migrator.AreEquivalent(settings, migrated));
        Assert.True(migrator.AreEquivalent(settings, sanitized));
        Assert.Contains(
            sanitized.ResourceBars,
            static bar => bar.MetricId == ResourceBreakdownMetricIds.VirtualMemoryUsage);
    }

    [Theory]
    [InlineData(ResourceBreakdownMetricIds.DiskIo)]
    [InlineData(ResourceBreakdownMetricIds.DiskRead)]
    [InlineData(ResourceBreakdownMetricIds.DiskWrite)]
    [InlineData(ResourceBreakdownMetricIds.NetworkTraffic)]
    [InlineData(ResourceBreakdownMetricIds.NetworkReceive)]
    [InlineData(ResourceBreakdownMetricIds.NetworkSend)]
    [InlineData(ResourceBreakdownMetricIds.NetworkRawTraffic)]
    [InlineData(ResourceBreakdownMetricIds.NetworkRawReceive)]
    [InlineData(ResourceBreakdownMetricIds.NetworkRawSend)]
    public void SanitizeForSave_ForcesThroughputBarsToActiveScale(string metricId)
    {
        var settings = DashboardSettingsDefaults.Create() with
        {
            ResourceBars = [new ResourceBarSettings($"resource-{metricId}", metricId, ResourceBreakdownScaleModes.Capacity)]
        };

        var sanitized = new DashboardSettingsMigrator().SanitizeForSave(
            settings,
            CreateSnapshot(CreateAvailableMetrics()));

        Assert.Equal(ResourceBreakdownScaleModes.Active, Assert.Single(sanitized.ResourceBars).ScaleMode);
    }

    [Theory]
    [InlineData(ResourceBreakdownMetricIds.CpuUsage)]
    [InlineData(ResourceBreakdownMetricIds.MemoryUsage)]
    [InlineData(ResourceBreakdownMetricIds.VirtualMemoryUsage)]
    [InlineData("gpu.0.usage")]
    [InlineData("gpu.1.vram")]
    public void CapacityScale_RemainsAvailableForBoundedResources(string metricId)
    {
        Assert.True(ResourceBreakdownScaleModes.SupportsCapacity(metricId));
        Assert.Equal(
            ResourceBreakdownScaleModes.Capacity,
            ResourceBreakdownScaleModes.Normalize(metricId, ResourceBreakdownScaleModes.Capacity));
    }

    [Fact]
    public void MetricCatalog_GreysUnavailableSensorOptions()
    {
        var items = CreateAvailableMetrics();
        items["cpu.coreVoltage"] = new MetricValue("cpu.coreVoltage", "CPU 电压", "CPU", "1.0 V", 1.0, "V", null, "AMD SMU / PawnIO");
        items["cpu.stapmPower"] = new MetricValue("cpu.stapmPower", "CPU STAPM 功耗", "CPU", "N/A", null, "W", null, "AMD SMU / PawnIO");
        items["cpu.fanRpm"] = new MetricValue("cpu.fanRpm", "CPU 风扇转速", "CPU", "N/A", null, "RPM", null, "Hardware monitor WMI");
        items["cpu.fanPercent"] = new MetricValue("cpu.fanPercent", "CPU 风扇百分比", "CPU", "N/A", null, "%", null, "Hardware monitor WMI");
        items["gpu.1.usage"] = new MetricValue("gpu.1.usage", "GPU1 占用率", "GPU1", "0%", 0, "%", 0, "NVIDIA GeForce test");
        items["gpu.1.current"] = new MetricValue("gpu.1.current", "GPU1 电流", "GPU1", "N/A", null, "A", null, "NVIDIA NVML");
        var snapshot = CreateSnapshot(items);

        var catalog = MetricCatalog.FromSnapshot(snapshot);
        var coreVoltage = catalog.Single(static item => item.Id == "cpu.coreVoltage");
        var stapmPower = catalog.Single(static item => item.Id == "cpu.stapmPower");
        var cpuFan = catalog.Single(static item => item.Id == "cpu.fanRpm");
        var gpuCurrent = catalog.Single(static item => item.Id == "gpu.1.current");

        Assert.True(coreVoltage.Selectable);
        Assert.False(stapmPower.Selectable);
        Assert.NotNull(stapmPower.DisabledReason);
        Assert.Equal("librehardwaremonitor-provider", cpuFan.RequiredComponentId);
        Assert.Equal("nvidia-nvapi-provider", gpuCurrent.RequiredComponentId);
    }

    [Fact]
    public void MetricCatalog_ExposesMotherboardSensorOptions()
    {
        var items = CreateAvailableMetrics();
        items["system.motherboardTemperature"] = new MetricValue("system.motherboardTemperature", "主板温度", "Motherboard", "43.0 °C", 43, "°C", null, "System");
        items["system.vrmTemperature"] = new MetricValue("system.vrmTemperature", "供电温度", "Motherboard", "72.0 °C", 72, "°C", null, "VRM MOS");
        items["system.chipsetTemperature"] = new MetricValue("system.chipsetTemperature", "芯片组温度", "Motherboard", "N/A", null, "°C", null, "主板");
        items["system.motherboardVoltage"] = new MetricValue("system.motherboardVoltage", "主板电压", "Motherboard", "N/A", null, "V", null, "主板");

        var catalog = MetricCatalog.FromSnapshot(CreateSnapshot(items));
        var motherboard = catalog.Single(static item => item.Id == "system.motherboardTemperature");
        var vrm = catalog.Single(static item => item.Id == "system.vrmTemperature");
        var chipset = catalog.Single(static item => item.Id == "system.chipsetTemperature");
        var voltage = catalog.Single(static item => item.Id == "system.motherboardVoltage");

        Assert.True(motherboard.Selectable);
        Assert.True(vrm.Selectable);
        Assert.Null(vrm.RequiredComponentId);
        Assert.False(chipset.Selectable);
        Assert.Equal("librehardwaremonitor-provider", chipset.RequiredComponentId);
        Assert.False(voltage.Selectable);
        Assert.Equal("librehardwaremonitor-provider", voltage.RequiredComponentId);
    }

    [Fact]
    public void MetricCatalog_AppliesCatalogPresentationToRawSnapshotLabels()
    {
        var items = CreateAvailableMetrics();
        items["cpu.temperature"] = new MetricValue(
            "cpu.temperature",
            "cpu.temperature",
            "cpu",
            "72 °C",
            72,
            "°C",
            null,
            "AMD SMU");
        items["gpu.0.usage"] = new MetricValue(
            "gpu.0.usage",
            "gpu.0.usage",
            "gpu.0",
            "8%",
            8,
            "%",
            8,
            "AMD Radeon");

        var presented = MetricCatalog.ApplyPresentation(CreateSnapshot(items));

        Assert.Equal("CPU 温度", presented.Items["cpu.temperature"].Label);
        Assert.Equal(MetricGroups.Cpu, presented.Items["cpu.temperature"].Group);
        Assert.Equal("GPU0 占用率", presented.Items["gpu.0.usage"].Label);
        Assert.Equal(MetricGroups.Gpu(0), presented.Items["gpu.0.usage"].Group);
        Assert.Equal("8%", presented.Items["gpu.0.usage"].DisplayValue);
    }

    [Fact]
    public void MetricCatalog_ProjectsStableGpuIdentityIntoEveryGpuMetric()
    {
        var items = CreateAvailableMetrics();
        var catalog = MetricCatalog.FromSnapshot(CreateSnapshot(
            items,
            CreateGpu(0, "gpu-integrated"),
            CreateGpu(1, "gpu-dedicated")));

        var vram = catalog.Single(static item => item.Id == "gpu.1.vram");
        var temperature = catalog.Single(
            static item => item.Id == "gpu.1.temperature");

        Assert.Equal("gpu", vram.ScopeKind);
        Assert.Equal("gpu-dedicated", vram.ScopeKey);
        Assert.Equal(vram.ScopeKey, temperature.ScopeKey);
    }

    private static Dictionary<string, MetricValue> CreateAvailableMetrics()
    {
        var metricIds = new[]
        {
            "cpu.usage",
            "cpu.frequency",
            "cpu.frequencyPercent",
            "cpu.temperature",
            "cpu.actualPower",
            "cpu.fanRpm",
            "cpu.fanPercent",
            "cpu.coreVoltage",
            "cpu.packageCurrent",
            "memory.usage",
            "memory.percent",
            "memory.temperature",
            "virtualMemory.usage",
            "gpu.0.usage",
            "gpu.0.graphicsClock",
            "gpu.0.graphicsClockPercent",
            "gpu.0.temperature",
            "gpu.0.coreVoltage",
            "gpu.0.current",
            "gpu.0.fanRpm",
            "gpu.0.fanPercent",
            "gpu.1.usage",
            "gpu.1.graphicsClock",
            "gpu.1.graphicsClockPercent",
            "gpu.1.power",
            "gpu.1.temperature",
            "gpu.1.vram",
            "gpu.1.vramPercent",
            "gpu.1.memoryClock",
            "gpu.1.fanRpm",
            "gpu.1.fanPercent",
            "gpu.1.coreVoltage",
            "gpu.1.current"
        };

        return metricIds.ToDictionary(
            static id => id,
            static id => new MetricValue(id, id, "test", "--", null, string.Empty, null, null));
    }

    private static Dictionary<string, MetricValue> CreateSelectableMetrics()
    {
        return CreateAvailableMetrics().ToDictionary(
            static pair => pair.Key,
            static pair => new MetricValue(
                pair.Key,
                pair.Key,
                "test",
                "1",
                1,
                string.Empty,
                null,
                "test"));
    }

    private static MetricValue EmptyMetric(string id)
    {
        return new MetricValue(
            id,
            id,
            "test",
            "-",
            null,
            string.Empty,
            null,
            "test");
    }

    private static HardwareMetricSnapshot CreateSnapshot(
        IReadOnlyDictionary<string, MetricValue> items,
        params GpuMetrics[] gpus)
    {
        return new HardwareMetricSnapshot(
            DateTimeOffset.Now,
            new CpuMetrics(
                "AMD Ryzen test",
                0,
                true,
                CpuMetricObservationStatus.Complete,
                1,
                1000,
                4000,
                5000,
                80,
                "test",
                new CpuSensorMetrics(
                    new HardwareSensorProviderState("AMD SMU / PawnIO", "Active", null),
                    null,
                    null,
                    null,
                    null)),
            new MemoryMetrics(1, 2, 50, true, "DDR5"),
            new VirtualMemoryMetrics(0, 0, 0, "fixed", IsSelectable: false),
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
            items);
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
                new HardwareSensorProviderState("test", "Active", null),
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

    private static void AddMetrics(Dictionary<string, MetricValue> items, params string[] metricIds)
    {
        foreach (var metricId in metricIds)
        {
            items[metricId] = new MetricValue(metricId, metricId, "test", "--", null, string.Empty, null, null);
        }
    }

    private static void AssertCard(DashboardCardSettings card, string id, string main, params string[] small)
    {
        Assert.True(CardEquals(card, id, main, small));
    }

    /*
     * 默认卡片按**这台机器实际有什么**生成，不按机型分支。
     *
     * 只有一套模板：处理器一张、内存一张，然后每块显卡各来一组
     * （占用率 / 显存 / 传感），每张卡是否出现只看**它自己的主指标**读不读得到。
     * 下面这几个用例把各种机型钉住 —— 它们要防的是有人再拿某个指标去当
     * "这是什么机型"的替代判据（先前就拿显存当过"是不是独立显卡"）。
     */

    [Fact]
    public void DefaultCards_StandardDesktop_GivesEveryGpuTheSameSetOfCards()
    {
        // 处理器 + 核显 + 独显：核显没有显存也没有自己的风扇，独显两样都有。
        var settings = DashboardSettingsDefaults.Create(ShapeMetrics(
            integratedGpu: true,
            discreteGpuCount: 1));

        var ids = settings.Cards.Select(static card => card.Id).ToArray();
        Assert.Equal(
            ["cpu", "memory", "gpu0", "gpu1", "vram1", "cpu-sensors", "gpu1-sensors"],
            ids);
    }

    [Fact]
    public void DefaultCards_NoIntegratedGpu_LeavesOutTheIntegratedCards()
    {
        // 没有核显的机器：目录里根本没有 gpu.0.*，所以那几张卡自然不存在。
        var settings = DashboardSettingsDefaults.Create(ShapeMetrics(
            integratedGpu: false,
            discreteGpuCount: 1));

        var ids = settings.Cards.Select(static card => card.Id).ToArray();
        Assert.Equal(["cpu", "memory", "gpu1", "vram1", "cpu-sensors", "gpu1-sensors"], ids);
        Assert.DoesNotContain("gpu0", ids);
    }

    [Fact]
    public void DefaultCards_MultipleDiscreteGpus_RepeatTheSameTemplatePerCard()
    {
        // 多显卡：每块卡都按同一组模板来，不是只照顾第一块。
        var settings = DashboardSettingsDefaults.Create(ShapeMetrics(
            integratedGpu: false,
            discreteGpuCount: 2));

        var ids = settings.Cards.Select(static card => card.Id).ToArray();
        Assert.Equal(
            ["cpu", "memory", "gpu1", "gpu2", "vram1", "vram2",
             "cpu-sensors", "gpu1-sensors", "gpu2-sensors"],
            ids);
    }

    [Fact]
    public void DefaultCards_UnifiedMemorySoc_KeepsTheFanCardWithoutVideoMemory()
    {
        // AI SoC：有 GPU、有风扇，但**没有独立显存**（和处理器共用内存）。
        // 显存卡不该出现，风扇卡该出现 —— 先前用显存当判据时这张风扇卡会被吞掉。
        var settings = DashboardSettingsDefaults.Create(ShapeMetrics(
            integratedGpu: false,
            discreteGpuCount: 0,
            unifiedMemoryGpu: true));

        var ids = settings.Cards.Select(static card => card.Id).ToArray();
        Assert.Equal(["cpu", "memory", "gpu0", "cpu-sensors", "gpu0-sensors"], ids);
        Assert.DoesNotContain("vram0", ids);
    }

    [Fact]
    public void DefaultCards_UnifiedMemorySoc_StillShowsGpuPowerOnTheUsageCard()
    {
        // 功耗和显存没有关系。没有显存的 GPU 一样可以报功耗。
        var settings = DashboardSettingsDefaults.Create(ShapeMetrics(
            integratedGpu: false,
            discreteGpuCount: 0,
            unifiedMemoryGpu: true));

        var usage = settings.Cards.Single(static card => card.Id == "gpu0");
        Assert.Contains("gpu.0.power", usage.Small);
    }

    /// <summary>
    /// 按机型拼一份"这台机器读得到哪些指标"。
    /// 卡片是从这份清单推出来的，所以用例只需要描述硬件，不必描述卡片。
    /// </summary>
    private static Dictionary<string, MetricValue> ShapeMetrics(
        bool integratedGpu,
        int discreteGpuCount,
        bool unifiedMemoryGpu = false)
    {
        var ids = new List<string>
        {
            "cpu.usage", "cpu.frequency", "cpu.temperature", "cpu.actualPower",
            "cpu.fanRpm", "cpu.coreVoltage", "cpu.packageCurrent",
            "memory.usage", "memory.percent"
        };

        // 核显：有占用率、频率、温度；没有独立显存，也没有自己的风扇。
        if (integratedGpu)
        {
            ids.AddRange(["gpu.0.usage", "gpu.0.graphicsClock", "gpu.0.temperature"]);
        }

        // AI SoC 的 GPU：和核显一样没有独立显存，但它有功耗读数和自己的风扇。
        if (unifiedMemoryGpu)
        {
            ids.AddRange(
            [
                "gpu.0.usage", "gpu.0.graphicsClock", "gpu.0.power", "gpu.0.temperature",
                "gpu.0.fanRpm", "gpu.0.coreVoltage"
            ]);
        }

        // 独显从 1 号开始编，和"核显占 0 号"的常见排布对齐。
        for (var index = 1; index <= discreteGpuCount; index++)
        {
            ids.AddRange(
            [
                $"gpu.{index}.usage",
                $"gpu.{index}.graphicsClock",
                $"gpu.{index}.power",
                $"gpu.{index}.temperature",
                $"gpu.{index}.vram",
                $"gpu.{index}.vramPercent",
                $"gpu.{index}.memoryClock",
                $"gpu.{index}.fanRpm",
                $"gpu.{index}.coreVoltage",
                $"gpu.{index}.current"
            ]);
        }

        return ids.Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(
            static id => id,
            static id => new MetricValue(id, id, "test", "--", null, string.Empty, null, null),
            StringComparer.OrdinalIgnoreCase);
    }

    private static bool CardEquals(DashboardCardSettings card, string id, string main, params string[] small)
    {
        return card.Id == id
            && card.Main == main
            && card.Small.SequenceEqual(small);
    }
}
