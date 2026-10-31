using ResourceManager.App.Application.CrashReports;

namespace ResourceManager.App.Endpoints;

// 崩溃记录 (docs/crash_dumps_2026-10-31.md 接口). The backend never opens anything: it returns the URL and paths, and
// the window process opens the browser or selects the file when the user clicks.
public static partial class ResourceManagerEndpointRouteBuilderExtensions
{
    internal static IEndpointRouteBuilder MapCrashReportEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/crash-reports", (ICrashReportOwner owner) => Results.Ok(owner.Read()));
        app.MapPost("/api/crash-reports/{id}/package", (string id, ICrashReportOwner owner) =>
            owner.Package(id) is { } result ? Results.Ok(result) : UnknownCrashReport());
        app.MapPost("/api/crash-reports/{id}/issue-link", (string id, ICrashReportOwner owner) =>
            owner.IssueLink(id) is { } result ? Results.Ok(result) : UnknownCrashReport());
        app.MapPost("/api/crash-reports/{id}/acknowledge", (string id, ICrashReportOwner owner) =>
            owner.Acknowledge(id) is { } result ? Results.Ok(result) : UnknownCrashReport());
        app.MapDelete("/api/crash-reports/{id}", (string id, ICrashReportOwner owner) =>
            owner.Delete(id) ? Results.NoContent() : UnknownCrashReport());
        return app;
    }

    private static IResult UnknownCrashReport()
        => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Unknown crash report");

    private static CompiledFrontendSubscription CompileCrashReportSubscription(
        ParsedFrontendSubscription subscription,
        ICrashReportOwner owner)
        => subscription.Compile(async (writer, cancellationToken) =>
        {
            await foreach (var list in owner.SubscribeAsync(cancellationToken))
                await writer.WriteAsync(subscription.Id, list, cancellationToken);
        });
}
