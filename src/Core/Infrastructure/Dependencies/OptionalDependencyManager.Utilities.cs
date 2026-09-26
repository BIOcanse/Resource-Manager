using System.Diagnostics;
using ResourceManager.App.Domain.Dependencies;

namespace ResourceManager.App.Infrastructure.Dependencies;

public sealed partial class OptionalDependencyManager
{
    private DependencyPaths GetPaths(OptionalDependencyDefinition definition)
    {
        var installDirectory = Path.Combine(DependenciesRoot, definition.Id);
        return new DependencyPaths(
            DependenciesRoot,
            Path.Combine(InstallerCacheRoot, definition.Id),
            installDirectory);
    }

    private static string? NormalizeCandidatePath(string? value)
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

    private static string NormalizeMatchText(string value)
    {
        var chars = value
            .Trim()
            .Select(static ch => char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : ' ')
            .ToArray();
        return string.Join(" ", new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static bool IsSameDirectory(string left, string right)
    {
        var normalizedLeft = NormalizeCandidatePath(left);
        var normalizedRight = NormalizeCandidatePath(right);
        return !string.IsNullOrWhiteSpace(normalizedLeft)
            && normalizedLeft.Equals(normalizedRight, StringComparison.OrdinalIgnoreCase);
    }

    private static string? ResolveInstallerPath(OptionalDependencyDefinition definition, string installerDirectory)
    {
        if (!Directory.Exists(installerDirectory))
        {
            return null;
        }

        foreach (var pattern in definition.InstallerFilePatterns)
        {
            var match = Directory
                .EnumerateFiles(installerDirectory, pattern, SearchOption.TopDirectoryOnly)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();

            if (match is not null)
            {
                return match;
            }
        }

        var exact = Path.Combine(installerDirectory, definition.InstallerFileName);
        return File.Exists(exact) ? exact : null;
    }

    private static void EnsureTerms(OptionalDependencyDefinition definition, bool acknowledgeExternalTerms)
    {
        if (definition.RequiresExternalTermsAcknowledgement && !acknowledgeExternalTerms)
        {
            throw new InvalidOperationException("继续前需要确认外部厂商条款。");
        }
    }

    private static DependencyDownloadProgress CreateProgress(
        string id,
        long bytesWritten,
        long? totalBytes,
        long startedAt)
    {
        var elapsedSeconds = Stopwatch.GetElapsedTime(startedAt).TotalSeconds;
        var speed = elapsedSeconds <= 0 ? (double?)null : bytesWritten / elapsedSeconds;
        var percent = totalBytes is > 0
            ? Math.Min(100, bytesWritten * 100d / totalBytes.Value)
            : (double?)null;
        return new DependencyDownloadProgress(id, bytesWritten, totalBytes, percent, speed);
    }
}
