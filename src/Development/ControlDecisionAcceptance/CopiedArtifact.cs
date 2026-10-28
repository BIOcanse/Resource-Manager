using System.Security.Cryptography;
using System.Text.Json;

namespace ResourceManager.ControlDecisionAcceptance;

internal sealed record ArtifactFile(string Path, long Length, string Sha256);
internal sealed record ArtifactManifest(string Contract, string SourceManifestSha256, IReadOnlyList<ArtifactFile> Files);

internal static class CopiedArtifact
{
    internal static ArtifactManifest Verify(string manifestPath, string sourceManifestPath)
    {
        var manifest = JsonSerializer.Deserialize<ArtifactManifest>(File.ReadAllText(manifestPath),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new InvalidDataException("Missing artifact manifest.");
        if (manifest.Contract != "resource-manager-control-decision-copy-v1"
            || manifest.Files.Count is < 3 or > 10000
            || !Hash(sourceManifestPath).Equals(manifest.SourceManifestSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The copied output has no accepted source manifest binding.");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            if (Path.IsPathFullyQualified(file.Path) || !seen.Add(file.Path))
                throw new InvalidDataException("Artifact paths must be unique and relative.");
            var absolute = Path.GetFullPath(Path.Combine(root, file.Path));
            if (!absolute.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Artifact path escaped the copied image.");
            var info = new FileInfo(absolute);
            if (!info.Exists || info.Length != file.Length || !Hash(absolute).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Copied artifact differs: {file.Path}");
        }
        foreach (var required in new[] { "ResourceManager.ControlDecisionAcceptance.dll", "ResourceManager.dll", "ResourceManager.NativeCore.dll", "ResourceManager.Ledger.Native.dll" })
            if (!seen.Contains(required)) throw new InvalidDataException($"Manifest omitted required artifact {required}.");
        // No auxiliary effect process can be selected by a default AppContext-based resolver.
        foreach (var path in EnumerateFiles(root))
        {
            var relative = Path.GetRelativePath(root, path);
            if (!seen.Contains(relative) && !Path.GetFullPath(path).Equals(Path.GetFullPath(manifestPath), StringComparison.OrdinalIgnoreCase)
                && !Path.GetFullPath(path).Equals(Path.GetFullPath(sourceManifestPath), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Unmanifested image file: {relative}");
            if (Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase)
                || relative.StartsWith("Dependencies" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || relative.StartsWith("GpuPlacementShim" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || relative.StartsWith("wwwroot" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Effect helper/frontend file forbidden in probe image: {relative}");
        }
        return manifest;
    }

    internal static IEnumerable<string> EnumerateFiles(string root)
    {
        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((root, 0));
        var nodes = 0;
        while (queue.TryDequeue(out var item))
        {
            if (++nodes > 12000 || item.Depth > 16) throw new InvalidDataException("Artifact tree exceeded its bounds.");
            if ((File.GetAttributes(item.Path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Artifact links are forbidden.");
            foreach (var file in Directory.EnumerateFiles(item.Path))
            {
                if (++nodes > 12000 || (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Artifact files exceeded their bounds or contain a link.");
                yield return file;
            }
            foreach (var child in Directory.EnumerateDirectories(item.Path)) queue.Enqueue((child, item.Depth + 1));
        }
    }
    internal static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}

