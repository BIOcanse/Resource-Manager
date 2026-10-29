using System.Security.Principal;

namespace ResourceManager.NativeUi;

/// <summary>
/// The key the bundled UI presents to the backend (user 2026-10-29, option A). The UI manifest requires an
/// elevated administrator, and an elevated administrator calls the backend without a key: the backend reads the
/// caller's own token and a keyless elevated administrator passes every route. Only a process that is not an
/// elevated administrator (an in-process host such as a test runner) presents the default key file. The empty
/// string is the existing "send no key header" value of the reader contract.
/// </summary>
internal static class NativeUiBackendCredential
{
    internal static Func<string?> ForCurrentProcess(string packageRoot)
        => ForElevation(IsElevatedAdministrator(), () => new NativeTrustedAccessKeyReader(packageRoot).Read);

    // The default key file is not opened at all when no key is presented.
    internal static Func<string?> ForElevation(bool elevatedAdministrator, Func<Func<string?>> defaultKeyReader)
        => elevatedAdministrator ? static () => string.Empty : defaultKeyReader();

    // The same fact the backend checks for a keyless loopback caller: the process token is in the
    // Administrators role, which holds only for an elevated (unfiltered) administrator token.
    internal static bool IsElevatedAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
