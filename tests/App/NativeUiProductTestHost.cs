using System.Collections.Concurrent;
using System.Drawing;
using System.Net.Http;
using System.Reflection;
using System.Threading.Channels;
using System.Windows.Forms;
using ResourceManager.NativeUi;
using ResourceManager.NativeUi.Blocks;
using ResourceManager.NativeUi.Preferences;
using ResourceManager.NativeUi.WebView;

namespace Resource_Manager_APP.Tests;

// Runs the actual application context and message loop. Only external resources
// are substituted; command routing, block entries and window composition are production code.
internal sealed class NativeUiProductTestHost : IAsyncDisposable
{
    private readonly TaskCompletionSource ready = Signal();
    private readonly TaskCompletionSource exited = Signal();
    private readonly Thread thread;
    private readonly bool startInBackground;
    private readonly bool failFirstPresentation;
    private Control dispatcher = null!;
    private NativeUiSingleInstanceCoordinator instance = null!;
    private readonly ConcurrentDictionary<BackendServiceSession, SessionRun> generations = new();
    internal TaskCompletionSource AllowConnect { get; } = Signal();
    internal TaskCompletionSource AllowReconnect { get; } = Signal();
    internal TaskCompletionSource ReleaseBrowser { get; } = Signal();
    internal TaskCompletionSource ReleaseTray { get; } = Signal();
    internal Channel<Browser> Browsers { get; } = Channel.CreateUnbounded<Browser>();
    internal ConcurrentQueue<MainForm> PresentedWindows { get; } = new();
    internal Channel<SessionRun> Sessions { get; } = Channel.CreateUnbounded<SessionRun>();
    internal Channel<(string Id, BackendServiceSession Session)> Consumers { get; } = Channel.CreateUnbounded<(string, BackendServiceSession)>();
    internal TaskCompletionSource TrayStopping { get; } = Signal();
    internal ResourceManagerApplicationContext Context { get; private set; } = null!;
    internal NativeUiBlockTrees Trees => Field<NativeUiBlockTrees>("trees");
    internal Task Exited => exited.Task;
    internal BusinessBlocksSnapshot? ExitSnapshot { get; private set; }
    internal NativeUiBackendConnectionBlock Connection => Field<NativeUiBackendConnectionBlock>("backendConnection");
    internal MainForm Window => Field<MainForm>("mainForm");
    internal FrontendLoopbackProxy Proxy => Field<FrontendLoopbackProxy>("frontendProxy");

    private int connectionAttempts;
    private readonly Func<CancellationToken, Task>? beforeProxyStart;
    private NativeUiProductTestHost(bool delayedConnection, bool slowBrowser, bool slowTray, bool startInBackground,
        bool pauseReconnect, bool failFirstPresentation, Func<CancellationToken, Task>? beforeProxyStart)
    {
        this.beforeProxyStart = beforeProxyStart;
        this.startInBackground = startInBackground;
        this.failFirstPresentation = failFirstPresentation;
        if (!delayedConnection) AllowConnect.SetResult();
        if (!pauseReconnect) AllowReconnect.SetResult();
        if (!slowBrowser) ReleaseBrowser.SetResult();
        if (!slowTray) ReleaseTray.SetResult();
        thread = new Thread(Run) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
    }

