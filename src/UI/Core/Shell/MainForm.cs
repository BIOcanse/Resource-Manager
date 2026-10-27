using ResourceManager.NativeUi.Localization;
using ResourceManager.NativeUi.WebView;

namespace ResourceManager.NativeUi;

public sealed partial class MainForm : Form
{
    private bool showWithoutActivation;

    private readonly IFrontendHost frontendHost;
    private readonly BackendAvailabilityPanel backendAvailabilityPanel;
    private Task<bool>? frontendHostInitializationTask;
    private bool appNavigationStarted;
    private bool backendContentEnabled;
    private FrontendConnectionState frontendConnectionState =
        FrontendConnectionState.NotStarted;
    private string? frontendConnectionDetail;
    private bool? lastPublishedHostVisibility;
    private bool desktopOccluded;
    private readonly ResizeGripMessageFilter resizeGripMessageFilter;
    private readonly Action<bool> taskManagerShortcutReplacementChanged;
    private readonly Action editableHotkeysChanged;
    // Omitted, startup failures go to the package's per-user diagnostics file.
    private readonly string? startupFailurePath;
    private FrontendBackendSessionProjection? backendSessionProjection;
    private readonly Func<ResourceManager.Shared.Runtime.ProcessActionBatch, Task<string>>? elevatedProcessAction;
    private readonly Func<ResourceManager.Shared.Runtime.ElevatedBackendMutation,
        Task<ResourceManager.Shared.Runtime.ElevatedBackendResponse>>? elevatedBackendRequest;

    internal MainForm(
        Action<bool> taskManagerShortcutReplacementChanged,
        Action editableHotkeysChanged,
        WebView2HostConfiguration hostConfiguration,
        Func<ResourceManager.Shared.Runtime.ProcessActionBatch, Task<string>>? elevatedProcessAction = null,
        Func<ResourceManager.Shared.Runtime.ElevatedBackendMutation,
            Task<ResourceManager.Shared.Runtime.ElevatedBackendResponse>>? elevatedBackendRequest = null,
        string? startupFailurePath = null)
        : this(taskManagerShortcutReplacementChanged, editableHotkeysChanged,
            new WebView2FrontendHost(hostConfiguration), elevatedProcessAction, elevatedBackendRequest,
            startupFailurePath)
    {
    }

