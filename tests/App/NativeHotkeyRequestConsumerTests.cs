using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core.Features;
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
    public async Task NativeDesktopPublisherStreamsChunkedNdjsonLinesWithItsKeyOnlyAsAHeader()
    {
        const string apiKey = "rm_sk_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        await using var server = await DesktopPublisherStreamServer.StartAsync();
        using var session = await CreateOwnedTestSessionAsync(server.Address, apiKey);
        var sample = CurrentProcessSample();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await session.RunDesktopPublisherStreamAsync(async (write, token) =>
        {
            await write(sample, token);
            // The first line reaches the backend while the body is still open: one chunk per line.
            await server.Received.WaitAsync(token);
            await write(sample with { ObservedAtUtcTicks = sample.ObservedAtUtcTicks + 1 }, token);
        }, deadline.Token);

        await server.WaitForEndsAsync(1, deadline.Token);
        var request = Assert.Single(server.Requests);
        Assert.Equal(apiKey, request.Key);
        Assert.Null(request.ContentLength);
        Assert.Contains("chunked", request.TransferEncoding, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("application/x-ndjson", request.ContentType, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, server.Lines.Count);
        Assert.All(server.Lines, line =>
        {
            Assert.DoesNotContain(apiKey, line, StringComparison.Ordinal);
            Assert.Contains("publisherProcessId", line, StringComparison.Ordinal);
        });
        // The body ended with its terminating chunk, not with an aborted request.
        Assert.Equal("completed", Assert.Single(server.Ends));

        // A rejected stream fails once its body has ended.
        server.RejectNext(StatusCodes.Status409Conflict);
        var rejected = await Assert.ThrowsAsync<HttpRequestException>(() => session.RunDesktopPublisherStreamAsync(
            (write, token) => write(sample, token), deadline.Token));
        Assert.Equal(System.Net.HttpStatusCode.Conflict, rejected.StatusCode);
    }

    [Fact]
    public async Task NativeDesktopPublisherStopEndsTheStreamWithItsTerminatingChunk()
    {
        const string apiKey = "rm_sk_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        await using var server = await DesktopPublisherStreamServer.StartAsync();
        using var session = await CreateOwnedTestSessionAsync(server.Address, apiKey);
        var sample = CurrentProcessSample();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var stop = new CancellationTokenSource();

        var streaming = session.RunDesktopPublisherStreamAsync(async (write, token) =>
        {
            await write(sample, token);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }, stop.Token);
        await server.Received.WaitAsync(deadline.Token);
        stop.Cancel();

        await streaming.WaitAsync(deadline.Token);
        await server.WaitForEndsAsync(1, deadline.Token);
        Assert.Equal("completed", Assert.Single(server.Ends));
        Assert.Single(server.Lines);
    }

    private static InteractiveDesktopObservation CurrentProcessSample()
    {
        using var process = Process.GetCurrentProcess();
        return new InteractiveDesktopObservation(process.Id, process.StartTime.ToUniversalTime().ToFileTimeUtc(),
            process.SessionId, DateTimeOffset.UtcNow.UtcTicks, false, false, null, []);
    }

    // A stand-in backend for the publisher stream: records each request's headers and lines and how
    // its body ended ("completed" at the terminating chunk, "aborted" otherwise).
    private sealed class DesktopPublisherStreamServer : IAsyncDisposable
    {
        private readonly WebApplication app;
        private int rejectStatus;

        private DesktopPublisherStreamServer(WebApplication app) => this.app = app;

        internal string Address { get; private set; } = "";
        internal SemaphoreSlim Received { get; } = new(0);
        internal ConcurrentQueue<(string Key, long? ContentLength, string TransferEncoding, string ContentType)> Requests { get; } = new();
        internal ConcurrentQueue<string> Lines { get; } = new();
        internal ConcurrentQueue<string> Ends { get; } = new();

        internal static async Task<DesktopPublisherStreamServer> StartAsync()
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var server = new DesktopPublisherStreamServer(builder.Build());
            server.app.MapPost(InteractiveDesktopObservation.PublisherStreamPath, server.RunAsync);
            server.app.MapGet("/api/runtime/capabilities", () => Results.Json(new { profileId = "test" }));
            await server.app.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));
            server.Address = Assert.Single(server.app.Services.GetRequiredService<IServer>().Features
                .Get<IServerAddressesFeature>()!.Addresses);
            return server;
        }

        internal void RejectNext(int status) => Volatile.Write(ref rejectStatus, status);

        internal async Task WaitForEndsAsync(int count, CancellationToken token)
        {
            while (Ends.Count < count) await Task.Delay(10, token);
        }

        private async Task RunAsync(HttpContext context)
        {
            Requests.Enqueue((context.Request.Headers[TrustedAccessHeaders.KeyHeader].ToString(),
                context.Request.ContentLength, context.Request.Headers.TransferEncoding.ToString(),
                context.Request.ContentType ?? ""));
            if (Interlocked.Exchange(ref rejectStatus, 0) is var status and not 0)
            {
                context.Response.StatusCode = status;
                return;
            }
            context.Features.Get<IHttpMinRequestBodyDataRateFeature>()!.MinDataRate = null;
            await context.Response.StartAsync();
            using var reader = new StreamReader(context.Request.Body);
            try
            {
                while (await reader.ReadLineAsync(context.RequestAborted) is { } line)
                {
                    Lines.Enqueue(line);
                    Received.Release();
                }
                Ends.Enqueue("completed");
            }
            catch (Exception) { Ends.Enqueue("aborted"); }
        }

        public async ValueTask DisposeAsync()
        {
            try { await app.StopAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
            finally { await app.DisposeAsync(); }
        }
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
