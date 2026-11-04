using Microsoft.Win32;
using ResourceManager.Shared.Localization;
using ResourceManager.Shared.Packages;

namespace ResourceManager.Launcher;

public sealed record DesktopInstallation(string Root, string Backend, string NativeUi, string Launcher)
{
    public string Resident => ProductDesktopLayout.ResidentFor(Root);
    public System.Diagnostics.ProcessStartInfo ResidentStartInfo(bool background) => new(Resident)
    {
        UseShellExecute = true,
        Arguments = background ? "--background-startup" : "--show-existing",
        WorkingDirectory = Path.GetDirectoryName(Resident)!
    };
    public static DesktopInstallation Read(RegistryKey root, ToolText? text = null)
        => NativeDesktopInstallation.Read(root, text);
}
