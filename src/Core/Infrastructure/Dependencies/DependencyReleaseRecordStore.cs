using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using ResourceManager.App.Domain.Dependencies;

namespace ResourceManager.App.Infrastructure.Dependencies;

internal sealed record DependencyReleaseRecord(string Tag, string AssetName, string AssetSha256);

[JsonSerializable(typeof(DependencyReleaseRecord))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal partial class DependencyReleaseJsonContext : JsonSerializerContext;

/// <summary>Release provenance for the bytes written by the component's existing installer/payload owner.</summary>
internal static class DependencyReleaseRecordStore
{
    internal const string FileName = ".component-release.json";
    internal static string InstalledPath(string directory) => Path.Combine(directory, FileName);
    internal static string DownloadPath(string installer) => installer + ".release.json";

    internal static DependencyReleaseRecord? Read(string path, ILogger? logger = null)
    {
        if (!File.Exists(path)) return null;
        try
        {
            DependencyInstallPaths.RejectLinks(path);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            var record = JsonSerializer.Deserialize(stream, DependencyReleaseJsonContext.Default.DependencyReleaseRecord);
            if (record is null || string.IsNullOrWhiteSpace(record.Tag) || string.IsNullOrWhiteSpace(record.AssetName)
                || record.AssetSha256 is not { Length: 64 } || !record.AssetSha256.All(char.IsAsciiHexDigit))
                throw new InvalidDataException("Invalid component release record.");
            return record;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        {
            logger?.LogWarning(error, "Component release record is unavailable or invalid: {RecordPath}", path);
            return null;
        }
    }

    internal static bool MatchesPin(DependencyReleaseRecord? record, OptionalDependencyDefinition definition)
        => definition.PayloadSource is { } payload
            ? Matches(record, payload.VerifiedTag, payload.VerifiedAssetName)
            : definition.ReleaseSource is { } release
                && Matches(record, release.VerifiedTag, release.VerifiedAssetName);

    internal static bool Matches(DependencyReleaseRecord? record, string tag, string asset)
        => record is not null && record.Tag == tag && record.AssetName == asset;

    internal static void Write(string path, DependencyReleaseRecord record)
    {
        DependencyInstallPaths.RejectLinks(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        DependencyInstallPaths.RejectLinks(path);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, record, DependencyReleaseJsonContext.Default.DependencyReleaseRecord);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static string Hash(string path)
    {
        DependencyInstallPaths.RejectLinks(path);
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
