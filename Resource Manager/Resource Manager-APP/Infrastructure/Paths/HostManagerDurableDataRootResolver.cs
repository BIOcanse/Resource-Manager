using Microsoft.Win32;

namespace ResourceManager.App.Infrastructure.Paths;

internal static class HostManagerDurableDataRootResolver
{
    private const string RegistrationKey = @"SOFTWARE\ResourceManager";
    private const string InstallRootValueName = "InstallRoot";
    private const string GreenInstallValueName = "GreenInstall";

    internal static string ResolveStableInstallRoot(
        string contentRootPath,
        bool allowUnregisteredFallback)
        => ResolveStableInstallRoot(
            contentRootPath,
            TryReadRegisteredInstallRoot(),
            Environment.GetEnvironmentVariable("RESOURCE_MANAGER_PACKAGE_ROOT"),
            allowUnregisteredFallback);

    internal static string ResolveStableInstallRoot(
        string contentRootPath,
        string? registeredInstallRoot)
        => ResolveStableInstallRoot(
            contentRootPath,
            registeredInstallRoot,
            allowUnregisteredFallback: true);

    internal static string ResolveStableInstallRoot(
        string contentRootPath,
        string? registeredInstallRoot,
        bool allowUnregisteredFallback)
        => ResolveStableInstallRoot(
            contentRootPath,
            registeredInstallRoot,
            explicitPackageRoot: null,
            allowUnregisteredFallback);

    internal static string ResolveStableInstallRoot(
        string contentRootPath,
        string? registeredInstallRoot,
        string? explicitPackageRoot,
        bool allowUnregisteredFallback)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRootPath);
        if (!string.IsNullOrWhiteSpace(explicitPackageRoot))
        {
            return NormalizeRegisteredInstallRoot(explicitPackageRoot);
        }

        if (registeredInstallRoot is not null)
        {
            return NormalizeRegisteredInstallRoot(registeredInstallRoot);
        }

        if (!allowUnregisteredFallback)
        {
            throw new InvalidDataException(
                "The installed Resource Manager deployment has no registered stable install root.");
        }

        var contentRoot = new DirectoryInfo(Path.GetFullPath(contentRootPath));
        for (var candidate = contentRoot; candidate is not null; candidate = candidate.Parent)
        {
            if (candidate.Name.Equals("Bin", StringComparison.OrdinalIgnoreCase)
                && candidate.Parent is not null)
            {
                return candidate.Parent.FullName;
            }
        }

        return Path.GetFullPath(PackagePathResolver.ResolvePackageRoot(contentRoot));
    }

    private static string? TryReadRegisteredInstallRoot()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var machineInstallRoots = new List<object>(capacity: 2);
        ReadRegisteredValue(
            RegistryHive.LocalMachine,
            RegistryView.Registry64,
            InstallRootValueName,
            machineInstallRoots);
        ReadRegisteredValue(
            RegistryHive.LocalMachine,
            RegistryView.Registry32,
            InstallRootValueName,
            machineInstallRoots);
        var machineInstallRoot = ResolveRegisteredInstallRoot(machineInstallRoots);
        if (machineInstallRoot is not null)
        {
            return machineInstallRoot;
        }

        var userInstallRoots = new List<object>(capacity: 2);
        var greenInstallMarkers = new List<object>(capacity: 2);
        ReadRegisteredValue(
            RegistryHive.CurrentUser,
            RegistryView.Registry64,
            InstallRootValueName,
            userInstallRoots);
        ReadRegisteredValue(
            RegistryHive.CurrentUser,
            RegistryView.Registry32,
            InstallRootValueName,
            userInstallRoots);
        ReadRegisteredValue(
            RegistryHive.CurrentUser,
            RegistryView.Registry64,
            GreenInstallValueName,
            greenInstallMarkers);
        ReadRegisteredValue(
            RegistryHive.CurrentUser,
            RegistryView.Registry32,
            GreenInstallValueName,
            greenInstallMarkers);
        return ResolveGreenInstallRoot(userInstallRoots, greenInstallMarkers);
    }

    internal static string? ResolveDeploymentInstallRoot(
        IReadOnlyList<object> machineInstallRoots,
        IReadOnlyList<object> userInstallRoots,
        IReadOnlyList<object> greenInstallMarkers)
    {
        var machineInstallRoot = ResolveRegisteredInstallRoot(machineInstallRoots);
        return machineInstallRoot
            ?? ResolveGreenInstallRoot(userInstallRoots, greenInstallMarkers);
    }

    internal static string? ResolveRegisteredInstallRoot(
        IReadOnlyList<object> rawValues)
    {
        ArgumentNullException.ThrowIfNull(rawValues);
        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in rawValues)
        {
            if (value is not string path)
            {
                throw new InvalidDataException(
                    "The Resource Manager machine install root registration is not a string.");
            }

            values.Add(NormalizeRegisteredInstallRoot(path));
        }
        return values.Count switch
        {
            0 => null,
            1 => values.Single(),
            _ => throw new InvalidDataException(
                "The Resource Manager machine registration contains conflicting install roots.")
        };
    }

    private static string? ResolveGreenInstallRoot(
        IReadOnlyList<object> userInstallRoots,
        IReadOnlyList<object> greenInstallMarkers)
    {
        ArgumentNullException.ThrowIfNull(userInstallRoots);
        ArgumentNullException.ThrowIfNull(greenInstallMarkers);
        var installRoot = ResolveRegisteredInstallRoot(userInstallRoots);
        if (installRoot is null && greenInstallMarkers.Count == 0)
        {
            return null;
        }

        if (installRoot is null || greenInstallMarkers.Count == 0)
        {
            throw new InvalidDataException(
                "The Resource Manager user registration is not a complete green installation.");
        }

        foreach (var marker in greenInstallMarkers)
        {
            if (marker is not int value || value != 1)
            {
                throw new InvalidDataException(
                    "The Resource Manager user registration has an invalid green-install marker.");
            }
        }
        return installRoot;
    }

    private static void ReadRegisteredValue(
        RegistryHive hive,
        RegistryView view,
        string valueName,
        ICollection<object> values)
    {
        using var baseKey = RegistryKey.OpenBaseKey(hive, view);
        using var registration = baseKey.OpenSubKey(RegistrationKey, writable: false);
        var missing = new object();
        var value = registration?.GetValue(
            valueName,
            missing,
            RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (value is null || ReferenceEquals(value, missing))
        {
            return;
        }

        values.Add(value);
    }

    private static string NormalizeRegisteredInstallRoot(string installRoot)
    {
        if (string.IsNullOrWhiteSpace(installRoot)
            || !Path.IsPathFullyQualified(installRoot))
        {
            throw new InvalidDataException(
                "The Resource Manager machine install root registration is not an absolute path.");
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(installRoot));
    }
}
