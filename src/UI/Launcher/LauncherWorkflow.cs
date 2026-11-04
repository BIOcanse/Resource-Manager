using ResourceManager.Shared.ServiceHosting;

namespace ResourceManager.Launcher;

public static class LauncherWorkflow
{
    // The interface manifest requires an elevated administrator. launchInterface uses ShellExecute either way:
    // an unelevated launcher gets the UAC consent prompt, an elevated one starts the interface with its own token.
    public static void Start(bool elevated, bool restart, string backend,
        Func<WindowsServiceState?> readService, Action startService, Action elevateService, Action launchInterface,
        string? failureMessage = null)
        => NativeLauncherWorkflow.Start(elevated, restart, backend, readService, startService,
            elevateService, launchInterface, failureMessage);
}
