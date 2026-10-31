using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ResourceManager.App.Application.Notifications;
using ResourceManager.App.Domain.TrustedAccess;
using ResourceManager.App.Endpoints;
using ResourceManager.App.Infrastructure.Security;
using ResourceManager.Shared.Runtime.TrustedAccess;

namespace Resource_Manager_APP.Tests;

public sealed class NotificationAccessTests
{
    private const string MemoryKind = "memory-protection.action";
    private const string UpdateKind = "update-available";
    private const string AccessKind = "access-request";

    [Fact]
    public async Task EachCallerReceivesOnlyTheKindsItsScopesHoldOnTheChannelAndGet()
    {
        var owner = NotificationTestSupport.Create();
        owner.Post(MemoryKind, NotificationFacts.Of(new MemoryProtectionActionNotificationFacts(
            new string('b', 32), "processTree", "launcher.exe", 40, 3, 0)));
        owner.Post(UpdateKind, NotificationFacts.Of(new UpdateAvailableNotificationFacts("0.3.0")));
        owner.Post(AccessKind, NotificationFacts.Of(new AccessRequestNotificationFacts(new string('a', 64), "tool", @"C:\Tools\tool.exe")));
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var added = TrustedAccessEndpointTestAuthentication.Add(builder.Services, AccessKeyScopes.Read);
        // Keyless requests in this host come from an elevated administrator, like the elevated tray.
        builder.Services.AddSingleton<ILoopbackApiAdministratorReader>(new Administrator());
        builder.Services.ConfigureHttpJsonOptions(options =>
            ResourceManagerEndpointRouteBuilderExtensions.ConfigureJsonSerializerOptions(options.SerializerOptions));
        builder.Services.AddSingleton<INotificationOwner>(owner);
        await using var app = TrustedAccessEndpointTestAuthentication.Build(builder);
        typeof(ResourceManagerEndpointRouteBuilderExtensions).GetMethod("MapSubscriptionChannelEndpoints",
            BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [app]);
        app.MapNotificationEndpoints();
        await app.StartAsync();
        async Task<string> Key(params string[] scopes) => (await added.Owner.CreateAsync(
            new AccessKeyCreateRequest("notification caller", AccessKeyPurposes.Control, scopes), CancellationToken.None)).ApiKey;
        // The read-only caller goes last: its live check posts more notifications.
        var callers = new (string Name, string? ApiKey, string[]? Kinds)[]
        {
            ("read+processes.read", await Key(AccessKeyScopes.Read, AccessKeyScopes.ProcessesRead), [MemoryKind, UpdateKind]),
            // The selector itself needs read, so a processes.read-only key is refused and receives nothing.
            ("processes.read", await Key(AccessKeyScopes.ProcessesRead), null),
            ("default key", await Key(AccessKeyScopes.Default), [MemoryKind, UpdateKind, AccessKind]),
            ("keyless administrator", null, [MemoryKind, UpdateKind, AccessKind]),
            ("read", added.ApiKey, [UpdateKind])
        };
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        foreach (var caller in callers)
        {
            using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = new Uri(address) };
            if (caller.ApiKey is { } apiKey) client.DefaultRequestHeaders.Add(TrustedAccessHeaders.KeyHeader, apiKey);
            using var get = await client.GetAsync("/api/notifications", timeout.Token);
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/subscriptions/stream")
            { Content = JsonContent.Create(new { version = 1, subscriptions = new[] { new { id = "notifications", path = "/api/notifications/subscribe" } } }) };
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (caller.Kinds is null)
            {
                Assert.Equal(HttpStatusCode.Forbidden, get.StatusCode);
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
                continue;
            }
            get.EnsureSuccessStatusCode();
            using (var body = JsonDocument.Parse(await get.Content.ReadAsStringAsync(timeout.Token)))
                Assert.Equal(caller.Kinds, Kinds(body.RootElement));
            response.EnsureSuccessStatusCode();
            using var input = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
            using var frame = JsonDocument.Parse((await input.ReadLineAsync(timeout.Token))!);
            Assert.Equal("notifications", frame.RootElement.GetProperty("subscriptionId").GetString());
            Assert.Equal(caller.Kinds, Kinds(frame.RootElement.GetProperty("value")));
            if (caller.Name != "read") continue;
            // A new notification of a kind this caller cannot read is no frame; its next frame is the next readable change.
            owner.Post(AccessKind, NotificationFacts.Of(new AccessRequestNotificationFacts(new string('c', 64), "other", @"C:\Tools\other.exe")));
            var update = owner.Post(UpdateKind, NotificationFacts.Of(new UpdateAvailableNotificationFacts("0.3.1")));
            using var next = JsonDocument.Parse((await input.ReadLineAsync(timeout.Token))!);
            var notifications = next.RootElement.GetProperty("value").GetProperty("notifications");
            Assert.Equal([UpdateKind, UpdateKind], Kinds(next.RootElement.GetProperty("value")));
            Assert.Equal(update.Sequence, notifications[1].GetProperty("sequence").GetInt64());
            Assert.Equal("0.3.1", notifications[1].GetProperty("facts").GetProperty("version").GetString());
        }
        Assert.True(TrustedAccessHttpPolicy.TryGetRequiredScopesForSubscription("/api/notifications/subscribe", out var scopes));
        Assert.Equal([AccessKeyScopes.Read], scopes);
    }

    [Fact]
    public void KeylessLocalCallerHoldsOnlyKeylessScopesAndNoContextShowsNothing()
    {
        var list = new NotificationList(NotificationKinds.All.Select((kind, index) => new NotificationItem(index + 1, kind.Name,
            DateTimeOffset.UnixEpoch, JsonDocument.Parse("{}").RootElement)).ToArray());
        var keylessLocal = new TrustedAccessAuthorizationContext(null, null, IsAdministrator: false, IsKeylessLocalCaller: true);
        Assert.Equal([MemoryKind, UpdateKind, NotificationKinds.CrashReport.Name],
            ResourceManagerEndpointRouteBuilderExtensions.VisibleNotifications(list, keylessLocal).Notifications.Select(item => item.Kind));
        var keylessAdministrator = new TrustedAccessAuthorizationContext(null, null, IsAdministrator: true, IsKeylessLocalCaller: false);
        Assert.Same(list, ResourceManagerEndpointRouteBuilderExtensions.VisibleNotifications(list, keylessAdministrator));
        var manager = new TrustedAccessAuthorizationContext(new AccessKeySummary("k", "manager", AccessKeyPurposes.Control,
            [AccessKeyScopes.KeysManage], DateTimeOffset.UnixEpoch, false), null, false, false);
        Assert.Equal([AccessKind],
            ResourceManagerEndpointRouteBuilderExtensions.VisibleNotifications(list, manager).Notifications.Select(item => item.Kind));
        Assert.Empty(ResourceManagerEndpointRouteBuilderExtensions.VisibleNotifications(list, null).Notifications);
        Assert.Equal(AccessKeyScopes.ProcessesRead, NotificationKinds.ScopeOf(MemoryKind));
        Assert.Equal(AccessKeyScopes.Read, NotificationKinds.ScopeOf(UpdateKind));
        Assert.Equal(AccessKeyScopes.KeysManage, NotificationKinds.ScopeOf(AccessKind));
        Assert.Null(NotificationKinds.ScopeOf("unknown-kind"));
    }

    private static string[] Kinds(JsonElement value)
        => value.GetProperty("notifications").EnumerateArray().Select(item => item.GetProperty("kind").GetString()!).ToArray();

    private sealed class Administrator : ILoopbackApiAdministratorReader
    {
        public bool IsAdministrator(HttpContext context) => true;
    }
}
