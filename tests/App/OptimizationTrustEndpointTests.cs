using System.Net;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Endpoints;
using ResourceManager.App.Infrastructure.Security;

namespace Resource_Manager_APP.Tests;

public sealed class OptimizationTrustEndpointTests
{
    // The optimization page's "Remove trust" button calls this route; it was only declared in an
    // endpoint file that was never mapped, so the button always failed with 404.
    [Fact]
    public async Task RemoveTrustRouteReachesTheReportOwner()
    {
        var reports = new Reports();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IHostManagerReportService>(reports);
        // Other routes in the mapped group need these services to exist; this request must not use them.
        builder.Services.AddSingleton<IOptimizationProtectionService>(_ =>
            throw new InvalidOperationException("Removing trust must not touch protection."));
        builder.Services.AddSingleton<IHostManagerSmartCoordinator>(_ =>
            throw new InvalidOperationException("Removing trust must not execute the scheduler."));
        await using var app = builder.Build();
        typeof(ResourceManagerEndpointRouteBuilderExtensions)
            .GetMethod("MapOptimizationEndpoints", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [app, LoopbackApiPipelineMode.StandardUser]);
        await app.StartAsync();
        using var client = new HttpClient
        {
            BaseAddress = new Uri(Assert.Single(app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses)),
            Timeout = TimeSpan.FromSeconds(10)
        };

        using var removed = await client.DeleteAsync("/api/optimization/trust/trust%3Aapp");
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.Equal(["trust:app"], reports.Removed);

        using var missing = await client.DeleteAsync("/api/optimization/trust/trust%3Agone");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        // Listing trust stays a report-overview concern; the old list route is not revived.
        using var list = await client.GetAsync("/api/optimization/trust");
        Assert.Equal(HttpStatusCode.NotFound, list.StatusCode);
        await app.StopAsync();
    }

    private sealed class Reports : IHostManagerReportService
    {
        internal List<string> Removed { get; } = [];

        public Task<bool> RemoveTrustedTargetAsync(string trustId, CancellationToken cancellationToken)
        {
            if (trustId != "trust:app") return Task.FromResult(false);
            Removed.Add(trustId);
            return Task.FromResult(true);
        }

        public Task<OptimizationReportOverview> GetReportsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<OptimizationReportOverview> RefreshReportsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<OptimizationReportItem?> GetReportAsync(string reportId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<TrustedOptimizationTarget?> DismissReportAsync(string reportId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<TrustedOptimizationTarget?> TrustReportAsync(string reportId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<TrustedOptimizationTarget>> GetTrustedTargetsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<TrustedOptimizationTarget>> RefreshTrustedTargetsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public OptimizationRecorderStatus GetStatus() => throw new NotSupportedException();
    }
}
