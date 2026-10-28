using System.Net;
using System.Reflection;
using System.Text.Json;
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
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReportOverviewWireIncludesHistoryIndependentTrustAndUnrecordedCount(bool refresh)
    {
        var at = DateTimeOffset.UnixEpoch;
        var report = new OptimizationReportItem("active-trusted", OptimizationReportTypes.BackgroundHighUsage,
            OptimizationReportStates.Active, OptimizationSeverity.Warning, OptimizationConfidence.High,
            at, at, at, at, OptimizationActivityContext.Unknown(),
            new OptimizationReportTarget(OptimizationReportTargetTypes.Software, "software:fixture", "Fixture",
                "fixture", "Fixture", "other", "other", [], [], null),
            new OptimizationReportEvidence(OptimizationResourceKinds.Memory, 100, 100, 100,
                OptimizationValueUnits.Bytes, 1, 1, 1), []) { Trusted = true };
        var reports = new Reports
        {
            Overview = new OptimizationReportOverview(at,
                [report, report with { Id = "history-trusted", State = OptimizationReportStates.History },
                    report with { Id = "history-untrusted", State = OptimizationReportStates.History, Trusted = false }],
                [], [], new OptimizationRecorderStatus(at, at, 1, 1, 1, 0, 0, 10)
                { UnrecordedReportCount = 5, EvaluationState = "completed" })
        };
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.ConfigureHttpJsonOptions(options =>
            ResourceManagerEndpointRouteBuilderExtensions.ConfigureJsonSerializerOptions(options.SerializerOptions));
        builder.Services.AddSingleton<IHostManagerReportService>(reports);
        builder.Services.AddSingleton<IOptimizationProtectionService>(_ => throw new InvalidOperationException("unused"));
        builder.Services.AddSingleton<IHostManagerSmartCoordinator>(_ => throw new InvalidOperationException("unused"));
        await using var app = builder.Build();
        typeof(ResourceManagerEndpointRouteBuilderExtensions)
            .GetMethod("MapOptimizationEndpoints", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [app, LoopbackApiPipelineMode.StandardUser]);
        await app.StartAsync();
        using var client = new HttpClient
        {
            BaseAddress = new Uri(Assert.Single(app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses)), Timeout = TimeSpan.FromSeconds(10)
        };

        using var response = refresh
            ? await client.PostAsync("/api/optimization/reports/refresh", null)
            : await client.GetAsync("/api/optimization/reports");
        response.EnsureSuccessStatusCode();
        using var wire = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var entries = wire.RootElement.GetProperty("reports");
        Assert.Equal(3, entries.GetArrayLength());
        Assert.Equal("active", entries[0].GetProperty("state").GetString());
        Assert.True(entries[0].GetProperty("trusted").GetBoolean());
        Assert.Equal("history", entries[1].GetProperty("state").GetString());
        Assert.True(entries[1].GetProperty("trusted").GetBoolean());
        Assert.Equal("history", entries[2].GetProperty("state").GetString());
        Assert.False(entries[2].GetProperty("trusted").GetBoolean());
        var status = wire.RootElement.GetProperty("status");
        Assert.Equal(1, status.GetProperty("activeReportCount").GetInt32());
        Assert.Equal(5, status.GetProperty("unrecordedReportCount").GetInt32());
        await app.StopAsync();
    }

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
        internal OptimizationReportOverview? Overview { get; init; }

        public Task<bool> RemoveTrustedTargetAsync(string trustId, CancellationToken cancellationToken)
        {
            if (trustId != "trust:app") return Task.FromResult(false);
            Removed.Add(trustId);
            return Task.FromResult(true);
        }

        public Task<OptimizationReportOverview> GetReportsAsync(CancellationToken cancellationToken)
            => Task.FromResult(Overview ?? throw new NotSupportedException());
        public Task<OptimizationReportOverview> RefreshReportsAsync(CancellationToken cancellationToken) => GetReportsAsync(cancellationToken);
        public Task<OptimizationReportItem?> GetReportAsync(string reportId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<TrustedOptimizationTarget?> DismissReportAsync(string reportId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<TrustedOptimizationTarget?> TrustReportAsync(string reportId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<TrustedOptimizationTarget>> GetTrustedTargetsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<TrustedOptimizationTarget>> RefreshTrustedTargetsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public OptimizationRecorderStatus GetStatus() => throw new NotSupportedException();
    }
}
