using System.Diagnostics;
using ResourceManager.NativeUi.Preferences;

namespace ResourceManager.NativeUi.Overlay;

internal sealed record OverlaySubscription(string Id, string Path);

internal static class OverlaySubscriptions
{
    // Items with a chosen source are read as raw fields instead of their part address (ids=): each chosen
    // device is one device=/fields= pair carrying the distinct field names chosen on it, so only those
    // (device, field) pairs are sampled.
    internal static OverlaySubscription[] Create(FrontendPreferences preferences, StartupModuleOperationsDocument? operations = null) =>
        preferences.Overlays.Where(pair => OverlayStartupModules.Enabled(pair.Key, pair.Value, operations ?? new(1, [])))
            .OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(static pair => (SoftwareId: pair.Key,
                Ids: pair.Value.Metrics.Where(id => pair.Value.SourceFor(id) is null)
                    .OrderBy(static id => id, StringComparer.Ordinal).ToArray(),
                Devices: pair.Value.Metrics.Select(pair.Value.SourceFor).OfType<FrontendOverlayMetricSource>()
                    .GroupBy(static source => source.DeviceIdentity, StringComparer.Ordinal)
                    .OrderBy(static device => device.Key, StringComparer.Ordinal)
                    .Select(static device => (Device: device.Key, Fields: device.Select(static source => source.FieldName)
                        .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()))
                    .ToArray()))
            .Where(static item => item.Ids.Length + item.Devices.Length > 0)
            .Select((item, index) => new OverlaySubscription($"overlay-{index}",
                "/api/performance-overlay/subscribe?intervalMs=500&softwareId=" + Uri.EscapeDataString(item.SoftwareId)
                + string.Concat(item.Ids.Select(static id => "&ids=" + Uri.EscapeDataString(id)))
                + string.Concat(item.Devices.Select(static device => "&device=" + Uri.EscapeDataString(device.Device)
                    + "&fields=" + string.Join(',', device.Fields.Select(Uri.EscapeDataString))))))
            .ToArray();

    // The controller observes operational settings without sampling. Only the
    // explicit data stream below owns observation dependencies.
    internal static async Task RunAsync(
        Func<CancellationToken, Task<StartupModuleOperationsDocument>> readOperations,
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
                    var operations = await readOperations(cancellationToken);
                    var desired = Create(readPreferences(), operations);
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
