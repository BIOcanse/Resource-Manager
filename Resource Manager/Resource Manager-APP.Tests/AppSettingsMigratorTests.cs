using System.Text.Json;
using System.Text.Json.Nodes;
using ResourceManager.App.Application.Settings;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.Settings;

namespace Resource_Manager_APP.Tests;

public sealed class AppSettingsMigratorTests
{
    [Theory]
    [InlineData("1.0.17", true)]
    [InlineData("1.0.22", true)]
    [InlineData("1.0.23", true)]
    [InlineData("1.0.24", true)]
    [InlineData("1.0.16", false)]
    [InlineData("1.0.25", false)]
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
        Assert.True(settings.Performance.SmartMonitoringEnabled);
        Assert.Equal(AppThemeModes.System, settings.Appearance.Theme);
        Assert.Equal([AppGpuPerformanceUseCases.General], settings.Performance.GpuPerformanceUseCases);
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

        Assert.Equal(AppThemeModes.System, settings.Appearance.Theme);
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

        Assert.True(settings.Performance.PreciseGpuPlacementEnabled);
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
    public void MigrateForRead_PreservesCurrentLogicRefreshCadenceShape()
    {
        using var document = JsonDocument.Parse(CurrentShapeJson);

        var settings = AppSettingsMigrator.MigrateForRead(document.RootElement);

        Assert.Equal(AppPresetNumericSettingModes.Aotu, settings.Performance.MonitorRefreshIntervalMs.Mode);
        Assert.Equal(AppLogicRefreshIntervalPresets.Responsive, settings.Performance.MonitorRefreshIntervalMs.Preset);
        Assert.Equal(1000, settings.Performance.MonitorRefreshIntervalMs.CustomValue);
        Assert.Equal(AppLogicRefreshIntervalPresets.Balanced, settings.Performance.ManagementRefreshIntervalMs.Preset);
        Assert.Equal(10000, settings.Performance.ManagementRefreshIntervalMs.CustomValue);
    }

