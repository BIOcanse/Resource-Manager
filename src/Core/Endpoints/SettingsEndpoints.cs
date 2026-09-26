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

namespace ResourceManager.App.Endpoints;

public static partial class ResourceManagerEndpointRouteBuilderExtensions
{
    private static IEndpointRouteBuilder MapSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/settings/app", async (
            IAppSettingsStore settingsStore,
            CancellationToken cancellationToken) =>
        {
            return Results.Ok(await settingsStore.LoadReadOnlyAsync(cancellationToken));
        }).AllowAnonymous();

        app.MapPut("/api/settings/app", async (
            AppSettings settings,
            IRuntimeSpecializationCoordinator runtimeSpecialization,
            CancellationToken cancellationToken) =>
        {
            var result = await runtimeSpecialization.ApplyAppSettingsAsync(
                settings,
                "app-settings-saved",
                cancellationToken);
            return result.RuntimeApplicationDisposition switch
            {
                "committedAndApplied" => Results.Ok(result),
                "committedWithCapabilityConstraints" => Results.Ok(result),
                "committedWithDeliveryFailures" => Results.Json(
                    result,
                    statusCode: StatusCodes.Status202Accepted),
                "savedNotApplied" => Results.Json(
                    result,
                    statusCode: StatusCodes.Status202Accepted),
                _ => Results.Json(
                    result,
                    statusCode: StatusCodes.Status409Conflict)
            };
        });

        app.MapPatch("/api/settings/app", async (
            AppSettingsPatchRequest patch,
            IRuntimeSpecializationCoordinator runtimeSpecialization,
            CancellationToken cancellationToken) =>
        {
            AppSettingsUpdateResult result;
            try
            {
                result = await runtimeSpecialization.ApplyAppSettingsPatchAsync(
                    patch,
                    "app-settings-patched",
                    cancellationToken);
            }
            catch (AppSettingsPatchException exception)
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Invalid application settings patch",
                    detail: exception.Message);
            }

            return result.RuntimeApplicationDisposition switch
            {
                "committedAndApplied" => Results.Ok(result),
                "committedWithCapabilityConstraints" => Results.Ok(result),
                "committedWithDeliveryFailures" => Results.Json(
                    result,
                    statusCode: StatusCodes.Status202Accepted),
                "savedNotApplied" => Results.Json(
                    result,
                    statusCode: StatusCodes.Status202Accepted),
                _ => Results.Json(
                    result,
                    statusCode: StatusCodes.Status409Conflict)
            };
        });

        app.MapPost("/api/settings/app/reapply", async (
            IAppSettingsStore settingsStore,
            IRuntimeSpecializationCoordinator runtimeSpecialization,
            CancellationToken cancellationToken) =>
        {
            var publication = await runtimeSpecialization.RebuildAsync(
                "app-settings-reapply",
                cancellationToken);
            var current = await settingsStore.LoadReadOnlyAsync(cancellationToken);
            var result = current with
            {
                RuntimeApplicationDisposition =
                    AppSettingsRuntimeApplicationDisposition.ResolvePublished(
                        publication.HasDeliveryFailures,
                        publication.Plan.RuntimeCapabilityConstrainedPaths),
                RuntimePlanVersion = publication.Plan.Version,
                RuntimePublicationSequence = publication.PublicationSequence,
                RuntimeDeliveryFailureCount = publication.DeliveryFailures.Count,
                RuntimeCapabilityConstrainedPaths =
                    publication.Plan.RuntimeCapabilityConstrainedPaths
            };
            return publication.HasDeliveryFailures
                ? Results.Json(result, statusCode: StatusCodes.Status202Accepted)
                : Results.Ok(result);
        });

        app.MapGet("/api/settings/dashboard", async (
            IDashboardSettingsStore settingsStore,
            DashboardSettingsMigrator settingsMigrator,
            IMetricSampler sampler,
            CancellationToken cancellationToken) =>
        {
            var result = await settingsStore.LoadReadOnlyAsync(cancellationToken);
            var snapshot = await TryGetDashboardMetricSnapshotAsync(sampler, cancellationToken);
            var settings = settingsMigrator.ResolveForRead(result, snapshot);

            return Results.Ok(result with { Settings = settings });
        }).AllowAnonymous();

        app.MapPut("/api/settings/dashboard", async (
            DashboardSettings settings,
            IDashboardSettingsStore settingsStore,
            DashboardSettingsMigrator settingsMigrator,
            IMetricSampler sampler,
            IRuntimeSpecializationCoordinator runtimeSpecialization,
            CancellationToken cancellationToken) =>
        {
            var snapshot = await TryGetDashboardMetricSnapshotAsync(sampler, cancellationToken);
            var sanitized = snapshot is not null
                ? settingsMigrator.SanitizeForSave(settings, snapshot)
                : settingsMigrator.SanitizeForSave(settings);
            var result = await settingsStore.SaveAsync(sanitized, cancellationToken);
            await runtimeSpecialization.RebuildAsync("dashboard-settings-saved", CancellationToken.None);
            return Results.Ok(result);
        });

        return app;
    }

    private static async Task<HardwareMetricSnapshot?> TryGetDashboardMetricSnapshotAsync(
        IMetricSampler sampler,
        CancellationToken cancellationToken)
    {
        try
        {
            return await sampler.GetSnapshotAsync(
                MetricSampleRequest.CatalogProbe,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }
}
