using System.Text.Json;
using System.Text.Json.Nodes;
using ResourceManager.App.Application.Settings;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.Settings;

namespace Resource_Manager_APP.Tests;

public sealed class AppSettingsMigratorTests
{
    [Theory]
    [InlineData("true", true, false)]
    [InlineData("false", false, false)]
    [InlineData("null", false, true)]
    [InlineData("\"true\"", false, true)]
    public void SmartRecordHistoryMigratesAndValidatesBoolean(string json, bool expected, bool rewrite)
    {
        var root = JsonNode.Parse(CurrentShapeJson)!;
        root["performance"]!["keepSmartRecordHistory"] = JsonNode.Parse(json);
        using var document = JsonDocument.Parse(root.ToJsonString());
        Assert.Equal(expected, AppSettingsMigrator.MigrateForRead(document.RootElement).Performance.KeepSmartRecordHistory);
        Assert.Equal(rewrite, AppSettingsMigrator.RequiresRewrite(document.RootElement));
    }

    [Fact]
    public void MissingSmartRecordHistoryDefaultsOffAndRewritesWithoutLosingOtherChoices()
    {
        var root = JsonNode.Parse(CurrentShapeJson)!;
        root["performance"]!.AsObject().Remove("keepSmartRecordHistory");
        root["performance"]!["preciseGpuPlacementEnabled"] = true;
        using var document = JsonDocument.Parse(root.ToJsonString());
        var settings = AppSettingsMigrator.MigrateForRead(document.RootElement);
        Assert.False(settings.Performance.KeepSmartRecordHistory);
        Assert.True(settings.Performance.PreciseGpuPlacementEnabled);
        Assert.True(AppSettingsMigrator.RequiresRewrite(document.RootElement));
    }

    [Fact]
    public void ReserveSchemaUpgradeDefaultsToDumpAndPreservesExplicitActionMode()
    {
        var root = JsonNode.Parse(CurrentShapeJson)!.AsObject();
        root["version"] = "1.0.35";
        root["memoryProtection"] = JsonNode.Parse("""{"enabled":false,"reserves":[]}""");
        using var old = JsonDocument.Parse(root.ToJsonString());
        Assert.Equal("dumpThenTerminate", AppSettingsMigrator.MigrateForRead(old.RootElement).MemoryProtection!.ActionMode);
        root["memoryProtection"]!["actionMode"] = "terminateOnly";
        using var configured = JsonDocument.Parse(root.ToJsonString());
        Assert.Equal("terminateOnly", AppSettingsMigrator.MigrateForRead(configured.RootElement).MemoryProtection!.ActionMode);
    }

    [Fact]
    public void OldMemoryDumpDirectoryDoesNotInventDiskReservationDuringUpgrade()
    {
        var root = JsonNode.Parse(CurrentShapeJson)!.AsObject();
        root["version"] = "1.0.34";
        root["memoryProtection"] = JsonNode.Parse("""{"enabled":true,"thresholdPercent":91,"checkIntervalSeconds":2,"dumpDirectory":"UserData/OldSlices"}""");
        using var document = JsonDocument.Parse(root.ToJsonString());
        var settings = AppSettingsMigrator.MigrateForRead(document.RootElement);
        Assert.Equal("1.0.37", settings.Version);
        Assert.True(settings.MemoryProtection!.Enabled);
        Assert.Equal(91, settings.MemoryProtection.ThresholdPercent);
        Assert.Empty(settings.MemoryProtection.Reserves!);
        Assert.DoesNotContain("dumpDirectory", JsonSerializer.Serialize(settings, JsonSerializerOptions.Web));
        Assert.True(AppSettingsMigrator.RequiresRewrite(document.RootElement));
    }

