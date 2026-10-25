using ResourceManager.NativeUi.Preferences;

namespace ResourceManager.NativeUi.Localization;

/// <summary>The shell reads the same frontend cache as the WebView.</summary>
internal static class PersistedLanguageReader
{
    public static string Read(string? settingsPath = null) =>
        (settingsPath is null ? FrontendPreferencesOwner.Current : FrontendPreferencesOwner.ForCache(settingsPath))
            .Read().Appearance.Language;
}
