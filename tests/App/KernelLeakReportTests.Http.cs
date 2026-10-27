using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Endpoints;
using ResourceManager.App.Infrastructure.KernelLeak;
using ResourceManager.App.Infrastructure.Security;
using ResourceManager.Shared.Runtime.TrustedAccess;

namespace Resource_Manager_APP.Tests;

public sealed partial class KernelLeakReportTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProductionChannelUsesProcessReadScopeAndDeliversTypedReport(bool allowed)
    {
        using var fixture = new Fixture();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        TrustedAccessEndpointTestAuthentication.Add(builder.Services,
            [allowed ? AccessKeyScopes.ProcessesRead : AccessKeyScopes.MetricsRead]);
        builder.Services.ConfigureHttpJsonOptions(options =>
            ResourceManagerEndpointRouteBuilderExtensions.ConfigureJsonSerializerOptions(options.SerializerOptions));
        builder.Services.AddSingleton(fixture.Reports);
        await using var app = TrustedAccessEndpointTestAuthentication.Build(builder);
        typeof(ResourceManagerEndpointRouteBuilderExtensions).GetMethod("MapSubscriptionChannelEndpoints",
            BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [app]);
        await app.StartAsync();
        using var client = TrustedAccessEndpointTestAuthentication.Client(app);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/subscriptions/stream")
        { Content = JsonContent.Create(new { version = 1, subscriptions = new[] { new { id = "leaks", path = "/api/kernel-leaks/subscribe" } } }) };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (!allowed)
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal(0, fixture.Processes.Starts); return;
        }
        response.EnsureSuccessStatusCode();
        using var input = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
        using (var initial = JsonDocument.Parse((await input.ReadLineAsync(timeout.Token))!))
            Assert.Equal("Unavailable", initial.RootElement.GetProperty("value").GetProperty("status").GetString());
        for (var index = 0; index < 3; index++)
        {
            var at = DateTimeOffset.UnixEpoch.AddSeconds(index);
            fixture.Processes.Push(new(at, []) { ProcessFields = new(at, null, [],
                [new(7, "42", [new(KernelLeakProcessSampleSource.HandleField, new MetricInteger((ulong)(1000 + index * 100)), null)])]) });
            var line = (await input.ReadLineAsync(timeout.Token))!;
            using var frame = JsonDocument.Parse(line);
            Assert.Equal("leaks", frame.RootElement.GetProperty("subscriptionId").GetString());
            var value = frame.RootElement.GetProperty("value");
            Assert.Equal(index == 2 ? "Suspected" : "Empty", value.GetProperty("status").GetString());
            if (index == 2)
            {
                var item = Assert.Single(value.GetProperty("items").EnumerateArray());
                Assert.Equal("ProcessHandles", item.GetProperty("resourceKind").GetString());
                Assert.Equal("42", item.GetProperty("holder").GetProperty("startKey").GetString());
                Assert.Equal(1200UL, item.GetProperty("currentValue").GetUInt64());
                File.WriteAllText(EvidencePath("wire-example.json"), line);
            }
        }
        response.Dispose();
        await fixture.Processes.Released.Task.WaitAsync(timeout.Token);
        Assert.Equal(0, fixture.Processes.Active);
        Assert.True(TrustedAccessHttpPolicy.TryGetRequiredScopesForSubscription("/api/kernel-leaks/subscribe", out var scopes));
        Assert.Equal([AccessKeyScopes.ProcessesRead], scopes);
    }
}