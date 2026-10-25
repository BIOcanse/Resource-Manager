using System.Diagnostics;
using ResourceManager.NativeUi.Preferences;

namespace ResourceManager.NativeUi.Overlay;

internal sealed record OverlaySubscription(string Id, string Path);
internal sealed record OverlaySettingsDocument(int Version, IReadOnlyList<OverlaySettings> Software);

internal static class OverlaySubscriptions
{
    internal static OverlaySubscription[] Create(IReadOnlyList<OverlaySettings> settings, FrontendPreferences preferences) =>
        settings.Where(static setting => setting.Enabled)
            .OrderBy(static setting => setting.SoftwareId, StringComparer.OrdinalIgnoreCase)
            .Select(setting => (setting.SoftwareId, Metrics: preferences.Overlays.FirstOrDefault(pair =>
                pair.Key.Equals(setting.SoftwareId, StringComparison.OrdinalIgnoreCase)).Value?.Metrics
                ?? FrontendPoints.MetricOrder))
            .Where(static item => item.Metrics.Count > 0)
            .Select((item, index) => new OverlaySubscription($"overlay-{index}",
                "/api/performance-overlay/subscribe?intervalMs=500&softwareId=" + Uri.EscapeDataString(item.SoftwareId)
                + string.Concat(item.Metrics.OrderBy(static id => id, StringComparer.Ordinal)
                    .Select(static id => "&ids=" + Uri.EscapeDataString(id)))))
            .ToArray();

    // The controller observes operational settings without sampling. Only the
    // explicit data stream below owns observation dependencies.
    internal static async Task RunAsync(
        Func<CancellationToken, Task<OverlaySettingsDocument>> readSettings,
        Func<FrontendPreferences> readPreferences,
        Func<OverlaySubscription[], CancellationToken, Task> observe,
        Action clear, TimeSpan settingsInterval, CancellationToken cancellationToken)
    {
        CancellationTokenSource? active = null;
        Task stream = Task.CompletedTask;
        string? previous = null;
        try
        {
            using var timer = new PeriodicTimer(settingsInterval);
            do
            {
                try
                {
                    var settings = await readSettings(cancellationToken);
                    var desired = Create(settings.Software, readPreferences());
                    var signature = string.Join('\n', desired.Select(static item => item.Path));
                    if (signature == previous) continue;
                    active?.Cancel();
                    await stream;
                    active?.Dispose(); active = null;
                    clear();
                    previous = signature;
                    if (desired.Length > 0)
                    {
                        active = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        stream = observe(desired, active.Token);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    Trace.TraceError("Overlay dependencies could not be refreshed: {0}", error);
                    active?.Cancel();
                    try { await stream; } catch (OperationCanceledException) { }
                    active?.Dispose(); active = null;
                    stream = Task.CompletedTask;
                    previous = null;
                    clear();
                }
            } while (await timer.WaitForNextTickAsync(cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally
        {
            active?.Cancel();
            try { await stream; } catch (OperationCanceledException) { }
            active?.Dispose();
        }
    }
}
