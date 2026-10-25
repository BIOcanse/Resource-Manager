using Microsoft.Win32;
using ResourceManager.Shared.Appearance;
using ResourceManager.NativeUi.Preferences;

namespace ResourceManager.NativeUi.WebView;

internal static class WebViewAppearanceBootstrap
{
    // 底色由界面风格和配色决定，目录在 src/Shared/Appearance/ui-styles.json；跟随系统的配色按 Windows 浅色/深色取。
    public static Color LoadBackgroundColor(string settingsPath)
    {
        var appearance = FrontendPreferencesOwner.ForCache(settingsPath).Read().Appearance;
        var background = UiStyleCatalog.ResolveStartupBackground(appearance.UiStyle, appearance.Theme, SystemUsesLightAppTheme());
        return Color.FromArgb(background.Red, background.Green, background.Blue);
    }

    public static string ToWebViewEnvironmentValue(Color color) =>
        $"{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";

    private static bool SystemUsesLightAppTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                writable: false);
            return key?.GetValue("AppsUseLightTheme") is not int value || value != 0;
        }
        catch (System.Security.SecurityException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }
}
