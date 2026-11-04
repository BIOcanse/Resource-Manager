using ResourceManager.App.Application.CrashReports;
using ResourceManager.App.Endpoints.Transport;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Endpoints;

// Crash report endpoints. The backend never opens anything: it returns paths, and the window process
// selects the file when the user clicks.
public static partial class ResourceManagerEndpointRouteBuilderExtensions
{
    internal static IEndpointRouteBuilder MapCrashReportEndpoints(this IEndpointRouteBuilder app)
    {
        // H1 removes this ASP.NET binding; the original-owner bridge retires in R-crash.
        app.MapGet("/api/crash-reports", (HttpContext context, ICrashReportOwner owner) =>
            NativeCrashReportResponse.Send(context, NativeCrashReportCapabilities.Invoke(owner, NativeCrashReportCapabilities.Operation.Read)));
        app.MapPost("/api/crash-reports/{id}/package", (HttpContext context, string id, ICrashReportOwner owner) =>
            NativeCrashReportResponse.Send(context, NativeCrashReportCapabilities.Invoke(owner, NativeCrashReportCapabilities.Operation.Package, id)));
        app.MapPost("/api/crash-reports/{id}/acknowledge", (HttpContext context, string id, ICrashReportOwner owner) =>
            NativeCrashReportResponse.Send(context, NativeCrashReportCapabilities.Invoke(owner, NativeCrashReportCapabilities.Operation.Acknowledge, id)));
        app.MapDelete("/api/crash-reports/{id}", (HttpContext context, string id, ICrashReportOwner owner) =>
            NativeCrashReportResponse.Send(context, NativeCrashReportCapabilities.Invoke(owner, NativeCrashReportCapabilities.Operation.Delete, id)));
        return app;
    }

    private static CompiledFrontendSubscription CompileCrashReportSubscription(
        ParsedFrontendSubscription subscription,
        ICrashReportOwner owner)
        => subscription.Compile(async (writer, cancellationToken) =>
        {
            await foreach (var list in owner.SubscribeAsync(cancellationToken))
                await writer.WriteAsync(subscription.Id, list, cancellationToken);
        });
}
