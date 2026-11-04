using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Domain.Settings;

namespace ResourceManager.App.Application.Settings;

public static class AppSettingsRuntimeCapabilityProjector
{
    public static AppSettings Project(
        AppSettings settings,
        RuntimeExecutionCapabilityPolicy capabilities)
        => Evaluate(settings, capabilities).Settings;

    public static AppSettingsRuntimeProjection Evaluate(
        AppSettings settings,
        RuntimeExecutionCapabilityPolicy capabilities)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(capabilities);

        var constrainedPaths = new List<string>();
        var performance = settings.Performance;
        var debug = settings.Debug;
        if (!capabilities.OptimizationRuntimeEnabled)
        {
            if (!string.Equals(
                    performance.OptimizationMode,
                    AppOptimizationModes.Normal,
                    StringComparison.OrdinalIgnoreCase))
            {
                constrainedPaths.Add("performance.optimizationMode");
            }
            if (debug.HostManagerSmartCoordinatorScoreOnlyEnabled)
            {
                constrainedPaths.Add(
                    "debug.hostManagerSmartCoordinatorScoreOnlyEnabled");
            }
            if (debug.HostManagerSmartCoordinatorPerformanceLogEnabled)
            {
                constrainedPaths.Add(
                    "debug.hostManagerSmartCoordinatorPerformanceLogEnabled");
            }
            performance = performance with
            {
                OptimizationMode = AppOptimizationModes.Normal
            };
            debug = debug with
            {
                HostManagerSmartCoordinatorScoreOnlyEnabled = false,
                HostManagerSmartCoordinatorPerformanceLogEnabled = false
            };
        }

        if (!capabilities.PreciseGpuPlacementEnabled)
        {
            if (performance.PreciseGpuPlacementEnabled)
            {
                constrainedPaths.Add(
                    "performance.preciseGpuPlacementEnabled");
            }
            performance = performance with
            {
                PreciseGpuPlacementEnabled = false
            };
        }

        var publicService = settings.PublicService;
        var aiModelService = settings.AiModelService;
        if (!capabilities.PublicServiceCoordinationEnabled)
        {
            if (publicService.Enabled)
            {
                constrainedPaths.Add("publicService.enabled");
            }
            if (publicService.FileIndexEnabled)
            {
                constrainedPaths.Add("publicService.fileIndexEnabled");
            }
            if (publicService.DatabaseServiceEnabled)
            {
                constrainedPaths.Add("publicService.databaseServiceEnabled");
            }
            if (publicService.AiModelCatalogEnabled)
            {
                constrainedPaths.Add("publicService.aiModelCatalogEnabled");
            }
            if (aiModelService.AutoStartEnabled)
            {
                constrainedPaths.Add("aiModelService.autoStartEnabled");
            }
            publicService = publicService with
            {
                Enabled = false,
                FileIndexEnabled = false,
                DatabaseServiceEnabled = false,
                AiModelCatalogEnabled = false
            };
            aiModelService = aiModelService with
            {
                AutoStartEnabled = false
            };
        }

        return new AppSettingsRuntimeProjection(
            settings with
            {
                Performance = performance,
                Debug = debug,
                PublicService = publicService,
                AiModelService = aiModelService
            },
            constrainedPaths.ToArray());
    }
}

public sealed record AppSettingsRuntimeProjection(
    AppSettings Settings,
    IReadOnlyList<string> ConstrainedPaths)
{
    public bool HasConstraints => ConstrainedPaths.Count > 0;
}
