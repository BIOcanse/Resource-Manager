using ResourceManager.NativeUi.Configuration;
using ResourceManager.NativeUi.Localization;
using System.Text.Json;

namespace ResourceManager.NativeUi.SystemIntegration;

internal sealed class TaskManagerShortcutReplacementController : IDisposable
{
    private readonly Action<string, ToolTipIcon> statusReporter;
    private readonly TaskManagerShortcutHook shortcutHook;
    private readonly TaskManagerLaunchReplacementRegistry launchReplacementRegistry = new();
    private readonly string debuggerCommand;
    private bool? appliedEnabled;
    private bool disposed;
    private string lastReportedStatus = string.Empty;

    public TaskManagerShortcutReplacementController(
        Action shortcutPressed,
        Action<string, ToolTipIcon> statusReporter)
    {
        this.statusReporter = statusReporter;
        shortcutHook = new TaskManagerShortcutHook(shortcutPressed);
        debuggerCommand = CreateDebuggerCommand();
    }

    /// <summary>Applies one read of app-settings.json (startup, a file change or an explicit reload).</summary>
    public void Apply(AppSettingsFileRead settings)
    {
        ApplyDesiredState(ReadEnabled(settings), force: false);
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
        shortcutHook.Dispose();
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
            ReportStatusOnce(NativeUiText.Current.TaskManagerHotkeyEnableFailed, ToolTipIcon.Warning);
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
            ReportStatusOnce(NativeUiText.Current.TaskManagerHotkeyDisableFailed, ToolTipIcon.Warning);
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

    // A missing or unreadable file means the replacement is off, as before.
    internal static bool ReadEnabled(AppSettingsFileRead settings) =>
        settings.State == AppSettingsFileReadState.Document
        && settings.Root.ValueKind == JsonValueKind.Object
        && settings.Root.TryGetProperty("systemIntegration", out var systemIntegration)
        && systemIntegration.ValueKind == JsonValueKind.Object
        && systemIntegration.TryGetProperty("taskManagerShortcutReplacementEnabled", out var enabled)
        && enabled.ValueKind == JsonValueKind.True;

    private static string CreateDebuggerCommand()
    {
        var executablePath = Environment.ProcessPath ?? Application.ExecutablePath;
        return $"\"{executablePath}\" --show-existing {TaskManagerLaunchReplacementRegistry.OwnedCommandMarker}";
    }
}
