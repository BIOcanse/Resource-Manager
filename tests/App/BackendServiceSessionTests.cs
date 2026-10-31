using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ResourceManager.App.Application.TrustedAccess;
using ResourceManager.App.Endpoints;
using ResourceManager.App.Infrastructure.Security;
using ResourceManager.NativeUi;
using ResourceManager.NativeUi.Configuration;
using ResourceManager.NativeUi.Localization;
using ResourceManager.NativeUi.WebView;
using ResourceManager.Shared.Runtime;
using ResourceManager.Shared.Runtime.TrustedAccess;

namespace Resource_Manager_APP.Tests;

public sealed class BackendServiceSessionTests
{
    [Fact]
    public async Task EmptyDefaultColdStartIsReadyAndProtectedProxyKeepsTheBackendDenial()
    {
        await using var backend = await Backend.StartAsync(enabled: false);
        using var session = backend.Session();
        session.PrepareWindowTransport();
        Assert.Equal(0, backend.KeyReads);
        Assert.Empty(backend.Requests);
        Assert.Empty(await backend.Keys.ListAsync(default));
        await using var proxy = new FrontendLoopbackProxy(backend.Address, session.SendFrontendRequestAsync);
        await proxy.StartAsync();
        using var client = Client(proxy);
        using var request = Request(proxy, "/api/access-keys");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("access-key-required", body.RootElement.GetProperty("error").GetString());
        Assert.True(backend.Requests.Last(item => item.Path == "/api/access-keys").Key is null);

        Assert.Equal(1, backend.KeyReads);
    }

    [Fact]
    public async Task DisablingDefaultDoesNotSendTheOldKey()
    {
        await using var backend = await Backend.StartAsync(enabled: true);
        using var session = backend.Session();
        session.PrepareWindowTransport();
        await using var proxy = new FrontendLoopbackProxy(backend.Address, session.SendFrontendRequestAsync);
        await proxy.StartAsync();
        using var client = Client(proxy);
        using (var first = Request(proxy, "/api/access-keys"))
        using (var response = await client.SendAsync(first)) Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var oldKey = backend.CurrentKey;
        Assert.Equal(1, backend.KeyReads);
        await backend.SetDefaultAsync(false);
        Assert.Equal(1, backend.KeyReads);
        using var after = Request(proxy, "/api/access-keys");
        using var denied = await client.SendAsync(after);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.True(backend.Requests.Last(item => item.Path == "/api/access-keys").Key is null);
        Assert.False(string.IsNullOrEmpty(oldKey));

    }

    [Fact]
    public async Task ReenabledDefaultIsReadByTheNextRequestInTheSameSessionAndProxy()
    {
        await using var backend = await Backend.StartAsync(enabled: false);
        using var session = backend.Session();
        session.PrepareWindowTransport();
        await using var proxy = new FrontendLoopbackProxy(backend.Address, session.SendFrontendRequestAsync);
        await proxy.StartAsync();
        using var client = Client(proxy);
        using (var before = Request(proxy, "/api/access-keys"))
        using (var denied = await client.SendAsync(before)) Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal(1, backend.KeyReads);
        await backend.SetDefaultAsync(true);
        for (var index = 0; index < 2; index++)
        {
            using var request = Request(proxy, "/api/access-keys");
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(backend.CurrentKey == backend.Requests.Last(item => item.Path == "/api/access-keys").Key,
                "The next request must use the reenabled credential.");
        }
        Assert.Equal(2, backend.KeyReads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KeyReadFailureIsExplicitAndCannotReuseTheSecretButStaticPagesStillLoad(bool untrusted)
    {
        await using var backend = await Backend.StartAsync(enabled: true);
        using var session = backend.Session();
        session.PrepareWindowTransport();
        using (var warm = new HttpRequestMessage(HttpMethod.Get, new Uri(backend.Address, "/api/access-keys")))
        using (var response = await session.SendFrontendRequestAsync(warm, default)) Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var forwardedBeforeFailure = backend.Requests.Count(item => item.Path == "/api/access-keys");
        backend.ReadFailure = untrusted ? new UnauthorizedAccessException("Fixture security validation denied.")
            : new IOException("Fixture committed read failed.");
        backend.ReplaceNativeFile();
        await using var proxy = new FrontendLoopbackProxy(backend.Address, session.SendFrontendRequestAsync);
        await proxy.StartAsync();
        using var client = Client(proxy);
        for (var index = 0; index < 2; index++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(backend.Address, "/api/access-keys"));
            var error = await Record.ExceptionAsync(() => session.SendFrontendRequestAsync(request, default));
            Assert.IsType(untrusted ? typeof(UnauthorizedAccessException) : typeof(IOException), error);
        }
        using (var request = Request(proxy, "/api/access-keys"))
        using (var response = await client.SendAsync(request)) Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(forwardedBeforeFailure, backend.Requests.Count(item => item.Path == "/api/access-keys"));
        Assert.Equal(4, backend.KeyReads); // Every failure invalidates the reader cache.
        foreach (var path in new[] { "/", "/assets/recovery.js" })
        {
            using var request = Request(proxy, path);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(backend.Requests.Last(item => item.Path == path).Key is null);
        }
        Assert.Equal(4, backend.KeyReads);
        backend.ReadFailure = null; // Same file identity: a failed read must not have cached the old secret.
        using var restored = Request(proxy, "/api/access-keys");
        using var recovered = await client.SendAsync(restored);
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        Assert.Equal(5, backend.KeyReads);
    }

    [Fact]
    public async Task MissingDefaultFileIsAnErrorRatherThanAnEmptyDisabledCredential()
    {
        await using var backend = await Backend.StartAsync(enabled: false);
        File.Delete(backend.NativeKeyPath);
        using var session = backend.Session();
        session.PrepareWindowTransport();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(backend.Address, "/api/access-keys"));
        await Assert.ThrowsAsync<IOException>(() => session.SendFrontendRequestAsync(request, default));
        Assert.DoesNotContain(backend.Requests, item => item.Path == "/api/access-keys");
    }

