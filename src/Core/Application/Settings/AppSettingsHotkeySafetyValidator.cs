using ResourceManager.App.Domain.Settings;

namespace ResourceManager.App.Application.Settings;

public static class AppSettingsHotkeySafetyValidator
{
    private static readonly HashSet<int> ApprovedModifierKeys =
    [
        17, 18, 91, 92, 162, 163, 164, 165
    ];

    private static readonly HashSet<int> AllModifierKeys =
    [
        16, 17, 18, 91, 92, 160, 161, 162, 163, 164, 165
    ];

    public static bool AreEnabledDestructiveHotkeysSafe(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.SystemIntegration.Hotkeys.All(static hotkey =>
            !hotkey.Enabled
            || !string.Equals(
                hotkey.ActionId,
                AppSystemIntegrationActionIds.ForceTerminateUnresponsiveAndForeground,
                StringComparison.Ordinal)
            || IsSafeDestructiveEncoding(hotkey.Encoding));
    }

    public static bool IsSafeDestructiveEncoding(IEnumerable<int>? encoding)
    {
        var normalized = AppSettingsNormalizer.NormalizeHotkeyEncoding(encoding);
        var keys = normalized
            .Where(static (value, index) => index % 2 == 0 && value != 0)
            .ToArray();
        return keys.Length >= 2
            && keys.Any(ApprovedModifierKeys.Contains)
            && keys.Any(static key => !AllModifierKeys.Contains(key));
    }
}
