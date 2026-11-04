using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Application.Software;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Infrastructure.GpuPlacement;
using ResourceManager.Shared.Packages;
using ResourceManager.App.Hosting.StartupCapabilities;

namespace ResourceManager.App.Endpoints;

public static partial class ResourceManagerEndpointRouteBuilderExtensions
{
    private static IEndpointRouteBuilder MapStartupModuleEndpoints(this IEndpointRouteBuilder app,
        StartupCapabilitySet startupCapabilities)
    {
        app.MapGet("/api/startup-modules/catalog", (PackageRuntimeCatalog catalog) => Results.Ok(catalog.StartupModules));
        app.MapGet("/api/startup-modules/settings", async (IStartupModuleSettingsStore store, CancellationToken token) =>
            Results.Ok(await store.GetAsync(token)));
        app.MapGet("/api/startup-modules/software/{softwareId}", async (string softwareId,
            IStartupModuleSettingsStore store, CancellationToken token) =>
            Results.Ok((await store.GetAsync(token)).Software.FirstOrDefault(item => item.SoftwareId.Equals(softwareId,
                StringComparison.OrdinalIgnoreCase)) ?? new StartupModuleSelection(softwareId, [])));
        if (startupCapabilities.Allows(StartupCapability.GpuLaunchInterceptionReconciliation))
        {
            app.MapPut("/api/startup-modules/software/{softwareId}", async (string softwareId, StartupModuleSelection selection,
                IStartupModuleSettingsStore store, ISoftwareRegistryView registry, GpuLaunchInterceptionReconciler reconciler,
                CancellationToken token) =>
            {
                try
                {
                    if (!(await registry.GetSoftwareAsync(token)).Any(item => item.Id.Equals(softwareId, StringComparison.OrdinalIgnoreCase)))
                        return Results.BadRequest(new EndpointCodePayload("startup-modules.unknown-software"));
                    var saved = await store.SaveAsync(selection with { SoftwareId = softwareId }, token);
                    await reconciler.ReconcileAsync(CancellationToken.None);
                    return Results.Ok(saved);
                }
                catch (ArgumentException) { return Results.BadRequest(new EndpointCodePayload("startup-modules.invalid-selection")); }
            });
        }
        return app;
    }
}
