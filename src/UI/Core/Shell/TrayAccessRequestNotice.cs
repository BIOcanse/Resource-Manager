using System.Net.Http;
using System.Text.Json;
using ResourceManager.NativeUi.Localization;
using ResourceManager.NativeUi.Preferences;
using ResourceManager.Shared.Runtime.TrustedAccess;

namespace ResourceManager.NativeUi;

internal sealed class TrayAccessRequestNotice(
    FrontendPreferencesOwner preferences, Func<bool> interfaceVisible,
    Action<string, string> showBalloon, Action<Action> dispatch, Func<NativeText>? readText = null)
{
    internal const string SubscriptionId = "access-requests";
    private static readonly JsonSerializerOptions WireJson = new(JsonSerializerDefaults.Web);

    // Called on the UI thread; prune even when notices are disabled or the window is visible.
    internal void ObservePending(IReadOnlyList<TrustedAccessRequestPendingWireSnapshot> pending)
    {
        var notified = preferences.PruneSystemAccessRequestNotifiedIds(
            pending.Select(request => request.RequestId).ToHashSet(StringComparer.Ordinal));
        foreach (var request in pending)
        {
            if (notified.Contains(request.RequestId)) continue;
            if (preferences.Read().Notifications.AccessRequestSystem != "on" || interfaceVisible()) break;
            // Never show a notice we cannot retain: overflowing frames cannot cause repeats.
            if (notified.Count == FrontendPreferencesOwner.MaximumAccessRequestNotificationIds) break;
            var text = readText is null ? NativeUiText.Current : readText();
            showBalloon(text.AccessRequestTitle, string.Format(text.AccessRequestMessageFormat,
                request.Name, Path.GetFileName(request.ObservedExecutablePath)));
            preferences.RecordSystemAccessRequestNotifiedId(request.RequestId);
            notified.Add(request.RequestId);
        }
    }

    internal void ObserveFrame(JsonElement value)
    {
        // A rejected selector is not an empty pending list and must not prune stored ids.
        if (value.ValueKind != JsonValueKind.Array) return;
        ObservePending(value.Deserialize<TrustedAccessRequestPendingWireSnapshot[]>(WireJson)
            ?? throw new InvalidDataException("Missing pending access requests."));
    }

    internal Task RunAsync(Func<CancellationToken, Task<HttpResponseMessage>> openStream,
        CancellationToken cancellationToken, Func<TimeSpan, CancellationToken, Task>? delay = null)
        => TrayNotificationStream.RunAsync(openStream,
            new Dictionary<string, Action<JsonElement>> { [SubscriptionId] = ObserveFrame }, dispatch, cancellationToken, delay);
}