    internal MainForm(
        Action<bool> taskManagerShortcutReplacementChanged,
        Action editableHotkeysChanged,
        IFrontendHost frontendHost,
        Func<ResourceManager.Shared.Runtime.ProcessActionBatch, Task<string>>? elevatedProcessAction = null,
        Func<ResourceManager.Shared.Runtime.ElevatedBackendMutation,
            Task<ResourceManager.Shared.Runtime.ElevatedBackendResponse>>? elevatedBackendRequest = null,
        string? startupFailurePath = null)
    {
        this.startupFailurePath = startupFailurePath;
        this.taskManagerShortcutReplacementChanged = taskManagerShortcutReplacementChanged;
        this.editableHotkeysChanged = editableHotkeysChanged;
        this.elevatedProcessAction = elevatedProcessAction;
        this.elevatedBackendRequest = elevatedBackendRequest;
        this.frontendHost = frontendHost;
        frontendHost.MessageReceived += HandleFrontendMessageReceived;
        frontendHost.NavigationCompleted += HandleFrontendNavigationCompleted;
        Text = NativeUiText.Current.AppName;
        StartPosition = FormStartPosition.Manual;
        MinimumSize = new Size(760, 520);
        Size = WindowPlacement.GetPreferredPrimaryScreenSize(MinimumSize);
        FormBorderStyle = FormBorderStyle.None;
        Padding = Padding.Empty;
        BackColor = frontendHost.BackgroundColor;

        Controls.Add(frontendHost.View);
        backendAvailabilityPanel = new BackendAvailabilityPanel(frontendHost.BackgroundColor);
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

    internal event EventHandler? WindowCloseRequested;

    internal event EventHandler? PreferencesChanged;

    public void PrepareForOpenOnPrimaryScreen()
    {
        WindowPlacement.PlaceForOpenOnPrimaryScreen(this);
    }

    internal bool PrepareForPassiveValidationOnSecondaryScreen()
    {
        return WindowPlacement.TryPlaceForPassiveValidationOnSecondaryScreen(this);
    }

    public Task StartFrontendAsync()
    {
        if (frontendStopping) return Task.CompletedTask;
        var starting = StartFrontendCoreAsync();
        TrackFrontendRequest(starting);
        return starting;
    }

    private async Task StartFrontendCoreAsync()
    {
        if (frontendStopping || !backendContentEnabled)
        {
            return;
        }

        if (!appNavigationStarted)
        {
            ShowFrontendLoading();
        }

        if (await EnsureFrontendHostAsync()
            && !frontendStopping
            && backendContentEnabled
            && frontendHost.IsReady)
        {
            PublishHostVisibility();
            if (!appNavigationStarted)
            {
                appNavigationStarted = true;
                frontendHost.Navigate(frontendHost.ApplicationAddress);
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

    internal void StopAndClose()
        => CompleteClose();

    internal void CompleteClose()
    {
        if (frontendStopTask is { IsCompleted: false }
            || (WindowCloseRequested is not null && frontendStopTask is null))
            throw new InvalidOperationException("Await frontend shutdown before closing the window.");
        frontendStopTask?.GetAwaiter().GetResult();
        finalClose = true;
        Hide();
        StopPolling();
        Close();
    }

    internal void SetDesktopOccluded(bool occluded)
    {
        if (desktopOccluded == occluded)
            return;
        desktopOccluded = occluded;
        PublishHostVisibility();
    }

    /// <summary>语言切换后重新取一遍窗口内常驻文案；状态文案由外壳状态投影推送。</summary>
    public void ApplyInterfaceLanguage() => backendAvailabilityPanel.ApplyText();

    public void SetStatusText(string text)
    {
        Text = string.IsNullOrWhiteSpace(text)
            ? NativeUiText.Current.AppName
            : string.Format(NativeUiText.Current.WindowTitleFormat, text);
    }

    internal void ShowBackendConnecting(bool reconnecting)
    {
        DisableBackendContent();
        SetStatusText(reconnecting ? NativeUiText.Current.StatusReconnecting : NativeUiText.Current.StatusConnecting);
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
        SetStatusText(NativeUiText.Current.StatusBackendUnavailable);
        backendAvailabilityPanel.ShowUnavailable(detail);
    }

    internal void ShowBackendReady(
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
        backendContentEnabled = true;
        appNavigationStarted = false;
        frontendHost.View.Visible = false;
        SetStatusText(NativeUiText.Current.StatusBackendReady);
        SetFrontendConnectionState(FrontendConnectionState.NotStarted);
    }

    protected override void OnFormClosing(FormClosingEventArgs args)
    {
        // WM_CLOSE covers Alt+F4, the system menu and the taskbar close command.
        // Keep the real bounds and window state intact while removing the HWND
        // from the screen before any closing handler can tear down the surface.
        Hide();
        // WinForms labels a raw WM_CLOSE (including taskbar close) TaskManagerClosing.
        // Session messages retain the window; SystemEvents owns confirmed program exit.
        if (!finalClose
            && args.CloseReason is (CloseReason.UserClosing or CloseReason.TaskManagerClosing)
            && WindowCloseRequested is not null)
        {
            args.Cancel = true;
            WindowCloseRequested.Invoke(this, EventArgs.Empty);
        }
        base.OnFormClosing(args);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Hide();
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
        SetStatusText(NativeUiText.Current.StatusFrontendLoading);
        backendAvailabilityPanel.ShowFrontendLoading();
        SetFrontendConnectionState(FrontendConnectionState.Loading);
    }

    private void ShowFrontendReady()
    {
        backendAvailabilityPanel.Visible = false;
        frontendHost.View.Visible = true;
        // 页面刚显示出来，焦点还停在刚被隐藏的那块面板上。
        // 不交过去的话，用户得先在页面里点一下滚轮和键盘才生效。
        frontendHost.FocusContent();
        SetStatusText(NativeUiText.Current.StatusReady);
        SetFrontendConnectionState(FrontendConnectionState.Ready);
    }

    /// <summary>
    /// 窗口被激活时，焦点回到页面。
    ///
    /// 从别的程序切回来（Alt+Tab、点标题栏、点任务栏）之后，
    /// WinForms 会把焦点还给上一个活动控件 —— 而这个窗口平时只有页面这一块内容，
    /// 上一个活动控件可能是早就隐藏掉的面板按钮。那种情况下滚轮和键盘都不进页面，
    /// 用户要先点一下才行。
    /// </summary>
    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        frontendHost.FocusContent();
    }

    private void ShowFrontendUnavailable(string detail)
    {
        frontendHost.View.Visible = false;
        SetStatusText(NativeUiText.Current.StatusFrontendUnavailable);
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
