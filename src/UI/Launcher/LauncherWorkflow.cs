using ResourceManager.Shared.ServiceHosting;

namespace ResourceManager.Launcher;

public static class LauncherWorkflow
{
    public static void Start(bool elevated, bool restart, string backend,
        Func<WindowsServiceState?> readService, Action startService, Action elevateService, Action launchInterface,
        string? failureMessage = null, Action? requestInterface = null)
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
        if (elevated)
            (requestInterface ?? throw new InvalidOperationException("The service UI opening request is unavailable."))();
        else launchInterface();
    }
}
