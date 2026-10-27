using ResourceManager.NativeUi.Blocks;
using ResourceManager.NativeUi.Localization;
using ResourceManager.NativeUi.StateItems;
using ResourceManager.NativeUi.WebView;
using ResourceManager.Shared.BusinessBlocks;

namespace ResourceManager.NativeUi;

/// <summary>Only the window branch lives here. The resident owns the parent demand and backend session.</summary>
internal sealed class NativeWindowApplicationContext : ApplicationContext
{
    private readonly ResidentWindowPipe pipe;
    private readonly NativeUiApplicationServices services;
    private readonly Control dispatcher = new();
    private readonly NativeWindowBlockTrees trees;
    private readonly CancellationTokenSource stopping = new();
    private readonly HashSet<Task> messages = [];
    private Task? pipeRun;
    private Task? stopRun;
    private MainForm? form;
    private FrontendLoopbackProxy? proxy;
    private NativeUiProxyBlock? proxyBlock;
    private BackendServiceSession? transport;
    private bool ready;
    private bool draining;
    private bool drained;
    private bool started;
    private bool parentVisible = true;
    private long generation;
    private long projectionRevision;
    private string command = "show";
    private FrontendBackendSessionProjection? projection;

    internal NativeWindowApplicationContext(ResidentWindowPipe pipe, NativeUiApplicationServices? services = null)
    {
        this.pipe = pipe;
        this.services = services ?? new();
        _ = dispatcher.Handle;
        trees = new(CreateBlock, new NativeUiTraceLogger<NativeWindowBlockTrees>(), new NativeUiTraceLogger<BusinessBlockController>());
        Application.Idle += Start;
    }

    private void Start(object? sender, EventArgs args)
    {
        if (started) return;
        started = true;
        Application.Idle -= Start;
        pipeRun = Task.Run(() => pipe.RunAsync(message => OnUiAsync(() => ReceiveAsync(message))));
    }

    private async Task ReceiveAsync(ResidentWindowMessage message)
    {
        if (draining) return;
        switch (message.Type)
        {
            case "parent-wants-me-running":
                await trees.SetParentWantsRunningAsync(message.Flag);
                if (!message.Flag) BeginStop();
                break;
            case "backend-session":
                if (message.Generation < generation) return;
                var changed = projection is null || generation != message.Generation || ready != message.Flag;
                generation = message.Generation;
                ready = message.Flag;
                if (changed)
                {
                    await RetireTransportAsync();
                    if (ready) PrepareTransport();
                    projection = ready && transport is not null ? FrontendBackendSessionProjection.CreateReady(++projectionRevision)
                        : FrontendBackendSessionProjection.CreateUnavailable(++projectionRevision, "backend-unavailable");
                    ApplyBackendState();
                }
                await trees.SetBackendSessionAsync(ready, generation);
                if (changed && ready && form is { } window && trees.ReadState(BlockUids.WebViewHost) == BusinessBlockState.Running)
                    SendFrontendStart(window);
                break;
            case "language":
                NativeUiText.Apply(message.Value);
                break;
            case "visibility":
                parentVisible = message.Flag;
                form?.SetDesktopOccluded(!parentVisible);
                break;
            case "show-window":
                command = message.Value!;
                if (form is { } shownWindow) Present(shownWindow);
                break;
        }
    }

    private IBusinessBlock CreateBlock(int id) => new WindowBlock(context => OnUiAsync(() => id switch
    {
        BlockUids.MainWindow => RunWindowAsync(context),
        BlockUids.LoopbackProxy => proxyBlock!.RunAsync(context),
        BlockUids.WebViewHost => RunWebViewAsync(context),
        _ => throw new InvalidOperationException("Unknown window block.")
    }));

