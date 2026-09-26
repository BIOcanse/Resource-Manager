namespace ResourceManager.App.Infrastructure.Paths;

public static class PackagePathResolver
{
    public static string ResolvePackageRoot(string contentRootPath)
    {
        return ResolvePackageRoot(
            new DirectoryInfo(contentRootPath),
            Environment.GetEnvironmentVariable("RESOURCE_MANAGER_PACKAGE_ROOT"));
    }

    public static string ResolvePackageRoot(DirectoryInfo appRoot)
    {
        return ResolvePackageRoot(
            appRoot,
            Environment.GetEnvironmentVariable("RESOURCE_MANAGER_PACKAGE_ROOT"));
    }

    internal static string ResolvePackageRoot(
        DirectoryInfo appRoot,
        string? overridePath)
    {
        ArgumentNullException.ThrowIfNull(appRoot);
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return Path.GetFullPath(overridePath);
        }

        if (IsAppDirectory(appRoot.Name) && appRoot.Parent is not null)
        {
            return appRoot.Parent.FullName;
        }

        if (appRoot.Parent?.Name.Equals("Bin", StringComparison.OrdinalIgnoreCase) == true
            && appRoot.Parent.Parent is not null)
        {
            return appRoot.Parent.Parent.FullName;
        }

        return appRoot.FullName;
    }

    private static bool IsAppDirectory(string directoryName)
    {
        return directoryName.Equals("APP", StringComparison.OrdinalIgnoreCase)
            || directoryName.EndsWith("-APP", StringComparison.OrdinalIgnoreCase);
    }
}
