using ResourceManager.App.Application.Settings;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Hosting.StartupCapabilities;

namespace ResourceManager.App.Endpoints;

public static partial class ResourceManagerEndpointRouteBuilderExtensions
{
    private static IEndpointRouteBuilder MapSettingsEndpoints(this IEndpointRouteBuilder app, StartupCapabilitySet startupCapabilities)
    {
        MapSettingsApplicationRequests(app, startupCapabilities);

        // Query projection: prefer published effective parameters. Before publication,
        // Resolve(saved settings) is a settings preview, not published/deployed state.
        // Wire "source" only distinguishes tier/user values, not this provenance.
        // S-tier owns the eventual single explicit initial-value/parameter contract.
        // The separate runtime snapshot below describes CLR running/next-start values.
        app.MapGet("/api/settings/memory-cpu-tier", async (
            HttpContext context,
            IAppSettingsStore settingsStore,
            CancellationToken cancellationToken) =>
        {
            var services = context.RequestServices;
            var authority = services.GetService<ResourceManager.Shared.BusinessBlocks.State.StateAuthority>();
            var published = authority?.Read<MemoryCpuTierResolution>(ResourceManager.App.StateItems.StateItemUids.MemoryCpuTierEffective);
            var resolution = Transport.NativeMemoryCpuTierResponse.UsePublished(published is { HasValue: true }) ? published!.Value.Value : MemoryCpuTierResolver.Resolve(
                ResourceManager.App.Infrastructure.Settings.MemoryCpuTierTable.Product,
                (await settingsStore.LoadReadOnlyAsync(cancellationToken)).Settings.MemoryCpuTier);
            var writer = services.GetService<ResourceManager.App.Infrastructure.Settings.RuntimeConfigWriter>();
            await Transport.NativeMemoryCpuTierResponse.Send(context, resolution, writer?.Read());
        }).AllowAnonymous();

        return app;
    }
}