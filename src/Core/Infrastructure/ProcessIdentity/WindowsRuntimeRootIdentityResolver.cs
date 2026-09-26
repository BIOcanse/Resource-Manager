using ResourceManager.App.Application.ProcessAttribution;
using ResourceManager.App.Domain.ProcessAttribution;
using ResourceManager.App.Domain.Software;

namespace ResourceManager.App.Infrastructure.ProcessIdentity;

public sealed class WindowsRuntimeRootIdentityResolver : IRuntimeRootIdentityResolver
{
    private static readonly string? WindowsRoot = NormalizePath(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
    private static readonly string? ProgramFilesRoot = NormalizePath(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
    private static readonly string? ProgramFilesX86Root = NormalizePath(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
    private static readonly string? LocalAppDataRoot = NormalizePath(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
    private static readonly string? RoamingAppDataRoot = NormalizePath(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));

    private static readonly HashSet<string> PortableAnchorNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Software",
        "Games",
        "Tools",
        "Apps",
        "Programs",
        "Dependencies"
    };

    private static readonly HashSet<string> BuildOutputSegmentNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin",
        "obj",
        "out",
        "output",
        "publish",
        "dist",
        "build"
    };

    public RuntimeAttributionObservation Observe(RuntimeProcessIdentity process)
    {
        var processPath = NormalizePath(process.ExecutablePath);
        if (processPath is null || IsWindowsOwnedPath(processPath))
        {
            return RuntimeAttributionObservation.NoMatch;
        }

        var rootPath = InferRuntimeRoot(processPath);
        if (rootPath is null || !IsUsefulRoot(rootPath, processPath))
        {
            return RuntimeAttributionObservation.NoMatch;
        }

        var displayName = ChooseDisplayName(process, rootPath);
        return RuntimeAttributionObservation.Matched(new RuntimeSoftwareAttribution(
            $"runtime-root:{NormalizeId(rootPath)}",
            displayName,
            SoftwareKinds.RuntimeRoot,
            SoftwareDisplayKinds.General,
            [rootPath]));
    }

    private static string? InferRuntimeRoot(string processPath)
    {
        return TryInferLocalAppDataRoot(processPath)
            ?? TryInferBuildProjectRoot(processPath)
            ?? TryInferChildOfRoot(processPath, ProgramFilesRoot)
            ?? TryInferChildOfRoot(processPath, ProgramFilesX86Root)
            ?? TryInferPortableAnchorRoot(processPath)
            ?? NormalizePath(Path.GetDirectoryName(processPath));
    }

    private static string? TryInferBuildProjectRoot(string processPath)
    {
        var segments = SplitPath(processPath);
        for (var index = segments.Length - 2; index >= 1; index--)
        {
            if (!BuildOutputSegmentNames.Contains(segments[index]))
            {
                continue;
            }

            var root = CombineSegments(segments[..index]);
            if (root is not null && !IsDriveRoot(root))
            {
                return root;
            }
        }

        return null;
    }

    private static string? TryInferLocalAppDataRoot(string processPath)
    {
        var programsRoot = LocalAppDataRoot is null
            ? null
            : NormalizePath(Path.Combine(LocalAppDataRoot, "Programs"));
        return TryInferChildOfRoot(processPath, programsRoot)
            ?? TryInferAppDataProductRoot(processPath, LocalAppDataRoot)
            ?? TryInferAppDataProductRoot(processPath, RoamingAppDataRoot);
    }

    private static string? TryInferAppDataProductRoot(string processPath, string? root)
    {
        if (root is null || !IsSameOrUnder(processPath, root))
        {
            return null;
        }

        var relative = Path.GetRelativePath(root, processPath);
        var parts = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
        {
            return null;
        }

        if (IsLikelyVendorFolder(parts[0]) && parts.Length >= 3)
        {
            return NormalizePath(Path.Combine(root, parts[0], parts[1]));
        }

        return NormalizePath(Path.Combine(root, parts[0]));
    }

    private static string? TryInferChildOfRoot(string processPath, string? root)
    {
        if (root is null || !IsSameOrUnder(processPath, root))
        {
            return null;
        }

        var relative = Path.GetRelativePath(root, processPath);
        var parts = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length >= 2 ? NormalizePath(Path.Combine(root, parts[0])) : null;
    }

    private static string? TryInferPortableAnchorRoot(string processPath)
    {
        var segments = SplitPath(processPath);
        for (var index = 0; index < segments.Length - 2; index++)
        {
            if (!PortableAnchorNames.Contains(segments[index]))
            {
                continue;
            }

            var root = CombineSegments(segments[..(index + 2)]);
            if (root is not null && !IsDriveRoot(root))
            {
                return root;
            }
        }

        return null;
    }

    private static string ChooseDisplayName(RuntimeProcessIdentity process, string rootPath)
    {
        var rootName = Path.GetFileName(rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var processPrefix = process.Name.Split(['.', '-', '_'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();

        if (!string.IsNullOrWhiteSpace(rootName)
            && !rootName.Equals("app", StringComparison.OrdinalIgnoreCase)
            && !rootName.Equals("bin", StringComparison.OrdinalIgnoreCase)
            && !rootName.Equals("x64", StringComparison.OrdinalIgnoreCase))
        {
            return rootName;
        }

        return string.IsNullOrWhiteSpace(processPrefix) ? process.Name : processPrefix;
    }

    private static bool IsLikelyVendorFolder(string value)
    {
        return value.Equals("OpenAI", StringComparison.OrdinalIgnoreCase)
            || value.Equals("Microsoft", StringComparison.OrdinalIgnoreCase)
            || value.Equals("Google", StringComparison.OrdinalIgnoreCase)
            || value.Equals("Tencent", StringComparison.OrdinalIgnoreCase)
            || value.Equals("NVIDIA", StringComparison.OrdinalIgnoreCase)
            || value.Equals("AMD", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUsefulRoot(string rootPath, string processPath)
    {
        if (IsDriveRoot(rootPath) || rootPath.Equals(processPath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !IsWindowsOwnedPath(rootPath);
    }

    private static bool IsWindowsOwnedPath(string path)
    {
        return IsUnderKnownRoot(path, WindowsRoot)
            || IsUnderKnownRoot(path, Path.Combine(ProgramFilesRoot ?? string.Empty, "WindowsApps"))
            || IsUnderKnownRoot(path, Path.Combine(ProgramFilesX86Root ?? string.Empty, "WindowsApps"));
    }

    private static string[] SplitPath(string path)
    {
        var root = Path.GetPathRoot(path);
        var relative = root is null ? path : path[root.Length..];
        var parts = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return root is null ? parts : [root, .. parts];
    }

    private static string? CombineSegments(ReadOnlySpan<string> segments)
    {
        if (segments.Length == 0)
        {
            return null;
        }

        var path = segments[0];
        for (var index = 1; index < segments.Length; index++)
        {
            path = Path.Combine(path, segments[index]);
        }

        return NormalizePath(path);
    }

    private static bool IsDriveRoot(string path)
    {
        var root = Path.GetPathRoot(path);
        return root is not null
            && path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Equals(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUnderKnownRoot(string candidate, string? root)
    {
        return root is not null && IsSameOrUnder(candidate, root);
    }

    private static bool IsSameOrUnder(string candidate, string root)
    {
        return candidate.Equals(root, StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string? NormalizePath(string? value)
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

    private static string NormalizeId(string value)
    {
        var chars = value
            .Trim()
            .ToLowerInvariant()
            .Select(static c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray();
        var id = string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(id) ? "unknown" : id;
    }
}
