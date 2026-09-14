using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using ResourceManager.App.Endpoints;
using ResourceManager.App.Hosting;
using ResourceManager.App.Hosting.StartupCapabilities;
using ResourceManager.App.Infrastructure.Security;

namespace Resource_Manager_APP.Tests;

[Collection(LocalResourceCapabilityHandlerProcessStateCollection.Name)]
public sealed class StartupCapabilityEndpointMapTests
{
    [Fact]
    public async Task NormalReadOnlyMapsObservationSurfaceWithoutEffectRoutes()
    {
        var routes = await CaptureRoutesAsync(StartupCapabilitySet.NormalReadOnly);

        Assert.Contains("GET /api/runtime/identity", routes);
        Assert.Contains("GET /api/runtime/capabilities", routes);
        Assert.Contains("GET /api/metrics/snapshot", routes);
        Assert.DoesNotContain("GET /api/metrics/subscribe", routes);
        Assert.Contains("POST /api/subscriptions/stream", routes);
        Assert.DoesNotContain("GET /api/metrics/gpu-specialized/subscribe", routes);
        Assert.DoesNotContain("GET /api/resource-monitor/subscribe", routes);
        Assert.DoesNotContain("GET /api/local-system/status/subscribe", routes);
        Assert.DoesNotContain(
            "GET /api/adapters/resource-manager/scheduling/subscribe",
            routes);
        Assert.Contains("GET /api/settings/app", routes);
        Assert.Contains("GET /api/components", routes);
        Assert.Contains("GET /api/dependencies", routes);
        Assert.Contains("GET /api/public/v1/capability", routes);
        Assert.Contains("GET /api/software/metadata/{softwareIdentityId}", routes);
        Assert.Contains("POST /api/adapters/resource-manager/scheduling", routes);

        Assert.DoesNotContain(routes, static route =>
            route.Contains(" /api/optimization/", StringComparison.Ordinal));
        Assert.DoesNotContain(routes, static route =>
            route.Contains(" /api/operations", StringComparison.Ordinal));
        Assert.DoesNotContain(routes, static route =>
            route.Contains(" /api/migrations/", StringComparison.Ordinal));
        Assert.DoesNotContain(routes, static route =>
            route.Contains(" /api/gpu-placement/", StringComparison.Ordinal));
        Assert.DoesNotContain("GET /api/software", routes);
        Assert.DoesNotContain(routes, static route =>
            route.StartsWith("POST /api/software", StringComparison.Ordinal)
            || route.StartsWith("DELETE /api/software", StringComparison.Ordinal));
        Assert.DoesNotContain(routes, static route =>
            route.Contains(" /api/index/", StringComparison.Ordinal));
        Assert.DoesNotContain("POST /api/components/{id}/download", routes);
        Assert.DoesNotContain("POST /api/dependencies/{id}/launch-installer", routes);
        Assert.DoesNotContain("POST /api/system/processes/terminate", routes);
        Assert.DoesNotContain("PUT /api/cpu/topology/performance-overrides", routes);
        Assert.DoesNotContain("GET /api/public/v1/sqlite/databases", routes);
        Assert.DoesNotContain("POST /api/adapters/register", routes);
        Assert.DoesNotContain("DELETE /api/adapters/{id}", routes);
        Assert.DoesNotContain("POST /api/controlled/register", routes);
        Assert.DoesNotContain("DELETE /api/controlled/{id:guid}", routes);

        var processLocalFrontendInputs = routes
            .Where(static route =>
                route.StartsWith("POST /api/adapters/resource-manager/", StringComparison.Ordinal))
            .OrderBy(static route => route, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
        [
            "POST /api/adapters/resource-manager/scheduling"
        ],
            processLocalFrontendInputs);
    }

    [Fact]
    public async Task FullProfileMapsExplicitEffectRoutes()
    {
        var routes = await CaptureRoutesAsync(StartupCapabilitySet.Full);

        Assert.Contains("POST /api/optimization/smart/run-once", routes);
        Assert.Contains(
            "GET /api/optimization/smart/diagnostics/decision-snapshot",
            routes);
        Assert.Contains("GET /api/operations", routes);
        Assert.DoesNotContain("GET /api/operations/subscribe", routes);
        Assert.Contains("POST /api/subscriptions/stream", routes);
        Assert.Contains("POST /api/migrations/execute", routes);
        Assert.Contains("PUT /api/gpu-placement/process-policy", routes);
        Assert.Contains("GET /api/software", routes);
        Assert.Contains("GET /api/software/metadata/{softwareIdentityId}", routes);
        Assert.Contains("GET /api/index/files/search", routes);
        Assert.Contains("POST /api/components/{id}/download", routes);
        Assert.Contains("POST /api/dependencies/{id}/launch-installer", routes);
        Assert.Contains("POST /api/system/processes/terminate", routes);
        Assert.Contains("PUT /api/cpu/topology/performance-overrides", routes);
        Assert.Contains("GET /api/public/v1/sqlite/databases", routes);
        Assert.Contains("POST /api/adapters/register", routes);
        Assert.Contains("DELETE /api/adapters/{id}", routes);
        Assert.Contains("POST /api/controlled/register", routes);
        Assert.Contains("DELETE /api/controlled/{id:guid}", routes);
    }

    private static async Task<HashSet<string>> CaptureRoutesAsync(
        StartupCapabilitySet startupCapabilities)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddResourceManagerApp(
            ["--no-native-ui"],
            startupCapabilities);
        await using var app = builder.Build();
        app.MapResourceManagerEndpoints(
            LoopbackApiPipelineMode.Administrator,
            startupCapabilities);

        return ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(static source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .SelectMany(static endpoint =>
            {
                var path = endpoint.RoutePattern.RawText
                    ?? throw new InvalidOperationException(
                        "A mapped API endpoint has no route pattern.");
                var methods = endpoint.Metadata
                    .GetMetadata<HttpMethodMetadata>()?
                    .HttpMethods
                    ?? [];
                return methods.Select(method => $"{method} {path}");
            })
            .ToHashSet(StringComparer.Ordinal);
    }
}
