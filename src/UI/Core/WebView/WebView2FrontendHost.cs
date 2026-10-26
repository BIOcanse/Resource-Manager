using Microsoft.Web.WebView2.Core;
using WebView2Control = Microsoft.Web.WebView2.WinForms.WebView2;

namespace ResourceManager.NativeUi.WebView;

internal sealed class WebView2FrontendHost : IFrontendHost
{
    private const string BaseBrowserArguments =
        "--disk-cache-size=1048576 --media-cache-size=1 "
        + "--disable-background-networking --disable-component-update "
        + "--disable-sync --disable-default-apps";
    private readonly WebView2HostConfiguration configuration;
    private readonly TrustedFrontendDocumentPolicy documentPolicy;
    private readonly FrontendLoopbackProxy proxy;
    private readonly WebView2Control surface;
    private readonly Color backgroundColor;
    private readonly CancellationTokenSource lifetime = new();
    private CoreWebView2Environment? environment;
    private CoreWebView2? coreWebView;
    private Task? initializationTask;
    private Task? stopTask;
    private TaskCompletionSource? browserExited;
    private bool requestFilterAdded;
    private bool eventsAttached;
    private bool trustedTopLevelDocumentActive;
    private bool disposed;
    private bool stopping;
    private bool browserExitRequired;

    public WebView2FrontendHost(WebView2HostConfiguration configuration, Color? initialBackgroundColor = null)
    {
        this.configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        proxy = configuration.Proxy;
        documentPolicy = new TrustedFrontendDocumentPolicy(proxy.ApplicationAddress);
        backgroundColor = initialBackgroundColor
            ?? WebViewAppearanceBootstrap.LoadBackgroundColor(configuration.SettingsPath);
        surface = new WebView2Control
        {
            Dock = DockStyle.Fill,
            BackColor = backgroundColor,
            DefaultBackgroundColor = backgroundColor
        };
    }

    public Control View => surface;

    public Color BackgroundColor => backgroundColor;

    public bool IsReady => !stopping && coreWebView is not null;

    public string ApplicationAddress => proxy.ApplicationAddress;

    internal CoreWebView2Environment? EnvironmentForDiagnostics => environment;

    /// <summary>
    /// 把键盘焦点交给页面。
    ///
    /// **窗口是活动窗口的时候，焦点就该在页面里。** 这个窗口平时只有页面这一块内容，
    /// 焦点停在别处（或者干脆没有）唯一的后果，就是用户得先在页面里点一下、
    /// 滚轮和键盘才开始生效 —— 那一下点击不传达任何意图，纯粹是在补我们没做的事。
    ///
    /// 页面没显示出来的时候不抢：那时候台面上是后端不可用面板，
    /// 焦点归它的按钮，用户可能正在用键盘操作它们。
    /// </summary>
    public void FocusContent()
    {
        if (disposed
            || !surface.IsHandleCreated
            || !surface.Visible
            || !IsReady)
        {
            return;
        }

        // WinForms 的 WebView2 在拿到焦点时会自己把焦点移进网页内容里，
        // 所以聚焦这个控件就够了，不必去碰底层的 controller。
        surface.Focus();
    }

    public event EventHandler<FrontendMessageReceivedEventArgs>? MessageReceived;

    public event EventHandler<FrontendNavigationCompletedEventArgs>? NavigationCompleted;

    public async Task InitializeAsync()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (stopping) throw new InvalidOperationException("The frontend host is stopping.");
        if (IsReady)
        {
            return;
        }

