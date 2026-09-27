using ResourceManager.Shared.Packages;

namespace ResourceManager.Installer;

internal sealed record PackageLayout(string Root, string Version, string Backend, string NativeUi,
    string InternalLauncher, string Start, string Installer)
{
    public static PackageLayout Verify(string packageRoot)
    {
        var verified = ReleasePackageLayout.Verify(packageRoot);
        return ForRoot(verified.Root, verified.Version);
    }

    public static PackageLayout ForRoot(string root, string version) => new(root, version,
        Path.Combine(root, "Bin", "ResourceManager", "ResourceManager.exe"),
        Path.Combine(root, "Bin", "ResourceManagerNativeUi", "ResourceManager.NativeUi.exe"),
        Path.Combine(root, "Bin", "ResourceManagerLauncher", "ResourceManager.Launcher.exe"),
        Path.Combine(root, "Start.exe"), Path.Combine(root, "Install.exe"));

    public static void RejectReparse(string path) => ReleasePackageLayout.RejectReparse(path);
}
