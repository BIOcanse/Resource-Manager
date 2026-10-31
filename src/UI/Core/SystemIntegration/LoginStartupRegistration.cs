using ResourceManager.Shared.Desktop;
using ResourceManager.Shared.Packages;

namespace ResourceManager.NativeUi.SystemIntegration;

// The current user's Task Scheduler logon task is the single persisted truth for frontend logon startup.
// The frontend manifest requires an elevated administrator, and Windows does not start such a program
// from a Run value; the task starts it with the user's elevated token.
//
// Persistent-entry compatibility (docs/frontend_block_trees_2026-10-26.md, 安装与持久入口): the logon task this
// class writes, the Task Manager IFEO Debugger value (TaskManagerShortcutReplacementController) and old shortcuts
// from older versions may start NativeUi directly. New entries start the resident; owned old tasks are
// rewritten on read, preserving disabled state. Delete ResidentHandoff, NativeUiLaunchOptions and this
// note, and make Program reject a start without the pipe, once a released version has rewritten the entries and
// every installation before that version has upgraded.
internal sealed class LoginStartupRegistration
{
    private readonly string executablePath;
    private readonly string userSid;
    private readonly ITaskSchedulerRootFolder tasks;

    internal LoginStartupRegistration(string executablePath, string userSid, ITaskSchedulerRootFolder tasks)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || executablePath.Contains('"'))
            throw new ArgumentException("Invalid startup executable path.");
        this.executablePath = Path.GetFullPath(executablePath);
        this.userSid = userSid;
        this.tasks = tasks;
        TaskName = FrontendLogonTask.NameFor(userSid);
    }

    internal string TaskName { get; }

    internal static LoginStartupRegistration ForCurrentProcess()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new(ProductDesktopLayout.ResidentBesideWindow(AppContext.BaseDirectory),
            identity.User?.Value ?? throw new InvalidOperationException("The frontend user has no SID."),
            new WindowsTaskSchedulerRootFolder());
    }

    internal bool Read() => ReadState() == FrontendLogonTaskState.Active;

    internal bool Set(bool enabled)
    {
        _ = ReadState(); // A task owned by another executable is never overwritten or deleted.
        if (enabled) tasks.Register(TaskName, FrontendLogonTask.BuildDefinition(executablePath, userSid));
        else tasks.Delete(TaskName);
        var actual = Read();
        if (actual != enabled) throw new IOException("Startup registration did not retain the requested value.");
        return actual;
    }

    private FrontendLogonTaskState? ReadState()
    {
        var definition = tasks.ReadDefinition(TaskName);
        if (definition is null) return null;
        var state = FrontendLogonTask.Inspect(definition, executablePath);
        var legacy = Path.Combine(Path.GetDirectoryName(executablePath)!, ProductDesktopLayout.WindowExecutableName);
        if (state == FrontendLogonTaskState.Foreign
            && string.Equals(Path.GetFileName(executablePath), ProductDesktopLayout.ResidentExecutableName, StringComparison.OrdinalIgnoreCase)
            && FrontendLogonTask.Inspect(definition, legacy) != FrontendLogonTaskState.Foreign)
        {
            tasks.Register(TaskName, FrontendLogonTask.RewriteExecutable(definition, legacy, executablePath));
            var saved = tasks.ReadDefinition(TaskName) ?? throw new IOException("The migrated frontend logon task was not retained.");
            state = FrontendLogonTask.Inspect(saved, executablePath);
        }
        return state == FrontendLogonTaskState.Foreign
            ? throw new InvalidDataException("The frontend logon task belongs to another executable.")
            : state;
    }
}
