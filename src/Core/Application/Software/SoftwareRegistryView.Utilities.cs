using ResourceManager.App.Domain.Software;

using ResourceManager.App.Domain.Messages;

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

    private static SoftwareOperationCapabilities NoUninstall(byte reasonCode)
    {
        return new SoftwareOperationCapabilities(
            false,
            "None",
            string.Empty,
            string.Empty,
            BackendMessage.Create(
                BackendMessageDomains.Software,
                BackendMessageCodes.Software.CannotUninstallAction),
            BackendMessage.Create(BackendMessageDomains.Software, reasonCode));
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
        var entryNameKey = NormalizeSoftwareKey(entry.Name);
        return knownRecords.Any(record =>
            NormalizeSoftwareKey(record.Name) == entryNameKey
            || RootOverlaps(entry.RootPaths, record.RootPaths));
    }

    private static bool RootOverlaps(IReadOnlyList<string> entryRoots, IReadOnlyList<string> knownRoots)
    {
        if (entryRoots.Count == 0)
        {
            return false;
        }

        var normalizedKnownRoots = knownRoots
            .Select(NormalizePathForCompare)
            .Where(static root => root is not null)
            .Select(static root => root!)
            .ToArray();
        return entryRoots
            .Select(NormalizePathForCompare)
            .Where(static root => root is not null)
            .Any(entryRoot => normalizedKnownRoots.Any(knownRoot =>
                IsSameOrUnder(entryRoot!, knownRoot)
                || IsSameOrUnder(knownRoot, entryRoot!)));
    }

    private static string NormalizeSoftwareKey(string value)
    {
        var chars = value
            .Trim()
            .Select(static ch => char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '\0')
            .Where(static ch => ch != '\0')
            .ToArray();
        return new string(chars);
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
