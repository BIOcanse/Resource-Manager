using ResourceManager.Shared.Desktop;

namespace ResourceManager.NativeUi.SystemIntegration;

// The current user's Task Scheduler logon task is the single persisted truth for frontend logon startup.
// The frontend manifest requires an elevated administrator, and Windows does not start such a program
// from a Run value; the task starts it with the user's elevated token.
//
// Persistent-entry compatibility (docs/frontend_block_trees_2026-10-26.md, 安装与持久入口): the logon task this
// class writes, the Task Manager IFEO Debugger value (TaskManagerShortcutReplacementController) and old shortcuts
// still start ResourceManager.NativeUi.exe directly. Started without the resident's pipe it only hands its
// single-instance command to ResourceManager.Desktop.exe (ResidentHandoff) and exits. Layout step 6 points these
// entries at the resident and rewrites existing ones on read. Delete ResidentHandoff, NativeUiLaunchOptions and this
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
        return new(Environment.ProcessPath
                ?? throw new InvalidOperationException("The frontend executable path is unavailable."),
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
        return state == FrontendLogonTaskState.Foreign
            ? throw new InvalidDataException("The frontend logon task belongs to another executable.")
            : state;
    }
}
