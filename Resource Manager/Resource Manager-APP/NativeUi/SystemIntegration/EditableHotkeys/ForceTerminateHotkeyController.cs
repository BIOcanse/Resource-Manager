using System.Text.Json;

namespace ResourceManager.NativeUi.SystemIntegration.EditableHotkeys;

internal sealed class ForceTerminateHotkeyController : IDisposable
{
    private const int SettingsPollIntervalMs = 2000;
    private const string ActionId = "force-terminate-unresponsive-and-foreground";

    private readonly string settingsPath;
    private readonly Action<int?> shortcutPressed;
    private readonly Action<string, ToolTipIcon> statusReporter;
    private readonly System.Windows.Forms.Timer settingsPollTimer;
    private EditableHotkeyDefinition? appliedDefinition;
    private EditableHotkeyHook? hook;
    private bool appliedEnabled;
    private bool disposed;

    public ForceTerminateHotkeyController(
        string settingsPath,
        Action<int?> shortcutPressed,
        Action<string, ToolTipIcon> statusReporter)
    {
        this.settingsPath = settingsPath;
        this.shortcutPressed = shortcutPressed;
        this.statusReporter = statusReporter;
        settingsPollTimer = new System.Windows.Forms.Timer { Interval = SettingsPollIntervalMs };
        settingsPollTimer.Tick += (_, _) => Reload();
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        Reload();
        settingsPollTimer.Start();
    }

    public void Reload()
    {
        if (disposed || !TryReadSettings(out var enabled, out var definition))
        {
            return;
        }

        if (enabled && !definition.IsSafeForDestructiveGlobalAction)
        {
            if (appliedEnabled || !Equals(appliedDefinition, definition))
            {
                statusReporter(
                    "强制结束快捷键已停用：必须同时包含 Ctrl、Alt 或 Win 修饰键以及一个非修饰键。",
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
            statusReporter("强制结束快捷键启用失败，请检查按键设置后重试。", ToolTipIcon.Warning);
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        settingsPollTimer.Stop();
        settingsPollTimer.Dispose();
        hook?.Dispose();
    }

    private bool TryReadSettings(out bool enabled, out EditableHotkeyDefinition definition)
    {
        enabled = false;
        definition = EditableHotkeyDefinition.Create([]);
        if (!File.Exists(settingsPath))
        {
            return true;
        }

        try
        {
            using var stream = Configuration.SettingsFileReader.Open(settingsPath);
            using var document = JsonDocument.Parse(stream);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("systemIntegration", out var systemIntegration)
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }
}
