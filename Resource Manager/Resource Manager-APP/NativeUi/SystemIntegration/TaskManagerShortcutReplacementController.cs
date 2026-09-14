using System.Text.Json;

namespace ResourceManager.NativeUi.SystemIntegration;

internal sealed class TaskManagerShortcutReplacementController : IDisposable
{
    private const int SettingsPollIntervalMs = 2000;

    private readonly string settingsPath;
    private readonly Action<string, ToolTipIcon> statusReporter;
    private readonly TaskManagerShortcutHook shortcutHook;
    private readonly TaskManagerLaunchReplacementRegistry launchReplacementRegistry = new();
    private readonly System.Windows.Forms.Timer settingsPollTimer;
    private readonly string debuggerCommand;
    private bool? appliedEnabled;
    private bool disposed;
    private string lastReportedStatus = string.Empty;

    public TaskManagerShortcutReplacementController(
        string settingsPath,
        Action shortcutPressed,
        Action<string, ToolTipIcon> statusReporter)
    {
        this.settingsPath = settingsPath;
        this.statusReporter = statusReporter;
        shortcutHook = new TaskManagerShortcutHook(shortcutPressed);
        debuggerCommand = CreateDebuggerCommand();
        settingsPollTimer = new System.Windows.Forms.Timer
        {
            Interval = SettingsPollIntervalMs
        };
        settingsPollTimer.Tick += (_, _) => ApplySettingsFileState(force: false);
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ApplySettingsFileState(force: true);
        settingsPollTimer.Start();
    }

    public void SetEnabledFromUi(bool enabled)
    {
        ApplyDesiredState(enabled, force: true);
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
        shortcutHook.Dispose();
    }

    private void ApplySettingsFileState(bool force)
    {
        ApplyDesiredState(ReadEnabledFromSettings(settingsPath), force);
    }

    private void ApplyDesiredState(bool enabled, bool force)
    {
        if (disposed || (!force && appliedEnabled == enabled))
        {
            return;
        }

        if (enabled)
        {
            EnableReplacement();
        }
        else
        {
            DisableReplacement();
        }

        appliedEnabled = enabled;
    }

    private void EnableReplacement()
    {
        try
        {
            shortcutHook.Enable();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine(ex);
            ReportStatusOnce("任务管理器快捷键启用失败，请稍后重试。", ToolTipIcon.Warning);
        }

        var result = launchReplacementRegistry.EnsureRegistered(debuggerCommand);
        if (!result.ColdStartAvailable)
        {
            ReportStatusOnce(result.Message, result.State == TaskManagerLaunchReplacementState.Failed ? ToolTipIcon.Error : ToolTipIcon.Warning);
        }
    }

    private void DisableReplacement()
    {
        try
        {
            shortcutHook.Disable();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine(ex);
            ReportStatusOnce("任务管理器快捷键关闭失败，请稍后重试。", ToolTipIcon.Warning);
        }

        var result = launchReplacementRegistry.RemoveIfOwned();
        if (result.State is TaskManagerLaunchReplacementState.NeedsElevation
            or TaskManagerLaunchReplacementState.Conflict
            or TaskManagerLaunchReplacementState.Failed)
        {
            ReportStatusOnce(result.Message, result.State == TaskManagerLaunchReplacementState.Failed ? ToolTipIcon.Error : ToolTipIcon.Warning);
        }
    }

    private void ReportStatusOnce(string message, ToolTipIcon icon)
    {
        if (string.IsNullOrWhiteSpace(message)
            || string.Equals(lastReportedStatus, message, StringComparison.Ordinal))
        {
            return;
        }

        lastReportedStatus = message;
        statusReporter(message, icon);
    }

    private static bool ReadEnabledFromSettings(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using var stream = Configuration.SettingsFileReader.Open(path);
            using var document = JsonDocument.Parse(stream);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("systemIntegration", out var systemIntegration)
                && systemIntegration.ValueKind == JsonValueKind.Object
                && systemIntegration.TryGetProperty("taskManagerShortcutReplacementEnabled", out var enabled)
                && enabled.ValueKind == JsonValueKind.True;
        }
        catch
        {
            return false;
        }
    }

    private static string CreateDebuggerCommand()
    {
        var executablePath = Environment.ProcessPath ?? Application.ExecutablePath;
        return $"\"{executablePath}\" --show-existing {TaskManagerLaunchReplacementRegistry.OwnedCommandMarker}";
    }
}
