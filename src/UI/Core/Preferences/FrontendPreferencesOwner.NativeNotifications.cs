using System.Text.Json;
using System.Text.Json.Nodes;

namespace ResourceManager.NativeUi.Preferences;

internal sealed partial class FrontendPreferencesOwner
{
    // Native-only state: web saves and preference resets never own this file.
    private string NativeNotificationPath => cachePath + ".native-notifications.json";

    /// <summary>
    /// The last backend notification sequence the tray handled (docs/notifications_2026-10-29.md, 托盘);
    /// null until the tray has received its first list.
    /// </summary>
    internal long? ReadLastHandledNotificationSequence()
    {
        lock (gate) return ReadNativeNotificationState().LastHandledSequence;
    }

    internal void RecordLastHandledNotificationSequence(long value)
    {
        if (value < 0) throw new InvalidDataException("Native notification sequence must not be negative.");
        lock (gate)
            WriteAtomic(NativeNotificationPath, JsonSerializer.SerializeToUtf8Bytes(
                new NativeNotificationState { LastHandledSequence = value }, Json));
    }

    private NativeNotificationState ReadNativeNotificationState()
    {
        if (!File.Exists(NativeNotificationPath)) return new();
        JsonNode? root;
        using (var stream = Configuration.SettingsFileReader.Open(NativeNotificationPath))
            root = JsonNode.Parse(stream);
        if (root is not JsonObject state) throw new InvalidDataException("Native notification state is not an object.");
        MigrateNativeNotificationFields(state);
        var value = state.Deserialize<NativeNotificationState>(Json)
            ?? throw new InvalidDataException("Native notification state is empty.");
        if (value.LastHandledSequence is < 0) throw new InvalidDataException("Native notification sequence is invalid.");
        return value;
    }

    // Migration native-notifications-sequence-v1: before 2026-10-29 the update and access request notices kept
    // updateSystemNotifiedVersion and accessRequestNotifiedIds here. The tray now follows the backend notification
    // sequence (docs/notifications_2026-10-29.md), so both are dropped on read and the next write leaves them out; such
    // a file has no sequence yet, so its first list only records, as on a fresh install. Delete once every supported
    // upgrade starts after the 2026-10-29 release.
    private static void MigrateNativeNotificationFields(JsonObject state)
    {
        state.Remove("updateSystemNotifiedVersion");
        state.Remove("accessRequestNotifiedIds");
    }

    private sealed record NativeNotificationState
    {
        public long? LastHandledSequence { get; init; }
    }
}
