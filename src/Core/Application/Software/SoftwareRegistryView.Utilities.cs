using ResourceManager.App.Domain.Software;


namespace ResourceManager.App.Application.Software;

public sealed partial class SoftwareRegistryView
{
    private static string NormalizeId(string value)
    {
        var chars = value
            .Trim()
            .Select(static ch => char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '-')
            .ToArray();
        var normalized = new string(chars).Trim('-');
        return string.IsNullOrWhiteSpace(normalized) ? "unknown" : normalized;
    }

    private static SoftwareOperationCapabilities NoUninstall(string constraint)
    {
        return new SoftwareOperationCapabilities(
            false,
            "None",
            constraint);
    }

    private static bool DirectoryHasContent(string path)
    {
        try
        {
            return Directory.Exists(path)
                && (Directory.EnumerateFiles(path).Any() || Directory.EnumerateDirectories(path).Any());
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    private static bool HasKnownSoftwareMatch(InstalledSoftwareEntry entry, IReadOnlyList<SoftwareRecord> knownRecords)
    {
        return knownRecords.Any(record =>
            record.Id.Equals(entry.Id, StringComparison.OrdinalIgnoreCase)
            || record.Name.Equals(entry.Name, StringComparison.OrdinalIgnoreCase)
                && entry.RootPaths.Select(NormalizePathForCompare).Where(root => root is not null)
                    .Any(root => record.RootPaths.Select(NormalizePathForCompare)
                        .Any(known => root!.Equals(known, StringComparison.OrdinalIgnoreCase))));
    }

    private static string? NormalizePathForCompare(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(value.Trim().Trim('"')))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static bool IsSameOrUnder(string candidate, string root)
    {
        if (candidate.Equals(root, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return candidate.StartsWith(
            root + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
    }
}
