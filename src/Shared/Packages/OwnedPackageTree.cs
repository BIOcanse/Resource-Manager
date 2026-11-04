namespace ResourceManager.Shared.Packages;

/// <summary>Finite, non-following traversal used before removing an explicitly owned tree.</summary>
public static class OwnedPackageTree
{
    public static IReadOnlyList<string> Read(string root)
    {
        root = Path.GetFullPath(root);
        var entries = new List<string>();
        var pending = new Queue<(string Path, int Depth)>();
        pending.Enqueue((root, 0));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (pending.TryDequeue(out var item))
        {
            if (item.Depth > 64 || !seen.Add(item.Path) || entries.Count >= 100_000)
                throw new IOException("Package tree exceeds its traversal bound.");
            ReleasePackageLayout.RejectReparse(item.Path);
            entries.Add(item.Path);
            if (!Directory.Exists(item.Path)) continue;
            foreach (var child in Directory.EnumerateFileSystemEntries(item.Path))
                pending.Enqueue((child, item.Depth + 1));
        }
        return entries;
    }

    public static void Delete(string root, string? keepFile = null, string? keepDirectory = null)
    {
        root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        keepFile = keepFile is null ? null : Path.GetFullPath(keepFile);
        keepDirectory = keepDirectory is null ? null : Path.GetFullPath(keepDirectory).TrimEnd(Path.DirectorySeparatorChar);
        foreach (var keep in new[] { keepFile, keepDirectory })
            if (keep is not null && !keep.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The retained path must be inside the owned root.");
        var entries = Read(root); // Complete preflight before deleting anything.
        foreach (var path in entries.Reverse())
        {
            if (keepDirectory is not null && (string.Equals(path, keepDirectory, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(keepDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))) continue;
            if (keepFile is not null && string.Equals(path, keepFile, StringComparison.OrdinalIgnoreCase)) continue;
            ReleasePackageLayout.RejectReparse(path);
            if (Directory.Exists(path))
            {
                // Preserve ancestors of each retained member.
                var prefix = path.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if ((keepFile is not null && keepFile.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    || (keepDirectory is not null && keepDirectory.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))) continue;
                Directory.Delete(path, recursive: false);
            }
            else File.Delete(path);
        }
    }
}
