using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Application.Settings;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Endpoints;
using ResourceManager.App.Hosting;
using ResourceManager.App.Hosting.StartupCapabilities;
using ResourceManager.App.Infrastructure.ResourceBreakdown;
using ResourceManager.App.Infrastructure.Security;
using ResourceManager.App.StateItems;
using ResourceManager.Shared.BusinessBlocks.State;
using ResourceManager.Shared.Runtime;
using ResourceManager.Shared.Runtime.TrustedAccess;

namespace Resource_Manager_APP.Tests;

public sealed class EventDrivenDesktopBackendTests
{
    [Fact]
    public async Task ProductionSettingsChannelDeliversCurrentThenOnlyCommittedContentChanges()
    {
        await using var fixture = await Fixture.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var client = fixture.Client();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/subscriptions/stream")
        {
            Content = JsonContent.Create(new { version = 1, subscriptions = new[] {
                new { id = "settings", path = "/api/settings/app/subscribe" },
                new { id = "modules", path = "/api/startup-modules/settings/subscribe" } } })
        };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
        var initial = new Dictionary<string, JsonElement>();
        for (var i = 0; i < 2; i++)
        {
            using var frame = JsonDocument.Parse((await reader.ReadLineAsync(timeout.Token))!);
            initial.Add(frame.RootElement.GetProperty("subscriptionId").GetString()!, frame.RootElement.GetProperty("value").Clone());
        }
        var settings = fixture.App.Services.GetRequiredService<IAppSettingsStore>();
        var modules = fixture.App.Services.GetRequiredService<IStartupModuleSettingsStore>();
        using var get = JsonDocument.Parse(await client.GetStringAsync("/api/settings/app", timeout.Token));
        Assert.Equal(get.RootElement.GetProperty("settings").GetRawText(), initial["settings"].GetRawText());
        using var getModules = JsonDocument.Parse(await client.GetStringAsync("/api/startup-modules/settings", timeout.Token));
        Assert.Equal(getModules.RootElement.GetRawText(), initial["modules"].GetRawText());
        Assert.Empty(initial["modules"].GetProperty("software").EnumerateArray());
        var authority = fixture.App.Services.GetRequiredService<StateAuthority>();
        Assert.Same((await settings.LoadAsync(timeout.Token)).Settings, authority.Read<AppSettings>(StateItemUids.AppSettingsCurrent).Value);
        Assert.Same(await modules.GetAsync(timeout.Token), authority.Read<StartupModuleSettingsDocument>(StateItemUids.StartupModuleSettingsCurrent).Value);
        Assert.Throws<NotSupportedException>(() => ((IList<AppEditableHotkeySettings>)authority.Read<AppSettings>(StateItemUids.AppSettingsCurrent).Value.SystemIntegration.Hotkeys).Clear());

        // A separately deserialized, content-identical save and GETs produce no frame.
        var pending = reader.ReadLineAsync(timeout.Token).AsTask();
        await settings.SaveAsync(initial["settings"].Deserialize<AppSettings>(JsonSerializerOptions.Web)!, timeout.Token);
        await settings.LoadReadOnlyAsync(timeout.Token);
        await modules.GetAsync(timeout.Token);
        Assert.NotSame(pending, await Task.WhenAny(pending, Task.Delay(200, timeout.Token)));

        var current = (await settings.LoadAsync(timeout.Token)).Settings;
        var changed = current with { SystemIntegration = current.SystemIntegration with { TaskManagerShortcutReplacementEnabled = true } };
        await settings.SaveAsync(changed, timeout.Token);
        using var next = JsonDocument.Parse((await pending)!);
        Assert.Equal("settings", next.RootElement.GetProperty("subscriptionId").GetString());
        Assert.True(next.RootElement.GetProperty("value").GetProperty("systemIntegration").GetProperty("taskManagerShortcutReplacementEnabled").GetBoolean());

        pending = reader.ReadLineAsync(timeout.Token).AsTask();
        await settings.SaveAsync(changed, timeout.Token);
        await modules.SaveAsync(new("game", ["fixture-module"]), timeout.Token);
        using var selected = JsonDocument.Parse((await pending)!);
        Assert.Equal("modules", selected.RootElement.GetProperty("subscriptionId").GetString());
        Assert.Equal("fixture-module", selected.RootElement.GetProperty("value").GetProperty("software")[0].GetProperty("moduleIds")[0].GetString());

        pending = reader.ReadLineAsync(timeout.Token).AsTask();
        await modules.SaveAsync(new("game", ["fixture-module"]), timeout.Token);
        await Assert.ThrowsAsync<ArgumentException>(() => modules.SaveAsync(new("game", ["unknown-module"]), timeout.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => settings.SaveValidatedAsync(current,
            (_, _) => throw new InvalidOperationException("validation rejected"), timeout.Token));
        await modules.GetAsync(timeout.Token);
        Assert.NotSame(pending, await Task.WhenAny(pending, Task.Delay(200, timeout.Token)));
        await modules.SaveAsync(new("game", []), timeout.Token);
        using var cleared = JsonDocument.Parse((await pending)!);
        Assert.Equal("modules", cleared.RootElement.GetProperty("subscriptionId").GetString());
        Assert.Empty(cleared.RootElement.GetProperty("value").GetProperty("software")[0].GetProperty("moduleIds").EnumerateArray());

        // A late holder, even with the same client ID, gets the current value immediately.
        using var lateRequest = new HttpRequestMessage(HttpMethod.Post, "/api/subscriptions/stream")
        { Content = JsonContent.Create(new { version = 1, subscriptions = new[] { new { id = "settings", path = "/api/settings/app/subscribe" } } }) };
        using var lateResponse = await client.SendAsync(lateRequest, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        lateResponse.EnsureSuccessStatusCode();
        using var lateReader = new StreamReader(await lateResponse.Content.ReadAsStreamAsync(timeout.Token));
        using var late = JsonDocument.Parse((await lateReader.ReadLineAsync(timeout.Token))!);
        Assert.True(late.RootElement.GetProperty("value").GetProperty("systemIntegration").GetProperty("taskManagerShortcutReplacementEnabled").GetBoolean());
        timeout.Cancel();
    }

    [Fact]
    public async Task ProductionIdleSettingsStreamClosesWhenCredentialIsRevoked()
    {
        await using var fixture = await Fixture.StartAsync();
        using var client = fixture.Client();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/subscriptions/stream")
        { Content = JsonContent.Create(new { version = 1, subscriptions = new[] { new { id = "settings", path = "/api/settings/app/subscribe" } } }) };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
        Assert.NotNull(await reader.ReadLineAsync(timeout.Token));
        var pending = reader.ReadLineAsync(timeout.Token).AsTask();
        Assert.True(await fixture.Credential.Owner.RevokeAsync(fixture.Credential.CredentialId, timeout.Token));
        Assert.Null(await pending);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProductionSettingsSubscriptionDoesNotWriteOrRepairTheSource(bool corrupt)
    {
        await using var fixture = await Fixture.StartAsync();
        var directory = Path.Combine(fixture.Root, "Config");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "app-settings.json");
        var original = corrupt ? "invalid settings" : JsonSerializer.Serialize(AppSettingsDefaults.Create(), JsonSerializerOptions.Web);
        await File.WriteAllTextAsync(path, original);
        using var client = fixture.Client();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/subscriptions/stream")
        { Content = JsonContent.Create(new { version = 1, subscriptions = new[] { new { id = "settings", path = "/api/settings/app/subscribe" } } }) };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
        using var current = JsonDocument.Parse((await reader.ReadLineAsync(timeout.Token))!);
        Assert.True(current.RootElement.GetProperty("value").TryGetProperty("systemIntegration", out _));
        Assert.Equal(original, await File.ReadAllTextAsync(path, timeout.Token));
        Assert.Single(Directory.EnumerateFiles(directory));
        timeout.Cancel();
    }

    [Fact]
    public async Task ProductionInvalidStartupSettingsCloseTheStreamWithoutInventingAnEmptyValue()
    {
        await using var fixture = await Fixture.StartAsync();
        var path = Path.Combine(fixture.Root, "UserData", "SoftwareProfiles", "startup-modules.local.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "invalid settings");
        using var client = fixture.Client();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/subscriptions/stream")
        { Content = JsonContent.Create(new { version = 1, subscriptions = new[] { new { id = "modules", path = "/api/startup-modules/settings/subscribe" } } }) };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync(timeout.Token));
        Assert.Equal(NoValueReason.NotPublished, fixture.App.Services.GetRequiredService<StateAuthority>()
            .Read<StartupModuleSettingsDocument>(StateItemUids.StartupModuleSettingsCurrent).NoValueReason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProductionDesktopSampleRemainsCurrentUntilConnectionClosesOrKeyIsRevoked(bool revoke)
    {
        await using var fixture = await Fixture.StartAsync();
        using var process = Process.GetCurrentProcess();
        Assert.True(process.SessionId > 0);
        using var client = fixture.Client();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var start = process.StartTime.ToUniversalTime().ToFileTimeUtc();
        var window = new DesktopProcessWindowObservation(process.Id, start, true, false);
        var sample = new InteractiveDesktopObservation(process.Id, start, process.SessionId, DateTimeOffset.UtcNow.UtcTicks,
            true, true, window, [window]);
        using (var selection = await client.PostAsync(InteractiveDesktopObservation.EndpointPath + "/publisher/select", null, timeout.Token))
            Assert.Equal(HttpStatusCode.NoContent, selection.StatusCode);
        using (var publication = await client.PostAsJsonAsync(InteractiveDesktopObservation.EndpointPath, sample, timeout.Token))
            Assert.Equal(HttpStatusCode.OK, publication.StatusCode);
        var store = fixture.App.Services.GetRequiredService<InteractiveDesktopObservationStore>();
        Assert.True(store.IsForeground(process.Id, start));
        await Task.Delay(TimeSpan.FromMilliseconds(1200), timeout.Token);
        Assert.True(store.IsForeground(process.Id, start));
        Assert.Equal(HostManagerRuntimeStates.ForegroundFocused, store.Read([new(process.Id, start)]).Processes[new(process.Id, start)].RuntimeState);
        if (revoke)
        {
            Assert.True(await fixture.Credential.Owner.RevokeAsync(fixture.Credential.CredentialId, timeout.Token));
            Assert.Null(store.IsForeground(process.Id, start));
        }
        else
        {
            client.Dispose();
            while (store.IsForeground(process.Id, start) is not null) await Task.Delay(10, timeout.Token);
        }
        Assert.Empty(store.Read([new(process.Id, start)]).Processes);
    }

    private sealed class Fixture(StartupModuleTestCatalog files, WebApplication app,
        TrustedAccessEndpointTestAuthentication.AddedCredential credential) : IAsyncDisposable
    {
        public WebApplication App => app;
        public string Root => files.Root;
        public TrustedAccessEndpointTestAuthentication.AddedCredential Credential => credential;
        public HttpClient Client() => TrustedAccessEndpointTestAuthentication.Client(app);
        public static async Task<Fixture> StartAsync()
        {
            var files = new StartupModuleTestCatalog();
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = files.Root });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            var capabilities = new StartupCapabilitySet("desktop-backend-test", StartupCapability.None);
            builder.Services.AddResourceManagerApp(["--no-native-ui"], capabilities);
            LoopbackApiPipelineConfigurator.Compile(false).ConfigureServices(builder.Services);
            builder.Services.RemoveAll<IHostedService>();
            builder.Services.AddSingleton(files.Catalog);
            var credential = TrustedAccessEndpointTestAuthentication.Add(builder.Services, AccessKeyScopes.Read, AccessKeyScopes.DesktopPublish);
            var app = TrustedAccessEndpointTestAuthentication.Build(builder);
            app.MapResourceManagerEndpoints(LoopbackApiPipelineConfigurator.Compile(false).Mode, capabilities);
            await app.StartAsync();
            return new(files, app, credential);
        }
        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
            files.Dispose();
        }
    }
}
