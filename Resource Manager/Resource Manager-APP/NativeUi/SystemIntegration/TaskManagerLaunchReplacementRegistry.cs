using Microsoft.Win32;

namespace ResourceManager.NativeUi.SystemIntegration;

internal sealed class TaskManagerLaunchReplacementRegistry
{
    public const string OwnedCommandMarker = "--resource-manager-task-manager-replacement";

    private const string TaskManagerIfeoKeyPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\taskmgr.exe";
    private const string DebuggerValueName = "Debugger";

    public TaskManagerLaunchReplacementResult EnsureRegistered(string debuggerCommand)
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(TaskManagerIfeoKeyPath, writable: true);
            if (key is null)
            {
                return TaskManagerLaunchReplacementResult.Failed("任务管理器快捷键启用失败，请稍后重试。");
            }

            var existing = key.GetValue(DebuggerValueName) as string;
            if (!string.IsNullOrWhiteSpace(existing)
                && !IsOwnedDebuggerCommand(existing))
            {
                return TaskManagerLaunchReplacementResult.Conflict(
                    "检测到其他任务管理器快捷键设置，已保留原设置。");
            }

            if (!string.Equals(existing, debuggerCommand, StringComparison.Ordinal))
            {
                key.SetValue(DebuggerValueName, debuggerCommand, RegistryValueKind.String);
            }

            return TaskManagerLaunchReplacementResult.Registered();
        }
        catch (UnauthorizedAccessException)
        {
            return TaskManagerLaunchReplacementResult.NeedsElevation(
                "需要管理员权限才能在资源管理器未运行时接管任务管理器快捷键。");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine(ex);
            return TaskManagerLaunchReplacementResult.Failed("任务管理器快捷键启用失败，请稍后重试。");
        }
    }

    public TaskManagerLaunchReplacementResult RemoveIfOwned()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(TaskManagerIfeoKeyPath, writable: true);
            if (key is null)
            {
                return TaskManagerLaunchReplacementResult.Removed();
            }

            var existing = key.GetValue(DebuggerValueName) as string;
            if (string.IsNullOrWhiteSpace(existing))
            {
                return TaskManagerLaunchReplacementResult.Removed();
            }

            if (!IsOwnedDebuggerCommand(existing))
            {
                return TaskManagerLaunchReplacementResult.Conflict(
                    "检测到其他任务管理器快捷键设置，已保留原设置。");
            }

            key.DeleteValue(DebuggerValueName, throwOnMissingValue: false);
            return TaskManagerLaunchReplacementResult.Removed();
        }
        catch (UnauthorizedAccessException)
        {
            return TaskManagerLaunchReplacementResult.NeedsElevation(
                "需要管理员权限才能完全关闭任务管理器快捷键替换。");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine(ex);
            return TaskManagerLaunchReplacementResult.Failed("任务管理器快捷键关闭失败，请稍后重试。");
        }
    }

    private static bool IsOwnedDebuggerCommand(string command)
    {
        return command.Contains(OwnedCommandMarker, StringComparison.OrdinalIgnoreCase);
    }
}

internal enum TaskManagerLaunchReplacementState
{
    Registered,
    Removed,
    NeedsElevation,
    Conflict,
    Failed
}

internal sealed record TaskManagerLaunchReplacementResult(
    TaskManagerLaunchReplacementState State,
    bool ColdStartAvailable,
    string Message)
{
    public static TaskManagerLaunchReplacementResult Registered() =>
        new(TaskManagerLaunchReplacementState.Registered, ColdStartAvailable: true, "任务管理器快捷键已启用。");

    public static TaskManagerLaunchReplacementResult Removed() =>
        new(TaskManagerLaunchReplacementState.Removed, ColdStartAvailable: false, "任务管理器快捷键已关闭。");

    public static TaskManagerLaunchReplacementResult NeedsElevation(string message) =>
        new(TaskManagerLaunchReplacementState.NeedsElevation, ColdStartAvailable: false, message);

    public static TaskManagerLaunchReplacementResult Conflict(string message) =>
        new(TaskManagerLaunchReplacementState.Conflict, ColdStartAvailable: false, message);

    public static TaskManagerLaunchReplacementResult Failed(string message) =>
        new(TaskManagerLaunchReplacementState.Failed, ColdStartAvailable: false, message);
}