        var task = initializationTask ??= InitializeCoreAsync();
        try
        {
            await task;
        }
        catch
        {
            if (ReferenceEquals(initializationTask, task))
            {
                initializationTask = null;
            }

            throw;
        }
    }

    public void Navigate(string address)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        if (!documentPolicy.IsTrustedDocument(address)
            && !TrustedFrontendDocumentPolicy.IsBlankDocument(address))
        {
            throw new ArgumentException(
                "The frontend host accepts only the configured application document or about:blank.",
                nameof(address));
        }

        GetCoreWebView().Navigate(address);
    }

    public void PostStringMessage(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        EnsureTrustedDocumentActive();
        GetCoreWebView().PostWebMessageAsString(message);
    }

    public void PostJsonMessage(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        EnsureTrustedDocumentActive();
        GetCoreWebView().PostWebMessageAsJson(json);
    }

    public void Dispose()
    {
        if (disposed) return;
        var stopped = StopAsync();
        if (!stopped.IsCompleted)
            throw new InvalidOperationException("Await StopAsync on the UI thread before disposing an initialized frontend host.");
        stopped.GetAwaiter().GetResult();
    }

    public Task StopAsync()
    {
        if (stopTask is not null) return stopTask;
        stopping = true;
        lifetime.Cancel();
        return stopTask = StopCoreAsync();
    }

    private async Task StopCoreAsync()
    {
        try
        {
            await FrontendHostShutdown.DrainAsync(initializationTask,
                CloseSurface, WaitForBrowserExitAsync);
        }
        finally { lifetime.Dispose(); }
    }

    private void CloseSurface()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        var currentCoreWebView = coreWebView ?? surface.CoreWebView2;
        if (eventsAttached && currentCoreWebView is not null)
        {
            currentCoreWebView.WebResourceRequested -= AuthorizePrivateProxyRequest;
            currentCoreWebView.WebMessageReceived -= HandleWebMessageReceived;
            currentCoreWebView.NavigationStarting -= HandleNavigationStarting;
            currentCoreWebView.NavigationCompleted -= HandleNavigationCompleted;
            currentCoreWebView.SourceChanged -= HandleSourceChanged;
            currentCoreWebView.NewWindowRequested -= HandleNewWindowRequested;
        }

        eventsAttached = false;
        trustedTopLevelDocumentActive = false;
        coreWebView = null;
        browserExitRequired = environment is not null
            && (browserExited?.Task.IsCompleted == true || environment.GetProcessInfos().Count != 0);
        surface.Dispose();
    }

    private async Task WaitForBrowserExitAsync()
    {
        var currentEnvironment = environment;
        if (currentEnvironment is null) return;
        try
        {
            // Initialization has joined, so no new controller can be created.
            // With no remaining processes there is no future exit to await.
            if (browserExited is { } exit && browserExitRequired)
                await exit.Task;
        }
        finally
        {
            currentEnvironment.BrowserProcessExited -= HandleBrowserProcessExited;
            environment = null;
        }
    }

    private void HandleBrowserProcessExited(object? sender, CoreWebView2BrowserProcessExitedEventArgs args)
        => browserExited?.TrySetResult();

    private async Task InitializeCoreAsync()
    {
        var proxyAddress = await proxy.WaitForAddressAsync(lifetime.Token);
        lifetime.Token.ThrowIfCancellationRequested();
        System.Diagnostics.Trace.WriteLine("WebView2 private HTTP proxy endpoint received.");
        Directory.CreateDirectory(configuration.ProfileDirectory);
        Environment.SetEnvironmentVariable(
            "WEBVIEW2_DEFAULT_BACKGROUND_COLOR",
            WebViewAppearanceBootstrap.ToWebViewEnvironmentValue(backgroundColor));

        var runtime = await BrowserRuntimeResolver.ResolveAsync();
        lifetime.Token.ThrowIfCancellationRequested();
        System.Diagnostics.Trace.WriteLine(
            $"WebView2 runtime selected: {runtime.Version} ({runtime.Source})");
        environment ??= await CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: runtime.BrowserExecutableFolder,
            userDataFolder: configuration.ProfileDirectory,
            options: new CoreWebView2EnvironmentOptions(ResolveBrowserArguments()
                + $" --proxy-server={proxyAddress.TrimEnd('/')} --proxy-bypass-list=<-loopback>"));
        browserExited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        environment.BrowserProcessExited -= HandleBrowserProcessExited;
        environment.BrowserProcessExited += HandleBrowserProcessExited;
        lifetime.Token.ThrowIfCancellationRequested();
        System.Diagnostics.Trace.WriteLine("WebView2 environment created.");
        System.Diagnostics.Trace.WriteLine($"WebView2 controller thread: {System.Environment.CurrentManagedThreadId}; apartment: {Thread.CurrentThread.GetApartmentState()}; context: {SynchronizationContext.Current?.GetType().FullName ?? "none"}.");

        await surface.EnsureCoreWebView2Async(environment);
        System.Diagnostics.Trace.WriteLine("WebView2 controller created.");
        var nextCoreWebView = surface.CoreWebView2
            ?? throw new InvalidOperationException("WebView2 did not provide a CoreWebView2 instance.");
        var developerSurfacesEnabled = ShouldEnableDeveloperSurfaces();
        nextCoreWebView.Settings.AreDefaultContextMenusEnabled =
            developerSurfacesEnabled;
        nextCoreWebView.Settings.AreDevToolsEnabled = developerSurfacesEnabled;
#if DEBUG
        var performanceBootstrap =
            WebViewDeveloperOptions.CreateFrontendPerformanceBootstrapScript(
                Environment.GetEnvironmentVariable(
                    WebViewDeveloperOptions.FrontendPerformanceCapacityEnvironmentVariable));
        if (performanceBootstrap is not null)
        {
            await nextCoreWebView.AddScriptToExecuteOnDocumentCreatedAsync(
                performanceBootstrap);
        }
