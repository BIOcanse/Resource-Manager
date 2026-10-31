using Microsoft.Win32;
using ResourceManager.Shared.ServiceHosting;
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
    {
        text ??= ToolText.For(AppLanguage.System);
        using var key = root.OpenSubKey(@"Software\ResourceManager")
            ?? throw new InvalidOperationException(text.NotInstalled);
        if (key.GetValue("ServiceName") as string != WindowsServiceRegistration.ProductServiceName)
            throw new InvalidOperationException(text.LauncherRegistrationInvalid);
        var folder = Path.GetFullPath(Required(key, "InstallRoot", text));
        var backend = Required(key, "BackendPath", text);
        var ui = Required(key, "NativeUiPath", text);
        var launcher = Required(key, "LauncherPath", text);
        var resident = ProductDesktopLayout.ResidentFor(folder);
        if (key.GetValue("DesktopPath") is string registeredResident
            && !string.Equals(Path.GetFullPath(registeredResident), resident, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(text.LauncherRegistrationInvalid);
        foreach (var path in new[] { backend, ui, resident, launcher })
        {
            var relative = Path.GetRelativePath(folder, path);
            if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(@"..\", StringComparison.Ordinal)
                || !File.Exists(path))
                throw new FileNotFoundException(text.LauncherProgramMissing, path);
        }
        return new(folder, Path.GetFullPath(backend), Path.GetFullPath(ui), Path.GetFullPath(launcher));
    }

    private static string Required(RegistryKey key, string name, ToolText text) =>
        key.GetValue(name) is string value && !string.IsNullOrWhiteSpace(value)
            ? value : throw new InvalidOperationException(text.Format(text.LauncherValueMissingFormat, name));
}
