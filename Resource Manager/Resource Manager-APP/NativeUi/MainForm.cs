using ResourceManager.NativeUi.WebView;

namespace ResourceManager.NativeUi;

public sealed partial class MainForm : Form
{
    private bool showWithoutActivation;
    private const int WsSysMenu = 0x00080000;
    private const int WsThickFrame = 0x00040000;
    private const int WsMinimizeBox = 0x00020000;
    private const int WsMaximizeBox = 0x00010000;

    private readonly WebView2FrontendHost frontendHost;
    private readonly BackendAvailabilityPanel backendAvailabilityPanel;
    private Task<bool>? frontendHostInitializationTask;
    private bool appNavigationStarted;
    private bool backendContentEnabled;
    private FrontendConnectionState frontendConnectionState =
        FrontendConnectionState.NotStarted;
    private string? frontendConnectionDetail;
    private bool? lastPublishedHostVisibility;
    private readonly ResizeGripMessageFilter resizeGripMessageFilter;
    private readonly Action<bool> taskManagerShortcutReplacementChanged;
    private readonly Action editableHotkeysChanged;
    private string? loopbackApiAccessToken;
    private FrontendBackendSessionProjection? backendSessionProjection;

    internal MainForm(
        Action<bool> taskManagerShortcutReplacementChanged,
        Action editableHotkeysChanged)
    {
        this.taskManagerShortcutReplacementChanged = taskManagerShortcutReplacementChanged;
        this.editableHotkeysChanged = editableHotkeysChanged;
        frontendHost = new WebView2FrontendHost(
            new WebView2HostConfiguration(
                NativeUiPaths.WebViewUserDataFolder,
                NativeUiPaths.AppSettingsPath,
                AppUrl,
                ApiRequestFilter,
                AccessTokenHeaderName,
                () => loopbackApiAccessToken));
        frontendHost.MessageReceived += HandleFrontendMessageReceived;
        frontendHost.NavigationCompleted += HandleFrontendNavigationCompleted;
        Text = "资源管理器";
        StartPosition = FormStartPosition.Manual;
        MinimumSize = new Size(760, 520);
        Size = WindowPlacement.GetPreferredPrimaryScreenSize(MinimumSize);
        FormBorderStyle = FormBorderStyle.None;
        Padding = Padding.Empty;
        BackColor = frontendHost.BackgroundColor;

        Controls.Add(frontendHost.View);
        backendAvailabilityPanel = new BackendAvailabilityPanel();
        backendAvailabilityPanel.RetryRequested += (_, _) =>
            BackendRetryRequested?.Invoke(this, EventArgs.Empty);
        backendAvailabilityPanel.DiagnosticsRequested += (_, _) =>
            DiagnosticsRequested?.Invoke(this, EventArgs.Empty);
        backendAvailabilityPanel.ExitRequested += (_, _) =>
            ExitRequested?.Invoke(this, EventArgs.Empty);
        Controls.Add(backendAvailabilityPanel);
        frontendHost.View.Visible = false;
        resizeGripMessageFilter = new ResizeGripMessageFilter(this);
        Application.AddMessageFilter(resizeGripMessageFilter);
    }

    internal event EventHandler? BackendRetryRequested;

    internal event EventHandler<FrontendConnectionStateChangedEventArgs>?
        FrontendConnectionStateChanged;

    internal event EventHandler? DiagnosticsRequested;

    internal event EventHandler? ExitRequested;

    public void PrepareForOpenOnPrimaryScreen()
    {
        WindowPlacement.PlaceForOpenOnPrimaryScreen(this);
    }

    internal bool PrepareForPassiveValidationOnSecondaryScreen()
    {
        return WindowPlacement.TryPlaceForPassiveValidationOnSecondaryScreen(this);
    }

    public async Task StartFrontendAsync()
    {
        if (!backendContentEnabled)
        {
            return;
        }

        if (!appNavigationStarted)
        {
            ShowFrontendLoading();
        }

        if (await EnsureFrontendHostAsync()
            && backendContentEnabled
            && frontendHost.IsReady)
        {
            PublishHostVisibility();
            if (!appNavigationStarted)
            {
                appNavigationStarted = true;
                frontendHost.Navigate(AppUrl);
            }
        }
    }

    protected override bool ShowWithoutActivation => showWithoutActivation;

    internal void ShowPassively()
    {
        showWithoutActivation = true;
        try
        {
            Show();
        }
        finally
        {
            showWithoutActivation = false;
        }
    }

