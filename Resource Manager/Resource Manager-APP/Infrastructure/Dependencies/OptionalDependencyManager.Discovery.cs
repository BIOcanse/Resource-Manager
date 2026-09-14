using ResourceManager.App.Domain.Dependencies;
using ResourceManager.App.Domain.Software;

namespace ResourceManager.App.Infrastructure.Dependencies;

public sealed partial class OptionalDependencyManager
{
    private static OptionalDependencyExternalInstall? ResolveExternalInstall(
        OptionalDependencyDefinition definition,
        IReadOnlyList<InstalledSoftwareEntry> installedSoftware,
        string managedInstallDirectory)
    {
        foreach (var entry in installedSoftware.Where(entry => IsInstalledSoftwareMatch(definition, entry)))
        {
            if (string.IsNullOrWhiteSpace(entry.InstallLocation))
            {
                continue;
            }

            var installDirectory = NormalizeCandidatePath(entry.InstallLocation);
            if (installDirectory is null
                || IsSameDirectory(installDirectory, managedInstallDirectory)
                || !IsDependencyInstalledAt(definition, installDirectory))
            {
                continue;
            }

            return new OptionalDependencyExternalInstall(
                installDirectory,
                $"WindowsInstalledSoftware:{entry.Name}");
        }

        foreach (var candidate in EnumerateCommonInstallCandidates(definition, managedInstallDirectory))
        {
            if (IsDependencyInstalledAt(definition, candidate))
            {
                return new OptionalDependencyExternalInstall(candidate, "CommonInstallRoot");
            }
        }

        return null;
    }

    private static bool IsDependencyInstalledAt(
        OptionalDependencyDefinition definition,
        string installDirectory)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(installDirectory) || !Directory.Exists(installDirectory))
            {
                return false;
            }

            if (definition.InstalledProbeRelativePaths.Count == 0)
            {
                return Directory.EnumerateFileSystemEntries(installDirectory).Any();
            }

            return definition.InstalledProbeRelativePaths.All(path =>
                File.Exists(Path.Combine(installDirectory, path)));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool IsInstalledSoftwareMatch(
        OptionalDependencyDefinition definition,
        InstalledSoftwareEntry entry)
    {
        var definitionName = NormalizeMatchText(definition.Name);
        var entryName = NormalizeMatchText(entry.Name);
        var vendor = NormalizeMatchText(definition.Vendor);
        var publisher = NormalizeMatchText(entry.Publisher ?? "");

        return (!string.IsNullOrWhiteSpace(entryName)
                && (entryName.Contains(definitionName, StringComparison.OrdinalIgnoreCase)
                    || definitionName.Contains(entryName, StringComparison.OrdinalIgnoreCase)))
            || (!string.IsNullOrWhiteSpace(vendor)
                && !string.IsNullOrWhiteSpace(publisher)
                && publisher.Contains(vendor, StringComparison.OrdinalIgnoreCase)
                && entryName.Contains(NormalizeMatchText(definition.InstallDirectoryName), StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> EnumerateCommonInstallCandidates(
        OptionalDependencyDefinition definition,
        string managedInstallDirectory)
    {
        var roots = new List<string?>();
        roots.Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        roots.Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));

        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed || !drive.IsReady)
            {
                continue;
            }

            roots.Add(Path.Combine(drive.RootDirectory.FullName, "Software"));
            roots.Add(Path.Combine(drive.RootDirectory.FullName, "Tools"));
            roots.Add(Path.Combine(drive.RootDirectory.FullName, "Apps"));
            roots.Add(Path.Combine(drive.RootDirectory.FullName, "Program Files"));
            roots.Add(Path.Combine(drive.RootDirectory.FullName, "Program Files (x86)"));
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots.Where(static root => !string.IsNullOrWhiteSpace(root)))
        {
            string candidate;
            try
            {
                candidate = Path.GetFullPath(Path.Combine(root!, definition.InstallDirectoryName));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            if (IsSameDirectory(candidate, managedInstallDirectory) || !seen.Add(candidate))
            {
                continue;
            }

            yield return candidate;
        }
    }
}
