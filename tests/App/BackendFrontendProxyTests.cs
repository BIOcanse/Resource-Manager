using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ResourceManager.NativeUi;
using ResourceManager.NativeUi.WebView;
using ResourceManager.Shared.Runtime.TrustedAccess;

namespace Resource_Manager_APP.Tests;

public sealed class BackendFrontendProxyTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public void BridgeCapabilityRequiresExactFixedSizeSecret()
    {
        using var proxy = new FrontendLoopbackProxy(new Uri("http://127.0.0.1:9321/"), (_, _) =>
            throw new InvalidOperationException("No requests expected."));
        Assert.True(proxy.MatchesCapability(proxy.Capability));
        Assert.False(proxy.MatchesCapability(null));
        Assert.False(proxy.MatchesCapability(proxy.Capability[..63]));
        Assert.False(proxy.MatchesCapability(proxy.Capability + "0"));
        foreach (var index in new[] { 0, 31, 63 })
        {
            var changed = proxy.Capability.ToCharArray();
            changed[index] = changed[index] == 'A' ? 'B' : 'A';
            Assert.False(proxy.MatchesCapability(new string(changed)));
        }
    }

    [Fact]
    public async Task EndpointReadIsPureAndPublishedBeforeStartReturns()
    {
        await using var proxy = new FrontendLoopbackProxy(new Uri("http://127.0.0.1:19421/"),
            (_, _) => throw new InvalidOperationException("No forwarding expected."));
        Assert.Empty(proxy.Address);
        Assert.Empty(proxy.Address);
        await proxy.StartAsync();
        var endpoint = proxy.Address;
        Assert.NotEmpty(endpoint);
        Assert.Equal(endpoint, proxy.Address);
    }

    [Fact]
    public async Task ConcurrentDisposalsBothWaitForActiveForwardingToDrain()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var returned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proxy = new FrontendLoopbackProxy(new Uri("http://127.0.0.1:9321/"), async (_, token) =>
        {
            entered.SetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException)
            {
                canceled.SetResult();
                await release.Task;
                throw;
            }
            finally { returned.SetResult(); }
            throw new InvalidOperationException("The forwarding operation must be canceled.");
        });
        await proxy.StartAsync();
        using var client = ProxyClient(proxy);
        using var request = AuthorizedRequest(proxy, "api/wait");
        var forwarding = client.SendAsync(request);
        Task? first = null;
        Task? second = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            first = proxy.DisposeAsync().AsTask();
            await canceled.Task.WaitAsync(TimeSpan.FromSeconds(10));
            second = proxy.DisposeAsync().AsTask();
            Assert.Same(first, second);
            Assert.False(first.IsCompleted);
            Assert.False(second.IsCompleted);
            release.SetResult();
            await Task.WhenAll(first, second);
            Assert.True(returned.Task.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
            await proxy.DisposeAsync();
            try { using var response = await forwarding; }
            catch (HttpRequestException) { }
        }
    }

    [Fact]
    public async Task PrivateBridgeDeniesMissingCapabilityForeignHostAndForeignOrigin()
    {
        var forwarded = 0;
        await using var proxy = new FrontendLoopbackProxy(new Uri("http://127.0.0.1:9321/"), (request, _) =>
        {
            Interlocked.Increment(ref forwarded);
            Assert.False(request.Headers.Contains(FrontendLoopbackProxy.CapabilityHeader));
            Assert.False(request.Headers.Contains(TrustedAccessHeaders.KeyHeader));
            Assert.Equal("http://127.0.0.1:9321", Assert.Single(request.Headers.GetValues("Origin")));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("accepted") });
        });
        await proxy.StartAsync();
        using var client = ProxyClient(proxy);
        foreach (var invalid in new[] { "missing", "wrong", "host", "origin", "duplicate" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, proxy.ApplicationAddress + "api/test");
            if (invalid != "missing") request.Headers.Add(FrontendLoopbackProxy.CapabilityHeader, invalid == "wrong" ? "wrong" : proxy.Capability);
            if (invalid == "host") request.Headers.Host = "example.com";
            if (invalid == "origin") request.Headers.Add("Origin", "http://example.com");
            if (invalid == "duplicate") request.Headers.Add(FrontendLoopbackProxy.CapabilityHeader, proxy.Capability);
            using var response = await client.SendAsync(request);
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden
                || invalid == "host" && response.StatusCode == HttpStatusCode.BadRequest);
        }
        Assert.Equal(0, forwarded);
        using var accepted = AuthorizedRequest(proxy, "api/test");
        accepted.Headers.Add("Origin", proxy.ApplicationAddress.TrimEnd('/'));
        accepted.Headers.Add(TrustedAccessHeaders.KeyHeader, "caller-supplied-key-must-be-stripped");
        using var result = await client.SendAsync(accepted);
        Assert.Equal("accepted", await result.Content.ReadAsStringAsync());
        Assert.Equal(1, forwarded);
    }

    [Fact]
    public async Task PageRequestRejectsReplacementPeerBeforeAnyHttpOrSecretBytesAreSent()
    {
        using var own = Process.GetCurrentProcess();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var address = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/");
        var unavailableBackend = new BackendProcessIdentity(own.Id + 100000, own.StartTime.ToUniversalTime().Ticks, Environment.ProcessPath!);
        using var pinned = new HttpClient(BackendProcessConnection.CreateHandler(() => unavailableBackend));
        await using var proxy = new FrontendLoopbackProxy(address, (request, token) =>
        {
            request.Headers.Add(TrustedAccessHeaders.KeyHeader, "default-secret-must-not-be-sent");
            return pinned.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        });
        await proxy.StartAsync();
        using var client = ProxyClient(proxy);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var request = AuthorizedRequest(proxy, "api/test");
        var pending = client.SendAsync(request, deadline.Token);
        using var replacement = await listener.AcceptTcpClientAsync(deadline.Token);
        Assert.Equal(0, await replacement.GetStream().ReadAsync(new byte[1024], deadline.Token));
        using var response = await pending;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task SubscriptionStreamsBeforeCompletionAndBrowserDisconnectCancelsUpstream()
    {
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var backend = await StartBackendAsync(async context =>
        {
            context.Response.ContentType = "application/x-ndjson";
            await context.Response.WriteAsync("{\"value\":1}\n", context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
            try { await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted); }
            catch (OperationCanceledException) { disconnected.TrySetResult(); }
        });
        using var pinned = new HttpClient(BackendProcessConnection.CreateHandler(CurrentProcessIdentity));
        await using var proxy = new FrontendLoopbackProxy(BackendAddress(backend),
            (request, token) => pinned.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token));
        await proxy.StartAsync();
        using var client = ProxyClient(proxy);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var request = AuthorizedRequest(proxy, "api/subscriptions/stream");
        using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var reader = new StreamReader(stream);
            Assert.Equal("{\"value\":1}", await reader.ReadLineAsync(deadline.Token));
        }
        await disconnected.Task.WaitAsync(deadline.Token);
    }

    [Fact]
    public async Task CurrentKeyReplacementTakesEffectInTheSameLiveProxyAndSession()
    {
        var currentKey = "rm_sk_" + new string('a', 64);
        var accepted = new System.Collections.Concurrent.ConcurrentQueue<string>();
        await using var backend = await StartBackendAsync(async context =>
        {
            if (context.Request.Path == "/api/runtime/capabilities")
            {
                Assert.False(context.Request.Headers.ContainsKey(TrustedAccessHeaders.KeyHeader));
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync("{\"profileId\":\"test\"}");
                return;
            }
            var supplied = context.Request.Headers[TrustedAccessHeaders.KeyHeader].ToString();
            if (supplied != Volatile.Read(ref currentKey)) { context.Response.StatusCode = 401; return; }
            accepted.Enqueue(supplied);
            await context.Response.WriteAsync("accepted");
        });
        using var session = new BackendServiceSession(BackendAddress(backend).AbsoluteUri,
            () => Volatile.Read(ref currentKey), CurrentProcessIdentity);
        await session.EnsureConnectedAsync(CancellationToken.None);
        await using var proxy = new FrontendLoopbackProxy(BackendAddress(backend), session.SendFrontendRequestAsync);
        await proxy.StartAsync();
        using var client = ProxyClient(proxy);
        foreach (var replacement in new[] { currentKey, "rm_sk_" + new string('b', 64) })
        {
            Volatile.Write(ref currentKey, replacement);
            using var request = AuthorizedRequest(proxy, "api/test");
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("accepted", await response.Content.ReadAsStringAsync());
        }
        Assert.Equal(new[] { "rm_sk_" + new string('a', 64), "rm_sk_" + new string('b', 64) }, accepted.ToArray());
    }

    [EnvironmentVariableFact("RESOURCE_MANAGER_WEBVIEW2_INTEGRATION_TEST", "1")]
    public async Task RawWebViewControllerWithoutProxyCanInitializeOnThePassiveStaDesktop()
    {
        var temporary = new TrustedAccessTemporaryDirectory();
        var browserExited = false;
        try
        {
            await OnHiddenUiThreadAsync(async () =>
                {
                    using var form = CreatePassiveTestForm();
                    var foreground = GetForegroundWindow();
                    form.Show();
                    Assert.Equal(foreground, GetForegroundWindow());
                    output.WriteLine($"Raw baseline parent: visible={form.Visible}, handle={form.IsHandleCreated}; apartment={Thread.CurrentThread.GetApartmentState()}, context={SynchronizationContext.Current?.GetType().Name}.");
                    var environment = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(
                        userDataFolder: Path.Combine(temporary.Root, "raw-webview")).WaitAsync(TimeSpan.FromSeconds(10));
                    var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    environment.BrowserProcessExited += (_, _) => { browserExited = true; exited.TrySetResult(); };
                    output.WriteLine("Raw baseline environment created with default options and no private proxy.");
                    var creating = environment.CreateCoreWebView2ControllerAsync(form.Handle);
                    Microsoft.Web.WebView2.Core.CoreWebView2Controller? controller = null;
                    try
                    {
                        controller = await creating.WaitAsync(TimeSpan.FromSeconds(5));
                        output.WriteLine("Raw baseline controller created.");
                    }
                    finally
                    {
                        controller?.Close();
                        form.Close();
                        try
                        {
                            if (controller is null)
                            {
                                var lateController = await creating.WaitAsync(TimeSpan.FromSeconds(5));
                                lateController.Close();
                            }
                        }
                        catch (Exception error) { output.WriteLine($"Raw baseline controller cleanup: {error.GetType().Name}."); }
                        try { await exited.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
                        catch (TimeoutException) { output.WriteLine("Raw baseline browser exit not observed before test-host teardown; unique profile retained."); }
                    }
                });
            }
            finally
            {
                if (browserExited) temporary.Dispose();
                else output.WriteLine($"Raw baseline profile retained until owned browser/test host exits: {temporary.Root}");
            }
        }

        [EnvironmentVariableFact("RESOURCE_MANAGER_WEBVIEW2_INTEGRATION_TEST", "1")]
        public async Task OpenWebViewUsesRotatedDefaultKeyWithoutReopeningOrReloading()
        {
            const string firstKey = "rm_sk_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            const string secondKey = "rm_sk_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
            string currentKey = firstKey;
            var acceptedKeys = new System.Collections.Concurrent.ConcurrentQueue<string>();
            await using var backend = await StartBackendAsync(async context =>
            {
                if (context.Request.Path == "/")
                {
                    context.Response.ContentType = "text/html";
                    await context.Response.WriteAsync("<!doctype html><html><body>Native bridge test</body></html>");
                    return;
                }
                if (context.Request.Path == "/api/runtime/capabilities")
                {
                    Assert.False(context.Request.Headers.ContainsKey(TrustedAccessHeaders.KeyHeader));
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync("{\"profileId\":\"test\"}");
                    return;
                }
                var key = context.Request.Headers[TrustedAccessHeaders.KeyHeader].ToString();
                if (key != Volatile.Read(ref currentKey)) { context.Response.StatusCode = 401; return; }
                acceptedKeys.Enqueue(key);
                await context.Response.WriteAsync("accepted");
            });
        using var session = new BackendServiceSession(BackendAddress(backend).AbsoluteUri,
            () => Volatile.Read(ref currentKey), CurrentProcessIdentity);
        await session.EnsureConnectedAsync(CancellationToken.None);
        var temporary = new TrustedAccessTemporaryDirectory();
        var initializationStarted = false;
        var browserExited = false;
        try
        {
        await OnHiddenUiThreadAsync(async () =>
        {
            output.WriteLine("Checking installed WebView2 runtime without the installer/consent path.");
            Assert.False(string.IsNullOrWhiteSpace(Microsoft.Web.WebView2.Core.CoreWebView2Environment.GetAvailableBrowserVersionString()));
            await using var proxy = new FrontendLoopbackProxy(BackendAddress(backend), session.SendFrontendRequestAsync);
            await proxy.StartAsync();
            using var form = CreatePassiveTestForm();
            using var startupLog = new StringWriter();
            using var trace = new TextWriterTraceListener(startupLog);
            using var host = new WebView2FrontendHost(new WebView2HostConfiguration(
                Path.Combine(temporary.Root, "webview"), Path.Combine(temporary.Root, "settings.json"),
                proxy));
            form.Controls.Add(host.View);
            _ = form.Handle;
            _ = host.View.Handle;
            var foreground = GetForegroundWindow();
            form.Show();
            Assert.Equal(foreground, GetForegroundWindow());
            output.WriteLine($"Hosted control: visible={host.View.Visible}, parentVisible={form.Visible}, handle={host.View.IsHandleCreated}; parentHandle={form.IsHandleCreated}.");
            var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Microsoft.Web.WebView2.Core.CoreWebView2Environment? observedEnvironment = null;
            Exception? failure = null;
            Trace.Listeners.Add(trace);
            try
            {
                output.WriteLine("Initializing hidden WebView2 host.");
                initializationStarted = true;
                await host.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(15));
                output.WriteLine("Initialized; navigating original backend origin through the private HTTP proxy.");
                Assert.Equal(BackendAddress(backend).AbsoluteUri, host.ApplicationAddress);
                var navigated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                host.NavigationCompleted += (_, args) =>
                {
                    if (args.IsSuccess && args.IsTrustedDocument) navigated.TrySetResult();
                };
                host.Navigate(host.ApplicationAddress);
                await navigated.Task.WaitAsync(TimeSpan.FromSeconds(15));
                output.WriteLine("Navigated; requesting with first credential.");
                var surface = (Microsoft.Web.WebView2.WinForms.WebView2)host.View;
                var core = surface.CoreWebView2;
                var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                host.MessageReceived += (_, args) => received.TrySetResult(JsonSerializer.Deserialize<string>(args.Json)!);
                const string script = "fetch('/api/test').then(r => r.status === 200 ? r.text() : 'http-' + r.status).then(v => chrome.webview.postMessage(v));";
                await core.ExecuteScriptAsync(script).WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal("accepted", await received.Task.WaitAsync(TimeSpan.FromSeconds(10)));
                Volatile.Write(ref currentKey, secondKey);
                output.WriteLine("Rotated; requesting with replacement credential in the same document.");
                received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                await core.ExecuteScriptAsync(script).WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal("accepted", await received.Task.WaitAsync(TimeSpan.FromSeconds(10)));
                Assert.Same(core, surface.CoreWebView2);
                Assert.Equal(new[] { firstKey, secondKey }, acceptedKeys.ToArray());
            }
            catch (Exception error) { failure = error; output.WriteLine(error.ToString()); throw; }
            finally
            {
                try
                {
                    observedEnvironment = host.EnvironmentForDiagnostics;
                    if (observedEnvironment is not null)
                        observedEnvironment.BrowserProcessExited += (_, _) => { browserExited = true; exited.TrySetResult(); };
                    await host.StopAsync();
                    form.Close();
                    if (observedEnvironment is not null)
                    {
                        output.WriteLine("Disposed; waiting for the owned browser process to exit.");
                        await exited.Task.WaitAsync(TimeSpan.FromSeconds(15));
                    }
                }
                catch (Exception error) when (failure is not null)
                {
                    output.WriteLine($"Hosted browser cleanup: {error.GetType().Name}; preserving original integration failure.");
                }
                finally
                {
                    Trace.Listeners.Remove(trace);
                    output.WriteLine(startupLog.ToString());
                }
            }
        });
        }
        finally
        {
            if (!initializationStarted || browserExited) temporary.Dispose();
            else output.WriteLine($"Hosted profile retained until owned browser/test host exits: {temporary.Root}");
        }
    }

    private static HttpRequestMessage AuthorizedRequest(FrontendLoopbackProxy proxy, string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, proxy.ApplicationAddress + path);
        request.Headers.Add(FrontendLoopbackProxy.CapabilityHeader, proxy.Capability);
        return request;
    }

    private static HttpClient ProxyClient(FrontendLoopbackProxy proxy) => new(new SocketsHttpHandler
    {
        UseProxy = true, Proxy = new WebProxy(proxy.Address), MaxResponseDrainSize = 0,
        AllowAutoRedirect = false
    });

    private sealed class PassiveWebViewTestForm : System.Windows.Forms.Form
    {
        protected override bool ShowWithoutActivation => true;
        protected override System.Windows.Forms.CreateParams CreateParams
        {
            get
            {
                var value = base.CreateParams;
                value.ExStyle |= 0x08000000 | 0x00000080; // No activation; tool window.
                return value;
            }
        }
    }

    private static PassiveWebViewTestForm CreatePassiveTestForm()
    {
        var screens = System.Windows.Forms.Screen.AllScreens;
        return new PassiveWebViewTestForm
        {
            StartPosition = System.Windows.Forms.FormStartPosition.Manual,
            Location = new System.Drawing.Point(screens.Max(screen => screen.Bounds.Right) + 1024,
                screens.Max(screen => screen.Bounds.Bottom) + 1024),
            Size = new System.Drawing.Size(480, 320), ShowInTaskbar = false
        };
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    private static BackendProcessIdentity CurrentProcessIdentity()
    {
        using var process = Process.GetCurrentProcess();
        return new(process.Id, process.StartTime.ToUniversalTime().Ticks, Environment.ProcessPath!);
    }

    private static async Task<WebApplication> StartBackendAsync(RequestDelegate handler)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();
        app.Run(handler);
        await app.StartAsync();
        return app;
    }

    private static Uri BackendAddress(WebApplication app) => new(app.Services.GetRequiredService<IServer>()
        .Features.Get<IServerAddressesFeature>()!.Addresses.Single() + "/");

    private static async Task OnHiddenUiThreadAsync(Func<Task> action)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            _ = System.Windows.Forms.Application.OleRequired();
            using var dispatcher = new System.Windows.Forms.Control();
            using var context = new System.Windows.Forms.ApplicationContext();
            _ = dispatcher.Handle;
            using var synchronization = new System.Windows.Forms.WindowsFormsSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(synchronization);
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await action(); completed.TrySetResult(); }
                catch (Exception error) { completed.TrySetException(error); }
                finally { context.ExitThread(); }
            }));
            System.Windows.Forms.Application.Run(context);
        }) { IsBackground = true, Name = "Native bridge hidden WebView test" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
    }
}
