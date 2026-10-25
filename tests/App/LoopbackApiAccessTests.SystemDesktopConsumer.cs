using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.Security;
using ResourceManager.App.Infrastructure.ServiceHosting;

namespace Resource_Manager_APP.Tests;

public sealed partial class LoopbackApiAccessTests
{
    [EnvironmentVariableFact("RESOURCE_MANAGER_SYSTEM_DESKTOP_TEST", "1")]
    public Task SystemBackendLaunchesOnlyTheSelectedNonElevatedDesktopUser()
        => RunSystemUserLaunchAsync();

    private static async Task RunSystemUserLaunchAsync()
    {
        using var identity = WindowsIdentity.GetCurrent();
        using var current = Process.GetCurrentProcess();
        Assert.True(identity.IsSystem, "Run only through the finite owned SYSTEM test helper.");
        Assert.Equal(0, current.SessionId);

        WindowsBackendProcessIdentityAccess.InitializeForCurrentSystemProcess();
        var session = uint.Parse(Environment.GetEnvironmentVariable("RM_SYSTEM_TARGET_SESSION")!);
        var expectedSid = new SecurityIdentifier(Environment.GetEnvironmentVariable("RM_SYSTEM_TARGET_SID")!).Value;
        Assert.NotEqual(identity.User!.Value, expectedSid);
        var broker = new WindowsInteractiveUserSessionBroker();
        Assert.Contains(session, broker.GetActiveSessionIds());
        Assert.Equal(expectedSid, broker.GetUserSid(session));

        var script = Path.Combine(Path.GetTempPath(), "owned-system-user-probe-" + Guid.NewGuid().ToString("N") + ".ps1");
        var report = Path.Combine(Path.GetTempPath(), "owned-system-user-report-" + Guid.NewGuid().ToString("N") + ".json");
        var release = Path.Combine(Path.GetTempPath(), "owned-system-user-release-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(script, """
            param([uint32]$BackendPid, [string]$ReportPath, [string]$Release)
            Add-Type -TypeDefinition @'
            using System;
            using System.Runtime.InteropServices;
            using Microsoft.Win32.SafeHandles;
            public static class OwnedSystemProcessAccess {
                [DllImport("kernel32.dll", SetLastError=true)]
                private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint processId);
                public static bool Denied(uint access, uint processId) {
                    using (var process = OpenProcess(access, false, processId)) return process.IsInvalid;
                }
            }
            '@
            $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
            $principal = New-Object System.Security.Principal.WindowsPrincipal($identity)
            $rights = @{
                FrontendIsSystem = $identity.IsSystem
                FrontendIsAdministrator = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
                TerminationDenied = [OwnedSystemProcessAccess]::Denied(0x0001, $BackendPid)
                VmReadDenied = [OwnedSystemProcessAccess]::Denied(0x0010, $BackendPid)
                VmWriteDenied = [OwnedSystemProcessAccess]::Denied(0x0020, $BackendPid)
                DaclWriteDenied = [OwnedSystemProcessAccess]::Denied(0x00040000, $BackendPid)
            } | ConvertTo-Json -Compress
            [IO.File]::WriteAllText($ReportPath, $rights)
            $deadline = [DateTime]::UtcNow.AddSeconds(20)
            while (-not [IO.File]::Exists($Release) -and [DateTime]::UtcNow -lt $deadline) {
                Start-Sleep -Milliseconds 20
            }
            """);
        var executable = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var arguments = string.Join(" ", new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script,
                current.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), report, release }
            .Select(static value => $"\"{value}\""));

        Assert.Throws<UnauthorizedAccessException>(() => broker.LaunchDesktopProcess(
            session, identity.User.Value, executable, arguments));
        using var child = Process.GetProcessById(checked((int)broker.LaunchDesktopProcess(
            session, expectedSid, executable, arguments)));
        _ = child.SafeHandle;
        using var deadlineToken = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            Assert.Equal(checked((int)session), child.SessionId);
            Assert.Equal(executable, child.MainModule!.FileName, ignoreCase: true);
            Assert.True(WindowsLoopbackApiAdministratorReader.TryReadProcessIdentity(child.Id, out var caller));
            Assert.Equal(expectedSid, caller.Caller.WindowsSid);
            Assert.Equal(checked((int)session), caller.Caller.WindowsSessionId);
            Assert.False(caller.Caller.IsAdministrator);
            Assert.Equal(executable, caller.ExecutablePath, ignoreCase: true);

            using var reportDeadline = CancellationTokenSource.CreateLinkedTokenSource(deadlineToken.Token);
            reportDeadline.CancelAfter(TimeSpan.FromSeconds(5));
            while (!File.Exists(report)) await Task.Delay(50, reportDeadline.Token);
            using var reportJson = JsonDocument.Parse(await File.ReadAllTextAsync(report, reportDeadline.Token));
            var rights = reportJson.RootElement;
            Assert.False(rights.GetProperty("FrontendIsSystem").GetBoolean());
            Assert.False(rights.GetProperty("FrontendIsAdministrator").GetBoolean());
            foreach (var property in new[] { "TerminationDenied", "VmReadDenied", "VmWriteDenied", "DaclWriteDenied" })
                Assert.True(rights.GetProperty(property).GetBoolean(), property);

            await File.WriteAllTextAsync(release, string.Empty, deadlineToken.Token);
            await child.WaitForExitAsync(deadlineToken.Token);
            Assert.Equal(0, child.ExitCode);
        }
        finally
        {
            await File.WriteAllTextAsync(release, string.Empty);
            if (!child.HasExited)
            {
                try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (TimeoutException) { child.Kill(); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
            }
            if (File.Exists(script)) File.Delete(script);
            if (File.Exists(report)) File.Delete(report);
            if (File.Exists(release)) File.Delete(release);
        }
    }
}