    internal static async Task<NativeUiProductTestHost> StartAsync(bool delayedConnection = false,
        bool slowBrowser = false, bool slowTray = false, bool startInBackground = false, bool pauseReconnect = false,
        bool failFirstPresentation = false, Func<CancellationToken, Task>? beforeProxyStart = null)
    {
        var host = new NativeUiProductTestHost(delayedConnection, slowBrowser, slowTray, startInBackground, pauseReconnect,
            failFirstPresentation, beforeProxyStart);
        host.thread.Start();
        try { await host.ready.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch { await host.exited.Task; throw; }
        return host;
    }

    private void Run()
    {
        try
        {
            using var synchronization = new WindowsFormsSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(synchronization);
            using var ui = new Control();
            _ = ui.Handle;
            dispatcher = ui;
            var identity = Guid.NewGuid().ToString("N");
            using var primary = NativeUiSingleInstanceCoordinator.Create("Local\\RM.UiPilot2." + identity, "RM.UiPilot2." + identity);
            instance = primary;
            var scratch = Environment.GetEnvironmentVariable("RM_REVIEW_SCRATCH")
                ?? throw new InvalidOperationException("A registered validation scratch root is required.");
            var root = Path.Combine(scratch, "product-ui-" + identity);
            Directory.CreateDirectory(root);
            var services = new NativeUiApplicationServices
            {
                ApiBaseAddress = "http://127.0.0.1:19421",
                WebViewUserDataFolder = Path.Combine(root, "profile"),
                FrontendPreferencesPath = Path.Combine(root, "preferences.json"),
                Preferences = new FrontendPreferencesOwner(Path.Combine(root, "preferences.json")),
                Hotkeys = new NoHotkeys(),
                BeforeProxyStart = beforeProxyStart,
                CreateSession = CreateSession,
                ConnectSession = async (session, token) =>
                {
                    generations[session].Monitor.Start();
                    await AllowConnect.Task.WaitAsync(token);
                    if (Interlocked.Increment(ref connectionAttempts) > 1)
                        await AllowReconnect.Task.WaitAsync(token);
                },
                ReconnectDelay = (_, token) => Task.CompletedTask,
                CreateFrontendHost = configuration =>
                {
                    var browser = new Browser(configuration.Proxy, ReleaseBrowser.Task,
                        () => Window.Visible);
                    Browsers.Writer.TryWrite(browser);
                    return browser;
                },
                CreateTrayIcon = menu => new NotifyIcon { ContextMenuStrip = menu, Visible = false },
                PresentWindow = (form, _, _) =>
                {
                    PresentedWindows.Enqueue(form);
                    if (failFirstPresentation && PresentedWindows.Count == 1)
                    {
                        _ = form.Handle;
                        throw new InvalidOperationException("Window presentation failed.");
                    }
                    form.ShowInTaskbar = false;
                    form.Location = new Point(-30000, -30000);
                    form.ShowPassively();
                },
                ObserveDesktop = (session, token) => Observe("desktop", session, token),
                ObserveOverlay = (session, token) => Observe("overlay", session, token),
                ObserveTray = (session, token) => Observe("tray", session, token)
            };
            using var context = new ResourceManagerApplicationContext(primary, startInBackground, services);
            Context = context;
            context.ThreadExit += (_, _) => ExitSnapshot = Trees.Read();
            ready.TrySetResult();
            Application.Run(context);
            exited.TrySetResult();
        }
        catch (Exception error)
        {
            ready.TrySetException(error);
            exited.TrySetException(error);
        }
    }

    private BackendServiceSession CreateSession()
    {
        var loss = new TaskCompletionSource<BackendServiceUnavailableEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        var monitor = new BackendServiceAvailabilityMonitor(_ => Task.FromResult(true),
            token => loss.Task.WaitAsync(token),
            delayAsync: (_, token) => Task.Delay(Timeout.InfiniteTimeSpan, token));
        var handlers = new List<TrackingHandler>();
        var session = new BackendServiceSession("http://127.0.0.1:19421", () => string.Empty, () => null, monitor,
            () => { var handler = new TrackingHandler(); handlers.Add(handler); return handler; });
        var run = new SessionRun(session, monitor, loss, handlers);
        generations[session] = run;
        Sessions.Writer.TryWrite(run);
        return session;
    }

    private async Task Observe(string id, BackendServiceSession session, CancellationToken token)
    {
        Consumers.Writer.TryWrite((id, session));
        try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            if (id == "tray")
            {
                TrayStopping.TrySetResult();
                await ReleaseTray.Task;
            }
        }
    }

    internal Task InvokeAsync(Action action)
    {
        var completion = Signal();
        dispatcher.BeginInvoke(new Action(() =>
        {
            try { action(); completion.TrySetResult(); }
            catch (Exception error) { completion.TrySetException(error); }
        }));
        return completion.Task;
    }

