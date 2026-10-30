using BlockUids = ResourceManager.NativeUi.LegacyStateItems.BlockUids;
using ResourceManager.NativeUi.Blocks;
using ResourceManager.Shared.BusinessBlocks;
using ResourceManager.NativeUi.Localization;
using ResourceManager.NativeUi.Overlay;
using ResourceManager.NativeUi.SystemIntegration;
using ResourceManager.NativeUi.SystemIntegration.EditableHotkeys;
using ResourceManager.NativeUi.WebView;

namespace ResourceManager.NativeUi;

public sealed partial class ResourceManagerApplicationContext : ApplicationContext
{
    private const string ApiBaseAddress = "http://127.0.0.1:9321";

    private readonly CancellationTokenSource shutdown = new();
    private readonly NativeUiSingleInstanceCoordinator singleInstance;
    private readonly bool startInBackground;
    private readonly Control uiDispatcher = new();
    private readonly NativeUiShutdownCoordinator shutdownCoordinator = new();
    private readonly PerformanceOverlayCoordinator performanceOverlay;
    private FrontendLoopbackProxy? frontendProxy;
    private readonly InteractiveDesktopObservationPublisher desktopObservations = new();
    private readonly NativeUiApplicationServices services;
    private readonly INativeUiHotkeys hotkeys;
    private ContextMenuStrip trayMenu = null!;
    private NotifyIcon trayIcon = null!;
    private TrayBackendNotifications backendNotifications = null!;
    private ToolStripMenuItem? backendStatusMenuItem;
    private ToolStripMenuItem? retryBackendMenuItem;
    private ToolStripMenuItem? openMenuItem;
    private ToolStripMenuItem? exitMenuItem;
    private MainForm? mainForm;
    private readonly NativeUiBlockTrees trees;
    private readonly NativeUiBackendConnectionBlock backendConnection;
    private BackendServiceSession? backendSession => backendConnection.CurrentSession;
    private bool backendReady => backendConnection.IsReady;
    private bool startupScheduled;
    private bool exiting;
    private bool treesDrained;
    private long backendSessionPublicationRevision;
    private FrontendBackendSessionProjection? frontendBackendSessionProjection;
    private BackendConnectionState backendConnectionState = BackendConnectionState.Connecting;
    private FrontendConnectionState frontendConnectionState =
        FrontendConnectionState.NotStarted;
    private int forceTerminateActionRunning;
    private int disposeStarted;

    internal ResourceManagerApplicationContext(
        NativeUiSingleInstanceCoordinator singleInstance,
        bool startInBackground = false,
        NativeUiApplicationServices? services = null)
    {
        this.singleInstance = singleInstance;
        this.startInBackground = startInBackground;
        this.services = services ?? new NativeUiApplicationServices();
        uiDispatcher.CreateControl();
        _ = uiDispatcher.Handle;
        performanceOverlay = new PerformanceOverlayCoordinator(uiDispatcher);
        backendConnection = new NativeUiBackendConnectionBlock(
            this.services.CreateSession ?? (() => BackendServiceSession.CreateDefault(this.services.ApiBaseAddress ?? ApiBaseAddress)),
            PublishBackendSessionAsync, this.services.ConnectSession, this.services.ReconnectDelay);
        trees = new NativeUiBlockTrees(CreateBlock, new NativeUiTraceLogger<NativeUiBlockTrees>(),
            new NativeUiTraceLogger<BusinessBlockController>());

        hotkeys = this.services.Hotkeys ?? new NativeUiHotkeys(
            new Configuration.AppSettingsFileObserver(NativeUiPaths.AppSettingsPath,
                action => uiDispatcher.BeginInvoke(action)),
            new TaskManagerShortcutReplacementController(
                OnTaskManagerShortcutPressed, ShowTaskManagerShortcutReplacementStatus),
            new ForceTerminateHotkeyController(
                OnForceTerminateHotkeyPressed, ShowForceTerminateHotkeyStatus));

        Microsoft.Win32.SystemEvents.SessionEnded += OnSessionEnded;
        Application.Idle += OnApplicationIdle;
    }

