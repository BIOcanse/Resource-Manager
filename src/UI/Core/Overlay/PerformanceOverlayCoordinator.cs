using System.Diagnostics;
using System.Drawing;
using System.Net.Http;
using System.Text.Json;
using ResourceManager.NativeUi.Localization;
using ResourceManager.NativeUi.Preferences;

namespace ResourceManager.NativeUi.Overlay;

internal sealed class PerformanceOverlayCoordinator : IDisposable, IAsyncDisposable
{
    private static readonly JsonSerializerOptions StreamJson = new(JsonSerializerDefaults.Web);
    private readonly Action<Action> dispatch;
    private readonly object callbackSync = new();
    private readonly Dictionary<long, Action> pendingCallbacks = [];
    private readonly System.Windows.Forms.Timer windowTimer;
    private readonly Dictionary<OverlayTargetId, DisplayTarget> targets = [];
    private CancellationTokenSource? subscriptionCancellation;
    private Task? completion;
    private long streamGeneration;
    private long callbackId;
    private bool acceptingCallbacks;
    private bool disposed;

    public PerformanceOverlayCoordinator(Control dispatcher)
        : this(action =>
        {
            if (dispatcher.IsDisposed || !dispatcher.IsHandleCreated)
                throw new InvalidOperationException("The overlay dispatcher is unavailable.");
            dispatcher.BeginInvoke(action);
        })
    {
    }

    internal PerformanceOverlayCoordinator(Action<Action> dispatch)
    {
        this.dispatch = dispatch;
        windowTimer = new System.Windows.Forms.Timer { Interval = 100 };
        windowTimer.Tick += (_, _) => Refresh();
    }

    internal int TargetCount => targets.Count;
    internal bool WindowRefreshTimerEnabled => windowTimer.Enabled;

    public void Start(BackendServiceSession session)
        => _ = RunAsync(session, CancellationToken.None);

    public Task RunAsync(BackendServiceSession session, CancellationToken cancellationToken)
        => RunAsync((post, token) => OverlaySubscriptions.RunAsync(
            session.ReadStartupModulesAsync, FrontendPreferencesOwner.Current.Read,
            (subscriptions, streamToken) => ReadStreamAsync(session, subscriptions, post, streamToken),
            () => post(Clear), TimeSpan.FromMilliseconds(500), token), cancellationToken);

    // Called and awaited on the original UI context, so output cleanup stays on its STA.
    internal Task RunAsync(Func<Action<Action>, CancellationToken, Task> observeAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(observeAsync);
        ObjectDisposedException.ThrowIf(disposed, this);
        if (completion is { IsCompleted: false }) return completion;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        subscriptionCancellation = cancellation;
        long generation;
        lock (callbackSync)
        {
            generation = ++streamGeneration;
            acceptingCallbacks = true;
        }
        NativeUiText.Changed += OnLanguageChanged;
        completion = RunCoreAsync(observeAsync, cancellation, generation);
        return completion;
    }

