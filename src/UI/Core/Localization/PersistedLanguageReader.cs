using ResourceManager.Shared.Localization;

namespace ResourceManager.NativeUi.Localization;

/// <summary>The shell reads the existing appearance.language preference and never writes it.</summary>
internal static class PersistedLanguageReader
{
    public static string Read(string? settingsPath = null) =>
        AppLanguage.ReadPreference(settingsPath ?? NativeUiPaths.AppSettingsPath);
}
