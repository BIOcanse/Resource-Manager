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
        app.MapTrustedAccessEndpoints();
        if (startupCapabilities.Allows(
                StartupCapability.PublicServiceCoordination))
        {
            app.MapPublicServiceEndpoints();
            app.MapPublicSqliteEndpoints();
            app.MapPublicAiModelEndpoints();
            app.MapAiGatewayCredentialEndpoints();
        }
        app.MapMetricsEndpoints();
        app.MapPerformanceOverlayEndpoints(startupCapabilities);
        app.MapStartupModuleEndpoints(startupCapabilities);
        app.MapSubscriptionChannelEndpoints();
        app.MapSystemEndpoints(startupCapabilities);
        app.MapDebugEndpoints();
        app.MapKernelLeakEndpoints();
        app.MapNotificationEndpoints();
        app.MapCrashReportEndpoints();
        app.MapSettingsEndpoints(startupCapabilities);
        if (startupCapabilities.Allows(StartupCapability.RuntimeEffectOwners)) app.MapHostMemoryProtectionEndpoints(startupCapabilities);
        app.MapUpdateEndpoints();
        app.MapResourceMonitorEndpoints();
        app.MapDiskUsageEndpoints(startupCapabilities);
        app.MapControlEndpoints(startupCapabilities);
        app.MapDebugAmdSmuEndpoint();
        app.MapDebugGpuControlEndpoint();
        app.MapAdapterEndpoints(startupCapabilities);
        if (startupCapabilities.Allows(StartupCapability.RuntimeEffectOwners))
        {
            app.MapOperationEndpoints();
            }
        if (startupCapabilities.Allows(StartupCapability.OptimizationRuntime))
        {
            app.MapOptimizationEndpoints(loopbackApiPipelineMode);
            app.MapCpuLeaseEndpoints();
        }
        app.MapComponentEndpoints(startupCapabilities);
        app.MapBrowserRuntimeEndpoints();
        app.MapSoftwareMetadataEndpoints();
        if (startupCapabilities.Allows(StartupCapability.MutablePersistence))
        {
            app.MapSoftwareEndpoints();
            app.MapFileIndexEndpoints();
            app.MapTargetedRecordingEndpoints();
        }
        if (startupCapabilities.Allows(
                StartupCapability.GpuLaunchInterceptionReconciliation))
        {
            app.MapGpuPlacementEndpoints();
        }
        app.MapDependencyEndpoints(startupCapabilities);
        app.MapDeviceTopologyEndpoints();
        app.MapApiNotFoundEndpoint();
        return app;
    }

    internal static void MapApiNotFoundEndpoint(this IEndpointRouteBuilder app) =>
        app.Map("/api/{**unmatchedPath}", () => Results.NotFound(new EndpointErrorPayload("api-endpoint-not-found")));
}
