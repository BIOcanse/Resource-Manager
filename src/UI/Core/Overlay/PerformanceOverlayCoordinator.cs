using System.Diagnostics;
using System.Drawing;
using System.Net.Http;
using System.Text.Json;
using ResourceManager.NativeUi.Localization;

namespace ResourceManager.NativeUi.Overlay;

internal sealed class PerformanceOverlayCoordinator : IDisposable
{
    private static readonly JsonSerializerOptions StreamJson = new(JsonSerializerDefaults.Web);
    private readonly Control dispatcher;
    private readonly System.Windows.Forms.Timer windowTimer;
    private readonly Dictionary<OverlayTargetId, DisplayTarget> targets = [];
    private CancellationTokenSource? subscriptionCancellation;
    private long streamGeneration;
    private bool disposed;

    public PerformanceOverlayCoordinator(Control dispatcher)
    {
        this.dispatcher = dispatcher;
        windowTimer = new System.Windows.Forms.Timer { Interval = 100 };
        windowTimer.Tick += (_, _) => Refresh();
        NativeUiText.Changed += OnLanguageChanged;
    }

    internal int TargetCount => targets.Count;

    public void Start(BackendServiceSession session)
    {
        Stop();
        if (disposed) return;
        var cancellation = new CancellationTokenSource();
        subscriptionCancellation = cancellation;
        windowTimer.Start();
        _ = ReadStreamAsync(session, ++streamGeneration, cancellation.Token);
    }

    public void Stop()
    {
        subscriptionCancellation?.Cancel();
        subscriptionCancellation?.Dispose();
        subscriptionCancellation = null;
        streamGeneration++;
        windowTimer.Stop();
        Clear();
    }

    internal void ApplySnapshot(OverlaySnapshot snapshot)
    {
        if (disposed || snapshot.Version != 1) { Clear(); return; }
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
        var signature = $"{target.ContentSignature}|{NativeUiText.Language}|{size.Width}x{size.Height}";
        if (signature != target.RenderSignature)
        {
            var image = OverlayBitmapRenderer.Render(target.Target, size);
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

    private async Task ReadStreamAsync(BackendServiceSession session, long generation,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var response = await session.OpenPerformanceOverlayStreamAsync(cancellationToken);
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var reader = new StreamReader(stream);
                while (!cancellationToken.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(cancellationToken);
                    if (line is null) break;
                    using var frame = JsonDocument.Parse(line);
                    var root = frame.RootElement;
                    if (!root.TryGetProperty("subscriptionId", out var subscriptionId)
                        || subscriptionId.GetString() != "performance-overlay"
                        || !root.TryGetProperty("value", out var value)) continue;
                    var snapshot = value.Deserialize<OverlaySnapshot>(StreamJson);
                    if (snapshot is not null) Post(() =>
                    {
                        if (generation == streamGeneration) ApplySnapshot(snapshot);
                    });
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                Trace.WriteLine(exception);
            }
            Post(() => { if (generation == streamGeneration) Clear(); });
            try { await Task.Delay(1000, cancellationToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void Post(Action action)
    {
        if (disposed || dispatcher.IsDisposed || !dispatcher.IsHandleCreated) return;
        try
        {
            dispatcher.BeginInvoke(new Action(() =>
            {
                if (!disposed) action();
            }));
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException) { }
    }

    private static string ContentSignature(OverlayTarget target) =>
        JsonSerializer.Serialize(new { target.Settings, target.Metrics }, StreamJson);

    private void OnLanguageChanged(object? sender, EventArgs args) => Post(() =>
    {
        foreach (var target in targets.Values) target.RenderSignature = null;
        Refresh();
    });

    private void Remove(OverlayTargetId id)
    {
        if (!targets.Remove(id, out var target)) return;
        target.ResetOutput();
    }

    private void Clear()
    {
        foreach (var target in targets.Values) target.ResetOutput();
        targets.Clear();
    }

    public void Dispose()
    {
        if (disposed) return;
        Stop();
        disposed = true;
        NativeUiText.Changed -= OnLanguageChanged;
        windowTimer.Dispose();
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
