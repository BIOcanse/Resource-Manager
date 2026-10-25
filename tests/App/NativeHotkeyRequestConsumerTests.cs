using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ResourceManager.App.Domain.LocalSystem;
using ResourceManager.App.Infrastructure.LocalSystem;
using ResourceManager.NativeUi;
using ResourceManager.NativeUi.Localization;
using ResourceManager.NativeUi.SystemIntegration.EditableHotkeys;
using ResourceManager.Shared.Runtime;
using ResourceManager.Shared.Runtime.TrustedAccess;

namespace Resource_Manager_APP.Tests;

public sealed class NativeHotkeyRequestConsumerTests
{
    [Fact]
    public async Task ActualNativeRequestBindsExactSelectedIdentityAtBackendConsumer()
    {
        var directory = Path.Combine(Path.GetTempPath(), "rm-hotkey-consumer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = Path.Combine(directory, "target.ps1");
        var release = Path.Combine(directory, "release");
        await File.WriteAllTextAsync(script, """
            param([string]$Release)
            $deadline = [DateTime]::UtcNow.AddSeconds(20)
            while (-not [IO.File]::Exists($Release) -and [DateTime]::UtcNow -lt $deadline) {
                Start-Sleep -Milliseconds 20
            }
            """);
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,
            "WindowsPowerShell", "v1.0", "powershell.exe")) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, release })
            start.ArgumentList.Add(argument);
        using var targetProcess = Process.Start(start)!;
        try
        {
            var target = Assert.IsType<ForceTerminateProcessTarget>(HungAndForegroundProcessResolver.ReadAllowedTarget(targetProcess.Id));
            Assert.Equal(targetProcess.StartTime.ToFileTimeUtc().ToString(CultureInfo.InvariantCulture), target.ProcessStartKey);
            Assert.True(ulong.Parse(target.ProcessStartKey, CultureInfo.InvariantCulture) > 1ul << 53,
                "The real Windows creation key must be preserved beyond JavaScript numeric precision.");

            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            await using var app = builder.Build();
            const string apiKey = "rm_sk_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            JsonElement? wire = null;
            SystemProcessOperationRequest? bound = null;
            WindowsSystemProcessActionService.NormalizedProcessTarget[]? normalized = null;
            app.MapPost("/api/system/processes/terminate", (SystemProcessOperationRequest request,
                Microsoft.AspNetCore.Http.HttpContext context) =>
            {
                Assert.Equal(apiKey, context.Request.Headers[TrustedAccessHeaders.KeyHeader].ToString());
                bound = request;
                normalized = WindowsSystemProcessActionService.NormalizeProcessTargets(request.Targets);
                // No termination implementation is registered or called.
                return Microsoft.AspNetCore.Http.Results.Ok(new SystemProcessOperationResult(SystemProcessAction.Terminate,
                    [new(target.ProcessId, "owned-probe", "Succeeded", SystemProcessOutcome.Terminated, ProcessStartKey: target.ProcessStartKey)]));
            });
            app.MapGet("/api/runtime/capabilities", () => Results.Json(new { profileId = "test" }));
            app.Use(async (context, next) =>
            {
                if (context.Request.Method != "POST") { await next(context); return; }
                context.Request.EnableBuffering();
                using var json = await JsonDocument.ParseAsync(context.Request.Body);
                wire = json.RootElement.Clone();
                context.Request.Body.Position = 0;
                await next(context);
            });
            await app.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));
            try
            {
                var address = Assert.Single(app.Services.GetRequiredService<IServer>().Features
                    .Get<IServerAddressesFeature>()!.Addresses);
                using var session = await CreateOwnedTestSessionAsync(address, apiKey);
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var result = await session.TerminateProcessesAsync([target], deadline.Token);
                Assert.NotNull(wire);
                Assert.False(wire.Value.TryGetProperty("processIds", out _));
                var item = Assert.Single(wire.Value.GetProperty("targets").EnumerateArray());
                Assert.Equal(JsonValueKind.String, item.GetProperty("processStartKey").ValueKind);
                Assert.Equal(target.ProcessStartKey, item.GetProperty("processStartKey").GetString());
                var boundTarget = Assert.Single(bound!.Targets);
                Assert.Equal(target.ProcessId, boundTarget.ProcessId);
                Assert.Equal(target.ProcessStartKey, boundTarget.ProcessStartKey);
                var exact = Assert.Single(normalized!);
                Assert.Equal(target.ProcessId, exact.ProcessId);
                Assert.Equal(ulong.Parse(target.ProcessStartKey, CultureInfo.InvariantCulture), exact.ProcessStartKey);
                Assert.Equal(string.Format(NativeUiText.Current.TerminateResultFormat, 1, 1), result);
                Assert.False(targetProcess.HasExited, "The probe was observed and serialized, never terminated.");
            }
            finally { await app.StopAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
        }
        finally
        {
            await File.WriteAllTextAsync(release, string.Empty);
            await targetProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
            Assert.Equal(0, targetProcess.ExitCode);
            File.Delete(script);
            File.Delete(release);
            Directory.Delete(directory);
        }
    }

    [Fact]
    public async Task NativeDesktopPublisherSendsItsKeyOnlyAsAHeaderOnTheSelectedConnection()
    {
        const string apiKey = "rm_sk_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        string? selectedConnection = null;
        string? publishedConnection = null;
        string? publishedJson = null;
        app.MapPost("/api/runtime/interactive-desktop/publisher/select", (HttpContext context) =>
        {
            Assert.Equal(apiKey, context.Request.Headers[TrustedAccessHeaders.KeyHeader].ToString());
            Assert.True(context.Request.ContentLength is null or 0);
            selectedConnection = context.Connection.Id;
            return Results.NoContent();
        });
        app.MapPost(InteractiveDesktopObservation.EndpointPath, async (HttpContext context) =>
        {
            Assert.Equal(apiKey, context.Request.Headers[TrustedAccessHeaders.KeyHeader].ToString());
            publishedConnection = context.Connection.Id;
            publishedJson = await new StreamReader(context.Request.Body).ReadToEndAsync();
            return Results.Ok();
        });
        app.MapGet("/api/runtime/capabilities", () => Results.Json(new { profileId = "test" }));
        await app.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            var address = Assert.Single(app.Services.GetRequiredService<IServer>().Features
                .Get<IServerAddressesFeature>()!.Addresses);
            using var session = await CreateOwnedTestSessionAsync(address, apiKey);
            using var process = Process.GetCurrentProcess();
            var sample = new InteractiveDesktopObservation(process.Id, process.StartTime.ToUniversalTime().ToFileTimeUtc(),
                process.SessionId, DateTimeOffset.UtcNow.UtcTicks, false, false, null, []);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));

            await session.PublishDesktopAsync(sample, deadline.Token);

            Assert.False(string.IsNullOrWhiteSpace(selectedConnection));
            Assert.Equal(selectedConnection, publishedConnection);
            Assert.NotNull(publishedJson);
            Assert.DoesNotContain(apiKey, publishedJson, StringComparison.Ordinal);
            Assert.Contains("publisherProcessId", publishedJson, StringComparison.Ordinal);
        }
        finally { await app.StopAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    private static async Task<BackendServiceSession> CreateOwnedTestSessionAsync(string address, string apiKey)
    {
        using var server = Process.GetCurrentProcess();
        var identity = new BackendProcessIdentity(server.Id, server.StartTime.ToUniversalTime().Ticks, Environment.ProcessPath!);
        var session = new BackendServiceSession(address, () => apiKey, () => identity);
        try
        {
            await session.EnsureConnectedAsync(CancellationToken.None);
            return session;
        }
        catch { session.Dispose(); throw; }
    }
}
