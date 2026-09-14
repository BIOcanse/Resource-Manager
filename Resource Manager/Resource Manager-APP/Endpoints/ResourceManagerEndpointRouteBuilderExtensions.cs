using ResourceManager.App.Infrastructure.Security;
using ResourceManager.App.Hosting.StartupCapabilities;

namespace ResourceManager.App.Endpoints;

public static partial class ResourceManagerEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapResourceManagerEndpoints(
        this IEndpointRouteBuilder app,
        LoopbackApiPipelineMode loopbackApiPipelineMode,
        StartupCapabilitySet startupCapabilities)
    {
        ArgumentNullException.ThrowIfNull(startupCapabilities);
        app.MapRuntimeIdentityEndpoints();
        app.MapPublicResourceEndpoints();
        if (startupCapabilities.Allows(
                StartupCapability.PublicServiceCoordination))
        {
            app.MapPublicServiceEndpoints();
            app.MapPublicSqliteEndpoints();
            app.MapPublicAiModelEndpoints();
            app.MapAiGatewayCredentialEndpoints();
        }
        app.MapMetricsEndpoints();
        app.MapSubscriptionChannelEndpoints();
        app.MapSystemEndpoints(startupCapabilities);
        app.MapDebugEndpoints();
        app.MapSettingsEndpoints();
        app.MapResourceMonitorEndpoints();
        app.MapFrontendRuntimeStateEndpoints();
        app.MapAdapterEndpoints(startupCapabilities);
        if (startupCapabilities.Allows(StartupCapability.RuntimeEffectOwners))
        {
            app.MapOperationEndpoints();
            app.MapMigrationEndpoints();
        }
        if (startupCapabilities.Allows(StartupCapability.OptimizationRuntime))
        {
            app.MapOptimizationEndpoints(loopbackApiPipelineMode);
        }
        app.MapComponentEndpoints(startupCapabilities);
        app.MapBrowserRuntimeEndpoints();
        app.MapSoftwareMetadataEndpoints();
        if (startupCapabilities.Allows(StartupCapability.MutablePersistence))
        {
            app.MapSoftwareEndpoints();
            app.MapFileIndexEndpoints();
        }
        if (startupCapabilities.Allows(
                StartupCapability.GpuLaunchInterceptionReconciliation))
        {
            app.MapGpuPlacementEndpoints();
        }
        app.MapDependencyEndpoints(startupCapabilities);
        app.MapDeviceTopologyEndpoints();
        return app;
    }
}
