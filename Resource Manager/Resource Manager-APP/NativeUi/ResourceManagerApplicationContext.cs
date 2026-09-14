using ResourceManager.NativeUi.SystemIntegration;
using ResourceManager.NativeUi.SystemIntegration.EditableHotkeys;

namespace ResourceManager.NativeUi;

public sealed class ResourceManagerApplicationContext : ApplicationContext
{
    private const string ApiBaseAddress = "http://127.0.0.1:9321";

    private readonly CancellationTokenSource shutdown = new();
    private readonly NativeUiSingleInstanceCoordinator singleInstance;
    private readonly bool startInBackground;
    private readonly Control uiDispatcher = new();
    private readonly NativeUiShutdownCoordinator shutdownCoordinator = new();
    private readonly TaskManagerShortcutReplacementController taskManagerShortcutReplacement;
    private readonly ForceTerminateHotkeyController forceTerminateHotkey;
    private readonly ContextMenuStrip trayMenu;
    private readonly NotifyIcon trayIcon;
    private BackendServiceSession? backendSession;
    private BackendServiceLossHandler? backendLossHandler;
    private ToolStripMenuItem? backendStatusMenuItem;
    private ToolStripMenuItem? retryBackendMenuItem;
    private MainForm? mainForm;
    private Task<bool>? backendConnectionTask;
    private Task? reconnectLoopTask;
    private CancellationTokenSource? reconnectDelayCancellation;
    private bool startupScheduled;
    private bool backendReady;
    private bool exiting;
    private long backendGeneration;
    private long backendSessionPublicationRevision;
    private FrontendBackendSessionProjection? frontendBackendSessionProjection;
    private BackendConnectionState backendConnectionState = BackendConnectionState.Connecting;
    private FrontendConnectionState frontendConnectionState =
        FrontendConnectionState.NotStarted;
    private int forceTerminateActionRunning;
    private int disposeStarted;

    internal ResourceManagerApplicationContext(
        NativeUiSingleInstanceCoordinator singleInstance,
        bool startInBackground = false)
    {
        this.singleInstance = singleInstance;
        this.startInBackground = startInBackground;
        uiDispatcher.CreateControl();
        _ = uiDispatcher.Handle;
        singleInstance.RequestReceived += OnSingleInstanceRequest;
        singleInstance.StartListening(shutdown.Token);

        trayMenu = BuildTrayMenu();
        trayIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "资源管理器",
            Visible = true,
            ContextMenuStrip = trayMenu
        };
        trayIcon.MouseClick += (_, args) =>
        {
            if (args.Button == MouseButtons.Left)
            {
                _ = ShowMainWindowAsync(startFrontend: true, activate: true);
            }
        };

        taskManagerShortcutReplacement = new TaskManagerShortcutReplacementController(
            NativeUiPaths.AppSettingsPath,
            OnTaskManagerShortcutPressed,
            ShowTaskManagerShortcutReplacementStatus);
        taskManagerShortcutReplacement.Start();

        forceTerminateHotkey = new ForceTerminateHotkeyController(
            NativeUiPaths.AppSettingsPath,
            OnForceTerminateHotkeyPressed,
            ShowForceTerminateHotkeyStatus);
        forceTerminateHotkey.Start();