    [Fact]
    public void CurrentMemoryReserveEntriesPreserveUserOrderAndCapacity()
    {
        var root = JsonNode.Parse(CurrentShapeJson)!.AsObject();
        root["memoryProtection"] = JsonNode.Parse("""{"enabled":false,"thresholdPercent":94,"checkIntervalSeconds":3,"reserves":[{"location":"UserData/SecondVolume","sizeBytes":131072},{"location":"UserData/FirstVolume","sizeBytes":65536}]}""");
        using var document = JsonDocument.Parse(root.ToJsonString());
        var settings = AppSettingsMigrator.MigrateForRead(document.RootElement).MemoryProtection!;
        Assert.Equal(new[] { new HostMemoryReserveSettings("UserData/SecondVolume", 131072),
            new HostMemoryReserveSettings("UserData/FirstVolume", 65536) }, settings.Reserves);
        Assert.Equal(94, settings.ThresholdPercent);
        Assert.Equal(3, settings.CheckIntervalSeconds);
    }

    [Fact]
    public void Version129_AddsRealtimeActionBudgetDefaultsAndPreservesExistingChoices()
    {
        var root = JsonNode.Parse(CurrentShapeJson)!.AsObject();
        root["version"] = "1.0.29";
        var performance = root["performance"]!.AsObject();
        performance.Remove("realtimeActionBudgetMode");
        performance.Remove("realtimeActionBudgetCount");
        performance.Remove("realtimeActionBudgetPercent");
        performance["optimizationMode"] = AppOptimizationModes.Smart;
        using var document = JsonDocument.Parse(root.ToJsonString());

        var migrated = AppSettingsMigrator.MigrateForRead(document.RootElement);

        Assert.True(AppSettingsMigrator.RequiresRewrite(document.RootElement));
        Assert.Equal(AppSettingsDefaults.CurrentVersion, migrated.Version);
        Assert.Equal(AppOptimizationModes.Smart, migrated.Performance.OptimizationMode);
        Assert.Equal(AppRealtimeActionBudgetModes.Fixed, migrated.Performance.RealtimeActionBudgetMode);
        Assert.Equal(32, migrated.Performance.RealtimeActionBudgetCount);
        Assert.Equal(5, migrated.Performance.RealtimeActionBudgetPercent);
        using var written = JsonDocument.Parse(JsonSerializer.Serialize(migrated, JsonSerializerOptions.Web));
        Assert.False(AppSettingsMigrator.RequiresRewrite(written.RootElement));
    }

    [Theory]
    [InlineData("realtimeActionBudgetMode")]
    [InlineData("realtimeActionBudgetCount")]
    [InlineData("realtimeActionBudgetPercent")]
    public void RequiresRewrite_AddsMissingRealtimeActionBudgetFields(string field)
    {
        var root = JsonNode.Parse(CurrentShapeJson)!.AsObject();
        root["performance"]!.AsObject().Remove(field);
        using var document = JsonDocument.Parse(root.ToJsonString());

        Assert.True(AppSettingsMigrator.RequiresRewrite(document.RootElement));
        var migrated = AppSettingsMigrator.MigrateForRead(document.RootElement);
        Assert.Equal(32, migrated.Performance.RealtimeActionBudgetCount);
        Assert.Equal(5, migrated.Performance.RealtimeActionBudgetPercent);
    }

    [Theory]
    [InlineData("\"percent\"", "64", "25", "percent", 64, 25, false)]
    [InlineData("\"fixed\"", "1", "100", "fixed", 1, 100, false)]
    [InlineData("\"percent\"", "8192", "1", "percent", 8192, 1, false)]
    [InlineData("\"fixed\"", "0", "101", "fixed", 1, 100, true)]
    [InlineData("\"percent\"", "8193", "0", "percent", 8192, 1, true)]
    [InlineData("\"other\"", "64", "25", "fixed", 64, 25, true)]
    [InlineData("true", "\"64\"", "5.5", "fixed", 32, 5, true)]
    public void MigrateForRead_NormalizesRealtimeActionBudgetAndRequiresCanonicalValues(
        string modeJson, string countJson, string percentJson,
        string expectedMode, int expectedCount, int expectedPercent, bool requiresRewrite)
    {
        var root = JsonNode.Parse(CurrentShapeJson)!.AsObject();
        root["performance"]!["realtimeActionBudgetMode"] = JsonNode.Parse(modeJson);
        root["performance"]!["realtimeActionBudgetCount"] = JsonNode.Parse(countJson);
        root["performance"]!["realtimeActionBudgetPercent"] = JsonNode.Parse(percentJson);
        using var document = JsonDocument.Parse(root.ToJsonString());

        var performance = AppSettingsMigrator.MigrateForRead(document.RootElement).Performance;

        Assert.Equal(requiresRewrite, AppSettingsMigrator.RequiresRewrite(document.RootElement));
        Assert.Equal(expectedMode, performance.RealtimeActionBudgetMode);
        Assert.Equal(expectedCount, performance.RealtimeActionBudgetCount);
        Assert.Equal(expectedPercent, performance.RealtimeActionBudgetPercent);
    }

