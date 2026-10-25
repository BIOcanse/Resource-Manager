using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
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
            const string token = "owned-hotkey-http-test";
            JsonElement? wire = null;
            SystemProcessOperationRequest? bound = null;
            WindowsSystemProcessActionService.NormalizedProcessTarget[]? normalized = null;
            app.MapPost("/api/system/processes/terminate", (SystemProcessOperationRequest request,
                Microsoft.AspNetCore.Http.HttpContext context) =>
            {
                Assert.Equal(token, context.Request.Headers["X-Resource-Manager-Token"].ToString());
                bound = request;
                normalized = WindowsSystemProcessActionService.NormalizeProcessTargets(request.Targets);
                // No termination implementation is registered or called.
                return Microsoft.AspNetCore.Http.Results.Ok(new SystemProcessOperationResult(SystemProcessAction.Terminate,
                    [new(target.ProcessId, "owned-probe", "Succeeded", SystemProcessOutcome.Terminated, ProcessStartKey: target.ProcessStartKey)]));
            });
            app.Use(async (context, next) =>
            {
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
                using var session = BackendServiceSession.CreateDefault(address);
                BindOwnedTestSession(session, token);
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

    private static void BindOwnedTestSession(BackendServiceSession session, string token)
    {
        // Seed only the completed credential-handshake state. All production
        // HTTP handlers, OS server checks and action serialization still run.
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = typeof(BackendServiceSession);
        using var server = Process.GetCurrentProcess();
        type.GetField("<BackendPath>k__BackingField", fields)!.SetValue(session, Environment.ProcessPath);
        type.GetField("backendCredential", fields)!.SetValue(session, new FrontendSessionCredentialReply(
            FrontendSessionCredentialContract.ProductId, FrontendSessionCredentialContract.ProtocolVersion,
            1, server.Id, server.StartTime.ToUniversalTime().Ticks, server.Id, server.StartTime.ToUniversalTime().Ticks,
            server.SessionId, token));
        var authorization = type.GetNestedType("BackendRequestAuthorization", BindingFlags.NonPublic)!
            .GetMethod("ForToken", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [token]);
        type.GetField("requestAuthorization", fields)!.SetValue(session, authorization);
    }
}