    // Called by the main root's owned UI callback scope.
    private void OnInterfaceLanguageChanged(object? sender, EventArgs args)
    {
        if (exiting) return;
        if (openMenuItem is not null) openMenuItem.Text = NativeUiText.Current.TrayOpen;
        if (exitMenuItem is not null) exitMenuItem.Text = NativeUiText.Current.TrayExit;
        mainForm?.ApplyInterfaceLanguage();
        UpdateShellStatus();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref disposeStarted, 1) == 0)
        {
            exiting = true;
            RunDisposalAction(() => mainForm?.Hide());
            RunDisposalAction(shutdown.Cancel);
            RunDisposalAction(() => Application.Idle -= OnApplicationIdle);
            RunDisposalAction(() => Microsoft.Win32.SystemEvents.SessionEnded -= OnSessionEnded);
            // Concurrent entries have already joined before ExitThread. These are process resources.
            RunDisposalAction(singleInstance.Dispose);
            RunDisposalAction(uiDispatcher.Dispose);
            RunDisposalAction(shutdown.Dispose);
        }

        base.Dispose(disposing);
    }

    protected override void ExitThreadCore()
    {
        if (!treesDrained)
        {
            ExitApplication("message-loop-exit");
            return;
        }
        base.ExitThreadCore();
    }

    private ContextMenuStrip BuildTrayMenu()
    {
        var menu = new ContextMenuStrip();
        backendStatusMenuItem = new ToolStripMenuItem(NativeUiText.Current.TrayBackendStatusConnecting)
        {
            Enabled = false
        };
        var openItem = openMenuItem = new ToolStripMenuItem(NativeUiText.Current.TrayOpen);
        openItem.Click += (_, _) =>
            ObserveUiCommand(ShowMainWindowAsync(startFrontend: true, activate: true));
        retryBackendMenuItem = new ToolStripMenuItem(NativeUiText.Current.TrayReconnectBackend);
        retryBackendMenuItem.Click += (_, _) => RequestBackendRetry();
        var exitItem = exitMenuItem = new ToolStripMenuItem(NativeUiText.Current.TrayExit);
        exitItem.Click += (_, _) => ExitApplication();
        menu.Items.AddRange(
        [
            backendStatusMenuItem,
            new ToolStripSeparator(),
            openItem,
            retryBackendMenuItem,
            new ToolStripSeparator(),
            exitItem
        ]);
        return menu;
    }

    private void OnApplicationIdle(object? sender, EventArgs args)
    {
        if (startupScheduled)
        {
            return;
        }

        startupScheduled = true;
        Application.Idle -= OnApplicationIdle;
        ObserveUiCommand(StartAsync());
    }

    private async Task StartAsync()
    {
        await trees.StartAsync(windowWanted: false);
        if (!startInBackground)
            await ShowMainWindowAsync(startFrontend: true, activate: true);
    }

    private async Task ShowMainWindowAsync(
        bool startFrontend,
        bool activate,
        bool placeOnSecondaryScreen = false)
    {
        if (exiting)
        {
            return;
        }

        // A close already accepted by the window owns its complete resource retirement.
        await windowRequests.WaitForCloseAsync();
        if (exiting) return;
        var previousWindow = mainForm;
        activateRequestedWindow = activate;
        secondaryRequestedWindow = placeOnSecondaryScreen;
        await trees.SetWindowWantedAsync(true, "show-window");
        await trees.WaitForStateAsync(BlockUids.MainWindow, BusinessBlockState.Running, shutdown.Token);
        var form = mainForm!;
        if (ReferenceEquals(previousWindow, form))
            PresentMainWindow(form, activate, placeOnSecondaryScreen);

        if (!backendReady)
        {
            ApplyBackendStateToForm(form);
            return;
        }

        if (startFrontend)
        {
            await StartMainWindowFrontendIfVisibleAsync();
        }
    }

    private void PresentMainWindow(MainForm form, bool activate, bool placeOnSecondaryScreen)
    {
        if (services.PresentWindow is { } present)
        {
            present(form, activate, placeOnSecondaryScreen);
            return;
        }
        var placedOnSecondaryScreen = placeOnSecondaryScreen
            && form.PrepareForPassiveValidationOnSecondaryScreen();
        var shouldPlaceOnPrimaryScreen = !placedOnSecondaryScreen
            && activate
            && (!form.Visible || form.WindowState == FormWindowState.Minimized);
        if (shouldPlaceOnPrimaryScreen)
        {
            form.PrepareForOpenOnPrimaryScreen();
        }

        if (!form.Visible)
        {
            if (activate)
            {
                form.Show();
            }
            else
            {
                form.ShowPassively();
            }
        }

        if (form.WindowState == FormWindowState.Minimized)
        {
            form.WindowState = FormWindowState.Normal;
        }

        if (activate)
        {
            form.BringToFront();
            form.TopMost = true;
            form.TopMost = false;
            form.Activate();
        }

    }

    // Called on the UI thread; the listener block owns and joins dispatch.
    private void OnSingleInstanceRequest(object? sender, string command)
    {
        if (exiting) return;
        if (command.Equals("exit", StringComparison.OrdinalIgnoreCase))
            ExitApplication("second-instance-exit");
        else if (command.Equals("show-passive", StringComparison.OrdinalIgnoreCase))
            ObserveUiCommand(ShowMainWindowAsync(startFrontend: true, activate: false));
        else if (command.Equals("show-passive-secondary", StringComparison.OrdinalIgnoreCase))
            ObserveUiCommand(ShowMainWindowAsync(startFrontend: true, activate: false, placeOnSecondaryScreen: true));
        else if (!command.Equals("ensure-running", StringComparison.OrdinalIgnoreCase))
            ObserveUiCommand(ShowMainWindowAsync(startFrontend: true, activate: true));
    }

    private MainForm GetOrCreateMainForm()
    {
        if (mainForm is { IsDisposed: false })
        {
            return mainForm;
        }

        var form = new MainForm(
            hotkeys.SetTaskManagerEnabled,
            hotkeys.ReloadForceTerminate,
            (services.CreateFrontendHost ?? (static configuration => new WebView2FrontendHost(configuration)))(
                new WebView2HostConfiguration(
                    services.WebViewUserDataFolder ?? NativeUiPaths.WebViewUserDataFolder,
                    services.FrontendPreferencesPath ?? NativeUiPaths.FrontendPreferencesPath, frontendProxy!)),
            services.WebViewStartupFailurePath);
        form.BackendRetryRequested += OnBackendRetryRequested;
        form.FrontendConnectionStateChanged += OnFrontendConnectionStateChanged;
        form.DiagnosticsRequested += OnDiagnosticsRequested;
        form.ExitRequested += OnExitRequested;
        form.WindowCloseRequested += OnWindowCloseRequested;
        form.FormClosed += OnMainFormClosed;
        form.HandleCreated += OnMainFormHandleCreated;
        form.HandleDestroyed += OnMainFormHandleDestroyed;
        form.PreferencesChanged += OnFrontendPreferencesChanged;
        mainForm = form;
        if (form.IsHandleCreated)
            desktopObservations.SetVisibilityWindow(form.Handle);
        ApplyBackendStateToForm(form);
        return form;
    }

    private void OnMainFormHandleCreated(object? sender, EventArgs args)
    {
        if (ReferenceEquals(mainForm, sender) && sender is MainForm form)
            desktopObservations.SetVisibilityWindow(form.Handle);
    }

    private void OnMainFormHandleDestroyed(object? sender, EventArgs args)
    {
        if (ReferenceEquals(mainForm, sender) && sender is MainForm form)
            desktopObservations.SetVisibilityWindow(IntPtr.Zero);
    }

    // The overlay reads the same preferences owner the window saves through.
    private void OnFrontendPreferencesChanged(object? sender, EventArgs args)
        => performanceOverlay.NotifyPreferencesChanged();

    // Called by the current desktop observation run's owned callback scope.
    private void OnObservedHostWindowVisibilityChanged(IntPtr window, bool visible)
    {
        if (!exiting && mainForm is { IsDisposed: false, IsHandleCreated: true } form
            && form.Handle == window)
            form.SetDesktopOccluded(!visible);
    }

    private async Task StartMainWindowFrontendIfVisibleAsync()
    {
        if (mainForm is not { IsDisposed: false, Visible: true } form) return;
        var token = windowLifetime;
        await trees.WaitForStateAsync(BlockUids.WebViewHost, BusinessBlockState.Running, token);
        if (ReferenceEquals(form, mainForm) && !token.IsCancellationRequested)
            await form.StartFrontendAsync();
    }

    private void OnTaskManagerShortcutPressed()
        => mainCallbacks?.Post(() =>
        {
            if (!exiting) ObserveUiCommand(ShowMainWindowAsync(startFrontend: true, activate: true));
        });

    private void ShowTaskManagerShortcutReplacementStatus(string message, ToolTipIcon icon)
    {
        if (exiting || trayIcon is null || !trayIcon.Visible)
        {
            return;
        }

        trayIcon.ShowBalloonTip(
            6000,
            NativeUiText.Current.TaskManagerHotkeyReplacement,
            message,
            icon);
    }

    private void OnForceTerminateHotkeyPressed(int? foregroundProcessId)
        => mainCallbacks?.Post(() =>
        {
            if (!exiting) ObserveUiCommand(ExecuteForceTerminateHotkeyAsync(foregroundProcessId));
        });

    private async Task ExecuteForceTerminateHotkeyAsync(int? foregroundProcessId)
    {
        if (Interlocked.Exchange(ref forceTerminateActionRunning, 1) != 0)
        {
            return;
        }

        try
        {
            var targets = HungAndForegroundProcessResolver.Resolve(foregroundProcessId);
            if (targets.Targets.Count == 0)
            {
                ShowForceTerminateHotkeyStatus(NativeUiText.Current.NoForegroundProcessToTerminate, ToolTipIcon.Info);
                return;
            }

            var session = backendSession;
            if (!backendReady || session is null)
            {
                ShowForceTerminateHotkeyStatus(NativeUiText.Current.BackendUnavailableNoTerminate, ToolTipIcon.Warning);
                RequestBackendRetry();
                return;
            }

            var message = await session.TerminateProcessesAsync(targets.Targets, shutdown.Token);
            ShowForceTerminateHotkeyStatus(message, ToolTipIcon.Info);
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine(ex);
            ShowForceTerminateHotkeyStatus(NativeUiText.Current.ForceTerminateFailed, ToolTipIcon.Error);
        }
        finally
        {
            Interlocked.Exchange(ref forceTerminateActionRunning, 0);
        }
    }

    private void ShowForceTerminateHotkeyStatus(string message, ToolTipIcon icon)
    {
        if (exiting || trayIcon is null || !trayIcon.Visible)
        {
            return;
        }

        trayIcon.ShowBalloonTip(5000, NativeUiText.Current.ForceTerminateHotkeyBalloonTitle, message, icon);
    }

    private void ExitApplication(string cause = "tray-exit")
    {
        // Hiding remains synchronous; resource release happens while the message loop is alive.
        mainForm?.Hide();
        var completion = shutdownCoordinator.ShutdownAsync(new NativeUiShutdownActions(
            MarkExiting: () =>
            {
                exiting = true;
                SetBackendConnectionState(BackendConnectionState.ShuttingDown);
                if (trayIcon is not null) trayIcon.Visible = false;
                shutdown.Cancel();
            },
            StopTrees: async () =>
            {
                await trees.ShutdownAsync(new(BusinessBlockRequestKind.User, "program", cause, 0));
                await trees.DisposeAsync();
                treesDrained = true;
            },
            ExitMessageLoop: ExitThread));
        // This task is the process join, not work owned by either root.
        shutdownObservation ??= ObserveShutdownAsync(completion);
    }

    private static async Task ObserveShutdownAsync(Task completion)
    {
        try { await completion; }
        catch (Exception error) { System.Diagnostics.Trace.WriteLine(error); }
    }

    private void RequestBackendRetry()
    {
        if (exiting)
        {
            return;
        }

        // Preserve the existing explicit frontend retry; failed listener startup is
        // serial work owned by the proxy block, never an automatic scheduler retry.
        proxyBlock?.RequestRetry();

        if (backendReady && backendSession is { } session)
        {
            mainForm?.ShowBackendReady(
                RequireBackendSessionProjection(ready: true));
            ObserveUiCommand(StartMainWindowFrontendIfVisibleAsync());
            return;
        }

        backendConnection.RequestRetry();
    }

    private void SetBackendConnectionState(
        BackendConnectionState state,
        string? detail = null)
    {
        backendConnectionState = state;
        if (state != BackendConnectionState.Ready)
        {
            frontendConnectionState = FrontendConnectionState.NotStarted;
        }
        UpdateShellStatus();

        if (state == BackendConnectionState.Degraded
            && mainForm is { IsDisposed: false } form
            && !string.IsNullOrWhiteSpace(detail))
        {
            form.ShowBackendUnavailable(
                detail,
                RequireBackendSessionProjection(ready: false));
        }
    }

    private void UpdateShellStatus()
    {
        var snapshot = ShellAvailabilityProjection.Project(
            backendConnectionState,
            frontendConnectionState);
        if (trayIcon is not null) trayIcon.Text = string.Format(NativeUiText.Current.TrayTooltipFormat, snapshot.StatusText);
        if (backendStatusMenuItem is not null)
        {
            backendStatusMenuItem.Text = string.Format(NativeUiText.Current.TrayStatusFormat, snapshot.StatusText);
        }
        if (retryBackendMenuItem is not null)
        {
            retryBackendMenuItem.Text = snapshot.RetryActionText;
            retryBackendMenuItem.Enabled = snapshot.RetryEnabled;
        }
        if (mainForm is { IsDisposed: false } form)
        {
            form.SetStatusText(snapshot.StatusText);
        }
    }

    private void ApplyBackendStateToForm(MainForm form)
    {
        switch (backendConnectionState)
        {
            case BackendConnectionState.Ready when backendSession is { } session:
                form.ShowBackendReady(
            RequireBackendSessionProjection(ready: true));
                break;
            case BackendConnectionState.Degraded:
                form.ShowBackendUnavailable(
                    NativeUiText.Current.NoVerifiedSession,
                    RequireBackendSessionProjection(ready: false));
                break;
            case BackendConnectionState.Reconnecting:
                form.ShowBackendConnecting(reconnecting: true);
                break;
            default:
                form.ShowBackendConnecting(reconnecting: false);
                break;
        }
    }

    private FrontendBackendSessionProjection SetReadyBackendSessionProjection()
    {
        var projection = FrontendBackendSessionProjection.CreateReady(
            checked(++backendSessionPublicationRevision));
        frontendBackendSessionProjection = projection;
        return projection;
    }

    private FrontendBackendSessionProjection SetUnavailableBackendSessionProjection()
    {
        var projection = FrontendBackendSessionProjection.CreateUnavailable(
            checked(++backendSessionPublicationRevision),
            "backend-unavailable");
        frontendBackendSessionProjection = projection;
        return projection;
    }

    private FrontendBackendSessionProjection RequireBackendSessionProjection(bool ready)
    {
        var projection = frontendBackendSessionProjection
            ?? throw new InvalidOperationException(
                "The frontend backend session projection is unavailable.");
        if (projection.IsReady != ready)
        {
            throw new InvalidOperationException(
                "The frontend backend session projection does not match the backend state.");
        }
        return projection;
    }

    private void ShowBackendUnavailableBalloon(string message)
    {
        if (!exiting && trayIcon is not null && trayIcon.Visible)
        {
            trayIcon.ShowBalloonTip(
                5000,
                NativeUiText.Current.BackendUnavailableBalloonTitle,
                message,
                ToolTipIcon.Warning);
        }
    }

    private void OnBackendRetryRequested(object? sender, EventArgs args) =>
        RequestBackendRetry();

    private void OnFrontendConnectionStateChanged(
        object? sender,
        FrontendConnectionStateChangedEventArgs args)
    {
        if (!ReferenceEquals(sender, mainForm))
        {
            return;
        }

        frontendConnectionState = args.State;
        UpdateShellStatus();
    }

    private void OnDiagnosticsRequested(object? sender, EventArgs args)
        => NativeUiDiagnostics.OpenFolder(_ => ShowBackendUnavailableBalloon(NativeUiText.Current.DiagnosticsOpenFailed));

    private void OnExitRequested(object? sender, EventArgs args) => ExitApplication();

    private void OnMainFormClosed(object? sender, FormClosedEventArgs args)
    {
        if (!ReferenceEquals(mainForm, sender))
        {
            return;
        }

        desktopObservations.SetVisibilityWindow(IntPtr.Zero);
        mainForm = null;
        frontendConnectionState = FrontendConnectionState.NotStarted;
        if (!exiting)
        {
            UpdateShellStatus();
        }
    }

    private void DisposeMainForm()
    {
        var form = mainForm;
        if (form is null)
        {
            return;
        }

        form.CompleteClose();
        form.WindowCloseRequested -= OnWindowCloseRequested;
        mainForm = null;
        form.BackendRetryRequested -= OnBackendRetryRequested;
        form.FrontendConnectionStateChanged -= OnFrontendConnectionStateChanged;
        form.DiagnosticsRequested -= OnDiagnosticsRequested;
        form.ExitRequested -= OnExitRequested;
        form.FormClosed -= OnMainFormClosed;
        form.HandleCreated -= OnMainFormHandleCreated;
        form.HandleDestroyed -= OnMainFormHandleDestroyed;
        form.PreferencesChanged -= OnFrontendPreferencesChanged;
        desktopObservations.SetVisibilityWindow(IntPtr.Zero);
        if (!form.IsDisposed)
        {
            form.Hide();
            form.StopPolling();
            form.Dispose();
        }
    }

    private static void RunDisposalAction(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine(ex);
        }
    }
}
