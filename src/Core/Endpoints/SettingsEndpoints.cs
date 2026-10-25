using ResourceManager.App.Hosting.StartupCapabilities;
using ResourceManager.App.Application.Adaptation;
using ResourceManager.App.Application.Components;
using ResourceManager.App.Application.Controlled;
using ResourceManager.App.Application.CpuTopology;
using ResourceManager.App.Application.Dependencies;
using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Application.LocalSystem;
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
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Domain.ResourceTable;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Infrastructure.Optimization.Reports;
using ResourceManager.App.Infrastructure.ServiceHosting;

namespace ResourceManager.App.Endpoints;

public static partial class ResourceManagerEndpointRouteBuilderExtensions
{
    private static IEndpointRouteBuilder MapSettingsEndpoints(this IEndpointRouteBuilder app, StartupCapabilitySet startupCapabilities)
    {
        app.MapGet("/api/settings/app", async (
            IAppSettingsStore settingsStore,
            IRuntimeSpecializationCoordinator runtimeSpecialization,
            ServiceRecoverySettings recovery,
            CancellationToken cancellationToken) =>
        {
            return Results.Ok(runtimeSpecialization.ProjectAppSettingsRuntimeState(
                await settingsStore.LoadReadOnlyAsync(cancellationToken)) with { ServiceRecovery = recovery.Diagnostics });
        }).AllowAnonymous();

        // What the 「报告判定」 settings section may edit, from the current runtime plan.
        app.MapGet("/api/settings/report-rules", (IRuntimePlanProvider plans) =>
            Results.Ok(new EditableReportRulesPayload(plans.Current.HostManager.ReportCoordinator.RuleSettings.Select(static rule => new EditableReportRulePayload(rule.Name, HostManagerReportPresentation.ReportType(rule.FactKind), rule.Comparison == 1 ? "atOrAbove" : "atOrBelow", rule.DisplayUnit, rule.Scale, rule.Step, rule.ThresholdMinimum, rule.ThresholdMaximum, rule.CountMaximum, rule.ZeroActivityMaximum, rule.EditableFields, rule.Defaults))))).AllowAnonymous();

        if (startupCapabilities.Allows(StartupCapability.MutablePersistence))
        app.MapPut("/api/settings/app", async (
            AppSettings settings,
            IRuntimeSpecializationCoordinator runtimeSpecialization,
            ServiceRecoverySettings recovery,
            CancellationToken cancellationToken) =>
        {
            var result = await runtimeSpecialization.ApplyAppSettingsAsync(
                settings,
                "app-settings-saved",
                cancellationToken);
            result = result with { ServiceRecovery = recovery.Diagnostics };
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

        if (startupCapabilities.Allows(StartupCapability.MutablePersistence))
        app.MapPatch("/api/settings/app", async (
            AppSettingsPatchRequest patch,
            IRuntimeSpecializationCoordinator runtimeSpecialization,
            ServiceRecoverySettings recovery,
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

            result = result with { ServiceRecovery = recovery.Diagnostics };
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

        if (startupCapabilities.Allows(StartupCapability.MutablePersistence))
        app.MapPost("/api/settings/app/reapply", async (
            IAppSettingsStore settingsStore,
            IRuntimeSpecializationCoordinator runtimeSpecialization,
            ServiceRecoverySettings recovery,
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
                ServiceRecovery = recovery.Diagnostics,
                RuntimeDeliveryFailureCount = publication.DeliveryFailures.Count,
                RuntimeCapabilityConstrainedPaths =
                    publication.Plan.RuntimeCapabilityConstrainedPaths
            };
            return publication.HasDeliveryFailures
                ? Results.Json(result, statusCode: StatusCodes.Status202Accepted)
                : Results.Ok(result);
        });

        return app;
    }

}
