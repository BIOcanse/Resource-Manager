using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ResourceManager.Shared.Runtime.TrustedAccess;

namespace ResourceManager.NativeUi.WebView;

// The shell owns this private HTTP proxy; only its session talks to the backend.
// A normal HTTP response preserves indefinite subscriptions and browser cancellation.
internal sealed class FrontendLoopbackProxy(
    Uri backendAddress,
    Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> forward) : IDisposable, IAsyncDisposable
{
    internal const string CapabilityHeader = "X-Resource-Manager-Native-Bridge";
    private static readonly HashSet<string> ExcludedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Connection", "Proxy-Connection", "Keep-Alive", "Proxy-Authenticate", "Proxy-Authorization",
        "TE", "Trailer", "Transfer-Encoding", "Upgrade", "Cookie", "Set-Cookie",
        "Authorization", TrustedAccessHeaders.KeyHeader, CapabilityHeader
    };
    private readonly string capability = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private readonly CancellationTokenSource lifetime = new();
    private readonly object sync = new();
    private WebApplication? application;
    private Task? startup;
    private bool disposed;

    internal string Address { get; private set; } = string.Empty;
    internal string ApplicationAddress => backendAddress.AbsoluteUri;
    internal string Capability => capability;

    internal async Task StartAsync()
    {
        Task current;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            current = startup ??= StartCoreAsync();
        }
        try { await current.ConfigureAwait(false); }
        catch
        {
            lock (sync) { if (ReferenceEquals(startup, current)) startup = null; }
            throw;
        }
    }

    private async Task StartCoreAsync()
    {
        if (application is not null) return;
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = [], ApplicationName = typeof(FrontendLoopbackProxy).Assembly.FullName,
            EnvironmentName = "Production"
        });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Limits.MaxConcurrentConnections = 64;
            options.Limits.MaxRequestBodySize = 30_000_000;
            options.Listen(IPAddress.Loopback, 0);
        });
        var app = builder.Build();
        app.Run(context => ForwardAsync(context, app.Lifetime.ApplicationStopping));
        try
        {
            await app.StartAsync(lifetime.Token).ConfigureAwait(false);
            Address = app.Services.GetRequiredService<IServer>().Features
                .Get<IServerAddressesFeature>()!.Addresses.Single().TrimEnd('/') + "/";
            application = app;
        }
        catch
        {
            await app.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task ForwardAsync(HttpContext context, CancellationToken stopping)
    {
        var request = context.Request;
        var origin = backendAddress;
        if (context.Connection.RemoteIpAddress is not { } remote || !IPAddress.IsLoopback(remote)
            || request.Host.Value != origin.Authority
            || !request.Headers.TryGetValue(CapabilityHeader, out var supplied)
            || supplied.Count != 1 || !MatchesCapability(supplied[0])
            || request.Headers.TryGetValue("Origin", out var origins)
                && (origins.Count != 1 || origins[0] != origin.GetLeftPart(UriPartial.Authority)))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, stopping);
        var token = cancellation.Token;
        var target = context.Features.Get<IHttpRequestFeature>()!.RawTarget;
        if (target is null || !Uri.TryCreate(target, UriKind.Absolute, out var destination)
            || destination.Scheme != origin.Scheme || destination.Authority != origin.Authority
            || destination.UserInfo.Length != 0 || destination.Fragment.Length != 0)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        using var outbound = new HttpRequestMessage(new HttpMethod(request.Method),
            destination);
        if (context.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true)
            outbound.Content = new StreamContent(request.Body);
        var excluded = new HashSet<string>(ExcludedHeaders, StringComparer.OrdinalIgnoreCase);
        foreach (var value in request.Headers.Connection)
            foreach (var name in (value ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                excluded.Add(name);
        foreach (var header in request.Headers)
        {
            if (excluded.Contains(header.Key) || header.Key.Equals("Origin", StringComparison.OrdinalIgnoreCase)
                || header.Key.Equals("Referer", StringComparison.OrdinalIgnoreCase)) continue;
            if (!outbound.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()))
                outbound.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
        }
        if (request.Headers.ContainsKey("Origin"))
            outbound.Headers.TryAddWithoutValidation("Origin", backendAddress.GetLeftPart(UriPartial.Authority));

        try
        {
            using var response = await forward(outbound, token).ConfigureAwait(false);
            context.Response.StatusCode = (int)response.StatusCode;
            var excludedResponse = new HashSet<string>(ExcludedHeaders, StringComparer.OrdinalIgnoreCase);
            foreach (var value in response.Headers.Connection)
                foreach (var name in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                    excludedResponse.Add(name);
            foreach (var header in response.Headers.Concat(response.Content.Headers))
                if (!excludedResponse.Contains(header.Key)) context.Response.Headers[header.Key] = header.Value.ToArray();
            await using var content = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            await context.Response.StartAsync(token).ConfigureAwait(false);
            await CopyStreamingAsync(content, context.Response.Body, token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is HttpRequestException or IOException or UnauthorizedAccessException
            or InvalidOperationException or OperationCanceledException)
        {
            System.Diagnostics.Trace.WriteLine($"Native frontend forwarding failed: {error.GetType().Name}");
            if (context.Response.HasStarted) context.Abort();
            else context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        }
    }

    internal bool MatchesCapability(string? supplied)
        => supplied is { Length: 64 }
            && CryptographicOperations.FixedTimeEquals(
                MemoryMarshal.AsBytes(supplied.AsSpan()), MemoryMarshal.AsBytes(capability.AsSpan()));

    internal static async Task CopyStreamingAsync(Stream source, Stream destination, CancellationToken token)
    {
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await source.ReadAsync(buffer, token).ConfigureAwait(false);
            if (read == 0) break;
            await destination.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            await destination.FlushAsync(token).ConfigureAwait(false);
        }
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        Task? pending;
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            pending = startup;
        }
        lifetime.Cancel();
        if (pending is not null)
        {
            try { await pending.ConfigureAwait(false); }
            catch (Exception error) { System.Diagnostics.Trace.WriteLine($"Native frontend proxy startup ended during disposal: {error.GetType().Name}"); }
        }
        var app = Interlocked.Exchange(ref application, null);
        lifetime.Dispose();
        if (app is null) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { await app.StopAsync(timeout.Token).ConfigureAwait(false); }
        finally { await app.DisposeAsync().ConfigureAwait(false); }
    }
}
