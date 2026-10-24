using System.Text.Json;
using System.Text.Json.Nodes;
using ResourceManager.App.Application.Settings;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.Settings;

namespace Resource_Manager_APP.Tests;

public sealed class AppSettingsMigratorTests
{
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
        Assert.Equal("1.0.28", migrated.Version);
        Assert.Equal(95, migrated.Performance.GpuOverflowUsagePercent);
        Assert.Equal(80, migrated.Performance.GpuOverflowDedicatedMemoryPercent);
        using var saved = JsonDocument.Parse(JsonSerializer.Serialize(migrated, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var performance = saved.RootElement.GetProperty("performance");
        Assert.False(performance.TryGetProperty("vramMoveDownPhysicalMemoryDangerPercent", out _));
        Assert.False(performance.TryGetProperty("physicalMemoryMoveDownVirtualMemoryDangerPercent", out _));
    }

    [Fact]
    public void MigrateForRead_PreservesAutomaticUpdatePreference()
    {
        var root = JsonNode.Parse(CurrentShapeJson)!.AsObject();
        root["update"]!["autoUpdateEnabled"] = true;
        using var document = JsonDocument.Parse(root.ToJsonString());

        var settings = AppSettingsMigrator.MigrateForRead(document.RootElement);

        Assert.True(settings.Update!.AutoUpdateEnabled);
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
    [InlineData("1.0.29", false)]
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
        Assert.Equal(AppAdaptiveBooleanModes.Enabled, settings.Performance.SmartMonitoringMode);
        Assert.Equal("system", settings.Appearance.Theme);
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

        Assert.Equal("system", settings.Appearance.Theme);
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
    public void MigrateForRead_PreservesCurrentLogicRefreshCadenceShape()
    {
        using var document = JsonDocument.Parse(CurrentShapeJson);

        var settings = AppSettingsMigrator.MigrateForRead(document.RootElement);

        Assert.Equal(AppPresetNumericSettingModes.Aotu, settings.Performance.MonitorRefreshIntervalMs.Mode);
        Assert.Equal(AppLogicRefreshIntervalPresets.Responsive, settings.Performance.MonitorRefreshIntervalMs.Preset);
        Assert.Equal(1000, settings.Performance.MonitorRefreshIntervalMs.CustomValue);
        Assert.Equal(AppLogicRefreshIntervalPresets.Balanced, settings.Performance.LocalSystemRefreshIntervalMs.Preset);
        Assert.Equal(60000, settings.Performance.LocalSystemRefreshIntervalMs.CustomValue);
    }

    [Fact]
    public void MigrateForRead_AddsDisabledEditableHotkeyWithoutResettingCurrentSettings()
    {
        using var document = JsonDocument.Parse(CurrentShapeJson);

        var settings = AppSettingsMigrator.MigrateForRead(document.RootElement);

        Assert.Equal("system", settings.Appearance.Theme);
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
        Assert.Equal("system", settings.Appearance.Theme);
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

        Assert.Equal("system", settings.Appearance.Theme);
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
            .Replace("\"version\": \"1.0.28\"", "\"version\": \"1.0.16\"", StringComparison.Ordinal)
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
            .Replace("\"version\": \"1.0.28\"", "\"version\": \"1.0.16\"", StringComparison.Ordinal)
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
            .Replace("\"version\": \"1.0.28\"", "\"version\": \"1.0.17\"", StringComparison.Ordinal)
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
    public void RequiresRewrite_AddsMissingUiStyleToCurrentShape()
    {
        using var document = JsonDocument.Parse(CurrentShapeJson.Replace(
            "\"uiStyle\": \"standard\",", string.Empty, StringComparison.Ordinal));

        Assert.True(AppSettingsMigrator.RequiresRewrite(document.RootElement));
        Assert.Equal("standard", AppSettingsMigrator.MigrateForRead(document.RootElement).Appearance.UiStyle);
    }

    [Theory]
    [InlineData("terminal", "amber", "terminal", "amber")]
    [InlineData("terminal", "system", "terminal", "phosphor")]
    [InlineData("soft", "lowContrast", "soft", "system")]
    [InlineData("unknown", "dark", "standard", "dark")]
    [InlineData("outline", "blueprint", "outline", "blueprint")]
    public void MigrateForRead_NormalizesThePaletteWithinTheStyle(
        string uiStyle,
        string theme,
        string expectedUiStyle,
        string expectedTheme)
    {
        using var document = JsonDocument.Parse(CurrentShapeJson
            .Replace("\"uiStyle\": \"standard\"", $"\"uiStyle\": \"{uiStyle}\"", StringComparison.Ordinal)
            .Replace("\"theme\": \"system\"", $"\"theme\": \"{theme}\"", StringComparison.Ordinal));

        var appearance = AppSettingsMigrator.MigrateForRead(document.RootElement).Appearance;

        Assert.Equal(expectedUiStyle, appearance.UiStyle);
        Assert.Equal(expectedTheme, appearance.Theme);
    }

    [Fact]
    public void Defaults_FollowTheDisplayWithSixtyFramesForCustom()
    {
        var appearance = AppSettingsDefaults.Create().Appearance;

        Assert.Equal(AppResourceBarFrameRateModes.Display, appearance.ResourceBarFrameRateMode);
        Assert.Equal(60, appearance.ResourceBarFrameRate);
    }

    [Theory]
    [InlineData("resourceBarFrameRateMode")]
    [InlineData("resourceBarFrameRate")]
    public void RequiresRewrite_AddsMissingResourceBarFrameRateToCurrentShape(string field)
    {
        var root = JsonNode.Parse(CurrentShapeJson)!.AsObject();
        root["appearance"]!.AsObject().Remove(field);
        using var document = JsonDocument.Parse(root.ToJsonString());

        var appearance = AppSettingsMigrator.MigrateForRead(document.RootElement).Appearance;

        Assert.True(AppSettingsMigrator.RequiresRewrite(document.RootElement));
        Assert.Equal(AppResourceBarFrameRateModes.Display, appearance.ResourceBarFrameRateMode);
        Assert.Equal(60, appearance.ResourceBarFrameRate);
    }

    [Theory]
    [InlineData("\"custom\"", "144", AppResourceBarFrameRateModes.Custom, 144)]
    [InlineData("\"custom\"", "10", AppResourceBarFrameRateModes.Custom, 10)]
    [InlineData("\"custom\"", "240", AppResourceBarFrameRateModes.Custom, 240)]
    [InlineData("\"custom\"", "5", AppResourceBarFrameRateModes.Custom, 10)]
    [InlineData("\"custom\"", "1000", AppResourceBarFrameRateModes.Custom, 240)]
    [InlineData("\"custom\"", "59.5", AppResourceBarFrameRateModes.Custom, 60)]
    [InlineData("\"custom\"", "\"90\"", AppResourceBarFrameRateModes.Custom, 60)]
    [InlineData("\"display\"", "30", AppResourceBarFrameRateModes.Display, 30)]
    [InlineData("\"unknown\"", "30", AppResourceBarFrameRateModes.Display, 30)]
    [InlineData("7", "30", AppResourceBarFrameRateModes.Display, 30)]
    public void MigrateForRead_NormalizesTheResourceBarFrameRate(
        string modeJson,
        string frameRateJson,
        string expectedMode,
        int expectedFrameRate)
    {
        var root = JsonNode.Parse(CurrentShapeJson)!.AsObject();
        root["appearance"]!["resourceBarFrameRateMode"] = JsonNode.Parse(modeJson);
        root["appearance"]!["resourceBarFrameRate"] = JsonNode.Parse(frameRateJson);
        using var document = JsonDocument.Parse(root.ToJsonString());

        var appearance = AppSettingsMigrator.MigrateForRead(document.RootElement).Appearance;

        Assert.Equal(expectedMode, appearance.ResourceBarFrameRateMode);
        Assert.Equal(expectedFrameRate, appearance.ResourceBarFrameRate);
    }

    [Fact]
    public void Defaults_ListOnlyThePreferredReadingSource()
    {
        Assert.Equal(AppReadingSourceModes.Preferred, AppSettingsDefaults.Create().Appearance.ReadingSourceMode);
    }

    [Fact]
    public void RequiresRewrite_AddsMissingReadingSourceModeToCurrentShape()
    {
        var root = JsonNode.Parse(CurrentShapeJson)!.AsObject();
        root["appearance"]!.AsObject().Remove("readingSourceMode");
        using var document = JsonDocument.Parse(root.ToJsonString());

        Assert.True(AppSettingsMigrator.RequiresRewrite(document.RootElement));
        Assert.Equal(
            AppReadingSourceModes.Preferred,
            AppSettingsMigrator.MigrateForRead(document.RootElement).Appearance.ReadingSourceMode);
    }

    [Theory]
    [InlineData("\"preferred\"", AppReadingSourceModes.Preferred)]
    [InlineData("\"all\"", AppReadingSourceModes.All)]
    [InlineData("\"All\"", AppReadingSourceModes.Preferred)]
    [InlineData("\"unknown\"", AppReadingSourceModes.Preferred)]
    [InlineData("true", AppReadingSourceModes.Preferred)]
    [InlineData("null", AppReadingSourceModes.Preferred)]
    public void MigrateForRead_NormalizesTheReadingSourceMode(string modeJson, string expectedMode)
    {
        var root = JsonNode.Parse(CurrentShapeJson)!.AsObject();
        root["appearance"]!["readingSourceMode"] = JsonNode.Parse(modeJson);
        using var document = JsonDocument.Parse(root.ToJsonString());

        Assert.Equal(expectedMode, AppSettingsMigrator.MigrateForRead(document.RootElement).Appearance.ReadingSourceMode);
    }

    private const string CurrentShapeJson =
        """
        {
          "version": "1.0.28",
          "performance": {
            "optimizationMode": "normal",
            "preciseGpuPlacementEnabled": false,
            "gpuOverflowUsagePercent": 95,
            "gpuOverflowDedicatedMemoryPercent": 80,
            "gpuPerformanceUseCases": ["general"],
            "smartMonitoringMode": "enabled",
            "monitorRefreshIntervalMs": { "mode": "aotu", "preset": "responsive", "customValue": 1000 },
            "resourceTableRefreshIntervalMs": { "mode": "aotu", "preset": "responsive", "customValue": 1000 },
            "localSystemRefreshIntervalMs": { "mode": "aotu", "preset": "balanced", "customValue": 60000 }
          },
          "appearance": {
            "uiStyle": "standard",
            "theme": "system",
            "animations": "auto",
            "resourceBarFrameRateMode": "display",
            "resourceBarFrameRate": 60,
            "barColorMode": "type",
            "fontSmoothing": "system",
            "language": "system",
            "readingSourceMode": "preferred"
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
          "update": { "autoUpdateEnabled": false },
          "control": { "enabled": true, "reassertionIntervalSeconds": 120 },
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
