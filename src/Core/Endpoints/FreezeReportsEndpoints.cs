using System.Text.Json;
using System.Text;
using ResourceManager.App.Hosting.StartupCapabilities;
using ResourceManager.App.Infrastructure.FreezeReports;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Endpoints;

public static partial class ResourceManagerEndpointRouteBuilderExtensions
{
    internal static IEndpointRouteBuilder MapFreezeReportsEndpoints(this IEndpointRouteBuilder app, StartupCapabilitySet capabilities)
    {
        app.MapGet("/api/system-events", (FreezeReportsHost owner) => FreezeResponse(() => owner.Request(0)));
        app.MapGet("/api/freeze-recorder", (FreezeReportsHost owner) => FreezeResponse(() => owner.Request(2)));
        app.MapGet("/api/freeze-recorder/reviews", (FreezeReportsHost owner) => FreezeResponse(() => owner.Request(3)));
        app.MapGet("/api/freeze-recorder/reviews/{id}/details", (string id, FreezeReportsHost owner) => FreezeResponse(() => owner.Request(4, id)));
        app.MapGet("/api/freeze-recorder/reviews/{id}/export", (string id, FreezeReportsHost owner) => FreezeResponse(() => owner.Request(5, id), export: true));
        // Both routes expose the same authoritative native settings document.
        app.MapGet("/api/system-events/settings", (FreezeReportsHost owner) => FreezeResponse(() => owner.Request(7)));
        app.MapGet("/api/freeze-recorder/settings", (FreezeReportsHost owner) => FreezeResponse(() => owner.Request(7)));
        app.MapGet("/api/system-events/settings/defaults", (FreezeReportsHost owner) => FreezeResponse(() => owner.Request(9)));
        app.MapGet("/api/freeze-recorder/settings/defaults", (FreezeReportsHost owner) => FreezeResponse(() => owner.Request(9)));
        if (capabilities.Allows(StartupCapability.MutablePersistence))
        {
            app.MapPost("/api/system-events/history/clear", (FreezeReportsHost owner) => FreezeResponse(() => owner.Request(1)));
            app.MapPost("/api/freeze-recorder/reviews/history/clear", (FreezeReportsHost owner) => FreezeResponse(() => owner.Request(6)));
            app.MapPatch("/api/system-events/settings", (JsonElement input, FreezeReportsHost owner) => FreezeResponse(() => owner.Patch(input)));
            app.MapPatch("/api/freeze-recorder/settings", (JsonElement input, FreezeReportsHost owner) => FreezeResponse(() => owner.Patch(input)));
        }
        return app;
    }
    // ASP.NET response plumbing only; retires with H1 native HTTP routing.
    internal static IResult FreezeResponse(Func<JsonElement> read, bool export = false)
    {
        try
        {
            var value = read();
            return export ? Results.File(Encoding.UTF8.GetBytes(value.GetRawText()), "application/json", "freeze-review.json") : Results.Json(value);
        }
        catch (NativeFreezeReportsException error)
        { return Results.Json(error.ErrorBody, statusCode: error.StatusCode); }
    }
}
