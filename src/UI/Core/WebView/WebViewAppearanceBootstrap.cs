using System.Text.Json;
using Microsoft.Win32;
using ResourceManager.Shared.Appearance;

namespace ResourceManager.NativeUi.WebView;

internal static class WebViewAppearanceBootstrap
{
    // 底色由界面风格和配色决定，目录在 src/Shared/Appearance/ui-styles.json；跟随系统的配色按 Windows 浅色/深色取。
    public static Color LoadBackgroundColor(string settingsPath)
    {
        var (uiStyle, theme) = LoadAppearance(settingsPath);
        var background = UiStyleCatalog.ResolveStartupBackground(uiStyle, theme, SystemUsesLightAppTheme());
        return Color.FromArgb(background.Red, background.Green, background.Blue);
    }

    public static string ToWebViewEnvironmentValue(Color color) =>
        $"{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";

    // 读不到或格式不对时返回空值，由目录归一化成默认风格和配色。
    private static (string? UiStyle, string? Theme) LoadAppearance(string settingsPath)
    {
        try
        {
            using var stream = Configuration.SettingsFileReader.Open(settingsPath);
            using var document = JsonDocument.Parse(stream);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("appearance", out var appearance)
                || appearance.ValueKind != JsonValueKind.Object)
            {
                return (null, null);
            }

            return (ReadString(appearance, "uiStyle"), ReadString(appearance, "theme"));
        }
        catch (IOException)
        {
            return (null, null);
        }
        catch (UnauthorizedAccessException)
        {
            return (null, null);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

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
