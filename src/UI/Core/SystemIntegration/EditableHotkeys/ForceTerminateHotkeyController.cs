using ResourceManager.NativeUi.Configuration;
using ResourceManager.NativeUi.Localization;
using System.Text.Json;

namespace ResourceManager.NativeUi.SystemIntegration.EditableHotkeys;

internal sealed class ForceTerminateHotkeyController : IDisposable
{
    private const string ActionId = "force-terminate-unresponsive-and-foreground";

    private readonly Action<int?> shortcutPressed;
    private readonly Action<string, ToolTipIcon> statusReporter;
    private EditableHotkeyDefinition? appliedDefinition;
    private EditableHotkeyHook? hook;
    private bool appliedEnabled;
    private bool disposed;

    public ForceTerminateHotkeyController(
        Action<int?> shortcutPressed,
        Action<string, ToolTipIcon> statusReporter)
    {
        this.shortcutPressed = shortcutPressed;
        this.statusReporter = statusReporter;
    }

    /// <summary>Applies one read of app-settings.json (startup, a file change or an explicit reload).</summary>
    public void Apply(AppSettingsFileRead settings)
    {
        if (disposed || !TryReadSettings(settings, out var enabled, out var definition))
        {
            return;
        }

        if (enabled && !definition.IsSafeForDestructiveGlobalAction)
        {
            if (appliedEnabled || !Equals(appliedDefinition, definition))
            {
                statusReporter(
                    NativeUiText.Current.ForceTerminateHotkeyDisabled,
                    ToolTipIcon.Warning);
            }
            enabled = false;
        }

        if (appliedEnabled == enabled && Equals(appliedDefinition, definition))
        {
            return;
        }

        hook?.Dispose();
        hook = null;
        appliedDefinition = definition;
        appliedEnabled = enabled;
        if (!enabled || definition.IsEmpty)
        {
            return;
        }

        try
        {
            hook = new EditableHotkeyHook(definition, () =>
            {
                var foregroundProcessId = HungAndForegroundProcessResolver.ReadForegroundProcessId();
                shortcutPressed(foregroundProcessId);
            });
            hook.Enable();
        }
        catch (Exception ex)
        {
            hook?.Dispose();
            hook = null;
            System.Diagnostics.Trace.WriteLine(ex);
            statusReporter(NativeUiText.Current.ForceTerminateHotkeyEnableFailed, ToolTipIcon.Warning);
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        hook?.Dispose();
    }

    // A missing file means no hotkey; an unreadable one keeps the applied hotkey, as before.
    internal static bool TryReadSettings(AppSettingsFileRead settings, out bool enabled, out EditableHotkeyDefinition definition)
    {
        enabled = false;
        definition = EditableHotkeyDefinition.Create([]);
        if (settings.State == AppSettingsFileReadState.Missing)
        {
            return true;
        }

        if (settings.State == AppSettingsFileReadState.Unreadable)
        {
            return false;
        }

        var root = settings.Root;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("systemIntegration", out var systemIntegration)
            || systemIntegration.ValueKind != JsonValueKind.Object
            || !systemIntegration.TryGetProperty("hotkeys", out var hotkeys)
            || hotkeys.ValueKind != JsonValueKind.Array)
        {
            return true;
        }

        foreach (var hotkey in hotkeys.EnumerateArray())
        {
            if (hotkey.ValueKind != JsonValueKind.Object
                || !hotkey.TryGetProperty("actionId", out var actionId)
                || actionId.ValueKind != JsonValueKind.String
                || !string.Equals(actionId.GetString(), ActionId, StringComparison.Ordinal))
            {
                continue;
            }

            enabled = hotkey.TryGetProperty("enabled", out var enabledValue)
                && enabledValue.ValueKind == JsonValueKind.True;
            definition = EditableHotkeyDefinition.Create(
                hotkey.TryGetProperty("encoding", out var encoding)
                && encoding.ValueKind == JsonValueKind.Array
                    ? encoding.EnumerateArray()
                        .Where(static item => item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out _))
                        .Select(static item => item.GetInt32())
                    : []);
            enabled &= !definition.IsEmpty;
            return true;
        }

        return true;
    }
}
