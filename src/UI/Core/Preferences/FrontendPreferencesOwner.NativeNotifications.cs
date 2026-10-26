using System.Text.Json;

namespace ResourceManager.NativeUi.Preferences;

internal sealed partial class FrontendPreferencesOwner
{
    internal const int MaximumAccessRequestNotificationIds = 128;
    // Native-only state: web saves and preference resets never own this file.
    private string NativeNotificationPath => cachePath + ".native-notifications.json";

    internal string? ReadSystemUpdateNotifiedVersion()
    {
        lock (gate)
        {
            return ReadNativeNotificationState().UpdateSystemNotifiedVersion;
        }
    }

    internal void RecordSystemUpdateNotifiedVersion(string value)
    {
        lock (gate)
            WriteAtomic(NativeNotificationPath,
                JsonSerializer.SerializeToUtf8Bytes(ReadNativeNotificationState() with { UpdateSystemNotifiedVersion = value }, Json));
    }

    internal string[] ReadSystemAccessRequestNotifiedIds()
    {
        lock (gate) return ReadNativeNotificationState().AccessRequestNotifiedIds.ToArray();
    }

    internal HashSet<string> PruneSystemAccessRequestNotifiedIds(IReadOnlySet<string> pendingIds)
    {
        lock (gate)
        {
            var current = ReadNativeNotificationState();
            var retained = current.AccessRequestNotifiedIds.Where(pendingIds.Contains).ToArray();
            if (retained.Length != current.AccessRequestNotifiedIds.Length)
                WriteAtomic(NativeNotificationPath, JsonSerializer.SerializeToUtf8Bytes(
                    current with { AccessRequestNotifiedIds = retained }, Json));
            return retained.ToHashSet(StringComparer.Ordinal);
        }
    }

    internal void RecordSystemAccessRequestNotifiedId(string id)
    {
        lock (gate)
        {
            var current = ReadNativeNotificationState();
            if (current.AccessRequestNotifiedIds.Contains(id, StringComparer.Ordinal)) return;
            if (string.IsNullOrWhiteSpace(id) || current.AccessRequestNotifiedIds.Length >= MaximumAccessRequestNotificationIds)
                throw new InvalidDataException("Native access request notification state exceeds its bound or has an invalid id.");
            WriteAtomic(NativeNotificationPath, JsonSerializer.SerializeToUtf8Bytes(
                current with { AccessRequestNotifiedIds = [.. current.AccessRequestNotifiedIds, id] }, Json));
        }
    }

    private NativeNotificationState ReadNativeNotificationState()
    {
        if (!File.Exists(NativeNotificationPath)) return new();
        using var stream = Configuration.SettingsFileReader.Open(NativeNotificationPath);
        var state = JsonSerializer.Deserialize<NativeNotificationState>(stream, Json)
            ?? throw new InvalidDataException("Native notification state is empty.");
        if (state.AccessRequestNotifiedIds is null || state.AccessRequestNotifiedIds.Length > MaximumAccessRequestNotificationIds
            || state.AccessRequestNotifiedIds.Any(string.IsNullOrWhiteSpace)
            || state.AccessRequestNotifiedIds.Distinct(StringComparer.Ordinal).Count() != state.AccessRequestNotifiedIds.Length)
            throw new InvalidDataException("Native access request notification state is invalid.");
        return state;
    }

    private sealed record NativeNotificationState
    {
        public NativeNotificationState() { }
        public string? UpdateSystemNotifiedVersion { get; init; }
        public string[] AccessRequestNotifiedIds { get; init; } = [];
    }
}
