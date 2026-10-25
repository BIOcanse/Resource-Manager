using System.Diagnostics;

namespace ResourceManager.Installer;

internal static class TrustedAccessInstallLifecycle
{
    internal const string InitializeArgument = "--initialize-trusted-access";

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
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backendExecutable);
        if (!string.Equals(argument, InitializeArgument, StringComparison.Ordinal))
        {
            throw new ArgumentException("Unsupported backend administration action.", nameof(argument));
        }

        var backend = Path.GetFullPath(backendExecutable);
        var start = new ProcessStartInfo(backend)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(backend)!
        };
        start.ArgumentList.Add(argument);
        using var process = Process.Start(start)
            ?? throw new IOException("Unable to start trusted access initialization.");
        if (!process.WaitForExit(120_000))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            throw new TimeoutException("Trusted access initialization did not finish in time.");
        }

        return process.ExitCode;
    }
}