    private async Task RunCoreAsync(Func<Action<Action>, CancellationToken, Task> observeAsync,
        CancellationTokenSource cancellation, long generation)
    {
        try { await observeAsync(action => Post(action, generation), cancellation.Token); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally
        {
            RetireCallbacks();
            if (!disposed)
            {
                windowTimer.Stop();
                Clear();
            }
            subscriptionCancellation = null;
            cancellation.Dispose();
        }
    }

    public void Stop()
    {
        subscriptionCancellation?.Cancel();
        RetireCallbacks();
        windowTimer.Stop();
        Clear();
    }

    public async Task StopAsync()
    {
        Stop();
        if (completion is not null) await completion;
    }

    private void RetireCallbacks()
    {
        lock (callbackSync)
        {
            acceptingCallbacks = false;
            streamGeneration++;
            // Posted thunks retain only an id; retire their actual actions now.
            pendingCallbacks.Clear();
        }
        NativeUiText.Changed -= OnLanguageChanged;
    }

    internal void ApplySnapshot(OverlaySnapshot snapshot)
    {
        if (disposed) return;
        if (snapshot.Version != 3) { Clear(); return; }
        var current = OverlayTargetSelection.Current(snapshot);
        foreach (var id in targets.Keys.Where(id => !current.ContainsKey(id)).ToArray())
            Remove(id);
        foreach (var (id, target) in current)
        {
            if (!targets.TryGetValue(id, out var existing))
                targets.Add(id, new DisplayTarget(target));
            else
            {
                var content = ContentSignature(target);
                if (content != existing.ContentSignature)
                {
                    var previousMode = existing.Mode;
                    existing.ContentSignature = content;
                    existing.Target = target;
                    existing.RenderSignature = null;
                    // A process first seen in external mode cannot acquire the DLL until its next launch.
                    if (target.Settings.Mode == "external") existing.ActiveMode = "external";
                    else if (previousMode == "injected" || existing.Injected is not null)
                        existing.ActiveMode = "injected";
                }
            }
        }
        UpdateWindowTimerState();
        Refresh();
    }

    internal void Refresh()
    {
        if (disposed) return;
        foreach (var (id, target) in targets.ToArray())
        {
            try
            {
                var window = OverlayTargetWindow.Read(id, target.Window);
                if (window.Exited) { Remove(id); continue; }
                if (window.Handle != target.Window)
                {
                    target.Window = window.Handle;
                    target.RenderSignature = null;
                    target.External?.Hide();
                    target.Injected?.Disable();
                    target.Injected?.SetTargetWindow(window.Handle);
                }
                if (window.Handle == 0 || !window.Visible)
                {
                    target.External?.Hide();
                    target.Injected?.Disable();
                    target.RenderSignature = null;
                    continue;
                }
                var injected = target.ActiveMode == "injected";
                if (injected)
                {
                    target.External?.Dispose();
                    target.External = null;
                    target.Injected ??= new InjectedOverlaySection(id, window.Handle);
                    target.Injected.SetTargetWindow(window.Handle);
                    var swapSize = target.Injected.SwapChainSize;
                    if (swapSize.Width <= 0 || swapSize.Height <= 0) continue;
                    RenderIfNeeded(target, swapSize, window.ClientBounds, injected: true);
                }
                else
                {
                    // Keep a loaded DLL's section disabled so a later switch back can reuse it.
                    target.Injected?.Disable();
                    if (!window.Foreground)
                    {
                        target.External?.Hide();
                        continue;
                    }
                    target.External ??= new ExternalOverlayWindow();
                    RenderIfNeeded(target, window.ClientBounds.Size, window.ClientBounds, injected: false);
                }
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                Trace.WriteLine(exception);
                target.ResetOutput();
            }
        }
    }

    private void RenderIfNeeded(DisplayTarget target, Size size, Rectangle bounds, bool injected)
    {
        var preferences = FrontendPreferencesOwner.Current;
        var presentation = preferences.Overlay(target.Target.SoftwareId);
        var signature = $"{target.ContentSignature}|{NativeUiText.Language}|{size.Width}x{size.Height}|{preferences.Revision}";
        if (signature != target.RenderSignature)
        {
            var image = OverlayBitmapRenderer.Render(target.Target, size, presentation);
            if (image is null)
            {
                target.External?.Hide();
                target.Injected?.Disable();
                return;
            }
            if (injected) target.Injected!.Publish(image);
            else target.External!.Update(image, bounds);
            target.RenderSignature = signature;
            target.LastBounds = bounds;
        }
        else if (!injected && (bounds != target.LastBounds || !target.External!.IsShown))
        {
            target.External!.Move(bounds);
            target.LastBounds = bounds;
        }
    }

    private async Task ReadStreamAsync(BackendServiceSession session, OverlaySubscription[] subscriptions, Action<Action> post,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var snapshots = new Dictionary<string, OverlaySnapshot>();
                using var response = await session.OpenPerformanceOverlayStreamAsync(subscriptions, cancellationToken);
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var reader = new StreamReader(stream);
                while (!cancellationToken.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(cancellationToken);
                    if (line is null) break;
                    using var frame = JsonDocument.Parse(line);
                    var root = frame.RootElement;
                    if (!root.TryGetProperty("subscriptionId", out var subscriptionId)
                        || !subscriptions.Any(item => item.Id == subscriptionId.GetString())
                        || !root.TryGetProperty("value", out var value)) continue;
                    var snapshot = value.Deserialize<OverlaySnapshot>(StreamJson);
                    if (snapshot is null) continue;
                    var preferences = FrontendPreferencesOwner.Current.Read();
                    // Each subscription is one software: its targets read the raw fields it carried.
                    snapshot = snapshot with { Targets = snapshot.Targets.Select(target =>
                    {
                        var choice = preferences.Overlays.FirstOrDefault(pair => pair.Key.Equals(target.SoftwareId, StringComparison.OrdinalIgnoreCase)).Value ?? new();
                        return target with { Settings = new(target.SoftwareId, choice.Mode == "injected" || choice.Enabled, choice.Mode), Fields = snapshot.Fields };
                    }).ToArray() };
                    snapshots[subscriptionId.GetString()!] = snapshot;
                    var combined = snapshot with { Targets = snapshots.Values.SelectMany(static item => item.Targets).ToArray() };
                    post(() =>
                    {
                        if (!cancellationToken.IsCancellationRequested) ApplySnapshot(combined);
                    });
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                Trace.WriteLine(exception);
            }
            post(Clear);
            try { await Task.Delay(1000, cancellationToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void Post(Action action, long generation)
    {
        lock (callbackSync)
        {
            if (disposed || !acceptingCallbacks || generation != streamGeneration) return;
            var id = ++callbackId;
            pendingCallbacks.Add(id, action);
            try
            {
                dispatch(() =>
                {
                    Action? callback;
                    lock (callbackSync)
                    {
                        if (!pendingCallbacks.Remove(id, out callback)) return;
                    }
                    callback();
                });
            }
            catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
            {
                pendingCallbacks.Remove(id);
            }
        }
    }

    private static string ContentSignature(OverlayTarget target) =>
        JsonSerializer.Serialize(new { target.Settings, target.Metrics, target.Fields }, StreamJson);

    private void OnLanguageChanged(object? sender, EventArgs args) => Post(() =>
    {
        foreach (var target in targets.Values) target.RenderSignature = null;
        Refresh();
    }, Volatile.Read(ref streamGeneration));

    private void Remove(OverlayTargetId id)
    {
        if (!targets.Remove(id, out var target)) return;
        target.ResetOutput();
        UpdateWindowTimerState();
    }

    private void Clear()
    {
        foreach (var target in targets.Values) target.ResetOutput();
        targets.Clear();
        UpdateWindowTimerState();
    }

    private void UpdateWindowTimerState()
    {
        if (disposed || targets.Count == 0)
            windowTimer.Stop();
        else if (!windowTimer.Enabled)
            windowTimer.Start();
    }

    public void Dispose()
    {
        if (disposed) return;
        Stop();
        disposed = true;
        windowTimer.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        try { await StopAsync(); }
        finally { Dispose(); }
    }

    private sealed class DisplayTarget(OverlayTarget target)
    {
        public OverlayTarget Target { get; set; } = target;
        public string ContentSignature { get; set; } = PerformanceOverlayCoordinator.ContentSignature(target);
        public string? RenderSignature { get; set; }
        public string Mode => ActiveMode;
        public string ActiveMode { get; set; } = target.Settings.Mode;
        public nint Window { get; set; }
        public Rectangle LastBounds { get; set; }
        public ExternalOverlayWindow? External { get; set; }
        public InjectedOverlaySection? Injected { get; set; }

        public void ResetOutput()
        {
            External?.Dispose();
            External = null;
            Injected?.Dispose();
            Injected = null;
            RenderSignature = null;
        }
    }
}