    private static HttpRequestMessage Request(FrontendLoopbackProxy proxy, string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(proxy.ApplicationAddress), path));
        request.Headers.Add(FrontendLoopbackProxy.CapabilityHeader, proxy.Capability);
        return request;
    }

    private static HttpClient Client(FrontendLoopbackProxy proxy) => new(new SocketsHttpHandler
    { UseProxy = true, Proxy = new WebProxy(proxy.Address), AllowAutoRedirect = false });

    private sealed class Backend : IAsyncDisposable
    {
        private readonly TrustedAccessTemporaryDirectory temporary = new();
        private readonly TrustedAccessTestKeyFileAccess keyFile;
        private readonly NativeTrustedAccessKeyReader reader;
        private WebApplication app = null!;
        private int keyReads;
        public readonly ConcurrentQueue<(string Path, string? Key)> Requests = new();
        public Exception? ReadFailure { get; set; }
        public ITrustedAccessKeyService Keys { get; }
        public int KeyReads => Volatile.Read(ref keyReads);
        public string? CurrentKey => keyFile.Read();
        public string NativeKeyPath => TrustedAccessKeyFile.PathForPackage(temporary.Root);
        public Uri Address => new(app.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()!.Addresses.Single() + "/");

        private Backend()
        {
            var state = TrustedAccessTestFactory.Create();
            Keys = state.Owner;
            keyFile = state.KeyFile;
            reader = new NativeTrustedAccessKeyReader(temporary.Root, () =>
            {
                Interlocked.Increment(ref keyReads);
                if (ReadFailure is { } failure) throw failure;
                if (!File.Exists(NativeKeyPath)) return null;
                using var input = SettingsFileReader.Open(NativeKeyPath);
                using var text = new StreamReader(input, Encoding.UTF8);
                return text.ReadToEnd();
            });
        }

        public static async Task<Backend> StartAsync(bool enabled)
        {
            var fixture = new Backend();
            try
            {
                await fixture.SetDefaultAsync(enabled);
                var builder = WebApplication.CreateSlimBuilder();
                builder.Logging.ClearProviders();
                builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
                var pipeline = LoopbackApiPipelineConfigurator.Compile(false);
                pipeline.ConfigureServices(builder.Services);
                builder.Services.AddSingleton<ITrustedAccessKeyService>(_ => fixture.Keys);
                builder.Services.AddSingleton<ILoopbackApiAdministratorReader>(new NonAdministrator());
                builder.Services.RemoveAll<IHostedService>();
                fixture.app = builder.Build();
                fixture.app.Use(async (context, next) =>
                {
                    fixture.Requests.Enqueue((context.Request.Path.Value!, context.Request.Headers.TryGetValue(
                        TrustedAccessHeaders.KeyHeader, out var key) ? key.ToString() : null));
                    await next(context);
                });
                pipeline.ConfigureApplication(fixture.app);
                fixture.app.MapGet("/api/runtime/capabilities", () => Results.Json(new { profileId = "native-recovery-test" }));
                fixture.app.MapGet("/", () => Results.Content("<!doctype html><title>Recovery</title>", "text/html"));
                fixture.app.MapGet("/assets/recovery.js", () => Results.Text("/* recovery shell */", "application/javascript"));
                fixture.app.MapTrustedAccessEndpoints();
                await fixture.app.StartAsync();
                return fixture;
            }
            catch { await fixture.DisposeAsync(); throw; }
        }

        public BackendServiceSession Session() => new(Address.AbsoluteUri, reader.Read, () =>
        {
            using var process = Process.GetCurrentProcess();
            return new(process.Id, process.StartTime.ToUniversalTime().Ticks, Environment.ProcessPath!);
        });

        public async Task SetDefaultAsync(bool enabled)
        {
            await Keys.SetDefaultAsync(enabled, default);
            ReplaceNativeFile();
        }

        public void ReplaceNativeFile()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(NativeKeyPath)!);
            var replacement = NativeKeyPath + ".replacement";
            File.WriteAllText(replacement, CurrentKey!, new UTF8Encoding(false));
            File.Move(replacement, NativeKeyPath, overwrite: true);
        }

        public async ValueTask DisposeAsync()
        {
            if (app is not null) await app.DisposeAsync();
            else (Keys as IDisposable)?.Dispose();
            temporary.Dispose();
        }

        private sealed class NonAdministrator : ILoopbackApiAdministratorReader
        {
            public bool IsAdministrator(HttpContext context) => false;
        }
    }
}