        Application.Idle += OnApplicationIdle;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref disposeStarted, 1) == 0)
        {
            exiting = true;
            RunDisposalAction(shutdown.Cancel);
            RunDisposalAction(() => reconnectDelayCancellation?.Cancel());
            RunDisposalAction(() => Application.Idle -= OnApplicationIdle);
            RunDisposalAction(() => singleInstance.RequestReceived -= OnSingleInstanceRequest);
            RunDisposalAction(DisposeMainForm);
            RunDisposalAction(DisposeBackendGeneration);
            RunDisposalAction(singleInstance.Dispose);
            RunDisposalAction(taskManagerShortcutReplacement.Dispose);
            RunDisposalAction(forceTerminateHotkey.Dispose);
            RunDisposalAction(uiDispatcher.Dispose);
            RunDisposalAction(() => trayIcon.Visible = false);
            RunDisposalAction(trayIcon.Dispose);
            RunDisposalAction(trayMenu.Dispose);
            RunDisposalAction(shutdown.Dispose);
        }

        base.Dispose(disposing);
    }

    private ContextMenuStrip BuildTrayMenu()
    {
        var menu = new ContextMenuStrip();
        backendStatusMenuItem = new ToolStripMenuItem("本地服务：正在连接")
        {
            Enabled = false
        };
        var openItem = new ToolStripMenuItem("打开");
        openItem.Click += (_, _) =>
            _ = ShowMainWindowAsync(startFrontend: true, activate: true);
        retryBackendMenuItem = new ToolStripMenuItem("重新连接本地服务");
        retryBackendMenuItem.Click += (_, _) => RequestBackendRetry();
        var exitItem = new ToolStripMenuItem("退出");
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
        _ = StartAsync();
    }

    private async Task StartAsync()
    {
        if (startInBackground)
        {
            _ = await EnsureBackendReadyAsync();
            return;
        }

        await ShowMainWindowAsync(startFrontend: false, activate: true);
        if (await EnsureBackendReadyAsync())
        {
            await StartMainWindowFrontendIfVisibleAsync();
        }
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

        var form = GetOrCreateMainForm();
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

        if (!backendReady)
        {
            ApplyBackendStateToForm(form);
            _ = EnsureBackendAndStartVisibleWindowAsync();
            return;
        }

        if (startFrontend)
        {
            await StartMainWindowFrontendIfVisibleAsync();
        }
    }

    private void OnSingleInstanceRequest(object? sender, string command)
    {
        if (uiDispatcher.IsDisposed)
        {
            return;
        }

        uiDispatcher.BeginInvoke(new Action(() =>
        {
            if (exiting)
            {
                return;
            }

            if (command.Equals("exit", StringComparison.OrdinalIgnoreCase))
            {
                ExitApplication();
            }
            else if (command.Equals("show-passive", StringComparison.OrdinalIgnoreCase))
            {
                _ = ShowMainWindowAsync(startFrontend: true, activate: false);
            }
            else if (command.Equals("show-passive-secondary", StringComparison.OrdinalIgnoreCase))
            {
                _ = ShowMainWindowAsync(
                    startFrontend: true,
                    activate: false,
                    placeOnSecondaryScreen: true);
            }
            else if (!command.Equals("ensure-running", StringComparison.OrdinalIgnoreCase))
            {
                _ = ShowMainWindowAsync(startFrontend: true, activate: true);
            }
        }));
    }

    private MainForm GetOrCreateMainForm()
    {
        if (mainForm is { IsDisposed: false })
        {
            return mainForm;
        }

        var form = new MainForm(
            taskManagerShortcutReplacement.SetEnabledFromUi,
            forceTerminateHotkey.Reload);
        form.BackendRetryRequested += OnBackendRetryRequested;
        form.FrontendConnectionStateChanged += OnFrontendConnectionStateChanged;
        form.DiagnosticsRequested += OnDiagnosticsRequested;
        form.ExitRequested += OnExitRequested;
        form.FormClosing += OnMainFormClosing;
        form.FormClosed += OnMainFormClosed;
        mainForm = form;
        ApplyBackendStateToForm(form);
        return form;
    }

    private async void OnMainFormClosing(object? sender, FormClosingEventArgs args)
    {
        if (exiting || args.CloseReason != CloseReason.UserClosing)
        {
            return;
        }

        args.Cancel = true;
        if (sender is MainForm form)
        {
            await form.HideToBackgroundAsync();
        }
    }

    private async Task<bool> EnsureBackendReadyAsync()
    {
        if (exiting || shutdown.IsCancellationRequested)
        {
            return false;
        }

        if (backendReady && backendSession is not null)
        {
            return true;
        }

        if (backendConnectionTask is not null)
        {
            return await backendConnectionTask;
        }

        var session = backendSession ?? CreateBackendGeneration();
        var generation = backendGeneration;
        var connectionTask = ConnectBackendGenerationAsync(session, generation);
        backendConnectionTask = connectionTask;

        try
        {
            return await connectionTask;
        }
        finally
        {
            if (ReferenceEquals(backendConnectionTask, connectionTask))
            {
                backendConnectionTask = null;
            }
        }
    }

    private async Task<bool> ConnectBackendGenerationAsync(
        BackendServiceSession session,
        long generation)
    {
        try
        {
            _ = await session.EnsureConnectedAsync(shutdown.Token);
            if (!IsCurrentBackendGeneration(session, generation) || exiting)
            {
                return false;
            }

            var projection = SetReadyBackendSessionProjection();
            backendReady = true;
            SetBackendConnectionState(BackendConnectionState.Ready);
            if (mainForm is { IsDisposed: false } form)
            {
                form.ShowBackendReady(session.AccessToken, projection);
            }
            return true;
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine(ex);
            if (IsCurrentBackendGeneration(session, generation))
            {
                HandleBackendUnavailable(
                    session,
                    generation,
                    BackendServiceUnavailableEventArgs.StartupFailed(ex));
            }
            return false;
        }
    }

    private async Task StartMainWindowFrontendIfVisibleAsync()
    {
        if (mainForm is { IsDisposed: false, Visible: true } form)
        {
            await form.StartFrontendAsync();
        }
    }

    private async Task EnsureBackendAndStartVisibleWindowAsync()
    {
        if (await EnsureBackendReadyAsync())
        {
            await StartMainWindowFrontendIfVisibleAsync();
        }
    }

    private void OnTaskManagerShortcutPressed()
    {
        if (uiDispatcher.IsDisposed)
        {
            return;
        }

        uiDispatcher.BeginInvoke(new Action(() =>
        {
            if (!exiting)
            {
                _ = ShowMainWindowAsync(startFrontend: true, activate: true);
            }
        }));
    }

    private void ShowTaskManagerShortcutReplacementStatus(string message, ToolTipIcon icon)
    {
        if (exiting || !trayIcon.Visible)
        {
            return;
        }

        trayIcon.ShowBalloonTip(
            6000,
            "任务管理器快捷键替换",
            message,
            icon);
    }

    private void OnForceTerminateHotkeyPressed(int? foregroundProcessId)
    {
        if (uiDispatcher.IsDisposed)
        {
            return;
        }

        uiDispatcher.BeginInvoke(new Action(() =>
        {
            if (!exiting)
            {
                _ = ExecuteForceTerminateHotkeyAsync(foregroundProcessId);
            }
        }));
    }

    private async Task ExecuteForceTerminateHotkeyAsync(int? foregroundProcessId)
    {
        if (Interlocked.Exchange(ref forceTerminateActionRunning, 1) != 0)
        {
            return;
        }

        try
        {
            var targets = HungAndForegroundProcessResolver.Resolve(foregroundProcessId);
            if (targets.ProcessIds.Count == 0)
            {
                ShowForceTerminateHotkeyStatus("没有可结束的前台或无响应程序。", ToolTipIcon.Info);
                return;
            }

            var session = backendSession;
            if (!backendReady || session is null)
            {
                ShowForceTerminateHotkeyStatus("本地服务不可用，未执行进程终止。", ToolTipIcon.Warning);
                RequestBackendRetry();
                return;
            }

            var message = await session.TerminateProcessesAsync(targets.ProcessIds, shutdown.Token);
            ShowForceTerminateHotkeyStatus(message, ToolTipIcon.Info);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine(ex);
            ShowForceTerminateHotkeyStatus("强制结束失败，请稍后重试。", ToolTipIcon.Error);
        }
        finally
        {
            Interlocked.Exchange(ref forceTerminateActionRunning, 0);
        }
    }

    private void ShowForceTerminateHotkeyStatus(string message, ToolTipIcon icon)
    {
        if (exiting || !trayIcon.Visible)
        {
            return;
        }

        trayIcon.ShowBalloonTip(5000, "强制结束快捷键", message, icon);
    }

    private void ExitApplication()
    {
        _ = shutdownCoordinator.TryShutdown(new NativeUiShutdownActions(
            MarkExiting: () =>
            {
                exiting = true;
                SetBackendConnectionState(BackendConnectionState.ShuttingDown);
            },
            CancelBackgroundWork: () =>
            {
                reconnectDelayCancellation?.Cancel();
                shutdown.Cancel();
            },
            HideTray: () =>
            {
                trayIcon.Visible = false;
            },
            StopAndCloseMainWindow: () =>
            {
                if (mainForm is { IsDisposed: false } form)
                {
                    form.StopPolling();
                    form.Close();
                }
            },
            ExitMessageLoop: ExitThread));
    }

    private BackendServiceSession CreateBackendGeneration()
    {
        if (backendSession is not null || backendLossHandler is not null)
        {
            throw new InvalidOperationException("A backend generation is already active.");
        }

        var session = BackendServiceSession.CreateDefault(ApiBaseAddress);
        var generation = checked(++backendGeneration);
        var lossHandler = new BackendServiceLossHandler(
            session,
            () => exiting || uiDispatcher.IsDisposed,
            action => uiDispatcher.BeginInvoke(action),
            unavailable => HandleBackendUnavailable(session, generation, unavailable));
        backendSession = session;
        backendLossHandler = lossHandler;
        SetBackendConnectionState(
            backendConnectionState == BackendConnectionState.Degraded
                ? BackendConnectionState.Reconnecting
                : BackendConnectionState.Connecting);
        return session;
    }

    private void HandleBackendUnavailable(
        BackendServiceSession session,
        long generation,
        BackendServiceUnavailableEventArgs unavailable)
    {
        if (exiting || !IsCurrentBackendGeneration(session, generation))
        {
            return;
        }

        System.Diagnostics.Trace.WriteLine(unavailable.Message);
        _ = SetUnavailableBackendSessionProjection();
        backendReady = false;
        DisposeBackendGeneration();
        SetBackendConnectionState(BackendConnectionState.Degraded, unavailable.Message);
        ShowBackendUnavailableBalloon(unavailable.Message);
        EnsureReconnectLoop(immediate: false);
    }

    private bool IsCurrentBackendGeneration(
        BackendServiceSession session,
        long generation) =>
        generation == backendGeneration && ReferenceEquals(session, backendSession);

    private void DisposeBackendGeneration()
    {
        var lossHandler = backendLossHandler;
        var session = backendSession;
        backendLossHandler = null;
        backendSession = null;
        lossHandler?.Dispose();
        session?.Dispose();
    }

    private void EnsureReconnectLoop(bool immediate)
    {
        if (exiting || backendReady || shutdown.IsCancellationRequested)
        {
            return;
        }

        if (immediate)
        {
            reconnectDelayCancellation?.Cancel();
        }

        if (reconnectLoopTask is { IsCompleted: false })
        {
            return;
        }

        reconnectLoopTask = RunReconnectLoopAsync(immediate);
    }

    private async Task RunReconnectLoopAsync(bool immediate)
    {
        var failedAttemptCount = 0;
        var skipDelay = immediate;
        try
        {
            while (!exiting && !shutdown.IsCancellationRequested && !backendReady)
            {
                if (!skipDelay)
                {
                    await WaitForReconnectDelayAsync(
                        BackendReconnectPolicy.GetDelay(failedAttemptCount));
                }
                skipDelay = false;
                if (exiting || shutdown.IsCancellationRequested || backendReady)
                {
                    return;
                }

                SetBackendConnectionState(BackendConnectionState.Reconnecting);
                mainForm?.ShowBackendConnecting(reconnecting: true);
                if (await EnsureBackendReadyAsync())
                {
                    await StartMainWindowFrontendIfVisibleAsync();
                    return;
                }

                failedAttemptCount++;
            }
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
        }
        finally
        {
            reconnectLoopTask = null;
        }
    }

    private async Task WaitForReconnectDelayAsync(TimeSpan delay)
    {
        using var delayCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
        reconnectDelayCancellation = delayCancellation;
        try
        {
            await Task.Delay(delay, delayCancellation.Token);
        }
        catch (OperationCanceledException) when (!shutdown.IsCancellationRequested)
        {
        }
        finally
        {
            if (ReferenceEquals(reconnectDelayCancellation, delayCancellation))
            {
                reconnectDelayCancellation = null;
            }
        }
    }

    private void RequestBackendRetry()
    {
        if (exiting)
        {
            return;
        }

        if (backendReady && backendSession is { } session)
        {
            mainForm?.ShowBackendReady(
                session.AccessToken,
                RequireBackendSessionProjection(ready: true));
            _ = StartMainWindowFrontendIfVisibleAsync();
            return;
        }

        EnsureReconnectLoop(immediate: true);
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
        trayIcon.Text = $"资源管理器 - {snapshot.StatusText}";
        if (backendStatusMenuItem is not null)
        {
            backendStatusMenuItem.Text = $"状态：{snapshot.StatusText}";
        }
        if (retryBackendMenuItem is not null)
        {
            retryBackendMenuItem.Text = snapshot.RetryActionText;
            retryBackendMenuItem.Enabled = snapshot.RetryEnabled;
        }
    }

    private void ApplyBackendStateToForm(MainForm form)
    {
        switch (backendConnectionState)
        {
            case BackendConnectionState.Ready when backendSession is { } session:
                form.ShowBackendReady(
            session.AccessToken,
            RequireBackendSessionProjection(ready: true));
                break;
            case BackendConnectionState.Degraded:
                form.ShowBackendUnavailable(
                    "尚未建立本地服务连接。",
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
        if (!exiting && trayIcon.Visible)
        {
            trayIcon.ShowBalloonTip(
                5000,
                "本地服务不可用",
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
    {
        try
        {
            var diagnosticsPath = NativeUiPaths.DiagnosticsFolder;
            Directory.CreateDirectory(diagnosticsPath);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{diagnosticsPath}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or System.ComponentModel.Win32Exception)
        {
            System.Diagnostics.Trace.WriteLine(ex);
            ShowBackendUnavailableBalloon("无法打开诊断目录。");
        }
    }

    private void OnExitRequested(object? sender, EventArgs args) => ExitApplication();

    private void OnMainFormClosed(object? sender, FormClosedEventArgs args)
    {
        if (!ReferenceEquals(mainForm, sender))
        {
            return;
        }

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
        mainForm = null;
        if (form is null)
        {
            return;
        }

        form.BackendRetryRequested -= OnBackendRetryRequested;
        form.FrontendConnectionStateChanged -= OnFrontendConnectionStateChanged;
        form.DiagnosticsRequested -= OnDiagnosticsRequested;
        form.ExitRequested -= OnExitRequested;
        form.FormClosing -= OnMainFormClosing;
        form.FormClosed -= OnMainFormClosed;
        if (!form.IsDisposed)
        {
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
