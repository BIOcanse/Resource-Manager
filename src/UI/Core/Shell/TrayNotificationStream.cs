using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace ResourceManager.NativeUi;

/// <summary>One stream and reconnect schedule for the tray's independent notice consumers.</summary>
internal static class TrayNotificationStream
{
    internal static async Task RunAsync(Func<CancellationToken, Task<HttpResponseMessage>> openStream,
        IReadOnlyDictionary<string, Action<JsonElement>> consumers, Action<Action> dispatch,
        CancellationToken cancellationToken, Func<TimeSpan, CancellationToken, Task>? delay = null)
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
                var selectorDenied = false;
                while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
                {
                    using var frame = JsonDocument.Parse(line);
                    var root = frame.RootElement;
                    if (!root.TryGetProperty("subscriptionId", out var id)
                        || !consumers.TryGetValue(id.GetString() ?? "", out var consume)
                        || !root.TryGetProperty("value", out var value)) continue;
                    // Unified channels can reject a selector inside a successful HTTP response.
                    if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("statusCode", out var status)
                        && status.TryGetInt32(out var code) && code is 401 or 403)
                    {
                        // Keep other authorized selectors running. Credential revocation ends the
                        // channel; an ended channel retries with the same bounded backoff below.
                        selectorDenied = true;
                        continue;
                    }
                    var snapshot = value.Clone();
                    dispatch(() =>
                    {
                        if (cancellationToken.IsCancellationRequested) return;
                        try { consume(snapshot); }
                        catch (Exception error) when (error is not OutOfMemoryException)
                        { Trace.TraceError("Tray notification failed: {0}", error); }
                    });
                }
                if (!selectorDenied)
                    Trace.TraceInformation("Tray notification subscription ended; retrying in {0}s.", retrySeconds);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (HttpRequestException error) when (error.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            { } // Expected while credentials/scopes are unavailable; retry without repeated logs.
            catch (Exception error) when (error is not OutOfMemoryException)
            { Trace.TraceWarning("Tray notification subscription failed; retrying in {0}s: {1}", retrySeconds, error); }
            try { await delay(TimeSpan.FromSeconds(retrySeconds), cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            retrySeconds = Math.Min(retrySeconds * 2, 30);
        }
    }
}
