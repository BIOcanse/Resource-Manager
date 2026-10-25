using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using ResourceManager.NativeUi.Localization;
using ResourceManager.NativeUi.Preferences;

namespace ResourceManager.NativeUi;

/// <summary>The tray consumes update facts independently of the lifetime of the web window.</summary>
internal sealed class TrayUpdateNotice(
    FrontendPreferencesOwner preferences,
    Func<bool> interfaceVisible,
    Action<string, string> showBalloon,
    Action<Action> dispatch,
    Func<NativeText>? readText = null)
{
    // Called on the UI thread so the visibility decision and balloon cannot race window changes.
    internal void ObserveVersion(string? version)
    {
        if (version is null) return;
        if (preferences.Read().Notifications.UpdateSystem != "on" || interfaceVisible()
            || version == preferences.ReadSystemUpdateNotifiedVersion()) return;
        var text = readText is null ? NativeUiText.Current : readText();
        showBalloon(text.UpdateAvailableTitle, string.Format(text.UpdateAvailableMessageFormat, version));
        preferences.RecordSystemUpdateNotifiedVersion(version);
    }

    internal async Task RunAsync(Func<CancellationToken, Task<HttpResponseMessage>> openStream,
        CancellationToken cancellationToken,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        delay ??= Task.Delay;
        var retrySeconds = 2;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var response = await openStream(cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                using var reader = new StreamReader(stream);
                while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
                {
                    using var frame = JsonDocument.Parse(line);
                    var root = frame.RootElement;
                    if (!root.TryGetProperty("subscriptionId", out var id) || id.GetString() != "updates"
                        || !root.TryGetProperty("value", out var value)
                        || !value.TryGetProperty("availableVersion", out var available)
                        || available.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)) continue;
                    var version = available.GetString();
                    dispatch(() =>
                    {
                        if (cancellationToken.IsCancellationRequested) return;
                        try { ObserveVersion(version); }
                        catch (Exception error) when (error is not OutOfMemoryException)
                        { Trace.TraceError("Tray update notification failed: {0}", error); }
                    });
                }
                Trace.TraceInformation("Tray update subscription ended; retrying in {0}s.", retrySeconds);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception error) when (error is not OutOfMemoryException)
            { Trace.TraceWarning("Tray update subscription failed; retrying in {0}s: {1}", retrySeconds, error); }
            try { await delay(TimeSpan.FromSeconds(retrySeconds), cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            retrySeconds = Math.Min(retrySeconds * 2, 30);
        }
    }
}
