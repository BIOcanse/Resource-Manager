using System.Text.Json;
using Microsoft.Win32;

namespace ResourceManager.NativeUi.WebView;

internal static class WebViewAppearanceBootstrap
{
    private static readonly Color LightBackground = Color.FromArgb(244, 247, 248);
    private static readonly Color DarkBackground = Color.FromArgb(20, 22, 23);
    private static readonly Color LowContrastBackground = Color.FromArgb(230, 233, 230);

    public static Color LoadBackgroundColor(string settingsPath)
    {
        var theme = LoadTheme(settingsPath);
        return theme switch
        {
            "dark" => DarkBackground,
            "lowContrast" => LowContrastBackground,
            "light" => LightBackground,
            _ => SystemUsesLightAppTheme() ? LightBackground : DarkBackground
        };
    }

    public static string ToWebViewEnvironmentValue(Color color) =>
        $"{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";

    private static string LoadTheme(string settingsPath)
    {
        try
        {
            using var stream = Configuration.SettingsFileReader.Open(settingsPath);
            using var document = JsonDocument.Parse(stream);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("appearance", out var appearance)
                && appearance.ValueKind == JsonValueKind.Object
                && appearance.TryGetProperty("theme", out var theme)
                && theme.ValueKind == JsonValueKind.String
                    ? theme.GetString() ?? "system"
                    : "system";
        }
        catch (IOException)
        {
            return "system";
        }
        catch (UnauthorizedAccessException)
        {
            return "system";
        }
        catch (JsonException)
        {
            return "system";
        }
    }

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
