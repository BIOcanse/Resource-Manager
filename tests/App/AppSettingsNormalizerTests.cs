using ResourceManager.App.Application.Settings;
using ResourceManager.App.Domain.Settings;

namespace Resource_Manager_APP.Tests;

public sealed class AppSettingsNormalizerTests
{
    [Fact]
    public void MissingSections_DoNotOptIntoOptionalServices()
    {
        var settings = AppSettingsNormalizer.Normalize(AppSettingsDefaults.Create() with
        {
            Performance = null!, PublicService = null!, AiModelService = null!, SystemIntegration = null!
        });
        Assert.True(settings.Performance.PreciseGpuPlacementEnabled);
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
    public void AutomaticCleanupLines_AreNormalizedIndependentlyFromMoveDownChecks()
    {
        var defaults = AppSettingsDefaults.Create();
        var settings = defaults with
        {
            Performance = defaults.Performance with
            {
                VramMoveDownPhysicalMemoryDangerPercent = 20,
                PhysicalMemoryMoveDownVirtualMemoryDangerPercent = 24,
                PhysicalMemoryAutomaticCleanupPercent = 5,
                VirtualMemoryAutomaticCleanupPercent = 7
            }
        };

        var normalized = AppSettingsNormalizer.Normalize(settings);

        Assert.Equal(20, normalized.Performance.VramMoveDownPhysicalMemoryDangerPercent);
        Assert.Equal(24, normalized.Performance.PhysicalMemoryMoveDownVirtualMemoryDangerPercent);
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
