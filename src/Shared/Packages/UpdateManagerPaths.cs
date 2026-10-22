namespace ResourceManager.Shared.Packages;

public static class UpdateManagerPaths
{
    public const string ExecutableName = "ResourceManager.UpdateManager.exe";
    public static string PackagedExecutable(string packageRoot)
        => Path.Combine(packageRoot, "Internal", "UpdateManager", ExecutableName);
    public static string InstalledDirectory(string installRoot)
        => Path.Combine(Path.GetFullPath(installRoot).TrimEnd(Path.DirectorySeparatorChar), "Internal", "Management");
    public static string InstalledExecutable(string installRoot)
        => Path.Combine(InstalledDirectory(installRoot), ExecutableName);

    // Retirement/uninstall and the explicit old-manager handoff recognize the real previous external registration.
    public static string RequireOwnedManager(string installRoot, string? registeredExecutable)
    {
        var root = Path.GetFullPath(installRoot).TrimEnd(Path.DirectorySeparatorChar);
        var legacy = Path.Combine(Path.GetDirectoryName(root)!, "ResourceManager.UpdateManager", ExecutableName);
        if (registeredExecutable is null ||
            (!string.Equals(registeredExecutable, InstalledExecutable(root), StringComparison.OrdinalIgnoreCase)
             && !string.Equals(registeredExecutable, legacy, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("The registered update manager does not belong to the installation.");
        return Path.GetFullPath(registeredExecutable);
    }
}
