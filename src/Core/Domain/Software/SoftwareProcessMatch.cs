namespace ResourceManager.App.Domain.Software;

/// <summary>
/// One matching rule for catalog entries that name a process instead of a software identity
/// (software catalog per-process assignments and the protection entries made from them).
///
/// A process name is its image file name with a trailing <c>.exe</c> removed, compared without case:
/// <c>dwm</c>, <c>dwm.exe</c> and <c>C:\Windows\System32\dwm.exe</c> all name <c>dwm</c>; an image with
/// another extension keeps it (<c>GameMon.des</c>). An entry that also gives an executable path matches
/// only that full path (environment variables expanded, compared without case).
/// </summary>
public static class SoftwareProcessMatch
{
    public static string? NormalizeProcessName(string? value)
    {
        var name = value?.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var separator = name.LastIndexOfAny(['\\', '/']);
        if (separator >= 0)
        {
            name = name[(separator + 1)..];
        }

        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }

        return name.Length == 0 ? null : name;
    }

    public static string? NormalizeExecutablePath(string? value)
    {
        var path = value?.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        path = Environment.ExpandEnvironmentVariables(path);
        try
        {
            path = Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
        }

        return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    /// <summary>
    /// <paramref name="processName"/> and <paramref name="executablePath"/> must already be normalized.
    /// </summary>
    public static bool Matches(
        string processName,
        string? executablePath,
        IReadOnlyList<string> processNames,
        IReadOnlyList<string> executablePaths)
    {
        if (executablePath is not null)
        {
            var pathMatched = false;
            foreach (var candidate in executablePaths)
            {
                if (string.Equals(NormalizeExecutablePath(candidate), executablePath, StringComparison.OrdinalIgnoreCase))
                {
                    pathMatched = true;
                    break;
                }
            }

            if (!pathMatched)
            {
                return false;
            }
        }

        foreach (var candidate in processNames)
        {
            if (string.Equals(NormalizeProcessName(candidate), processName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        foreach (var candidate in executablePaths)
        {
            if (string.Equals(NormalizeProcessName(candidate), processName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
