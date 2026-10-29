using ResourceManager.Shared.ServiceHosting;

namespace ResourceManager.Launcher;

public static class LauncherWorkflow
{
    // The interface manifest requires an elevated administrator. launchInterface uses ShellExecute either way:
    // an unelevated launcher gets the UAC consent prompt, an elevated one starts the interface with its own token.
    public static void Start(bool elevated, bool restart, string backend,
        Func<WindowsServiceState?> readService, Action startService, Action elevateService, Action launchInterface,
        string? failureMessage = null)
    {
        var state = readService();
        if (state is not null) WindowsServiceRegistration.RequireExpectedBinary(state, backend);
        if (state is null || !state.Running || restart)
        {
            if (elevated) startService();
            else elevateService();
        }
        var running = readService() ?? throw new InvalidOperationException(failureMessage ?? "The SYSTEM service did not start.");
        WindowsServiceRegistration.RequireExpectedBinary(running, backend);
        if (!running.Running) throw new InvalidOperationException(failureMessage ?? "The SYSTEM service did not reach the running state.");
        launchInterface();
    }
}
