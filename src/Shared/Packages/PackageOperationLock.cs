namespace ResourceManager.Shared.Packages;

public static class PackageOperationLock
{
    public static FileStream Acquire(string installRoot)
    {
        return AcquireDirectory(UpdateManagerPaths.InstalledDirectory(installRoot));
    }

    public static FileStream AcquireDirectory(string directory)
    {
        ReleasePackageLayout.RejectReparse(directory);
        var path = Path.Combine(directory, "operation.lock");
        if (File.Exists(path)) ReleasePackageLayout.RejectReparse(path);
        return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
}