    internal Task TrayExitAsync() => InvokeAsync(() => Field<ToolStripMenuItem>("exitMenuItem").PerformClick());
    internal Task WindowCloseAsync() => InvokeAsync(() => Window.Close());
    internal Task SessionEndedAsync() => SystemSessionMessageAsync(0x0016, 1);
    internal async Task<nint> SystemSessionMessageAsync(uint message, nint confirmed)
    {
        // Exercise the actual SystemEvents subscription, including background startup.
        // The .NET implementation owns one private HWND inside this test process.
        var type = typeof(Microsoft.Win32.SystemEvents);
        var systemEvents = type.GetField("s_systemEvents", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var handle = (nint)type.GetField("_windowHandle", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(systemEvents)!;
        Assert.NotEqual(nint.Zero, handle);
        var result = await Task.Run(() => SendMessage(handle, message, confirmed, 0));
        await InvokeAsync(() => { }); // Process the product handler's UI dispatch.
        return result;
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);
    internal async Task SendCommandAsync(string command)
    {
        await Trees.WaitForStateAsync(NativeUiBlockIds.SingleInstance, BusinessBlockState.Running).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(await Task.Run(() => instance.SignalRequest(command)));
    }
    internal async Task<Browser> NextBrowserAsync()
        => await Browsers.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
    internal async Task<SessionRun> NextSessionAsync()
        => await Sessions.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
    private T Field<T>(string name) => (T)typeof(ResourceManagerApplicationContext)
        .GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Context)!;

    public async ValueTask DisposeAsync()
    {
        ReleaseBrowser.TrySetResult();
        ReleaseTray.TrySetResult();
        AllowConnect.TrySetResult();
        AllowReconnect.TrySetResult();
        if (!Exited.IsCompleted)
        {
            var request = InvokeAsync(Context.ExitThread);
            await Task.WhenAny(request, Exited).WaitAsync(TimeSpan.FromSeconds(10));
        }
        await Exited.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)), "Product message loop did not finish.");
    }

    internal sealed record SessionRun(BackendServiceSession Session, BackendServiceAvailabilityMonitor Monitor,
        TaskCompletionSource<BackendServiceUnavailableEventArgs> Loss, IReadOnlyList<TrackingHandler> Handlers);
    internal sealed class TrackingHandler : HttpMessageHandler
    {
        private int disposals;
        internal int Disposals => Volatile.Read(ref disposals);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            => throw new InvalidOperationException("The isolated fixture never contacts a backend.");
        protected override void Dispose(bool disposing)
        {
            if (disposing) Interlocked.Increment(ref disposals);
            base.Dispose(disposing);
        }
    }
    internal sealed class Browser(FrontendLoopbackProxy proxy, Task browserExit, Func<bool> windowVisible) : IFrontendHost
    {
        private Task? stopping;
        internal TaskCompletionSource Initialized { get; } = Signal();
        internal TaskCompletionSource SurfaceClosed { get; } = Signal();
        internal bool VisibleWhenInitialized { get; private set; }
        public Control View { get; } = new Panel();
        public Color BackgroundColor => Color.Black;
        public bool IsReady { get; private set; }
        public string ApplicationAddress => proxy.ApplicationAddress;
        public event EventHandler<FrontendMessageReceivedEventArgs>? MessageReceived { add { } remove { } }
        public event EventHandler<FrontendNavigationCompletedEventArgs>? NavigationCompleted { add { } remove { } }
        public Task InitializeAsync()
        {
            VisibleWhenInitialized = windowVisible();
            IsReady = true;
            Initialized.TrySetResult();
            return Task.CompletedTask;
        }
        public Task StopAsync() => stopping ??= StopCoreAsync();
        private async Task StopCoreAsync()
        {
            IsReady = false;
            View.Dispose();
            SurfaceClosed.TrySetResult();
            await browserExit;
        }
        public void Dispose() => View.Dispose();
        public void Navigate(string address) { }
        public void PostStringMessage(string message) { }
        public void PostJsonMessage(string json) { }
        public void FocusContent() { }
    }
    private sealed class NoHotkeys : INativeUiHotkeys
    {
        public void Start() { }
        public void SetTaskManagerEnabled(bool enabled) { }
        public void ReloadForceTerminate() { }
        public void Dispose() { }
    }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
