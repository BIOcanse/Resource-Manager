using ResourceManager.App.Application.Settings;
using ResourceManager.App.Domain.Settings;

namespace Resource_Manager_APP.Tests;

public sealed class AppSettingsNormalizerTests
{
    [Fact]
    public void RealtimeActionBudget_DefaultsToFixedCountWithStoredPercent()
    {
        var performance = AppSettingsDefaults.Create().Performance;

        Assert.Equal(AppRealtimeActionBudgetModes.Fixed, performance.RealtimeActionBudgetMode);
        Assert.Equal(32, performance.RealtimeActionBudgetCount);
        Assert.Equal(5, performance.RealtimeActionBudgetPercent);
    }

    [Theory]
    [InlineData("fixed", 0, 101, "fixed", 1, 100)]
    [InlineData("percent", 8193, 0, "percent", 8192, 1)]
    [InlineData("fixed", 8192, 1, "fixed", 8192, 1)]
    [InlineData("percent", 1, 100, "percent", 1, 100)]
    [InlineData("invalid", 64, 25, "fixed", 64, 25)]
    [InlineData("Percent", 64, 25, "fixed", 64, 25)]
    [InlineData(null, 64, 25, "fixed", 64, 25)]
    public void RealtimeActionBudget_NormalizesBothValuesIndependentlyOfMode(
        string? mode, int count, int percent, string expectedMode, int expectedCount, int expectedPercent)
    {
        var defaults = AppSettingsDefaults.Create();
        var normalized = AppSettingsNormalizer.Normalize(defaults with
        {
            Performance = defaults.Performance with
            {
                RealtimeActionBudgetMode = mode!,
                RealtimeActionBudgetCount = count,
                RealtimeActionBudgetPercent = percent
            }
        });

        Assert.Equal(expectedMode, normalized.Performance.RealtimeActionBudgetMode);
        Assert.Equal(expectedCount, normalized.Performance.RealtimeActionBudgetCount);
        Assert.Equal(expectedPercent, normalized.Performance.RealtimeActionBudgetPercent);
    }

    [Theory]
    [InlineData(0, 255, 1, 100)]
    [InlineData(100, 1, 100, 1)]
    [InlineData(95, 80, 95, 80)]
    public void GpuOverflowThresholds_ClampToValidPolicyPercentages(byte usage, byte memory, int expectedUsage, int expectedMemory)
    {
        var defaults = AppSettingsDefaults.Create();
        var actual = AppSettingsNormalizer.Normalize(defaults with
        {
            Performance = defaults.Performance with { GpuOverflowUsagePercent = usage, GpuOverflowDedicatedMemoryPercent = memory }
        });
        Assert.Equal(expectedUsage, actual.Performance.GpuOverflowUsagePercent);
        Assert.Equal(expectedMemory, actual.Performance.GpuOverflowDedicatedMemoryPercent);
    }

    [Fact]
    public void MissingSections_DoNotOptIntoOptionalServices()
    {
        var settings = AppSettingsNormalizer.Normalize(AppSettingsDefaults.Create() with
        {
            Performance = null!, PublicService = null!, AiModelService = null!, SystemIntegration = null!
        });
        Assert.False(settings.Performance.PreciseGpuPlacementEnabled);
        Assert.Equal(AppRealtimeActionBudgetModes.Fixed, settings.Performance.RealtimeActionBudgetMode);
        Assert.Equal(32, settings.Performance.RealtimeActionBudgetCount);
        Assert.Equal(5, settings.Performance.RealtimeActionBudgetPercent);
        Assert.False(settings.PublicService.Enabled);
        Assert.False(settings.PublicService.FileIndexEnabled);
        Assert.False(settings.PublicService.DatabaseServiceEnabled);
        Assert.False(settings.PublicService.AiModelCatalogEnabled);
        Assert.False(settings.AiModelService.AutoStartEnabled);
        Assert.False(settings.SystemIntegration.AutoStartEnabled);
    }

    [Fact]
    public void FontSmoothingDisabled_IsPreserved()
    {
        var defaults = AppSettingsDefaults.Create();
        var settings = defaults with
        {
            Appearance = defaults.Appearance with
            {
                FontSmoothing = AppFontSmoothingModes.Disabled
            }
        };

        var normalized = AppSettingsNormalizer.Normalize(settings);

        Assert.Equal(AppFontSmoothingModes.Disabled, normalized.Appearance.FontSmoothing);
    }

