namespace ResourceManager.Shared.Packages;

/// <summary>The product's resident and window share one directory. Build scripts project these format
/// identities for PowerShell and Zig; deployment and runtime code use this owner directly.</summary>
public static class ProductDesktopLayout
{
    public const string DirectoryName = "ResourceManagerNativeUi";
    public const string ResidentExecutableName = "ResourceManager.Desktop.exe";
    public const string WindowExecutableName = "ResourceManager.NativeUi.exe";
    public const string BackendDirectoryName = "ResourceManager";
    public const string BackendExecutableName = "ResourceManager.exe";
    public const string TaskManagerCommandMarker = "--resource-manager-task-manager-replacement";

    public static string DirectoryFor(string root) => Path.Combine(root, "Bin", DirectoryName);
    public static string ResidentFor(string root) => Path.Combine(DirectoryFor(root), ResidentExecutableName);
    public static string WindowFor(string root) => Path.Combine(DirectoryFor(root), WindowExecutableName);
    public static string ResidentBesideWindow(string directory) => Path.Combine(directory, ResidentExecutableName);
    public static string PackageRootFromUiDirectory(string directory) => Path.GetFullPath(Path.Combine(directory, "..", ".."));
}
