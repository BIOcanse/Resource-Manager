using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ResourceManager.App.Application.CrashReports;
using ResourceManager.App.Domain.TrustedAccess;
using ResourceManager.App.Endpoints;
using ResourceManager.App.Infrastructure.Security;
using ResourceManager.Shared.Runtime.TrustedAccess;
using static Resource_Manager_APP.Tests.CrashReportTestSupport;

namespace Resource_Manager_APP.Tests;

public sealed class CrashReportEndpointTests
{
    [Fact]
    public async Task CrashReportEndpointsSerializeSubscribeAndRequireSettingsWriteScope()
    {
        using var root = new TempRoot();
        var at = DateTimeOffset.UtcNow.AddMinutes(-1);
        WriteDump(root.Path, "ResourceManager.exe", 71, at);
        var (owner, _) = Owner(root.Path, new FixedPlan(Plan()), new FakeEvents(), new ManualTime(at.AddMinutes(1)));
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var credential = TrustedAccessEndpointTestAuthentication.Add(builder.Services, AccessKeyScopes.Read);
        var administrator = new Administrator();
        builder.Services.AddSingleton<ILoopbackApiAdministratorReader>(administrator);
        builder.Services.AddSingleton<ICrashReportOwner>(owner);
        builder.Services.ConfigureHttpJsonOptions(options =>
            ResourceManagerEndpointRouteBuilderExtensions.ConfigureJsonSerializerOptions(options.SerializerOptions));
        await using var app = TrustedAccessEndpointTestAuthentication.Build(builder);
        foreach (var method in new[] { "MapCrashReportEndpoints", "MapSubscriptionChannelEndpoints" })
            typeof(ResourceManagerEndpointRouteBuilderExtensions).GetMethod(method,
                BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [app]);
        await app.StartAsync();
        using var client = TrustedAccessEndpointTestAuthentication.Client(app);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var list = JsonDocument.Parse(await client.GetStringAsync("/api/crash-reports", timeout.Token));
        var record = Assert.Single(list.RootElement.GetProperty("reports").EnumerateArray());
        var id = record.GetProperty("id").GetString()!;
        Assert.False(record.GetProperty("acknowledged").GetBoolean());
        using (var request = new HttpRequestMessage(HttpMethod.Post, "/api/subscriptions/stream")
        {
            Content = JsonContent.Create(new { version = 1, subscriptions = new[] {
                new { id = "crashes", path = "/api/crash-reports/subscribe" } } })
        })
        using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token))
        {
            response.EnsureSuccessStatusCode();
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
            var frame = await reader.ReadLineAsync(timeout.Token);
            Assert.Contains(id, frame);
            Assert.Contains("acknowledged", frame);
        }
        foreach (var action in new[] { "package", "issue-link", "acknowledge" })
        {
            using var denied = await client.PostAsync($"/api/crash-reports/{id}/{action}", null, timeout.Token);
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }
        using (var denied = await client.DeleteAsync($"/api/crash-reports/{id}", timeout.Token))
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        var writer = await credential.Owner.CreateAsync(new AccessKeyCreateRequest("crash reports writer",
            AccessKeyPurposes.Control, [AccessKeyScopes.Read, AccessKeyScopes.MachineWrite]), timeout.Token);
        client.DefaultRequestHeaders.Remove(TrustedAccessHeaders.KeyHeader);
        client.DefaultRequestHeaders.Add(TrustedAccessHeaders.KeyHeader, writer.ApiKey);
        administrator.Elevated = true; // machine.write requires an elevated holder, just like settings writes.
        foreach (var action in new[] { "package", "issue-link", "acknowledge" })
        {
            using var response = await client.PostAsync($"/api/crash-reports/{id}/{action}", null, timeout.Token);
            response.EnsureSuccessStatusCode();
            using var receipt = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            Assert.Equal(id, receipt.RootElement.GetProperty("report").GetProperty("id").GetString());
        }
        Assert.True(Assert.Single(owner.Read().Reports).Acknowledged);
        using (var deleted = await client.DeleteAsync($"/api/crash-reports/{id}", timeout.Token))
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        using (var missing = await client.PostAsync($"/api/crash-reports/{id}/acknowledge", null, timeout.Token))
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    private sealed class Administrator : ILoopbackApiAdministratorReader
    {
        public bool Elevated { get; set; }
        public bool IsAdministrator(HttpContext context) => Elevated;
    }
}
