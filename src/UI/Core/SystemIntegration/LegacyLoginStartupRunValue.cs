using System.Diagnostics;
using System.Security;
using Microsoft.Win32;

namespace ResourceManager.NativeUi.SystemIntegration;

internal enum LegacyLoginStartupOutcome
{
    Absent,
    Migrated,
    Foreign
}

/// <summary>
/// Upgrade adapter for versions before the elevated frontend, which kept the user's logon-startup choice in
/// the current user's Run value. Windows does not start a requireAdministrator program from Run, so the choice
/// moves into the logon task and the value is deleted afterwards. A value owned by another executable is left
/// alone, and nothing reads the Run value as startup state. Delete this adapter when no supported upgrade
/// source predates the elevated frontend.
/// </summary>
internal static class LegacyLoginStartupRunValue
{
    internal const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string ValueName = "ResourceManager.Desktop";

    // runKey is relative to currentUser. Tests use their own path because some security products silently
    // revert deletion of any key ending in CurrentVersion\Run, so cleanup never finishes.
    internal static LegacyLoginStartupOutcome Migrate(RegistryKey currentUser, string executablePath,
        LoginStartupRegistration registration, string runKey = RunKey)
    {
        using var key = currentUser.OpenSubKey(runKey, writable: true);
        var value = key?.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (key is null || value is null) return LegacyLoginStartupOutcome.Absent;
        var command = $"\"{Path.GetFullPath(executablePath)}\" --background-startup";
        if (value is not string actual || !actual.Equals(command, StringComparison.OrdinalIgnoreCase))
            return LegacyLoginStartupOutcome.Foreign;
        // The task is registered first; a failure keeps the value, which Windows no longer starts.
        _ = registration.Set(true);
        key.DeleteValue(ValueName, throwOnMissingValue: false);
        return LegacyLoginStartupOutcome.Migrated;
    }

    /// <summary>One serial startup step of the primary frontend instance; the outcome is only logged.</summary>
    internal static void MigrateCurrentUser()
    {
        try
        {
            var registration = LoginStartupRegistration.ForCurrentProcess();
            using var currentUser = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            var outcome = Migrate(currentUser, Environment.ProcessPath!, registration);
            if (outcome != LegacyLoginStartupOutcome.Absent)
                Trace.TraceInformation("Legacy frontend Run value: {0} (logon task {1}).", outcome, registration.TaskName);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or SecurityException
            or InvalidOperationException or ArgumentException or InvalidDataException)
        {
            Trace.TraceWarning("Legacy frontend Run value was not migrated ({0}); it is kept.", failure.GetType().Name);
        }
    }
}