    [Fact]
    public void AutomaticCleanupLines_AreNormalizedIndependentlyFromGpuOverflow()
    {
        var defaults = AppSettingsDefaults.Create();
        var settings = defaults with
        {
            Performance = defaults.Performance with
            {
                GpuOverflowUsagePercent = 20,
                GpuOverflowDedicatedMemoryPercent = 24,
                PhysicalMemoryAutomaticCleanupPercent = 5,
                VirtualMemoryAutomaticCleanupPercent = 7
            }
        };

        var normalized = AppSettingsNormalizer.Normalize(settings);

        Assert.Equal(20, normalized.Performance.GpuOverflowUsagePercent);
        Assert.Equal(24, normalized.Performance.GpuOverflowDedicatedMemoryPercent);
        Assert.Equal(5, normalized.Performance.PhysicalMemoryAutomaticCleanupPercent);
        Assert.Equal(7, normalized.Performance.VirtualMemoryAutomaticCleanupPercent);
    }

    [Fact]
    public void OptimizationTargetUsageLines_AreNormalizedIndependently()
    {
        var defaults = AppSettingsDefaults.Create();
        var settings = defaults with
        {
            Performance = defaults.Performance with
            {
                PhysicalMemoryOptimizationTargetUsagePercent = 1,
                VirtualMemoryOptimizationTargetUsagePercent = 100
            }
        };

        var normalized = AppSettingsNormalizer.Normalize(settings);

        Assert.Equal(5, normalized.Performance.PhysicalMemoryOptimizationTargetUsagePercent);
        Assert.Equal(95, normalized.Performance.VirtualMemoryOptimizationTargetUsagePercent);
    }

    [Fact]
    public void EditableHotkeyEncoding_RemovesGapsDuplicatesAndInvalidKeys()
    {
        var normalized = AppSettingsNormalizer.NormalizeHotkeyEncoding(
            [17, 1, 0, 0, 17, 1, 46]);

        Assert.Equal([17, 1, 46, 0, 0, 0, 0], normalized);
    }

    [Fact]
    public void EditableHotkeyWithoutKeys_IsDisabled()
    {
        var defaults = AppSettingsDefaults.Create();
        var settings = defaults with
        {
            SystemIntegration = defaults.SystemIntegration with
            {
                Hotkeys =
                [
                    new AppEditableHotkeySettings(
                        AppSystemIntegrationActionIds.ForceTerminateUnresponsiveAndForeground,
                        Enabled: true,
                        Encoding: [0, 0, 0, 0, 0, 0, 0])
                ]
            }
        };

        var normalized = AppSettingsNormalizer.Normalize(settings);

        Assert.False(normalized.SystemIntegration.Hotkeys.Single().Enabled);
    }

    [Fact]
    public void DestructiveEditableHotkey_WithSingleOrdinaryKey_IsDisabled()
    {
        var defaults = AppSettingsDefaults.Create();
        var settings = defaults with
        {
            SystemIntegration = defaults.SystemIntegration with
            {
                Hotkeys =
                [
                    new AppEditableHotkeySettings(
                        AppSystemIntegrationActionIds.ForceTerminateUnresponsiveAndForeground,
                        Enabled: true,
                        Encoding: [65, 0, 0, 0, 0, 0, 0])
                ]
            }
        };

        var normalized = AppSettingsNormalizer.Normalize(settings);

        Assert.False(normalized.SystemIntegration.Hotkeys.Single().Enabled);
        Assert.False(AppSettingsHotkeySafetyValidator.AreEnabledDestructiveHotkeysSafe(settings));
    }

    [Fact]
    public void DestructiveEditableHotkey_WithApprovedModifierAndTrigger_IsPreserved()
    {
        var defaults = AppSettingsDefaults.Create();
        var settings = defaults with
        {
            SystemIntegration = defaults.SystemIntegration with
            {
                Hotkeys =
                [
                    new AppEditableHotkeySettings(
                        AppSystemIntegrationActionIds.ForceTerminateUnresponsiveAndForeground,
                        Enabled: true,
                        Encoding: [17, 0, 46, 0, 0, 0, 0])
                ]
            }
        };

        var normalized = AppSettingsNormalizer.Normalize(settings);

        Assert.True(normalized.SystemIntegration.Hotkeys.Single().Enabled);
        Assert.True(AppSettingsHotkeySafetyValidator.AreEnabledDestructiveHotkeysSafe(settings));
    }

    [Fact]
    public void PublicService_PreservesIndependentCapabilityChoices()
    {
        var defaults = AppSettingsDefaults.Create();
        var settings = defaults with
        {
            PublicService = new AppLocalPublicServiceSettings(
                Enabled: true,
                FileIndexEnabled: false,
                DatabaseServiceEnabled: false,
                AiModelCatalogEnabled: true)
        };

        var normalized = AppSettingsNormalizer.Normalize(settings);

        Assert.True(normalized.PublicService.Enabled);
        Assert.False(normalized.PublicService.FileIndexEnabled);
        Assert.False(normalized.PublicService.DatabaseServiceEnabled);
        Assert.True(normalized.PublicService.AiModelCatalogEnabled);
    }
}
