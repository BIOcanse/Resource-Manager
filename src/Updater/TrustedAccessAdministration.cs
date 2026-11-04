using System.Diagnostics;

namespace ResourceManager.Updater;

// Runs the backend's explicit trusted-access administration for the installer and the update manager.
internal static class TrustedAccessAdministration
{
    internal const string InitializeArgument = "--initialize-trusted-access";

    internal static int Run(string backendExecutable, string argument)
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
