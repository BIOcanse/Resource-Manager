using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Domain.Updates;
using ResourceManager.App.Endpoints;
using ResourceManager.App.Infrastructure.Updates;
using ResourceManager.Shared.Runtime.TrustedAccess;

namespace Resource_Manager_APP.Tests;

public sealed class ProductUpdateSubscriptionTests
{
    [Fact]
    public async Task UnifiedSubscriptionUsesStatusWireAndPublishesCheckChangesWithUtcTimes()
    {
        var root = Path.Combine(Path.GetTempPath(), "rm-update-stream-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var version = "0.3.0";
        var coordinator = new ProductUpdateCoordinator(
            _ => Task.FromResult(new ProductVersionCatalog("resource-manager", "product", "0.2.4", "loaded", true,
                DateTimeOffset.UtcNow, [new("version:" + version, version, "0.3", "stable", null, "package.zip", "unused", "unused", true,
                    ProductVersionEligibility.Eligible)], null)),
            (_, _) => throw new InvalidOperationException("Notify must not prepare."),
            (_, _) => throw new InvalidOperationException("Notify must not launch."),
            new(root), TimeProvider.System, NullLogger<ProductUpdateCoordinator>.Instance);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.ConfigureHttpJsonOptions(options =>
            ResourceManagerEndpointRouteBuilderExtensions.ConfigureJsonSerializerOptions(options.SerializerOptions));
        builder.Services.AddSingleton(coordinator);
        TrustedAccessEndpointTestAuthentication.Add(builder.Services, AccessKeyScopes.Read);
        await using var app = TrustedAccessEndpointTestAuthentication.Build(builder);
        foreach (var method in new[] { "MapUpdateEndpoints", "MapSubscriptionChannelEndpoints" })
            typeof(ResourceManagerEndpointRouteBuilderExtensions).GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [app]);
        try
        {
            await app.StartAsync();
            using var client = TrustedAccessEndpointTestAuthentication.Client(app);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/subscriptions/stream")
            {
                Content = JsonContent.Create(new { version = 1, subscriptions = new[] {
                    new { id = "updates", path = "/api/updates/product/status/subscribe" } } })
            };
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
            using var initial = JsonDocument.Parse((await reader.ReadLineAsync(timeout.Token))!);
            Assert.Equal("updates", initial.RootElement.GetProperty("subscriptionId").GetString());
            using var get = JsonDocument.Parse(await client.GetStringAsync("/api/updates/product/status", timeout.Token));
            Assert.Equal(get.RootElement.GetRawText(), initial.RootElement.GetProperty("value").GetRawText());
            Assert.Equal(JsonValueKind.Null, initial.RootElement.GetProperty("value").GetProperty("availableVersion").ValueKind);

            await coordinator.CheckCatalogAsync(timeout.Token);
            using var changed = JsonDocument.Parse((await reader.ReadLineAsync(timeout.Token))!);
            var state = changed.RootElement.GetProperty("value");
            Assert.Equal("0.3.0", state.GetProperty("availableVersion").GetString());
            foreach (var name in new[] { "lastCheckedAt", "updatedAt" })
                Assert.Equal(TimeSpan.Zero, state.GetProperty(name).GetDateTimeOffset().Offset);
            coordinator.ConfigureSchedule("notify", true, 24, true, false);
            using var scheduled = JsonDocument.Parse((await reader.ReadLineAsync(timeout.Token))!);
            Assert.Equal(TimeSpan.Zero, scheduled.RootElement.GetProperty("value").GetProperty("nextCheckAt").GetDateTimeOffset().Offset);
            timeout.Cancel();
        }
        finally
        {
            await app.StopAsync();
            // This fixture creates only these two files and one Config directory.
            var config = Path.Combine(root, "Config");
            if (Directory.Exists(config))
            {
                foreach (var file in Directory.EnumerateFiles(config)) File.Delete(file);
                Directory.Delete(config);
            }
            Directory.Delete(root);
        }
    }
}
