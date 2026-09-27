namespace ResourceManager.Shared.Packages;

public static class UpdateManagerPaths
{
    public const string ExecutableName = "ResourceManager.UpdateManager.exe";

    public static string PackagedExecutable(string packageRoot)
        => Path.Combine(packageRoot, "Internal", "UpdateManager", ExecutableName);

    public static string InstalledDirectory(string installRoot)
    {
        var root = Path.GetFullPath(installRoot).TrimEnd(Path.DirectorySeparatorChar);
        var parent = Path.GetDirectoryName(root)
            ?? throw new InvalidOperationException("安装目录缺少父目录。");
        return Path.Combine(parent, "ResourceManager.UpdateManager");
    }

    public static string InstalledExecutable(string installRoot)
        => Path.Combine(InstalledDirectory(installRoot), ExecutableName);
}