    public void StopPolling()
    {
        PublishHostVisibility(forceHidden: true);
    }

    internal Task HideToBackgroundAsync()
    {
        StopPolling();
        Hide();
        return Task.CompletedTask;
    }

    public void SetStatusText(string text)
    {
        Text = string.IsNullOrWhiteSpace(text)
            ? "资源管理器"
            : $"资源管理器 - {text}";
    }

    public void SetLoopbackApiAccessToken(string? accessToken)
    {
        loopbackApiAccessToken = string.IsNullOrWhiteSpace(accessToken)
            ? null
            : accessToken;
    }

    internal void ShowBackendConnecting(bool reconnecting)
    {
        DisableBackendContent();
        SetStatusText(reconnecting ? "正在重新连接本地服务" : "正在连接本地服务");
        backendAvailabilityPanel.ShowConnecting(reconnecting);
    }

    internal void ShowBackendUnavailable(
        string detail,
        FrontendBackendSessionProjection projection)
    {
        ArgumentNullException.ThrowIfNull(projection);
        if (projection.IsReady)
        {
            throw new ArgumentException(
                "An unavailable backend projection is required.",
                nameof(projection));
        }

        backendSessionProjection = projection;
        PublishBackendSessionProjection();
        DisableBackendContent();
        SetStatusText("本地服务不可用");
        backendAvailabilityPanel.ShowUnavailable(detail);
    }

    internal void ShowBackendReady(
        string? accessToken,
        FrontendBackendSessionProjection projection)
    {
        ArgumentNullException.ThrowIfNull(projection);
        if (!projection.IsReady)
        {
            throw new ArgumentException(
                "A ready backend projection is required.",
                nameof(projection));
        }

        backendSessionProjection = projection;
        SetLoopbackApiAccessToken(accessToken);
        backendContentEnabled = true;
        appNavigationStarted = false;
        frontendHost.View.Visible = false;
        SetStatusText("本地服务已就绪");
        SetFrontendConnectionState(FrontendConnectionState.NotStarted);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Application.RemoveMessageFilter(resizeGripMessageFilter);
            frontendHost.MessageReceived -= HandleFrontendMessageReceived;
            frontendHost.NavigationCompleted -= HandleFrontendNavigationCompleted;
            frontendHost.Dispose();
        }

        base.Dispose(disposing);
    }

    private void DisableBackendContent()
    {
        backendContentEnabled = false;
        SetLoopbackApiAccessToken(null);
        StopPolling();
        appNavigationStarted = false;
        lastPublishedHostVisibility = null;
        frontendHost.View.Visible = false;
        SetFrontendConnectionState(FrontendConnectionState.NotStarted);
        if (!frontendHost.IsReady)
        {
            return;
        }

        try
        {
            frontendHost.Navigate("about:blank");
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void ShowFrontendLoading()
    {
        frontendHost.View.Visible = false;
        SetStatusText("正在加载界面");
        backendAvailabilityPanel.ShowFrontendLoading();
        SetFrontendConnectionState(FrontendConnectionState.Loading);
    }

    private void ShowFrontendReady()
    {
        backendAvailabilityPanel.Visible = false;
        frontendHost.View.Visible = true;
        SetStatusText("已就绪");
        SetFrontendConnectionState(FrontendConnectionState.Ready);
    }

    private void ShowFrontendUnavailable(string detail)
    {
        frontendHost.View.Visible = false;
        SetStatusText("界面不可用");
        backendAvailabilityPanel.ShowFrontendUnavailable(detail);
        SetFrontendConnectionState(FrontendConnectionState.Unavailable, detail);
    }

    private void SetFrontendConnectionState(
        FrontendConnectionState state,
        string? detail = null)
    {
        var normalizedDetail = string.IsNullOrWhiteSpace(detail) ? null : detail;
        if (frontendConnectionState == state
            && string.Equals(
                frontendConnectionDetail,
                normalizedDetail,
                StringComparison.Ordinal))
        {
            return;
        }

        frontendConnectionState = state;
        frontendConnectionDetail = normalizedDetail;
        FrontendConnectionStateChanged?.Invoke(
            this,
            new FrontendConnectionStateChangedEventArgs(state, normalizedDetail));
    }

    protected override void OnVisibleChanged(EventArgs args)
    {
        base.OnVisibleChanged(args);
        PublishHostVisibility();
    }

    protected override void OnResize(EventArgs args)
    {
        base.OnResize(args);
        PublishHostVisibility();
    }
}
