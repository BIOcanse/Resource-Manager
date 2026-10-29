using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using ResourceManager.NativeUi.Localization;
using ResourceManager.NativeUi.Preferences;

namespace ResourceManager.NativeUi;

/// <summary>
/// The tray's consumer of the backend notification list (docs/notifications_2026-10-29.md, 托盘 and 通知种类): it
/// follows the notification sequence, decides from the frontend preferences whether each kind shows a Windows
/// notification, and writes the text from the native shell's language resources.
/// </summary>
internal sealed class TrayBackendNotifications(
    FrontendPreferencesOwner preferences,
    Action<string, string> showBalloon,
    Func<string>? readLanguage = null)
{
    internal const string SubscriptionId = "notifications";
    internal const string SubscriptionPath = "/api/notifications/subscribe";
    internal const string MemoryProtectionKind = "memory-protection.action";
    internal const string UpdateAvailableKind = "update-available";
    internal const string AccessRequestKind = "access-request";

    /// <summary>Called on the UI dispatch thread with each frame value of the notification subscription.</summary>
    internal void ObserveFrame(JsonElement value)
    {
        // A rejected selector or any other value without a list is not an empty list: it must not move the record.
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("notifications", out var list)
            || list.ValueKind != JsonValueKind.Array) return;
        if (!TryReadList(list, out var notifications))
        {
            Trace.TraceWarning("Tray notification list has an invalid sequence or kind; the frame is ignored.");
            return;
        }
        var latest = notifications.Count == 0 ? 0 : notifications[^1].Sequence;
        var record = preferences.ReadLastHandledNotificationSequence();
        if (record is null)
        {
            // The first list ever only records where the tray starts; older notifications are not shown.
            preferences.RecordLastHandledNotificationSequence(latest);
            return;
        }
        if (latest < record)
        {
            // The backend notification file was rebuilt and its sequence started again.
            record = 0;
            preferences.RecordLastHandledNotificationSequence(0);
        }
        foreach (var notification in notifications)
        {
            if (notification.Sequence <= record) continue;
            Handle(notification);
            // Stored after each notification, so a restart never shows the same one twice.
            preferences.RecordLastHandledNotificationSequence(notification.Sequence);
            record = notification.Sequence;
        }
    }

    /// <summary>Reads the tray's notification stream; <paramref name="dispatch"/> runs each frame on the UI thread.</summary>
    internal Task RunAsync(Func<CancellationToken, Task<HttpResponseMessage>> openStream, Action<Action> dispatch,
        CancellationToken cancellationToken, Func<TimeSpan, CancellationToken, Task>? delay = null)
        => TrayNotificationStream.RunAsync(openStream,
            new Dictionary<string, Action<JsonElement>> { [SubscriptionId] = ObserveFrame }, dispatch, cancellationToken, delay);

    private void Handle(Notification notification)
    {
        var known = notification.Kind is MemoryProtectionKind or UpdateAvailableKind or AccessRequestKind;
        if (!known) return; // Unknown kinds are skipped without invalidating the frame.
        var language = readLanguage is null ? NativeUiText.Language : readLanguage();
        var text = NativeTextCatalog.For(language);
        var culture = ShellCulture(language);
        var message = notification.Kind switch
        {
            MemoryProtectionKind => MemoryProtectionMessage(notification.Facts, text, culture),
            UpdateAvailableKind => UpdateMessage(notification.Facts, text, culture),
            _ => AccessRequestMessage(notification.Facts, text, culture)
        };
        if (message is null)
        {
            Trace.TraceWarning("Tray notification {0} of kind {1} has facts that do not decode; it is not shown.",
                notification.Sequence, notification.Kind);
            return;
        }
        // Both switches are read at the moment of the decision; the *InApp values belong to the web frontend.
        var settings = preferences.Read().Notifications;
        var enabled = notification.Kind switch
        {
            MemoryProtectionKind => settings.MemoryProtectionNotify == "on" && settings.MemoryProtectionSystem == "on",
            UpdateAvailableKind => settings.UpdateNotify == "on" && settings.UpdateSystem == "on",
            _ => settings.AccessRequestNotify == "on" && settings.AccessRequestSystem == "on"
        };
        // A click opens the main window through the tray's balloon click. The shell cannot open a specific frontend
        // page yet, so an access request also opens just the main window rather than the trusted access page.
        if (enabled) showBalloon(message.Value.Title, message.Value.Body);
    }

    private static (string Title, string Body)? MemoryProtectionMessage(JsonElement? facts, NativeText text, CultureInfo culture)
    {
        if (facts is not { ValueKind: JsonValueKind.Object } value
            || !TryString(value, "targetKind", out var targetKind) || targetKind is not ("process" or "processTree")
            || !TryString(value, "name", out var name)
            || !TryCount(value, "processId", out var processId)
            || !TryCount(value, "bytesWritten", out var bytesWritten)) return null;
        var size = FormatDumpSize(bytesWritten, culture);
        if (targetKind == "process")
            return (text.MemoryProtectionTitle,
                string.Format(culture, text.MemoryProtectionProcessMessageFormat, name, processId, size));
        // Only the process-tree text shows the number of processes, so only it requires recordCount.
        if (!TryCount(value, "recordCount", out var recordCount)) return null;
        return (text.MemoryProtectionTitle,
            string.Format(culture, text.MemoryProtectionTreeMessageFormat, name, processId, recordCount, size));
    }

    private static (string Title, string Body)? UpdateMessage(JsonElement? facts, NativeText text, CultureInfo culture)
    {
        if (facts is not { ValueKind: JsonValueKind.Object } value
            || !TryString(value, "version", out var version) || version.Length == 0) return null;
        return (text.UpdateAvailableTitle, string.Format(culture, text.UpdateAvailableMessageFormat, version));
    }

    private static (string Title, string Body)? AccessRequestMessage(JsonElement? facts, NativeText text, CultureInfo culture)
    {
        // The self-reported name can be forged, so the observed executable's file name is shown with it.
        if (facts is not { ValueKind: JsonValueKind.Object } value
            || !TryString(value, "name", out var name) || name.Length == 0
            || !TryString(value, "observedExecutablePath", out var path) || path.Length == 0) return null;
        var fileName = Path.GetFileName(path);
        if (fileName.Length == 0) return null;
        return (text.AccessRequestTitle, string.Format(culture, text.AccessRequestMessageFormat, name, fileName));
    }

    /// <summary>Binary units with one decimal from KiB up: 0 is "0 B", 1536 is "1.5 KiB" in the invariant culture.</summary>
    internal static string FormatDumpSize(long bytes, CultureInfo culture)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        string[] units = ["KiB", "MiB", "GiB"];
        if (bytes < 1024) return bytes.ToString(culture) + " B";
        var size = bytes / 1024d;
        var unit = 0;
        while (unit < units.Length - 1 && size >= 1024)
        {
            size /= 1024;
            unit++;
        }
        return size.ToString("0.0", culture) + " " + units[unit];
    }

    /// <summary>The culture of the shell language, or the invariant culture when the runtime has none for it.</summary>
    internal static CultureInfo ShellCulture(string language)
    {
        try { return CultureInfo.GetCultureInfo(language); }
        catch (CultureNotFoundException) { return CultureInfo.InvariantCulture; }
    }

    private static bool TryReadList(JsonElement list, out List<Notification> notifications)
    {
        notifications = new List<Notification>(list.GetArrayLength());
        var previous = 0L;
        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("sequence", out var sequenceValue) || sequenceValue.ValueKind != JsonValueKind.Number
                || !sequenceValue.TryGetInt64(out var sequence) || sequence < 1 || sequence <= previous
                || !TryString(item, "kind", out var kind)) return false;
            notifications.Add(new Notification(sequence, kind,
                item.TryGetProperty("facts", out var facts) ? (JsonElement?)facts : null));
            previous = sequence;
        }
        return true;
    }

    private static bool TryString(JsonElement value, string name, out string result)
    {
        result = "";
        if (!value.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String) return false;
        result = property.GetString()!;
        return true;
    }

    private static bool TryCount(JsonElement value, string name, out long result)
    {
        result = 0;
        return value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt64(out result) && result >= 0;
    }

    private readonly record struct Notification(long Sequence, string Kind, JsonElement? Facts);
}
