using Microsoft.Extensions.Logging;
using ResourceManager.NativeUi.Blocks;
using ResourceManager.NativeUi.Localization;
using ResourceManager.NativeUi.WebView;
using ResourceManager.Shared.BusinessBlocks;

namespace ResourceManager.NativeUi;

public sealed partial class ResourceManagerApplicationContext
{
    private readonly HashSet<Task> uiCommands = [];
    private NativeUiCallbackLifetime? mainCallbacks;
    private Task? shutdownObservation;
    private TaskCompletionSource frontendRequested = NewSignal();
    private CancellationToken windowLifetime;
    private readonly NativeUiWindowRequests windowRequests = new();
    private NativeUiProxyBlock? proxyBlock;

    private IBusinessBlock CreateBlock(string id) => new UiBlock(context => RunOnUiAsync(() => id switch
    {
        NativeUiBlockIds.MainRoot => RunMainRootAsync(context),
        NativeUiBlockIds.TrayRoot => RunTrayRootAsync(context),
        NativeUiBlockIds.SingleInstance => RunSingleInstanceAsync(context),
        NativeUiBlockIds.BackendConnection => backendConnection.RunAsync(context),
        NativeUiBlockIds.MainWindow => RunWindowAsync(context),
        NativeUiBlockIds.LoopbackProxy => RunProxyAsync(context),
        NativeUiBlockIds.WebViewHost => RunWebViewAsync(context),
        NativeUiBlockIds.DesktopObservation => RunDesktopAsync(context),
        NativeUiBlockIds.Overlay => RunOverlayAsync(context),
        NativeUiBlockIds.TrayNotifications => RunTrayNotificationsAsync(context),
        _ => throw new InvalidOperationException("Unknown native UI block: " + id)
    }));

    private async Task RunMainRootAsync(BusinessBlockRunContext context)
    {
        var callbacks = mainCallbacks = NewCallbacks();
        void LanguageChanged(object? sender, EventArgs args)
            => callbacks.Post(() => OnInterfaceLanguageChanged(sender, args));
        NativeUiText.Changed += LanguageChanged;
        try
        {
            // Preference polling stays inside these owners for this pilot. Their rule migration
            // needs an authoritative preference change source (see the UI tree report).
            taskManagerShortcutReplacement.Start();
            forceTerminateHotkey.Start();
            context.ReportRunning();
            await UntilStoppedAsync(context.StopNotification);
        }
        finally
        {
            NativeUiText.Changed -= LanguageChanged;
            await callbacks.StopAsync();
            taskManagerShortcutReplacement.Dispose();
            forceTerminateHotkey.Dispose();
            await performanceOverlay.DisposeAsync();
            await desktopObservations.DisposeAsync();
            await Task.WhenAll(uiCommands.ToArray());
            uiCommands.Clear();
        }
    }

