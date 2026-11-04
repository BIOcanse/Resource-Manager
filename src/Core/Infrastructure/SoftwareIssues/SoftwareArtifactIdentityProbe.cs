using System.Diagnostics;
using System.Security.Cryptography;

namespace ResourceManager.App.Infrastructure.SoftwareIssues;

internal interface ISoftwareArtifactIdentityProbe
{
    ValueTask<SoftwareArtifactIdentity?> ReadAsync(
        string path,
        CancellationToken cancellationToken);
}

internal sealed record SoftwareArtifactIdentity(
    string Path,
    long Length,
    string Sha256,
    string? FileVersion);

internal sealed class SoftwareArtifactIdentityProbe : ISoftwareArtifactIdentityProbe
{
    public async ValueTask<SoftwareArtifactIdentity?> ReadAsync(
        string path,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedPath = SoftwareIssueArtifactPath.Normalize(path);
        if (normalizedPath is null || !File.Exists(normalizedPath))
        {
            return null;
        }

        try
        {
            await using var stream = OpenForExactIdentityRead(normalizedPath);
            var length = stream.Length;
            var sha256 = Convert.ToHexString(
                await SHA256.HashDataAsync(stream, cancellationToken));
            var version = FileVersionInfo.GetVersionInfo(normalizedPath).FileVersion;
            return new SoftwareArtifactIdentity(
                normalizedPath,
                length,
                sha256,
                string.IsNullOrWhiteSpace(version) ? null : version.Trim());
        }
        catch (Exception ex) when (ex is FileNotFoundException
            or DirectoryNotFoundException
            or IOException
            or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static FileStream OpenForExactIdentityRead(string normalizedPath)
        => new(
            normalizedPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
}

internal static class SoftwareIssueArtifactPath
{
    internal static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(
                value.Trim().Trim('"'));
            if (!Path.IsPathFullyQualified(expanded))
            {
                return null;
            }

            return Path.GetFullPath(expanded)
                .TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            return null;
        }
    }
}