    [Fact]
    public void MigrateForRead_AddsDisabledEditableHotkeyWithoutResettingCurrentSettings()
    {
        using var document = JsonDocument.Parse(CurrentShapeJson);

        var settings = AppSettingsMigrator.MigrateForRead(document.RootElement);

        Assert.Equal(AppThemeModes.System, settings.Appearance.Theme);
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

    [Theory]
    [InlineData("type")]
    [InlineData("distinct")]
    public void MigrateForRead_PreservesBothResourceBarColorModes(string barColorMode)
    {
        var root = JsonNode.Parse(CurrentShapeJson)!.AsObject();
        root["version"] = "1.0.22";
        root["appearance"]!["barColorMode"] = barColorMode;
        using var document = JsonDocument.Parse(root.ToJsonString());

        var settings = AppSettingsMigrator.MigrateForRead(document.RootElement);

        Assert.Equal(barColorMode, settings.Appearance.BarColorMode);
        Assert.Equal(AppThemeModes.System, settings.Appearance.Theme);
        Assert.True(settings.PublicService.Enabled);
    }

    [Fact]
    public void MigrateForRead_DropsRetiredDisplayOutputSettingsWithoutResettingOtherSettings()
    {
        var root = JsonNode.Parse(CurrentShapeJson)!.AsObject();
        root["version"] = "1.0.20";
        var appearance = root["appearance"]!.AsObject();
        appearance["sceneReferenceWhiteLog2Q16"] = 524_288;
        appearance["outputDisplayProfiles"] = new JsonArray
        {
            new JsonObject
            {
                ["displayId"] = "*",
                ["minimumLuminanceMilliNits"] = 8_000,
                ["maximumLuminanceMilliNits"] = 400_000,
                ["colorGamut"] = "srgb",
                ["toneMappingCurve"] = "linear"
            }
        };
        appearance["outputMinimumNits"] = 0;
        appearance["outputMaximumNits"] = 972;
        appearance["outputColorGamut"] = "dciP3";
        appearance["outputToneMappingCurve"] = "highlightCompression";
        using var document = JsonDocument.Parse(root.ToJsonString());

        var settings = AppSettingsMigrator.MigrateForRead(document.RootElement);
        var serialized = JsonSerializer.Serialize(settings, JsonSerializerOptions.Web);

        Assert.Equal(AppThemeModes.System, settings.Appearance.Theme);
        Assert.DoesNotContain("sceneReferenceWhite", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("outputDisplayProfiles", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("outputMinimumNits", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("outputMaximumNits", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("outputColorGamut", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("outputToneMappingCurve", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.True(AppSettingsMigrator.RequiresRewrite(document.RootElement));
    }

    [Fact]
    public void RequiresRewrite_RejectsRetiredDisplayOutputSettingsInCurrentShape()
    {
        var root = JsonNode.Parse(CurrentShapeJson)!.AsObject();
        root["appearance"]!["outputDisplayProfiles"] = new JsonArray();
        using var document = JsonDocument.Parse(root.ToJsonString());

        Assert.True(AppSettingsMigrator.RequiresRewrite(document.RootElement));
    }

    [Fact]
    public void MigrateForRead_DropsLegacyExternalUiDebugSetting()
    {
        using var document = JsonDocument.Parse(CurrentShapeJson
            .Replace("\"version\": \"1.0.24\"", "\"version\": \"1.0.16\"", StringComparison.Ordinal)
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
            .Replace("\"version\": \"1.0.24\"", "\"version\": \"1.0.16\"", StringComparison.Ordinal)
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
            .Replace("\"version\": \"1.0.24\"", "\"version\": \"1.0.17\"", StringComparison.Ordinal)
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

    [Fact]
    public void MigrateForRead_PreservesMemoryOnlyMode()
    {
        using var document = JsonDocument.Parse(CurrentShapeJson.Replace(
            "\"optimizationMode\": \"normal\"",
            "\"optimizationMode\": \"limited\"",
            StringComparison.Ordinal));

        var settings = AppSettingsMigrator.MigrateForRead(document.RootElement);

        Assert.Equal(AppOptimizationModes.MemoryOnly, settings.Performance.OptimizationMode);
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

    private const string CurrentShapeJson =
        """
        {
          "version": "1.0.24",
          "performance": {
            "smartMonitoringEnabled": true,
            "monitoringIdleSeconds": 5,
            "optimizationMode": "normal",
            "pauseFrontendRefreshWhenHiddenInNormalMode": true,
            "preciseGpuPlacementEnabled": false,
            "vramMoveDownPhysicalMemoryDangerPercent": 10,
            "physicalMemoryMoveDownVirtualMemoryDangerPercent": 12,
            "gpuPerformanceUseCases": ["general"],
            "smartMonitoringMode": "auto",
            "frontendHiddenRefreshMode": "auto",
            "automaticSchedulingOptimizationsEnabled": true,
            "monitorRefreshIntervalMs": { "mode": "aotu", "preset": "responsive", "customValue": 1000 },
            "resourceTableRefreshIntervalMs": { "mode": "aotu", "preset": "responsive", "customValue": 1000 },
            "managementRefreshIntervalMs": { "mode": "aotu", "preset": "balanced", "customValue": 10000 },
            "discoveryRefreshIntervalMs": { "mode": "aotu", "preset": "responsive", "customValue": 3000 },
            "optimizationRefreshIntervalMs": { "mode": "aotu", "preset": "balanced", "customValue": 10000 },
            "localSystemRefreshIntervalMs": { "mode": "aotu", "preset": "balanced", "customValue": 60000 }
          },
          "appearance": {
            "theme": "system",
            "animations": "auto",
            "resourceBarHardwareAccelerationEnabled": true,
            "resourceBarHardwareAccelerationMode": "auto",
            "barColorMode": "type",
            "fontSmoothing": "system",
            "language": "system"
          },
          "systemIntegration": {
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
