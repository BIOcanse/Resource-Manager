namespace ResourceManager.App.Infrastructure.Migration;
public sealed partial class SoftwareDataDiscoveryManager
{
    private sealed partial class DiscoverySessionState
    {
        private void StartDirectoryWatchers()
        {
            foreach (var root in roots.Where(static item => Directory.Exists(item.Path)))
            {
                var watcher = new FileSystemWatcher(root.Path)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.DirectoryName
                        | NotifyFilters.FileName
                        | NotifyFilters.LastWrite
                        | NotifyFilters.CreationTime
                        | NotifyFilters.Size
                };

                watcher.Created += (_, args) => ObserveFallback(root, args.FullPath, "Created");
                watcher.Changed += (_, args) => ObserveFallback(root, args.FullPath, "Changed");
                watcher.Renamed += (_, args) => ObserveFallback(root, args.FullPath, "Renamed");
                watcher.Error += (_, _) =>
                {
                    ProviderMessage = "目录监控缓冲区可能溢出；候选仍可作为参考。";
                    Message = ProviderMessage;
                };
                watcher.EnableRaisingEvents = true;
                watchers.Add(watcher);
            }
        }
        private void ObserveFallback(DiscoveryRoot root, string changedPath, string eventType)
        {
            if (State != "Running")
            {
                return;
            }

            ObservePath("DirectoryWatcher", eventType, changedPath, 0, "unknown", null, DateTimeOffset.Now, root);
        }

        private void ObservePath(
            string provider,
            string eventType,
            string changedPath,
            int processId,
            string processName,
            long? sizeBytes,
            DateTimeOffset observedAt,
            DiscoveryRoot? knownRoot = null)
        {
            var root = knownRoot ?? roots.FirstOrDefault(item => IsUnder(changedPath, item.Path));
            if (root is null)
            {
                return;
            }

            var candidatePath = GetCandidateDirectory(root.Path, changedPath);
            if (candidatePath is null)
            {
                return;
            }

            var candidate = candidates.GetOrAdd(candidatePath, path => new CandidateState(path, root.Path, root.TargetCategory, ProcessNames.Count > 0 || ProgramRootPaths.Count > 0));
            candidate.Touch(provider, eventType, changedPath, processId, NormalizeProcessName(processName), sizeBytes, observedAt);
            Interlocked.Increment(ref observedWriteCount);
        }

        private static string? GetCandidateDirectory(string root, string changedPath)
        {
            var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var fullPath = Path.GetFullPath(changedPath);
            if (!fullPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var relative = Path.GetRelativePath(normalizedRoot, fullPath);
            var firstSegment = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(firstSegment) || firstSegment == "." || firstSegment == "..")
            {
                return null;
            }

            if (IgnoredTopLevelSegments.Contains(firstSegment, StringComparer.OrdinalIgnoreCase))
            {
                return null;
            }

            return Path.Combine(normalizedRoot, firstSegment);
        }

        private static bool IsUnder(string path, string candidateRoot)
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(candidateRoot))
            {
                return false;
            }

            try
            {
                var normalizedPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var normalizedRoot = Path.GetFullPath(candidateRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                return normalizedPath.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase)
                    || normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
    }
}
