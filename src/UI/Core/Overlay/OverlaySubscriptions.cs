using System.Diagnostics;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
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

    internal const string OperationsSubscriptionId = "startup-modules";
    internal const string OperationsSubscriptionPath = "/api/startup-modules/settings/subscribe";

    // The controller observes operational settings without sampling: the backend pushes the current
    // module selections when the subscription opens and again after each committed change, and the
    // local preferences' changes arrive as wakeups. Each one recomputes the desired data
    // subscriptions; only a different set replaces the data stream. When the settings subscription
    // ends or fails, the data stream is released and the subscription is opened again after
    // reconnectDelay. Only the explicit data stream below owns observation dependencies.
    internal static async Task RunAsync(
        Func<CancellationToken, IAsyncEnumerable<StartupModuleOperationsDocument>> subscribeOperations,
        Func<FrontendPreferences> readPreferences,
        ChannelReader<bool> preferencesChanged,
        Func<OverlaySubscription[], CancellationToken, Task> observe,
        Action clear, TimeSpan reconnectDelay, CancellationToken cancellationToken)
    {
        CancellationTokenSource? active = null;
        Task stream = Task.CompletedTask;
        string? previous = null;
        async Task ReleaseAsync()
        {
            active?.Cancel();
            try { await stream; }
            catch (OperationCanceledException) { }
            catch (Exception error) when (error is not OutOfMemoryException)
            { Trace.TraceError("Overlay data stream failed: {0}", error); }
            finally
            {
                active?.Dispose(); active = null;
                stream = Task.CompletedTask;
            }
        }
        async Task ApplyAsync(StartupModuleOperationsDocument operations)
        {
            try
            {
                var desired = Create(readPreferences(), operations);
                var signature = string.Join('\n', desired.Select(static item => item.Path));
                if (signature == previous) return;
                await ReleaseAsync();
                clear();
                previous = signature;
                if (desired.Length > 0)
                {
                    active = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    stream = observe(desired, active.Token);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                // The next pushed frame or preference change computes the set again.
                Trace.TraceError("Overlay dependencies could not be refreshed: {0}", error);
                await ReleaseAsync();
                previous = null;
                clear();
            }
        }
        var preferenceWake = preferencesChanged.WaitToReadAsync(cancellationToken).AsTask();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                using (var subscription = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    IAsyncEnumerator<StartupModuleOperationsDocument>? frames = null;
                    var nextFrame = Task.FromResult(false);
                    try
                    {
                        frames = subscribeOperations(subscription.Token).GetAsyncEnumerator(subscription.Token);
                        nextFrame = frames.MoveNextAsync().AsTask();
                        StartupModuleOperationsDocument? operations = null;
                        while (true)
                        {
                            if (await Task.WhenAny(nextFrame, preferenceWake) == nextFrame)
                            {
                                if (!await nextFrame) break;
                                operations = frames.Current;
                                nextFrame = frames.MoveNextAsync().AsTask();
                            }
                            else
                            {
                                // A completed channel never wakes again.
                                if (!await preferenceWake) preferenceWake = new TaskCompletionSource<bool>().Task;
                                else
                                {
                                    while (preferencesChanged.TryRead(out _)) { }
                                    preferenceWake = preferencesChanged.WaitToReadAsync(cancellationToken).AsTask();
                                }
                                if (operations is null) continue;
                            }
                            await ApplyAsync(operations);
                        }
                        Trace.TraceInformation("Overlay settings subscription ended; reconnecting.");
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
                    catch (Exception error) when (error is not OutOfMemoryException)
                    {
                        Trace.TraceError("Overlay settings subscription failed; reconnecting: {0}", error);
                    }
                    finally
                    {
                        subscription.Cancel();
                        // Already observed above when it failed; here it only has to finish.
                        try { await nextFrame; }
                        catch (Exception error) when (error is not OutOfMemoryException) { }
                        if (frames is not null) await frames.DisposeAsync();
                    }
                }
                await ReleaseAsync();
                previous = null;
                clear();
                await Task.Delay(reconnectDelay, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally
        {
            await ReleaseAsync();
        }
    }

    // Reads the pushed module selections of one settings subscription channel. The sequence ends when
    // the channel ends; a malformed frame or a denied selector fails it.
    internal static async IAsyncEnumerable<StartupModuleOperationsDocument> ReadOperationsAsync(
        Func<CancellationToken, Task<HttpResponseMessage>> openStream,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var response = await openStream(cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            StartupModuleOperationsDocument document;
            using (var frame = JsonDocument.Parse(line))
            {
                var root = frame.RootElement;
                if (!root.TryGetProperty("subscriptionId", out var id) || id.GetString() != OperationsSubscriptionId
                    || !root.TryGetProperty("value", out var value)) continue;
                if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("statusCode", out var status))
                    throw new HttpRequestException($"Startup module settings subscription returned {status}.");
                document = value.Deserialize(OverlayStartupModulesJson.Default.StartupModuleOperationsDocument)
                    ?? throw new InvalidDataException("Missing startup module operations.");
            }
            if (document.Version != 1 || document.Software is null)
                throw new InvalidDataException("Invalid startup module operations.");
            yield return document;
        }
    }
}
