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

    internal void ObserveFrame(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("availableVersion", out var available)
            || available.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)) return;
        ObserveVersion(available.GetString());
    }

    internal Task RunAsync(Func<CancellationToken, Task<HttpResponseMessage>> openStream,
        CancellationToken cancellationToken, Func<TimeSpan, CancellationToken, Task>? delay = null)
        => TrayNotificationStream.RunAsync(openStream,
            new Dictionary<string, Action<JsonElement>> { ["updates"] = ObserveFrame }, dispatch, cancellationToken, delay);
}
