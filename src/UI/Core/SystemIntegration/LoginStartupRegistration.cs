using Microsoft.Win32;

namespace ResourceManager.NativeUi.SystemIntegration;

// The current user's Run value is the single persisted truth for frontend logon startup.
internal sealed class LoginStartupRegistration
{
    internal const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string ValueName = "ResourceManager.Desktop";
    private readonly string command;
    private readonly string runKey;

    // runKey is relative to the key passed to Read and Set. Tests use their own path because some security
    // products silently revert deletion of any key ending in CurrentVersion\Run, so cleanup never finishes.
    internal LoginStartupRegistration(string executablePath, string runKey = RunKey)
    {
        var path = Path.GetFullPath(executablePath);
        if (path.Contains('"')) throw new ArgumentException("Invalid startup executable path.");
        command = $"\"{path}\" --background-startup";
        this.runKey = runKey;
    }

    internal bool Read(RegistryKey currentUser)
    {
        using var key = currentUser.OpenSubKey(runKey);
        return ReadValue(key);
    }

    internal bool Set(RegistryKey currentUser, bool enabled)
    {
        using var key = currentUser.CreateSubKey(runKey, writable: true);
        _ = ReadValue(key); // A value owned by another executable is never overwritten or deleted.
        if (enabled) key.SetValue(ValueName, command, RegistryValueKind.String);
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
        var actual = ReadValue(key);
        if (actual != enabled) throw new IOException("Startup registration did not retain the requested value.");
        return actual;
    }

    private bool ReadValue(RegistryKey? key)
    {
        var value = key?.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (value is null) return false;
        if (value is string actual && actual.Equals(command, StringComparison.OrdinalIgnoreCase)) return true;
        throw new InvalidDataException("The frontend startup registration belongs to another executable.");
    }
}
