using Microsoft.Win32;
using ResourceManager.Shared.ServiceHosting;

namespace ResourceManager.Launcher;

public sealed record DesktopInstallation(string Root, string Backend, string NativeUi, string Launcher)
{
    public static DesktopInstallation Read(RegistryKey root)
    {
        using var key = root.OpenSubKey(@"Software\ResourceManager")
            ?? throw new InvalidOperationException("Resource Manager is not registered. Run the directory registration first.");
        if (key.GetValue("ServiceName") as string != WindowsServiceRegistration.ProductServiceName)
            throw new InvalidOperationException("The registered service name is invalid.");
        var folder = Path.GetFullPath(Required(key, "InstallRoot"));
        var backend = Required(key, "BackendPath");
        var ui = Required(key, "NativeUiPath");
        var launcher = Required(key, "LauncherPath");
        foreach (var path in new[] { backend, ui, launcher })
        {
            var relative = Path.GetRelativePath(folder, path);
            if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(@"..\", StringComparison.Ordinal)
                || !File.Exists(path))
                throw new FileNotFoundException("A registered program is missing or outside its package.", path);
        }
        return new(folder, Path.GetFullPath(backend), Path.GetFullPath(ui), Path.GetFullPath(launcher));
    }

    private static string Required(RegistryKey key, string name) =>
        key.GetValue(name) is string value && !string.IsNullOrWhiteSpace(value)
            ? value : throw new InvalidOperationException($"Missing installation value: {name}.");
}
