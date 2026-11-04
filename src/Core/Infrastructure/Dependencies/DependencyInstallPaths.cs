namespace ResourceManager.App.Infrastructure.Dependencies;

internal static class DependencyInstallPaths
{
    internal const int MaximumEntries = 100_000;

    internal static bool IsRegularFile(string path) => NativeDependencyProbe.IsRegularFile(path);

    internal static void RejectLinks(string path) => NativeDependencyProbe.RejectLinks(path);

    internal static string Relative(string name)
    {
        var normalized = name.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(normalized) || normalized.StartsWith('/') || normalized.Contains(':')
            || normalized.Split('/').Any(part => part is "" or "." or ".."
                || part.EndsWith('.') || part.EndsWith(' ') || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new InvalidDataException($"Invalid component archive path: {name}");
        return normalized;
    }

    internal static string InRoot(string root, string relative)
    {
        var directory = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(Path.Combine(root, Relative(relative)));
        if (!target.StartsWith(directory, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Component path escapes its root.");
        return target;
    }

    // Only staging/backup directories created by the payload writer use this bounded cleanup.
    internal static void ValidateTree(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        var entries = 0;
        while (pending.TryPop(out var directory))
        {
            RejectLinks(directory);
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                if (++entries > MaximumEntries) throw new IOException("Component path validation exceeded its entry bound.");
                RejectLinks(path);
                if (Directory.Exists(path)) pending.Push(path);
            }
        }
    }

    internal static void DeleteOwnedTree(string root)
    {
        if (!Directory.Exists(root)) return;
        RejectLinks(root);
        var pending = new Stack<string>();
        var directories = new List<string>();
        pending.Push(root);
        var entries = 0;
        while (pending.TryPop(out var directory))
        {
            RejectLinks(directory);
            directories.Add(directory);
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                if (++entries > MaximumEntries) throw new IOException("Component cleanup exceeded its entry bound.");
                RejectLinks(path);
                if (Directory.Exists(path)) pending.Push(path);
                else File.Delete(path);
            }
        }
        for (var index = directories.Count - 1; index >= 0; index--) Directory.Delete(directories[index]);
    }
}
