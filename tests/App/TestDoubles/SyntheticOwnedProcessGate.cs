using System.Diagnostics;

namespace Resource_Manager_APP.Tests;

internal sealed class SyntheticOwnedProcessGate : IDisposable
{
    private readonly EventWaitHandle release;

    private SyntheticOwnedProcessGate(ProcessStartInfo startInfo, EventWaitHandle release)
    {
        StartInfo = startInfo;
        this.release = release;
    }

    internal ProcessStartInfo StartInfo { get; }

    internal void Release() => release.Set();

    public void Dispose()
    {
        release.Set();
        release.Dispose();
    }

    internal static SyntheticOwnedProcessGate Create(int exitCode)
    {
        if (exitCode is < 0 or > 255)
            throw new ArgumentOutOfRangeException(nameof(exitCode));

        var eventName = $"Local\\ResourceManager-owned-installer-test-{Guid.NewGuid():N}";
        var release = new EventWaitHandle(false, EventResetMode.ManualReset, eventName);
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(
                    Environment.SystemDirectory,
                    "WindowsPowerShell",
                    "v1.0",
                    "powershell.exe"),
                CreateNoWindow = true,
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add(
                $"$gate=[System.Threading.EventWaitHandle]::OpenExisting('{eventName}'); "
                + "try { $null=$gate.WaitOne() } finally { $gate.Dispose() }; "
                + $"exit {exitCode}");
            return new SyntheticOwnedProcessGate(startInfo, release);
        }
        catch
        {
            release.Dispose();
            throw;
        }
    }
}
