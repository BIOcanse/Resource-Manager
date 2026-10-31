using ResourceManager.App.Application.Components;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Application.Monitoring;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Application.Settings;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Hosting.StartupCapabilities;
using ResourceManager.App.Infrastructure.ResourceBreakdown;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.Settings;
using ResourceManager.Shared.Runtime;

namespace ResourceManager.App.Endpoints;

public static partial class ResourceManagerEndpointRouteBuilderExtensions
{
    internal static IEndpointRouteBuilder MapPerformanceOverlayEndpoints(this IEndpointRouteBuilder app, StartupCapabilitySet startupCapabilities)
    {
        app.MapGet("/api/performance-overlay/metrics/catalog", async (
            HardwareMetricCatalogState catalog,
            IComponentManager componentManager,
            CancellationToken cancellationToken) =>
            Results.Ok(MetricCatalog.WithInstalledComponents(
                MetricCatalog.ForPerformanceOverlay(catalog.Current),
                await InstalledComponentIdsAsync(componentManager, cancellationToken))))
            .AllowAnonymous();

        // One software's overlay as the backend settings hold it: the stored entry, or the declared default
        // entry (switch off) when none is stored. Writes go through PATCH /api/settings/app with the revision.
        app.MapGet("/api/performance-overlay/software/{softwareId}", async (
            string softwareId,
            IAppSettingsStore settingsStore,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(softwareId)) return Results.BadRequest();
            var current = await settingsStore.LoadReadOnlyAsync(cancellationToken);
            var stored = PerformanceOverlayToggle.Find(current.Settings.PerformanceOverlay, softwareId.Trim());
            return Results.Ok(new PerformanceOverlaySoftwareView(stored?.Key ?? softwareId.Trim(), stored is not null,
                current.Revision, stored?.Value ?? (AppSettingsDefaults.PerformanceOverlay.Entry() with { Enabled = false }),
                PerformanceOverlayEditingConstraints.FromDefaults(AppSettingsDefaults.PerformanceOverlay)));
        }).AllowAnonymous();

        // The attributable software of the console session's foreground window, or of the named process, with
        // its current switch: the resident's tray item reads it for the window that was foreground before the
        // tray took the focus.
        app.MapGet("/api/performance-overlay/foreground", async (
            int? processId,
            long? processStartKey,
            HttpContext context,
            IAppSettingsStore settingsStore,
            CancellationToken cancellationToken) =>
        {
            if (processId is <= 0 || processStartKey is < 0 || processId is null && processStartKey is not null)
                return Results.BadRequest();
            var resolved = await ResolveOverlaySoftwareAsync(context.RequestServices, processId, processStartKey, cancellationToken);
            if (resolved.SoftwareId is null)
                return Results.Json(new PerformanceOverlayRejection(resolved.Reason!), statusCode: StatusCodes.Status409Conflict);
            var current = await settingsStore.LoadReadOnlyAsync(cancellationToken);
            var stored = PerformanceOverlayToggle.Find(current.Settings.PerformanceOverlay, resolved.SoftwareId);
            return Results.Ok(new PerformanceOverlayForegroundView(stored?.Key ?? resolved.SoftwareId, resolved.SoftwareName!,
                stored?.Value.Enabled ?? false));
        }).AllowAnonymous();

        if (startupCapabilities.Allows(StartupCapability.MutablePersistence))
        app.MapPost("/api/performance-overlay/toggle", async (
            PerformanceOverlayToggleRequest? request,
            HttpContext context,
            IAppSettingsStore settingsStore,
            IRuntimeSpecializationCoordinator runtimeSpecialization,
            CancellationToken cancellationToken) =>
        {
            if (request?.SoftwareId is { } id && string.IsNullOrWhiteSpace(id)) return Results.BadRequest();
            var (receipt, rejection) = await PerformanceOverlayToggle.ToggleAsync(request,
                token => ResolveOverlaySoftwareAsync(context.RequestServices, null, null, token),
                settingsStore.LoadReadOnlyAsync,
                (patch, token) => runtimeSpecialization.ApplyAppSettingsPatchAsync(patch, "performance-overlay-toggled", token),
                cancellationToken);
            return receipt is not null
                ? Results.Json(receipt, statusCode: receipt.RuntimeApplicationDisposition is "savedNotApplied"
                    or AppSettingsRuntimeApplicationDisposition.CommittedWithDeliveryFailures
                        ? StatusCodes.Status202Accepted : StatusCodes.Status200OK)
                : Results.Json(new PerformanceOverlayRejection(rejection!), statusCode: StatusCodes.Status409Conflict);
        });

        return app;
    }

    // The foreground (or the named process) to its software: the active console session's observation from
    // the selected desktop publisher, then the backend's current inventory and attribution publication.
    // Read (event) first; only an unavailable publication uses the shared one-shot sampling chain. No polling.
    private static async Task<ForegroundSoftwareResolution> ResolveOverlaySoftwareAsync(IServiceProvider services,
        int? processId, long? processStartKey, CancellationToken cancellationToken)
    {
        if (processId is null)
        {
            var desktops = services.GetService<InteractiveDesktopObservationStore>();
            var session = unchecked((int)ResourceManager.App.Infrastructure.Windows.NativeMethods.WTSGetActiveConsoleSessionId());
            if (desktops?.ReadForeground(session) is not { } observed)
                return ForegroundSoftwareResolution.Rejected(PerformanceOverlayRejections.NoDesktopObservation);
            if (!observed.Known || observed.Foreground is null)
                return ForegroundSoftwareResolution.Rejected(PerformanceOverlayRejections.NoForegroundWindow);
            processId = observed.Foreground.ProcessId;
            processStartKey = observed.Foreground.CreationFileTimeUtc == DesktopProcessWindowObservation.UnverifiedCreationTime
                ? null : observed.Foreground.CreationFileTimeUtc;
        }
        var source = services.GetRequiredService<ISchedulingProcessFactObservationSource>();
        var published = PerformanceOverlayToggle.ResolvePublishedSoftware(source, processId.Value, processStartKey);
        if (published.Reason != PerformanceOverlayRejections.ProcessInventoryUnavailable) return published;
        // The read-only startup profile can omit the push source; it still exposes published data and reasons.
        if (services.GetService<IResourceBreakdownPushSource>() is not { } resources) return published;
        var request = new ResourceBreakdownSampleRequest([], new Dictionary<string, string>(), ProcessSampleDetailLevel.ResourceTableBasic)
        { PublicationDatasetIds = [SamplingDatasetIds.ProcessInventory, SamplingDatasetIds.ProcessAttribution] };
        if (await resources.FetchOnceAsync("performance-overlay:foreground", request, cancellationToken) is null) return published;
        return PerformanceOverlayToggle.ResolvePublishedSoftware(source, processId.Value, processStartKey);
    }
}
