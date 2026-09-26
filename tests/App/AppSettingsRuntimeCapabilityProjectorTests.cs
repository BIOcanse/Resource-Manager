using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Application.Settings;
using ResourceManager.App.Domain.Settings;

namespace Resource_Manager_APP.Tests;

public sealed class AppSettingsRuntimeCapabilityProjectorTests
{
    [Fact]
    public void ReadOnlyPolicyCannotBeElevatedByPersistedSettings()
    {
        var persisted = AppSettingsDefaults.Create() with
        {
            Performance = AppSettingsDefaults.Create().Performance with
            {
                OptimizationMode = AppOptimizationModes.Smart,
                PreciseGpuPlacementEnabled = true
            },
            Debug = AppSettingsDefaults.Create().Debug with
            {
                HostManagerSmartCoordinatorScoreOnlyEnabled = true,
                HostManagerSmartCoordinatorPerformanceLogEnabled = true
            },
            PublicService = new AppLocalPublicServiceSettings(true, true, true, true),
            AiModelService = AppSettingsDefaults.Create().AiModelService with
            {
                AutoStartEnabled = true
            }
        };

        var projection = AppSettingsRuntimeCapabilityProjector.Evaluate(
            persisted,
            new RuntimeExecutionCapabilityPolicy(false, false, false));
        var effective = projection.Settings;

        Assert.Equal(AppOptimizationModes.Normal, effective.Performance.OptimizationMode);
        Assert.False(effective.Performance.PreciseGpuPlacementEnabled);
        Assert.False(effective.Debug.HostManagerSmartCoordinatorScoreOnlyEnabled);
        Assert.False(effective.Debug.HostManagerSmartCoordinatorPerformanceLogEnabled);
        Assert.False(effective.PublicService.Enabled);
        Assert.False(effective.PublicService.FileIndexEnabled);
        Assert.False(effective.PublicService.DatabaseServiceEnabled);
        Assert.False(effective.PublicService.AiModelCatalogEnabled);
        Assert.False(effective.AiModelService.AutoStartEnabled);

        Assert.Equal(AppOptimizationModes.Smart, persisted.Performance.OptimizationMode);
        Assert.True(persisted.Performance.PreciseGpuPlacementEnabled);
        Assert.True(persisted.PublicService.Enabled);
        Assert.True(persisted.AiModelService.AutoStartEnabled);
        Assert.Equal(
            [
                "performance.optimizationMode",
                "debug.hostManagerSmartCoordinatorScoreOnlyEnabled",
                "debug.hostManagerSmartCoordinatorPerformanceLogEnabled",
                "performance.preciseGpuPlacementEnabled",
                "publicService.enabled",
                "publicService.fileIndexEnabled",
                "publicService.databaseServiceEnabled",
                "publicService.aiModelCatalogEnabled",
                "aiModelService.autoStartEnabled"
            ],
            projection.ConstrainedPaths);
    }

    [Fact]
    public void FullPolicyPreservesNormalizedRuntimeSettings()
    {
        var persisted = AppSettingsDefaults.Create() with
        {
            Performance = AppSettingsDefaults.Create().Performance with
            {
                OptimizationMode = AppOptimizationModes.MemoryOnly,
                PreciseGpuPlacementEnabled = true
            },
            PublicService = new AppLocalPublicServiceSettings(true, true, true, true),
            AiModelService = AppSettingsDefaults.Create().AiModelService with
            {
                AutoStartEnabled = true
            }
        };

        var effective = AppSettingsRuntimeCapabilityProjector.Project(
            persisted,
            new RuntimeExecutionCapabilityPolicy(true, true, true));

        Assert.Equal(persisted, effective);
    }

    [Fact]
    public void PublishedDispositionDoesNotClaimConstrainedSettingsWereApplied()
    {
        Assert.Equal(
            AppSettingsRuntimeApplicationDisposition.CommittedWithCapabilityConstraints,
            AppSettingsRuntimeApplicationDisposition.ResolvePublished(
                hasDeliveryFailures: false,
                ["performance.optimizationMode"]));
        Assert.Equal(
            AppSettingsRuntimeApplicationDisposition.CommittedAndApplied,
            AppSettingsRuntimeApplicationDisposition.ResolvePublished(
                hasDeliveryFailures: false,
                []));
        Assert.Equal(
            AppSettingsRuntimeApplicationDisposition.CommittedWithDeliveryFailures,
            AppSettingsRuntimeApplicationDisposition.ResolvePublished(
                hasDeliveryFailures: true,
                ["performance.optimizationMode"]));
    }
}
