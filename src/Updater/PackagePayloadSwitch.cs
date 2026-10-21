using ResourceManager.Shared.Packages;

namespace ResourceManager.Updater;

/// <summary>Moves product contents while the root and its running maintenance directory stay in place.</summary>
internal static class PackagePayloadSwitch
{
    internal static void Move(string source, string destination)
    {
        if (!Directory.Exists(source)) return;
        ReleasePackageLayout.RejectReparse(source);
        Directory.CreateDirectory(destination);
        ReleasePackageLayout.RejectReparse(destination);
        foreach (var relative in Members(source))
        {
            var from = Path.Combine(source, relative);
            var to = Path.Combine(destination, relative);
            if (File.Exists(to) || Directory.Exists(to)) throw new IOException("Payload destination is occupied: " + to);
            var parent = Path.GetDirectoryName(to)!;
            if (Directory.Exists(parent)) ReleasePackageLayout.RejectReparse(parent);
            Directory.CreateDirectory(parent);
            ReleasePackageLayout.RejectReparse(parent);
            ReleasePackageLayout.RejectReparse(from);
            if (Directory.Exists(from)) Directory.Move(from, to);
            else File.Move(from, to);
        }
    }

    private static IReadOnlyList<string> BoundedEntries(string directory)
    {
        var entries = new List<string>();
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            if (entries.Count >= 2000) throw new IOException("Payload entry count exceeds the limit.");
            entries.Add(entry);
        }
        entries.Sort(StringComparer.OrdinalIgnoreCase);
        return entries;
    }

    private static IReadOnlyList<string> Members(string root)
    {
        var result = new List<string>();
        foreach (var entry in BoundedEntries(root))
        {
            if (result.Count >= 2000) throw new IOException("Payload entry count exceeds the limit.");
            ReleasePackageLayout.RejectReparse(entry);
            var name = Path.GetFileName(entry);
            if (!name.Equals("Internal", StringComparison.OrdinalIgnoreCase)) result.Add(name);
            else
            {
                if (!Directory.Exists(entry)) throw new InvalidDataException("Internal must be a directory.");
                foreach (var child in BoundedEntries(entry))
                {
                    if (Path.GetFileName(child).Equals("Management", StringComparison.OrdinalIgnoreCase)) continue;
                    if (result.Count >= 2000) throw new IOException("Payload entry count exceeds the limit.");
                    ReleasePackageLayout.RejectReparse(child);
                    result.Add(Path.Combine("Internal", Path.GetFileName(child)));
                }
            }
        }
        return result;
    }

    internal static void Restore(string root, string workspace, string stage, Action<string> writeStage)
    {
        var previous = Path.Combine(workspace, "previous");
        if (stage is "nextPromoting" or "nextPromoted" or "removingNew")
        {
            writeStage("removingNew");
            Move(root, Path.Combine(workspace, "failed"));
        }
        // Once restoration has begun, a retry only moves remaining previous items.
        // It must never move already restored old items into the failed copy.
        writeStage("restoringPrevious");
        if (Directory.Exists(previous)) Move(previous, root);
        else if (!Directory.Exists(root)) throw new DirectoryNotFoundException("The previous installation is missing: " + root);
    }
}
