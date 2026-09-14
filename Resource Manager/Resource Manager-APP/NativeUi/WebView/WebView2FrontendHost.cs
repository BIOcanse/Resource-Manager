using Microsoft.Web.WebView2.Core;
using WebView2Control = Microsoft.Web.WebView2.WinForms.WebView2;

namespace ResourceManager.NativeUi.WebView;

internal sealed class WebView2FrontendHost : IDisposable
{
    private const string BaseBrowserArguments =
        "--disk-cache-size=1048576 --media-cache-size=1 "
        + "--disable-background-networking --disable-component-update "
        + "--disable-sync --disable-default-apps";
    private readonly WebView2HostConfiguration configuration;
    private readonly TrustedFrontendDocumentPolicy documentPolicy;
    private readonly WebView2Control surface;
    private readonly Color backgroundColor;
    private CoreWebView2Environment? environment;
    private CoreWebView2? coreWebView;
    private Task? initializationTask;
    private bool requestFilterAdded;
    private bool eventsAttached;
    private bool trustedTopLevelDocumentActive;
    private bool disposed;

    public WebView2FrontendHost(WebView2HostConfiguration configuration)
    {
        this.configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        documentPolicy = new TrustedFrontendDocumentPolicy(configuration.ApplicationAddress);
        backgroundColor = WebViewAppearanceBootstrap.LoadBackgroundColor(configuration.SettingsPath);
        surface = new WebView2Control
        {
            Dock = DockStyle.Fill,
            BackColor = backgroundColor,
            DefaultBackgroundColor = backgroundColor
        };
    }

    public Control View => surface;

    public Color BackgroundColor => backgroundColor;

    public bool IsReady => coreWebView is not null;

    public event EventHandler<FrontendMessageReceivedEventArgs>? MessageReceived;

    public event EventHandler<FrontendNavigationCompletedEventArgs>? NavigationCompleted;

    public async Task InitializeAsync()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
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
        if (disposed)
        {
            return;
        }

        disposed = true;
        var currentCoreWebView = coreWebView ?? surface.CoreWebView2;
        if (eventsAttached && currentCoreWebView is not null)
        {
            currentCoreWebView.WebResourceRequested -= AttachLoopbackApiAccessToken;
            currentCoreWebView.WebMessageReceived -= HandleWebMessageReceived;
            currentCoreWebView.NavigationStarting -= HandleNavigationStarting;
            currentCoreWebView.NavigationCompleted -= HandleNavigationCompleted;
            currentCoreWebView.SourceChanged -= HandleSourceChanged;
            currentCoreWebView.NewWindowRequested -= HandleNewWindowRequested;
        }

        eventsAttached = false;
        trustedTopLevelDocumentActive = false;
        coreWebView = null;
        environment = null;
        surface.Dispose();
    }

    private async Task InitializeCoreAsync()
    {
        Directory.CreateDirectory(configuration.ProfileDirectory);
        Environment.SetEnvironmentVariable(
            "WEBVIEW2_DEFAULT_BACKGROUND_COLOR",
            WebViewAppearanceBootstrap.ToWebViewEnvironmentValue(backgroundColor));

        var runtime = BrowserRuntimeResolver.Resolve();
        System.Diagnostics.Trace.WriteLine(
            $"WebView2 runtime selected: {runtime.Version} ({runtime.Source})");
        environment ??= await CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: runtime.BrowserExecutableFolder,
            userDataFolder: configuration.ProfileDirectory,
            options: new CoreWebView2EnvironmentOptions(ResolveBrowserArguments()));

        await surface.EnsureCoreWebView2Async(environment);
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
                configuration.LoopbackApiRequestFilter,
                CoreWebView2WebResourceContext.All);
            requestFilterAdded = true;
        }

        if (!eventsAttached)
        {
            nextCoreWebView.WebResourceRequested += AttachLoopbackApiAccessToken;
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

    private void AttachLoopbackApiAccessToken(
        object? sender,
        CoreWebView2WebResourceRequestedEventArgs args)
    {
        if (!trustedTopLevelDocumentActive
            || !documentPolicy.IsTrustedApiRequest(args.Request.Uri))
        {
            return;
        }

        var accessToken = configuration.AccessTokenProvider();
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return;
        }

        args.Request.Headers.SetHeader(
            configuration.AccessTokenHeaderName,
            accessToken);
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
    string ApplicationAddress,
    string LoopbackApiRequestFilter,
    string AccessTokenHeaderName,
    Func<string?> AccessTokenProvider);

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