#endif
        if (!requestFilterAdded)
        {
            nextCoreWebView.AddWebResourceRequestedFilter(
                proxy.ApplicationAddress + "*",
                CoreWebView2WebResourceContext.All,
                CoreWebView2WebResourceRequestSourceKinds.Document);
            requestFilterAdded = true;
        }

        if (!eventsAttached)
        {
            nextCoreWebView.WebResourceRequested += AuthorizePrivateProxyRequest;
            nextCoreWebView.WebMessageReceived += HandleWebMessageReceived;
            nextCoreWebView.NavigationStarting += HandleNavigationStarting;
            nextCoreWebView.NavigationCompleted += HandleNavigationCompleted;
            nextCoreWebView.SourceChanged += HandleSourceChanged;
            nextCoreWebView.NewWindowRequested += HandleNewWindowRequested;
            eventsAttached = true;
        }

        coreWebView = nextCoreWebView;
    }

    private static bool ShouldEnableDeveloperSurfaces()
    {
#if DEBUG
        return string.Equals(
            Environment.GetEnvironmentVariable(
                "RESOURCE_MANAGER_WEBVIEW_DEVELOPER_SURFACES"),
            "1",
            StringComparison.Ordinal);
#else
        return false;
#endif
    }

    private static string ResolveBrowserArguments()
    {
#if DEBUG
        return WebViewDeveloperOptions.AddLoopbackRemoteDebugging(
            BaseBrowserArguments,
            Environment.GetEnvironmentVariable(
                WebViewDeveloperOptions.RemoteDebuggingPortEnvironmentVariable));
#else
        return BaseBrowserArguments;
#endif
    }

    private CoreWebView2 GetCoreWebView()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return coreWebView
            ?? throw new InvalidOperationException("The frontend host is not initialized.");
    }

    private void EnsureTrustedDocumentActive()
    {
        if (!trustedTopLevelDocumentActive)
        {
            throw new InvalidOperationException(
                "The privileged frontend bridge is unavailable outside the application document.");
        }
    }

    private void AuthorizePrivateProxyRequest(
        object? sender,
        CoreWebView2WebResourceRequestedEventArgs args)
    {
        if (!trustedTopLevelDocumentActive
            || !documentPolicy.IsTrustedResourceRequest(args.Request.Uri))
        {
            return;
        }

        args.Request.Headers.SetHeader(
            FrontendLoopbackProxy.CapabilityHeader,
            proxy.Capability);
    }

    private void HandleWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        if (!trustedTopLevelDocumentActive
            || !documentPolicy.IsTrustedDocument(args.Source))
        {
            return;
        }

        MessageReceived?.Invoke(this, new FrontendMessageReceivedEventArgs(args.WebMessageAsJson));
    }

    private void HandleNavigationStarting(
        object? sender,
        CoreWebView2NavigationStartingEventArgs args)
    {
        if (documentPolicy.IsTrustedDocument(args.Uri))
        {
            trustedTopLevelDocumentActive = true;
            return;
        }

        trustedTopLevelDocumentActive = false;
        if (!TrustedFrontendDocumentPolicy.IsBlankDocument(args.Uri))
        {
            args.Cancel = true;
        }
    }

    private void HandleNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        var trustedDocument = args.IsSuccess
            && documentPolicy.IsTrustedDocument(GetCoreWebView().Source);
        trustedTopLevelDocumentActive = trustedDocument;
        NavigationCompleted?.Invoke(
            this,
            new FrontendNavigationCompletedEventArgs(
                args.IsSuccess,
                args.WebErrorStatus == CoreWebView2WebErrorStatus.ConnectionAborted,
                trustedDocument));
    }

    private void HandleSourceChanged(object? sender, CoreWebView2SourceChangedEventArgs args)
    {
        if (!documentPolicy.IsTrustedDocument(GetCoreWebView().Source))
        {
            trustedTopLevelDocumentActive = false;
        }
    }

    private void HandleNewWindowRequested(
        object? sender,
        CoreWebView2NewWindowRequestedEventArgs args)
    {
        args.Handled = true;
        try
        {
            ExternalBrowserLink.TryOpen(args.Uri, trustedTopLevelDocumentActive,
                args.IsUserInitiated, static start =>
                {
                    using var process = System.Diagnostics.Process.Start(start);
                });
        }
        catch (System.ComponentModel.Win32Exception error)
        {
            System.Diagnostics.Trace.WriteLine(error);
        }
    }
}

internal sealed record WebView2HostConfiguration(
    string ProfileDirectory,
    string SettingsPath,
    FrontendLoopbackProxy Proxy);

internal sealed class FrontendMessageReceivedEventArgs(string json) : EventArgs
{
    public string Json { get; } = json;
}

internal sealed class FrontendNavigationCompletedEventArgs(
    bool isSuccess,
    bool wasConnectionAborted,
    bool isTrustedDocument) : EventArgs
{
    public bool IsSuccess { get; } = isSuccess;

    public bool WasConnectionAborted { get; } = wasConnectionAborted;

    public bool IsTrustedDocument { get; } = isTrustedDocument;
}
