using ResourceManager.App.Application.Adaptation;
using ResourceManager.App.Application.Components;
using ResourceManager.App.Application.Controlled;
using ResourceManager.App.Application.CpuTopology;
using ResourceManager.App.Application.Dependencies;
using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Application.LocalSystem;
using ResourceManager.App.Application.Migration;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Application.ResourceTable;
using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Application.Settings;
using ResourceManager.App.Application.Software;
using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Domain.Components;
using ResourceManager.App.Domain.Controlled;
using ResourceManager.App.Domain.Dependencies;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Domain.LocalSystem;
using ResourceManager.App.Domain.Migration;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Domain.ResourceTable;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Infrastructure.ResourceTable;
using ResourceManager.App.Hosting.StartupCapabilities;

namespace ResourceManager.App.Endpoints;

public static partial class ResourceManagerEndpointRouteBuilderExtensions
{
    private static IEndpointRouteBuilder MapAdapterEndpoints(
        this IEndpointRouteBuilder app,
        StartupCapabilitySet startupCapabilities)
    {
        app.MapGet("/api/controlled", (IControlledSoftwareRegistry registry) =>
        {
            return Results.Ok(registry.GetAll());
        }).AllowAnonymous();

        app.MapGet("/api/adapters", async (
            IAdapterSoftwareRegistry registry,
            CancellationToken cancellationToken) =>
        {
            return Results.Ok(await registry.GetAllAsync(cancellationToken));
        }).AllowAnonymous();

        if (!startupCapabilities.Allows(StartupCapability.MutablePersistence))
        {
            return app;
        }

        app.MapPost("/api/adapters/register", async (
            AdapterSoftwareRegistrationRequest request,
            IAdapterResourceMarkerProbe markerProbe,
            IAdapterSoftwareRegistry registry,
            IRuntimeSpecializationCoordinator runtimeSpecialization,
            CancellationToken cancellationToken) =>
        {
            var validation = AdapterRegistrationValidator.Validate(request);
            if (validation is not null)
            {
                return Results.BadRequest(new { error = validation });
            }

            var probeResult = await markerProbe.ProbeAsync(request.ResourceMarkerEndpoint!, cancellationToken);
            if (probeResult.State != AdapterResourceMarkerStates.Online)
            {
                return Results.BadRequest(new
                {
                    error = "Resource marker endpoint is not reachable.",
                    markerProbe = probeResult
                });
            }

            var result = await registry.RegisterAsync(request, probeResult, cancellationToken);
            await runtimeSpecialization.RebuildAsync("adapter-registered", CancellationToken.None);
            return result.Created
                ? Results.Created($"/api/adapters/{result.Registration.Id}", result.Registration)
                : Results.Ok(result.Registration);
        });

        app.MapDelete("/api/adapters/{id}", async (
            string id,
            IAdapterSoftwareRegistry registry,
            IRuntimeSpecializationCoordinator runtimeSpecialization,
            CancellationToken cancellationToken) =>
        {
            if (!await registry.RemoveAsync(id, cancellationToken))
            {
                return Results.NotFound();
            }

            await runtimeSpecialization.RebuildAsync("adapter-removed", CancellationToken.None);
            return Results.NoContent();
        });

        app.MapPost("/api/controlled/register", (ControlledSoftwareRegistrationRequest request, IControlledSoftwareRegistry registry) =>
        {
            var validation = ControlledRegistrationValidator.Validate(request);
            if (validation is not null)
            {
                return Results.BadRequest(new { error = validation });
            }

            var registration = registry.Register(request);
            return Results.Created($"/api/controlled/{registration.Id}", registration);
        });

        app.MapDelete("/api/controlled/{id:guid}", (Guid id, IControlledSoftwareRegistry registry) =>
        {
            return registry.Remove(id) ? Results.NoContent() : Results.NotFound();
        });

        return app;
    }
}
