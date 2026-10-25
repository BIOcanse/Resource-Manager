using System.Text.Json;

namespace ResourceManager.NativeUi.Preferences;

internal sealed partial class FrontendPreferencesOwner
{
    // Native-only state: web saves and preference resets never own this file.
    private string NativeNotificationPath => cachePath + ".native-notifications.json";

    internal string? ReadSystemUpdateNotifiedVersion()
    {
        lock (gate)
        {
            if (!File.Exists(NativeNotificationPath)) return null;
            using var stream = Configuration.SettingsFileReader.Open(NativeNotificationPath);
            return JsonSerializer.Deserialize<NativeNotificationState>(stream, Json)?.UpdateSystemNotifiedVersion
                ?? throw new InvalidDataException("Native notification state is empty.");
        }
    }

    internal void RecordSystemUpdateNotifiedVersion(string value)
    {
        lock (gate)
            WriteAtomic(NativeNotificationPath,
                JsonSerializer.SerializeToUtf8Bytes(new NativeNotificationState(value), Json));
    }

    private sealed record NativeNotificationState(string UpdateSystemNotifiedVersion);
}
