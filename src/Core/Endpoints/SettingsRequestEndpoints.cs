using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Application.Settings;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Endpoints.Transport;
using ResourceManager.App.Hosting.StartupCapabilities;
using ResourceManager.App.Infrastructure.CrashReports;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.ServiceHosting;
using ResourceManager.App.Infrastructure.Settings;

namespace ResourceManager.App.Endpoints;

public static partial class ResourceManagerEndpointRouteBuilderExtensions
{
    // H1 removes these ASP.NET registrations / JSON sends. No settings policy
    // lives in this shell; pipe consumers use the same NativeSettingsRequests.
    private static void MapSettingsApplicationRequests(IEndpointRouteBuilder app, StartupCapabilitySet capabilities)
    {
        app.MapGet("/api/settings/app", async (IAppSettingsStore store, IRuntimeSpecializationCoordinator coordinator,
            ServiceRecoverySettings recovery, CrashDumpSettings dumps, CrashDumpKeySettings keys, CancellationToken token) =>
            SendSettingsResponse(await NativeSettingsRequests.InvokeAsync(NativeSettingsRequests.Operation.Read,
                store, coordinator, recovery, dumps, keys, token))).AllowAnonymous();
        app.MapGet("/api/settings/report-rules", (HttpContext context, IRuntimePlanProvider plans) =>
            NativeSettingsReportRulesResponse.Send(context, plans.Current.HostManager.ReportCoordinator.RuleSettings)).AllowAnonymous();
        if (!capabilities.Allows(StartupCapability.MutablePersistence)) return;
        app.MapPut("/api/settings/app", async (AppSettings settings, IAppSettingsStore store,
            IRuntimeSpecializationCoordinator coordinator, ServiceRecoverySettings recovery, CrashDumpSettings dumps,
            CrashDumpKeySettings keys, CancellationToken token) => SendSettingsResponse(await NativeSettingsRequests.InvokeAsync(
                NativeSettingsRequests.Operation.Write, store, coordinator, recovery, dumps, keys, token, settings: settings)));
        app.MapPatch("/api/settings/app", async (AppSettingsPatchRequest patch, IAppSettingsStore store,
            IRuntimeSpecializationCoordinator coordinator, ServiceRecoverySettings recovery, CrashDumpSettings dumps,
            CrashDumpKeySettings keys, CancellationToken token) => SendSettingsResponse(await NativeSettingsRequests.InvokeAsync(
                NativeSettingsRequests.Operation.Patch, store, coordinator, recovery, dumps, keys, token, patch: patch)));
        app.MapPost("/api/settings/app/reapply", async (IAppSettingsStore store, IRuntimeSpecializationCoordinator coordinator,
            ServiceRecoverySettings recovery, CrashDumpSettings dumps, CrashDumpKeySettings keys, CancellationToken token) =>
            SendSettingsResponse(await NativeSettingsRequests.InvokeAsync(NativeSettingsRequests.Operation.Reapply,
                store, coordinator, recovery, dumps, keys, token)));
    }

    private static IResult SendSettingsResponse(NativeSettingsRequests.Response response) => response.ProblemTitle is { } title
        ? Results.Problem(statusCode: response.Status, title: title, detail: response.ProblemDetail)
        : Results.Json((AppSettingsUpdateResult)response.Value!, statusCode: response.Status);
}