    private async Task RunTrayRootAsync(BusinessBlockRunContext context)
    {
        trayMenu = BuildTrayMenu();
        trayIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = NativeUiText.Current.AppName,
            Visible = !exiting,
            ContextMenuStrip = trayMenu
        };
        trayIcon.MouseClick += (_, args) =>
        {
            if (args.Button == MouseButtons.Left)
                ObserveUiCommand(ShowMainWindowAsync(startFrontend: true, activate: true));
        };
        trayIcon.BalloonTipClicked += (_, _) =>
            ObserveUiCommand(ShowMainWindowAsync(startFrontend: true, activate: true));
        bool InterfaceVisible() => mainForm is { IsDisposed: false, Visible: true,
            WindowState: not FormWindowState.Minimized };
        void ShowNotice(string title, string message) => trayIcon.ShowBalloonTip(6000, title, message, ToolTipIcon.Info);
        dispatchTrayNotice = action =>
        {
            if (context.StopNotification.IsCancellationRequested || exiting) return;
            uiDispatcher.BeginInvoke(new Action(() =>
            {
                if (!context.StopNotification.IsCancellationRequested && !exiting) action();
            }));
        };
        updateNotice = new TrayUpdateNotice(Preferences.FrontendPreferencesOwner.Current,
            InterfaceVisible, ShowNotice, dispatchTrayNotice);
        accessRequestNotice = new TrayAccessRequestNotice(Preferences.FrontendPreferencesOwner.Current,
            InterfaceVisible, ShowNotice, dispatchTrayNotice);
        UpdateShellStatus();
        context.ReportRunning();
        try { await UntilStoppedAsync(context.StopNotification); }
        finally
        {
            trayIcon.Visible = false;
            trayIcon.Dispose();
            trayMenu.Dispose();
            trayIcon = null!;
            backendStatusMenuItem = retryBackendMenuItem = openMenuItem = exitMenuItem = null;
        }
    }

    private async Task RunSingleInstanceAsync(BusinessBlockRunContext context)
    {
        var callbacks = NewCallbacks();
        void Request(object? sender, string command)
            => callbacks.Post(() => OnSingleInstanceRequest(sender, command));
        singleInstance.RequestReceived += Request;
        try
        {
            var run = singleInstance.RunAsync(context.StopNotification);
            context.ReportRunning();
            await run;
        }
        finally
        {
            singleInstance.RequestReceived -= Request;
            await callbacks.StopAsync();
        }
    }

    private async Task RunWindowAsync(BusinessBlockRunContext context)
    {
        using var commands = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
        windowLifetime = commands.Token;
        frontendRequested = NewSignal();
        frontendProxy = new FrontendLoopbackProxy(new Uri(ApiBaseAddress + "/"),
            (request, token) => backendReady && backendSession is { } current
                ? current.SendFrontendRequestAsync(request, token)
                : Task.FromException<System.Net.Http.HttpResponseMessage>(
                    new InvalidOperationException(NativeUiText.Current.BackendPipelineNotReady)));
        proxyBlock = new NativeUiProxyBlock(frontendProxy);
        _ = GetOrCreateMainForm();
        context.ReportRunning();
        try { await UntilStoppedAsync(context.StopNotification); }
        finally
        {
            commands.Cancel();
            // Also covers a close before either child entry was dispatched.
            if (mainForm is { } form) await form.StopFrontendAsync();
            await frontendProxy.DisposeAsync();
            DisposeMainForm();
            frontendProxy = null;
            proxyBlock = null;
        }
    }

    private Task RunProxyAsync(BusinessBlockRunContext context) => proxyBlock!.RunAsync(context);

    private async Task RunWebViewAsync(BusinessBlockRunContext context)
    {
        var form = mainForm!;
        try
        {
            // Keep the existing runtime-install prompt timing: the visible window asks to
            // initialize after backend readiness. A background launch never creates WebView2.
            await frontendRequested.Task.WaitAsync(context.StopNotification);
            _ = await form.InitializeFrontendForBlockAsync().WaitAsync(context.StopNotification);
            context.ReportRunning();
            await UntilStoppedAsync(context.StopNotification);
        }
        catch (OperationCanceledException) when (context.StopNotification.IsCancellationRequested) { }
        finally { await form.StopFrontendAsync(); }
    }

    private async Task RunDesktopAsync(BusinessBlockRunContext context)
    {
        var session = backendSession;
        if (session is null) return;
        var callbacks = NewCallbacks();
        void VisibilityChanged(IntPtr window, bool visible)
            => callbacks.Post(() => OnObservedHostWindowVisibilityChanged(window, visible));
        desktopObservations.HostWindowVisibilityChanged += VisibilityChanged;
        try
        {
            var run = desktopObservations.RunAsync(session, context.StopNotification);
            context.ReportRunning();
            await run;
        }
        finally
        {
            desktopObservations.HostWindowVisibilityChanged -= VisibilityChanged;
            await callbacks.StopAsync();
        }
    }

    private async Task RunOverlayAsync(BusinessBlockRunContext context)
    {
        var session = backendSession;
        if (session is null) return;
        var run = performanceOverlay.RunAsync(session, context.StopNotification);
        context.ReportRunning();
        await run;
    }

    private async Task RunTrayNotificationsAsync(BusinessBlockRunContext context)
    {
        var session = backendSession;
        if (session is null) return;
        var callbacks = NewCallbacks();
        var run = TrayNotificationStream.RunAsync(session.OpenTrayNotificationStreamAsync,
            new Dictionary<string, Action<System.Text.Json.JsonElement>>
            {
                ["updates"] = updateNotice.ObserveFrame,
                [TrayAccessRequestNotice.SubscriptionId] = accessRequestNotice.ObserveFrame
            }, callbacks.Post, context.StopNotification);
        context.ReportRunning();
        try { await run; }
        finally
        { await callbacks.StopAsync(); }
    }

    private async Task PublishBackendSessionAsync(BackendConnectionState state, BackendServiceSession? session,
        long generation, BackendServiceUnavailableEventArgs? unavailable)
    {
        if (state == BackendConnectionState.Ready)
        {
            var projection = SetReadyBackendSessionProjection();
            SetBackendConnectionState(state);
            mainForm?.ShowBackendReady(projection);
            await trees.SetBackendSessionReadyAsync(true, generation);
            ObserveUiCommand(StartMainWindowFrontendIfVisibleAsync());
            return;
        }

        if (state is BackendConnectionState.Degraded or BackendConnectionState.ShuttingDown)
        {
            _ = SetUnavailableBackendSessionProjection();
            SetBackendConnectionState(state, unavailable?.Message);
            if (unavailable is not null) ShowBackendUnavailableBalloon(unavailable.Message);
            await trees.SetBackendSessionReadyAsync(false, generation);
            // Data-owner retirement: keep the old HTTP clients until all existing users
            // release them. Each tree withdraws its own children from the shared fact.
            await Task.WhenAll(
                trees.WaitForStateAsync(NativeUiBlockIds.DesktopObservation, BusinessBlockState.Stopped),
                trees.WaitForStateAsync(NativeUiBlockIds.Overlay, BusinessBlockState.Stopped),
                trees.WaitForStateAsync(NativeUiBlockIds.TrayNotifications, BusinessBlockState.Stopped));
            return;
        }

        SetBackendConnectionState(state);
        mainForm?.ShowBackendConnecting(reconnecting: state == BackendConnectionState.Reconnecting);
    }

    private void OnWindowCloseRequested(object? sender, EventArgs args)
    {
        if (ReferenceEquals(sender, mainForm))
            ObserveUiCommand(windowRequests.CloseAsync(CloseWindowBranchAsync));
    }

    private async Task CloseWindowBranchAsync()
    {
        await trees.SetWindowWantedAsync(false, "window-close");
        await trees.WaitForStateAsync(NativeUiBlockIds.MainWindow, BusinessBlockState.Stopped);
    }

    private void OnSessionEnding(object sender, Microsoft.Win32.SessionEndingEventArgs args)
    {
        if (uiDispatcher.IsDisposed) return;
        // Do not veto Windows logoff. Keep our message pump alive while the roots drain;
        // Windows' process shutdown deadline remains the outer forced-end policy.
        uiDispatcher.BeginInvoke(new Action(() => ExitApplication("windows-session-ending")));
    }

    private void ObserveUiCommand(Task command)
    {
        uiCommands.RemoveWhere(task => task.IsCompleted);
        uiCommands.Add(ObserveCommandAsync(command));
    }

    private static async Task ObserveCommandAsync(Task command)
    {
        try { await command; }
        catch (OperationCanceledException) { }
        catch (Exception error) { System.Diagnostics.Trace.WriteLine(error); }
    }

    private Task RunOnUiAsync(Func<Task> run)
    {
        if (!uiDispatcher.InvokeRequired) return run();
        var completion = NewSignal();
        uiDispatcher.BeginInvoke(new Action(async () =>
        {
            try { await run(); completion.TrySetResult(); }
            catch (OperationCanceledException error) { completion.TrySetCanceled(error.CancellationToken); }
            catch (Exception error) { completion.TrySetException(error); }
        }));
        return completion.Task;
    }

    private NativeUiCallbackLifetime NewCallbacks() => new(action => RunOnUiAsync(() =>
    {
        action();
        return Task.CompletedTask;
    }));

    private static async Task UntilStoppedAsync(CancellationToken stop)
    {
        try { await Task.Delay(Timeout.InfiniteTimeSpan, stop); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed class UiBlock(Func<BusinessBlockRunContext, Task> run) : IBusinessBlock
    {
        public Task RunAsync(BusinessBlockRunContext context) => run(context);
    }
}

internal sealed class NativeUiTraceLogger<T> : ILogger<T>
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
        => System.Diagnostics.Trace.WriteLine($"{typeof(T).Name}: {formatter(state, exception)}");
}
