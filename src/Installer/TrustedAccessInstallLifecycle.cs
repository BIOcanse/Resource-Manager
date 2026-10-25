using ResourceManager.Updater;

namespace ResourceManager.Installer;

internal static class TrustedAccessInstallLifecycle
{
    internal const string InitializeArgument = TrustedAccessAdministration.InitializeArgument;

    internal static void InitializeBeforeServiceRegistration(
        string backendExecutable,
        Func<string, string, int> runAdministration,
        Action registerService)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backendExecutable);
        ArgumentNullException.ThrowIfNull(runAdministration);
        ArgumentNullException.ThrowIfNull(registerService);

        var exitCode = runAdministration(backendExecutable, InitializeArgument);
        if (exitCode != 0)
        {
            throw new IOException(
                $"Trusted access initialization failed ({exitCode}); the backend service was not registered.");
        }

        registerService();
    }

    internal static int RunBackendAdministration(string backendExecutable, string argument)
        => TrustedAccessAdministration.Run(backendExecutable, argument);
}