    private async Task RunWindowAsync(BusinessBlockRunContext context)
    {
        proxy = new FrontendLoopbackProxy(new Uri((services.ApiBaseAddress ?? "http://127.0.0.1:9321").TrimEnd('/') + "/"),
            SendFrontendRequestAsync, services.BeforeProxyStart);
        proxyBlock = new(proxy, failure => form?.RecordProxyStartupFailure(failure));
        var host = (services.CreateFrontendHost ?? (static configuration => new WebView2FrontendHost(configuration)))(
            new(services.WebViewUserDataFolder ?? NativeUiPaths.WebViewUserDataFolder,
                services.FrontendPreferencesPath ?? NativeUiPaths.FrontendPreferencesPath, proxy));
        var window = form = new MainForm(
            _ => Send("preferences-changed", "scope", "backend"),
            () => Send("preferences-changed", "scope", "backend"), host,
            batch => WithTransportAsync(session => session.ExecuteElevatedProcessActionAsync(batch, stopping.Token)),
            mutation => WithTransportAsync(session => session.ExecuteElevatedBackendRequestAsync(mutation, stopping.Token)),
            services.WebViewStartupFailurePath);
        void LanguageChanged(object? _, EventArgs __)
        {
            if (!window.IsDisposed) window.ApplyInterfaceLanguage();
            Send("language", "language", NativeUiText.Language);
        }
        void HandleChanged(object? _, EventArgs __)
        {
            if (!window.IsHandleCreated) Send("window-handle", "handle", "0");
            else if (window.Visible) Send("window-handle", "handle",
                window.Handle.ToInt64().ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        void CloseRequested(object? _, EventArgs __) => Send("user-close");
        void ExitRequested(object? _, EventArgs __) => Send("applyExit");
        void DiagnosticsRequested(object? _, EventArgs __) => NativeUiDiagnostics.OpenFolder();
        void PreferencesChanged(object? _, EventArgs __) => Send("preferences-changed", "scope", "frontend");
        void Retry(object? _, EventArgs __)
        {
            if (ready && transport is null)
            {
                PrepareTransport();
                if (transport is not null) projection = FrontendBackendSessionProjection.CreateReady(++projectionRevision);
            }
            ApplyBackendState(); proxyBlock?.RequestRetry();
            if (ready && transport is not null) SendFrontendStart(window);
        }
        NativeUiText.Changed += LanguageChanged;
        window.HandleDestroyed += HandleChanged;
        window.WindowCloseRequested += CloseRequested;
        window.ExitRequested += ExitRequested;
        window.DiagnosticsRequested += DiagnosticsRequested;
        window.PreferencesChanged += PreferencesChanged;
        window.BackendRetryRequested += Retry;
        try
        {
            ApplyBackendState();
            window.SetDesktopOccluded(!parentVisible);
            Present(window);
            context.ReportRunning();
            window.HandleCreated += HandleChanged;
            HandleChanged(window, EventArgs.Empty);
            await UntilStoppedAsync(context.StopNotification);
        }
        finally
        {
            await window.StopFrontendAsync();
            NativeUiText.Changed -= LanguageChanged;
            window.WindowCloseRequested -= CloseRequested;
            window.ExitRequested -= ExitRequested;
            window.DiagnosticsRequested -= DiagnosticsRequested;
            window.PreferencesChanged -= PreferencesChanged;
            window.BackendRetryRequested -= Retry;
            window.CompleteClose();
            window.HandleCreated -= HandleChanged;
            window.HandleDestroyed -= HandleChanged;
            window.Dispose();
            form = null;
            await proxy.DisposeAsync();
            proxy = null;
            proxyBlock = null;
        }
    }

    private async Task RunWebViewAsync(BusinessBlockRunContext context)
    {
        var window = form!;
        try
        {
            _ = await window.InitializeFrontendForBlockAsync().WaitAsync(context.StopNotification);
            context.ReportRunning();
            await window.StartFrontendAsync();
            await UntilStoppedAsync(context.StopNotification);
        }
        catch (OperationCanceledException) when (context.StopNotification.IsCancellationRequested) { }
        finally { await window.StopFrontendAsync(); }
    }

    private void Present(MainForm window)
    {
        var activate = command == "show";
        var secondary = command == "show-passive-secondary";
        if (services.PresentWindow is { } present) { present(window, activate, secondary); return; }
        var placed = secondary && window.PrepareForPassiveValidationOnSecondaryScreen();
        if (activate && !placed && (!window.Visible || window.WindowState == FormWindowState.Minimized)) window.PrepareForOpenOnPrimaryScreen();
        if (!window.Visible) { if (activate) window.Show(); else window.ShowPassively(); }
        if (window.WindowState == FormWindowState.Minimized) window.WindowState = FormWindowState.Normal;
        if (activate) { window.BringToFront(); window.Activate(); }
    }

    private void PrepareTransport()
    {
        var candidate = (services.CreateSession ?? (() => BackendServiceSession.CreateDefault(services.ApiBaseAddress ?? "http://127.0.0.1:9321")))();
        try { candidate.PrepareWindowTransport(); Volatile.Write(ref transport, candidate); }
        catch (Exception error) { candidate.Dispose(); System.Diagnostics.Trace.WriteLine(error); }
    }
    private async Task RetireTransportAsync()
    {
        if (Interlocked.Exchange(ref transport, null) is { } previous) await previous.DisposeAsync();
    }
    private async Task<T> WithTransportAsync<T>(Func<BackendServiceSession, Task<T>> action)
    {
        using var hold = Volatile.Read(ref transport)?.TryAcquire()
            ?? throw new InvalidOperationException(NativeUiText.Current.BackendPipelineNotReady);
        return await action(hold.Session);
    }
    private async Task<System.Net.Http.HttpResponseMessage> SendFrontendRequestAsync(System.Net.Http.HttpRequestMessage request, CancellationToken token)
    {
        var hold = Volatile.Read(ref transport)?.TryAcquire()
            ?? throw new InvalidOperationException(NativeUiText.Current.BackendPipelineNotReady);
        try
        {
            var response = await hold.Session.SendFrontendRequestAsync(request, token);
            response.Content = new HeldContent(response.Content, hold);
            return response;
        }
        catch { hold.Dispose(); throw; }
    }
    private void ApplyBackendState()
    {
        if (form is not { } window) return;
        if (ready && transport is not null && projection is { IsReady: true } current) window.ShowBackendReady(current);
        else if (projection is { IsReady: false } unavailable) window.ShowBackendUnavailable(NativeUiText.Current.NoVerifiedSession, unavailable);
        else window.ShowBackendConnecting(reconnecting: generation != 0);
    }
    private void SendFrontendStart(MainForm window)
    {
        messages.RemoveWhere(task => task.IsCompleted);
        messages.Add(window.StartFrontendAsync());
    }
    private void Send(string type, string? field = null, string? value = null)
    {
        messages.RemoveWhere(task => task.IsCompleted);
        try { messages.Add(pipe.SendAsync(type, field, value)); }
        catch (Exception error) when (error is InvalidOperationException or InvalidDataException)
        {
            System.Diagnostics.Trace.WriteLine(error);
            pipe.Dispose(); // A failed fact transport withdraws the parent's demand through the reader.
        }
    }
    private void BeginStop()
    {
        if (draining) return;
        draining = true;
        form?.Hide();
        stopping.Cancel();
        stopRun = StopAsync();
    }
    private async Task StopAsync()
    {
        await trees.DisposeAsync();
        await RetireTransportAsync();
        await pipe.DisposeAsync();
        if (pipeRun is not null) await pipeRun;
        await Task.WhenAll(messages);
        drained = true;
        ExitThread();
    }
    protected override void ExitThreadCore() { if (!drained) { BeginStop(); return; } base.ExitThreadCore(); }
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Application.Idle -= Start;
            pipe.Dispose();
            dispatcher.Dispose();
            stopping.Dispose();
        }
        base.Dispose(disposing);
    }
    private Task OnUiAsync(Func<Task> action)
    {
        if (!dispatcher.InvokeRequired) return action();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        dispatcher.BeginInvoke(new Action(async () =>
        {
            try { await action(); completion.TrySetResult(); }
            catch (Exception error) { completion.TrySetException(error); }
        }));
        return completion.Task;
    }
    private static async Task UntilStoppedAsync(CancellationToken token)
    {
        try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
    private sealed class WindowBlock(Func<BusinessBlockRunContext, Task> run) : IBusinessBlock
    { public Task RunAsync(BusinessBlockRunContext context) => run(context); }
    private sealed class HeldContent : System.Net.Http.HttpContent
    {
        private readonly System.Net.Http.HttpContent inner;
        private readonly BackendServiceSession.Hold hold;
        internal HeldContent(System.Net.Http.HttpContent inner, BackendServiceSession.Hold hold)
        {
            this.inner = inner; this.hold = hold;
            foreach (var header in inner.Headers) Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
            => inner.CopyToAsync(stream);
        protected override bool TryComputeLength(out long length)
        { length = inner.Headers.ContentLength.GetValueOrDefault(); return inner.Headers.ContentLength.HasValue; }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { inner.Dispose(); hold.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
