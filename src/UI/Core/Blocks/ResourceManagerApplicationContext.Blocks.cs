using BlockUids = ResourceManager.NativeUi.LegacyStateItems.BlockUids;
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
    private CancellationToken windowLifetime;
    private bool activateRequestedWindow = true;
    private bool secondaryRequestedWindow;
    private readonly NativeUiWindowRequests windowRequests = new();
    private NativeUiProxyBlock? proxyBlock;

    private IBusinessBlock CreateBlock(int id) => new UiBlock(context => RunOnUiAsync(() => id switch
    {
        BlockUids.MainRoot => RunMainRootAsync(context),
        BlockUids.TrayRoot => RunTrayRootAsync(context),
        BlockUids.SingleInstance => RunSingleInstanceAsync(context),
        BlockUids.BackendConnection => RunBackendConnectionAsync(context),
        BlockUids.MainWindow => RunWindowAsync(context),
        BlockUids.LoopbackProxy => RunProxyAsync(context),
        BlockUids.WebViewHost => RunWebViewAsync(context),
        BlockUids.DesktopObservation => RunDesktopAsync(context),
        BlockUids.Overlay => RunOverlayAsync(context),
        BlockUids.TrayNotifications => RunTrayNotificationsAsync(context),
        _ => throw new InvalidOperationException("Unknown native UI block: " + id)
    }));

    private async Task RunBackendConnectionAsync(BusinessBlockRunContext context)
    {
        await trees.StartBackendSessionPublicationAsync();
        try { await backendConnection.RunAsync(context); }
        finally { await trees.StopBackendSessionPublicationAsync(); }
    }

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
            hotkeys.Start();
            context.ReportRunning();
            await UntilStoppedAsync(context.StopNotification);
        }
        finally
        {
            NativeUiText.Changed -= LanguageChanged;
            await callbacks.StopAsync();
            hotkeys.Dispose();
            await performanceOverlay.DisposeAsync();
            await desktopObservations.DisposeAsync();
            await Task.WhenAll(uiCommands.ToArray());
            uiCommands.Clear();
        }
    }

    private async Task RunTrayRootAsync(BusinessBlockRunContext context)
    {
        trayMenu = BuildTrayMenu();
        trayIcon = services.CreateTrayIcon is { } createTray
            ? createTray(trayMenu) : new NotifyIcon
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
        // A balloon click opens the main window (BalloonTipClicked above), whichever kind it shows.
        void ShowNotice(string title, string message) => trayIcon.ShowBalloonTip(6000, title, message, ToolTipIcon.Info);
        backendNotifications = new TrayBackendNotifications(
            services.Preferences ?? Preferences.FrontendPreferencesOwner.Current, ShowNotice);
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
        frontendProxy = new FrontendLoopbackProxy(new Uri((services.ApiBaseAddress ?? ApiBaseAddress).TrimEnd('/') + "/"),
            (request, token) => backendReady && backendSession is { } current
                ? current.SendFrontendRequestAsync(request, token)
                : Task.FromException<System.Net.Http.HttpResponseMessage>(
                    new InvalidOperationException(NativeUiText.Current.BackendPipelineNotReady)), services.BeforeProxyStart);
        proxyBlock = new NativeUiProxyBlock(frontendProxy, failure => mainForm?.RecordProxyStartupFailure(failure));
        try
        {
            var window = GetOrCreateMainForm();
            // Showing is serial preparation of the window. Descendants cannot begin
            // initialization until their window is visible and reports Running.
            PresentMainWindow(window, activateRequestedWindow, secondaryRequestedWindow);
            context.ReportRunning();
            await UntilStoppedAsync(context.StopNotification);
        }
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
            _ = await form.InitializeFrontendForBlockAsync().WaitAsync(context.StopNotification);
            context.ReportRunning();
            await UntilStoppedAsync(context.StopNotification);
        }
        catch (OperationCanceledException) when (context.StopNotification.IsCancellationRequested) { }
        finally { await form.StopFrontendAsync(); }
    }

    private async Task RunDesktopAsync(BusinessBlockRunContext context)
    {
        using var hold = backendConnection.TryAcquireCurrentSession();
        if (hold is null) return;
        var session = hold.Session;
        var callbacks = NewCallbacks();
        void VisibilityChanged(IntPtr window, bool visible)
            => callbacks.Post(() => OnObservedHostWindowVisibilityChanged(window, visible));
        desktopObservations.HostWindowVisibilityChanged += VisibilityChanged;
        try
        {
            var run = services.ObserveDesktop is { } observe
                ? observe(session, context.StopNotification) : desktopObservations.RunAsync(session, context.StopNotification);
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
        using var hold = backendConnection.TryAcquireCurrentSession();
        if (hold is null) return;
        var session = hold.Session;
        var run = services.ObserveOverlay is { } observe
            ? observe(session, context.StopNotification) : performanceOverlay.RunAsync(session, context.StopNotification);
        context.ReportRunning();
        await run;
    }

    private async Task RunTrayNotificationsAsync(BusinessBlockRunContext context)
    {
        using var hold = backendConnection.TryAcquireCurrentSession();
        if (hold is null) return;
        var session = hold.Session;
        var callbacks = NewCallbacks();
        var run = services.ObserveTray is { } observe ? observe(session, context.StopNotification)
            : backendNotifications.RunAsync(session.OpenTrayNotificationStreamAsync, callbacks.Post, context.StopNotification);
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
        await trees.WaitForStateAsync(BlockUids.MainWindow, BusinessBlockState.Stopped);
    }

    private void OnSessionEnded(object sender, Microsoft.Win32.SessionEndedEventArgs args)
    {
        if (uiDispatcher.IsDisposed) return;
        // Windows has confirmed session end. Keep our message pump alive while the roots drain;
        // Windows' process shutdown deadline remains the outer forced-end policy.
        uiDispatcher.BeginInvoke(new Action(() => ExitApplication("windows-session-ended")));
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
