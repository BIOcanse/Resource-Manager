using ResourceManager.Shared.Packages;

namespace ResourceManager.Updater;

/// <summary>Moves product contents while the root and its running maintenance directory stay in place.</summary>
internal static class PackagePayloadSwitch
{
    internal static void Move(string source, string destination, bool preserveAuthority = false)
    {
        if (!Directory.Exists(source)) return;
        ReleasePackageLayout.RejectReparse(source);
        Directory.CreateDirectory(destination);
        ReleasePackageLayout.RejectReparse(destination);
        foreach (var relative in Members(source, preserveAuthority))
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

    private static IReadOnlyList<string> Members(string root, bool preserveAuthority)
    {
        var result = new List<string>();
        foreach (var entry in BoundedEntries(root))
        {
            if (result.Count >= 2000) throw new IOException("Payload entry count exceeds the limit.");
            ReleasePackageLayout.RejectReparse(entry);
            var name = Path.GetFileName(entry);
            // Runtime authority is bound to the stable root and service SID.
            // Moving/copying it as payload changes ownership and breaks its ACL.
            if (preserveAuthority && name.Equals(".resource-manager-validation-authority", StringComparison.OrdinalIgnoreCase)) continue;
            if (preserveAuthority && name.Equals("UserData", StringComparison.OrdinalIgnoreCase))
            {
                if (!Directory.Exists(entry)) throw new InvalidDataException("UserData must be a directory.");
                foreach (var child in BoundedEntries(entry))
                {
                    ReleasePackageLayout.RejectReparse(child);
                    if (Path.GetFileName(child).Equals("HostManager", StringComparison.OrdinalIgnoreCase)) continue;
                    if (result.Count >= 2000) throw new IOException("Payload entry count exceeds the limit.");
                    result.Add(Path.Combine(name, Path.GetFileName(child)));
                }
            }
            else if (!name.Equals("Internal", StringComparison.OrdinalIgnoreCase)) result.Add(name);
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

    internal static void Restore(string root, string workspace, string stage, Action<string> writeStage, bool preserveAuthority = false)
    {
        var previous = Path.Combine(workspace, "previous");
        if (stage is "nextPromoting" or "nextPromoted" or "removingNew")
        {
            writeStage("removingNew");
            Move(root, Path.Combine(workspace, "failed"), preserveAuthority);
        }
        // Once restoration has begun, a retry only moves remaining previous items.
        // It must never move already restored old items into the failed copy.
        writeStage("restoringPrevious");
        if (Directory.Exists(previous)) Move(previous, root, preserveAuthority);
        else if (!Directory.Exists(root)) throw new DirectoryNotFoundException("The previous installation is missing: " + root);
    }
}