    [Fact]
    public void Version127_DiscardsRetiredDangerFieldsAndAddsGpuOverflowDefaults()
    {
        using var input = JsonDocument.Parse("""
            { "version": "1.0.27", "performance": {
                "vramMoveDownPhysicalMemoryDangerPercent": 23,
                "physicalMemoryMoveDownVirtualMemoryDangerPercent": 31
            } }
            """);
        var migrated = AppSettingsMigrator.MigrateForRead(input.RootElement);
        Assert.Equal(AppSettingsDefaults.CurrentVersion, migrated.Version);
        Assert.Equal(95, migrated.Performance.GpuOverflowUsagePercent);
        Assert.Equal(80, migrated.Performance.GpuOverflowDedicatedMemoryPercent);
        using var saved = JsonDocument.Parse(JsonSerializer.Serialize(migrated, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var performance = saved.RootElement.GetProperty("performance");
        Assert.False(performance.TryGetProperty("vramMoveDownPhysicalMemoryDangerPercent", out _));
        Assert.False(performance.TryGetProperty("physicalMemoryMoveDownVirtualMemoryDangerPercent", out _));
    }

    [Fact]
    public void Version128_RewritesRetiredGpuUseCasesAndAddsCheckInterval()
    {
        var input = JsonNode.Parse(CurrentShapeJson)!.AsObject();
        input["version"] = "1.0.28";
        input["performance"]!["gpuPerformanceUseCases"] = new JsonArray("gaming", "ai");
        input["update"]!.AsObject().Remove("checkIntervalHours");
        using var document = JsonDocument.Parse(input.ToJsonString());
        Assert.True(AppSettingsMigrator.RequiresRewrite(document.RootElement));
        var migrated = AppSettingsMigrator.MigrateForRead(document.RootElement);
        Assert.Equal(AppSettingsDefaults.CurrentVersion, migrated.Version);
        Assert.Equal(24, migrated.Update!.CheckIntervalHours);
        using var written = JsonDocument.Parse(JsonSerializer.Serialize(migrated, JsonSerializerOptions.Web));
        Assert.False(written.RootElement.GetProperty("performance").TryGetProperty("gpuPerformanceUseCases", out _));
        Assert.False(AppSettingsMigrator.RequiresRewrite(written.RootElement));
    }

    [Theory]
    [InlineData("0", 24)]
    [InlineData("169", 168)]
    [InlineData("24", 24)]
    [InlineData("\"24\"", 24)]
    public void CheckIntervalIsNormalizedAndCanBePatched(string input, int expected)
    {
        using var patch = JsonDocument.Parse("{\"update\":{\"checkIntervalHours\":" + input + "}}");
        if (patch.RootElement.GetProperty("update").GetProperty("checkIntervalHours").ValueKind == JsonValueKind.String)
        {
            var root = JsonNode.Parse(CurrentShapeJson)!;
            root["update"]!["checkIntervalHours"] = "24";
            using var invalid = JsonDocument.Parse(root.ToJsonString());
            Assert.True(AppSettingsMigrator.RequiresRewrite(invalid.RootElement));
            Assert.Equal(expected, AppSettingsMigrator.MigrateForRead(invalid.RootElement).Update!.CheckIntervalHours);
            return;
        }
        Assert.Equal(expected, AppSettingsPatchApplier.Apply(AppSettingsDefaults.Create(), patch.RootElement).Update!.CheckIntervalHours);
    }

    [Fact]
    public void MigrateForRead_PreservesAutomaticUpdatePreference()
    {
        var root = JsonNode.Parse(CurrentShapeJson)!.AsObject();
        root["update"]!["autoUpdateEnabled"] = true;
        root["update"]!.AsObject().Remove("mode");
        root["update"]!.AsObject().Remove("periodicCheckEnabled");
        using var document = JsonDocument.Parse(root.ToJsonString());

        var settings = AppSettingsMigrator.MigrateForRead(document.RootElement);

        Assert.Equal(AppUpdateModes.Automatic, settings.Update!.Mode);
        Assert.True(settings.Update.PeriodicCheckEnabled);
    }

    [Theory]
    [InlineData("1.0.17", true)]
    [InlineData("1.0.22", true)]
    [InlineData("1.0.23", true)]
    [InlineData("1.0.24", true)]
    [InlineData("1.0.25", true)]
    [InlineData("1.0.16", false)]
    [InlineData("1.0.26", true)]
    [InlineData("1.0.27", true)]
    [InlineData("1.0.28", true)]
    [InlineData("1.0.29", true)]
    [InlineData("1.0.30", true)]
    [InlineData("1.0.31", true)]
    [InlineData("1.0.32", true)]
    [InlineData("1.0.33", true)]
    [InlineData("1.0.34", true)]
    [InlineData("1.0.35", true)]
    [InlineData("1.0.36", true)]
    [InlineData("1.0.37", true)]
    [InlineData("1.0.38", false)]
    [InlineData("1.0.24.0", false)]
    [InlineData("99.0.0", false)]
    public void SupportsSourceVersion_UsesTheSingleContiguousMigrationRange(
        string sourceVersion,
        bool expected)
    {
        Assert.Equal(expected, AppSettingsMigrator.SupportsSourceVersion(sourceVersion));
    }

    [Fact]
    public void MigrateForRead_ReturnsDefaultsForNonCurrentShape()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "version": 1,
              "performance": {
                "smartMonitoringEnabled": false,
                "monitoringIdleSeconds": 60
              },
              "appearance": {
                "theme": "dark"
              }
            }
            """);

        var settings = AppSettingsMigrator.MigrateForRead(document.RootElement);

        Assert.Equal(AppSettingsDefaults.CurrentVersion, settings.Version);
        Assert.False(settings.SystemIntegration.TaskManagerShortcutReplacementEnabled);
        Assert.False(settings.SystemIntegration.Hotkeys.Single().Enabled);
        Assert.False(settings.Debug.DebugModeEnabled);
        Assert.False(settings.Debug.DebugLogEnabled);
        Assert.False(settings.Debug.HostManagerSmartCoordinatorScoreOnlyEnabled);
        Assert.False(settings.Debug.HostManagerSmartCoordinatorPerformanceLogEnabled);
        Assert.False(settings.PublicService.Enabled);
        Assert.False(settings.PublicService.FileIndexEnabled);
        Assert.False(settings.PublicService.DatabaseServiceEnabled);
        Assert.False(settings.PublicService.AiModelCatalogEnabled);
        Assert.Equal("lm-studio", settings.AiModelService.Provider);
        Assert.Equal("http://127.0.0.1:1234", settings.AiModelService.Endpoint);
        Assert.False(settings.AiModelService.AutoStartEnabled);
        Assert.True(AppSettingsMigrator.RequiresRewrite(document.RootElement));
    }

    [Fact]
    public void RequiresRewrite_ReturnsFalseForCurrentShape()
    {
        using var document = JsonDocument.Parse(CurrentShapeJson);

        Assert.False(AppSettingsMigrator.RequiresRewrite(document.RootElement));
    }

    [Fact]
    public void MigrateForRead_DropsRetiredSamplingDispatchModeWithoutResettingOtherSettings()
    {
        var root = JsonNode.Parse(CurrentShapeJson)!.AsObject();
        root["version"] = "1.0.22";
        root["performance"]!["samplingDispatchMode"] = "normal";
        using var document = JsonDocument.Parse(root.ToJsonString());

        var settings = AppSettingsMigrator.MigrateForRead(document.RootElement);
        var serialized = JsonSerializer.Serialize(settings);

        Assert.False(settings.Performance.PreciseGpuPlacementEnabled);
        Assert.DoesNotContain("samplingDispatchMode", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.True(AppSettingsMigrator.RequiresRewrite(document.RootElement));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MigrateForRead_UsesCanonicalDefaultsWhenSupportedShapeOmitsValues(bool omitSections)
    {
        var root = JsonNode.Parse(CurrentShapeJson)!.AsObject();
        root["version"] = "1.0.22";
        root["performance"]!.AsObject().Remove("preciseGpuPlacementEnabled");
        if (omitSections)
        {
            root.Remove("publicService");
            root.Remove("aiModelService");
        }
        else
        {
            root["publicService"]!.AsObject().Remove("enabled");
            root["publicService"]!.AsObject().Remove("fileIndexEnabled");
            root["publicService"]!.AsObject().Remove("databaseServiceEnabled");
            root["publicService"]!.AsObject().Remove("aiModelCatalogEnabled");
            root["aiModelService"]!.AsObject().Remove("autoStartEnabled");
        }
        using var document = JsonDocument.Parse(root.ToJsonString());

        var settings = AppSettingsMigrator.MigrateForRead(document.RootElement);

        Assert.False(settings.Performance.PreciseGpuPlacementEnabled);
        Assert.False(settings.PublicService.Enabled);
        Assert.False(settings.PublicService.FileIndexEnabled);
        Assert.False(settings.PublicService.DatabaseServiceEnabled);
        Assert.False(settings.PublicService.AiModelCatalogEnabled);
        Assert.False(settings.AiModelService.AutoStartEnabled);
    }

    [Fact]
    public void RequiresRewrite_RejectsRetiredSamplingDispatchModeInCurrentShape()
    {
        var root = JsonNode.Parse(CurrentShapeJson)!.AsObject();
        root["performance"]!["samplingDispatchMode"] = "smooth";
        using var document = JsonDocument.Parse(root.ToJsonString());

        Assert.True(AppSettingsMigrator.RequiresRewrite(document.RootElement));
    }

    [Fact]
    public void MigrateForRead_DropsVersion118SelfOptimizationControlPlane()
    {
        var root = JsonNode.Parse(CurrentShapeJson)!.AsObject();
        root["version"] = "1.0.18";
        root["debug"]!["hostManagerSmartCoordinatorScoreOnlyEnabled"] = true;
        root["selfOptimization"] = new JsonObject
        {
            ["enabled"] = true,
            ["loopIntervalSeconds"] = 30,
            ["privateMemorySoftLimitBytes"] = 805_306_368,
            ["workingSetSoftLimitBytes"] = 536_870_912,
            ["consecutiveSamplesBeforeAction"] = 3,
            ["minimumActionIntervalSeconds"] = 120
        };
        using var document = JsonDocument.Parse(root.ToJsonString());

        var settings = AppSettingsMigrator.MigrateForRead(document.RootElement);
        var serialized = JsonSerializer.Serialize(settings, JsonSerializerOptions.Web);

        Assert.Equal(AppSettingsDefaults.CurrentVersion, settings.Version);
        Assert.True(settings.Debug.HostManagerSmartCoordinatorScoreOnlyEnabled);
        Assert.DoesNotContain("selfOptimization", serialized, StringComparison.Ordinal);
        Assert.True(AppSettingsMigrator.RequiresRewrite(document.RootElement));
    }

    [Fact]
    public void RequiresRewrite_RejectsRetiredSelfOptimizationInCurrentShape()
    {
        var root = JsonNode.Parse(CurrentShapeJson)!.AsObject();
        root["selfOptimization"] = new JsonObject();
        using var document = JsonDocument.Parse(root.ToJsonString());

        Assert.True(AppSettingsMigrator.RequiresRewrite(document.RootElement));
    }

    [Fact]
    public void MigrateForRead_AddsDisabledEditableHotkeyWithoutResettingCurrentSettings()
    {
        using var document = JsonDocument.Parse(CurrentShapeJson);

        var settings = AppSettingsMigrator.MigrateForRead(document.RootElement);

        var hotkey = Assert.Single(settings.SystemIntegration.Hotkeys);
        Assert.Equal(AppSystemIntegrationActionIds.ForceTerminateUnresponsiveAndForeground, hotkey.ActionId);
        Assert.False(hotkey.Enabled);
        Assert.Equal([0, 0, 0, 0, 0, 0, 0], hotkey.Encoding);
        Assert.True(settings.PublicService.Enabled);
        Assert.True(settings.PublicService.FileIndexEnabled);
        Assert.True(settings.PublicService.DatabaseServiceEnabled);
        Assert.True(settings.PublicService.AiModelCatalogEnabled);
        Assert.True(settings.AiModelService.AutoStartEnabled);
    }

    [Fact]
    public void MigrateForRead_DropsLegacyExternalUiDebugSetting()
    {
        using var document = JsonDocument.Parse(CurrentShapeJson
            .Replace($"\"version\": \"{AppSettingsDefaults.CurrentVersion}\"", "\"version\": \"1.0.16\"", StringComparison.Ordinal)
            .Replace(
                "\"hostManagerSmartCoordinatorPerformanceLogEnabled\": false",
                "\"hostManagerSmartCoordinatorPerformanceLogEnabled\": false, \"externalDebugEnabled\": true",
                StringComparison.Ordinal));

        var settings = AppSettingsMigrator.MigrateForRead(document.RootElement);
        var serialized = JsonSerializer.Serialize(settings);

        Assert.DoesNotContain("externalDebugEnabled", serialized, StringComparison.Ordinal);
        Assert.True(AppSettingsMigrator.RequiresRewrite(document.RootElement));
    }

    [Fact]
    public void MigrateForRead_DoesNotImportVersion117KeysFromOlderSchemas()
    {
        using var document = JsonDocument.Parse(CurrentShapeJson
            .Replace($"\"version\": \"{AppSettingsDefaults.CurrentVersion}\"", "\"version\": \"1.0.16\"", StringComparison.Ordinal)
            .Replace(
                "\"hostManagerSmartCoordinatorScoreOnlyEnabled\": false",
                "\"smartOptimizationScoreOnlyEnabled\": true",
                StringComparison.Ordinal)
            .Replace(
                "\"hostManagerSmartCoordinatorPerformanceLogEnabled\": false",
                "\"smartOptimizationPerformanceLogEnabled\": true",
                StringComparison.Ordinal));

        var settings = AppSettingsMigrator.MigrateForRead(document.RootElement);

        Assert.False(settings.Debug.HostManagerSmartCoordinatorScoreOnlyEnabled);
        Assert.False(settings.Debug.HostManagerSmartCoordinatorPerformanceLogEnabled);
    }

    [Fact]
    public void MigrateForRead_ConvertsVersion117HostManagerCoordinatorDebugKeysOnce()
    {
        using var document = JsonDocument.Parse(CurrentShapeJson
            .Replace($"\"version\": \"{AppSettingsDefaults.CurrentVersion}\"", "\"version\": \"1.0.17\"", StringComparison.Ordinal)
            .Replace(
                "\"hostManagerSmartCoordinatorScoreOnlyEnabled\": false",
                "\"smartOptimizationScoreOnlyEnabled\": true",
                StringComparison.Ordinal)
            .Replace(
                "\"hostManagerSmartCoordinatorPerformanceLogEnabled\": false",
                "\"smartOptimizationPerformanceLogEnabled\": true",
                StringComparison.Ordinal));

        var settings = AppSettingsMigrator.MigrateForRead(document.RootElement);
        var serialized = JsonSerializer.Serialize(settings, JsonSerializerOptions.Web);

        Assert.True(settings.Debug.HostManagerSmartCoordinatorScoreOnlyEnabled);
        Assert.True(settings.Debug.HostManagerSmartCoordinatorPerformanceLogEnabled);
        Assert.Contains("hostManagerSmartCoordinatorScoreOnlyEnabled", serialized, StringComparison.Ordinal);
        Assert.Contains("hostManagerSmartCoordinatorPerformanceLogEnabled", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("smartOptimizationScoreOnlyEnabled", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("smartOptimizationPerformanceLogEnabled", serialized, StringComparison.Ordinal);
        Assert.True(AppSettingsMigrator.RequiresRewrite(document.RootElement));
    }

    [Fact]
    public void RequiresRewrite_RejectsMissingOrInvalidCurrentHostManagerCoordinatorDebugKeys()
    {
        var missingRoot = JsonNode.Parse(CurrentShapeJson)!.AsObject();
        missingRoot["debug"]!.AsObject().Remove("hostManagerSmartCoordinatorScoreOnlyEnabled");
        using var missingDocument = JsonDocument.Parse(missingRoot.ToJsonString());

        var invalidRoot = JsonNode.Parse(CurrentShapeJson)!.AsObject();
        invalidRoot["debug"]!["hostManagerSmartCoordinatorPerformanceLogEnabled"] = "false";
        using var invalidDocument = JsonDocument.Parse(invalidRoot.ToJsonString());

        Assert.True(AppSettingsMigrator.RequiresRewrite(missingDocument.RootElement));
        Assert.True(AppSettingsMigrator.RequiresRewrite(invalidDocument.RootElement));
    }

    [Fact]
    public void RequiresRewrite_RejectsCurrentShapeContainingLegacyCoordinatorKeys()
    {
        var root = JsonNode.Parse(CurrentShapeJson)!.AsObject();
        root["debug"]!["smartOptimizationScoreOnlyEnabled"] = false;
        root["debug"]!["smartOptimizationPerformanceLogEnabled"] = false;
        using var document = JsonDocument.Parse(root.ToJsonString());

        Assert.True(AppSettingsMigrator.RequiresRewrite(document.RootElement));
    }

    [Fact]
    public void HostManagerSmartCoordinatorModeNormalize_AcceptsOnlyCurrentModes()
    {
        Assert.Equal(AppOptimizationModes.Normal, HostManagerOptimizationModes.Normalize("unsupported"));
        Assert.Equal(AppOptimizationModes.Normal, HostManagerOptimizationModes.Normalize(AppOptimizationModes.Normal));
        Assert.Equal(AppOptimizationModes.MemoryOnly, HostManagerOptimizationModes.Normalize(AppOptimizationModes.MemoryOnly));
        Assert.Equal(AppOptimizationModes.Smart, HostManagerOptimizationModes.Normalize(AppOptimizationModes.Smart));
    }

    [Theory]
    [InlineData(AppOptimizationModes.MemoryOnly)]
    [InlineData(AppOptimizationModes.CpuOnly)]
    [InlineData(AppOptimizationModes.GpuOnly)]
    [InlineData(AppOptimizationModes.MemoryCpu)]
    [InlineData(AppOptimizationModes.MemoryGpu)]
    [InlineData(AppOptimizationModes.CpuGpu)]
    [InlineData(AppOptimizationModes.Smart)]
    public void MigrateForRead_PreservesSupportedSchedulingCombination(string mode)
    {
        using var document = JsonDocument.Parse(CurrentShapeJson.Replace(
            "\"optimizationMode\": \"normal\"",
            $"\"optimizationMode\": \"{mode}\"",
            StringComparison.Ordinal));

        var settings = AppSettingsMigrator.MigrateForRead(document.RootElement);

        Assert.Equal(mode, settings.Performance.OptimizationMode);
        Assert.False(AppSettingsMigrator.RequiresRewrite(document.RootElement));
    }

    [Fact]
    public void MigrateForRead_DropsRetiredSchedulingOptimizationToggle()
    {
        var root = JsonNode.Parse(CurrentShapeJson)!.AsObject();
        root["version"] = "1.0.24";
        root["performance"]!["automaticSchedulingOptimizationsEnabled"] = false;
        using var document = JsonDocument.Parse(root.ToJsonString());

        var settings = AppSettingsMigrator.MigrateForRead(document.RootElement);
        var serialized = JsonSerializer.Serialize(settings, JsonSerializerOptions.Web);

        Assert.DoesNotContain("automaticSchedulingOptimizationsEnabled", serialized, StringComparison.Ordinal);
        Assert.True(AppSettingsMigrator.RequiresRewrite(document.RootElement));
    }

    [Fact]
    public void MigrateForRead_DropsRetiredBackgroundReportSamplingSettings()
    {
        var root = JsonNode.Parse(CurrentShapeJson)!.AsObject();
        root["version"] = "1.0.21";
        root["performance"]!["stopNormalModeBackgroundReportsWhenStable"] = true;
        root["performance"]!["normalModeSettlingCheckSeconds"] = 30;
        root["performance"]!["backgroundReportSamplingMode"] = "continuous";
        using var document = JsonDocument.Parse(root.ToJsonString());

        var settings = AppSettingsMigrator.MigrateForRead(document.RootElement);
        var serialized = JsonSerializer.Serialize(settings);

        Assert.Equal(AppSettingsDefaults.CurrentVersion, settings.Version);
        Assert.DoesNotContain(
            "stopNormalModeBackgroundReportsWhenStable",
            serialized,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "normalModeSettlingCheckSeconds",
            serialized,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "backgroundReportSamplingMode",
            serialized,
            StringComparison.Ordinal);
        Assert.True(AppSettingsMigrator.RequiresRewrite(document.RootElement));
    }

    [Fact]
    public void FileWithoutTheMemoryCpuTierSectionReadsAsTierThreeWithoutOverrides()
    {
        var root = JsonNode.Parse(CurrentShapeJson)!;
        root.AsObject().Remove("memoryCpuTier");
        using var old = JsonDocument.Parse(root.ToJsonString());
        var tier = AppSettingsMigrator.MigrateForRead(old.RootElement).MemoryCpuTier;
        Assert.NotNull(tier);
        Assert.Equal(MemoryCpuTierSettings.DefaultTier, tier.Tier);
        Assert.True(tier.Overrides is null or { Count: 0 });

        root["memoryCpuTier"] = JsonNode.Parse("""{"tier":5,"overrides":{"gcGen0MaxBudget":null,"singleRequestStopBufferMs":30000}}""");
        using var current = JsonDocument.Parse(root.ToJsonString());
        var stored = AppSettingsMigrator.MigrateForRead(current.RootElement).MemoryCpuTier!;
        Assert.Equal(5, stored.Tier);
        Assert.Equal(JsonValueKind.Null, stored.Overrides!["gcGen0MaxBudget"].ValueKind);
        Assert.Equal(30000, stored.Overrides["singleRequestStopBufferMs"].GetInt32());
    }

    private const string CurrentShapeJson =
        """
        {
          "version": "1.0.37",
          "memoryProtection": { "minimumProcessGrowthBytes": 1073741824, "protectedProcesses": ["explorer.exe"] },
          "performance": {
            "optimizationMode": "normal",
            "preciseGpuPlacementEnabled": false,
            "gpuOverflowUsagePercent": 95,
            "gpuOverflowDedicatedMemoryPercent": 80,
            "realtimeActionBudgetMode": "fixed",
            "realtimeActionBudgetCount": 32,
            "realtimeActionBudgetPercent": 5,
            "keepSmartRecordHistory": false
          },
          "systemIntegration": {
            "restartAfterCrashEnabled": true,
            "crashRestartDelaySeconds": 10,
            "crashRestartCount": 3,
            "autoStartEnabled": false,
            "taskManagerShortcutReplacementEnabled": false
          },
          "publicService": {
            "enabled": true,
            "fileIndexEnabled": true,
            "databaseServiceEnabled": true,
            "aiModelCatalogEnabled": true
          },
          "aiModelService": {
            "provider": "lm-studio",
            "endpoint": "http://127.0.0.1:1234",
            "autoStartEnabled": true
          },
          "update": { "mode": "automatic", "periodicCheckEnabled": false, "checkIntervalHours": 24 },
          "control": { "enabled": true, "reassertionIntervalSeconds": 120 },
          "reports": { "ruleOverrides": [] },
          "debug": {
            "debugModeEnabled": false,
            "loopbackAuthenticationDisabled": false,
            "debugLogEnabled": false,
            "hostManagerSmartCoordinatorScoreOnlyEnabled": false,
            "hostManagerSmartCoordinatorPerformanceLogEnabled": false
          }
        }
        """;
}
