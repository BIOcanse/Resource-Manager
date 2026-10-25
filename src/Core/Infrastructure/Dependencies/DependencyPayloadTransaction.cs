using System.IO.Compression;

namespace ResourceManager.App.Infrastructure.Dependencies;

/// <summary>Prepare immutable new files before the live root is touched. A release publishes by directory rename;
/// a same-release repair publishes only complete missing files and preserves the existing record.</summary>
internal sealed class DependencyPayloadTransaction : IDisposable
{
    private readonly string live;
    private readonly string stage;
    private readonly bool replaceRelease;
    private readonly ILogger? logger;
    private readonly List<string> selected = [];
    private bool prepared;

    internal DependencyPayloadTransaction(string live, bool replaceRelease, ILogger? logger = null)
    {
        this.live = Path.GetFullPath(live);
        this.replaceRelease = replaceRelease;
        this.logger = logger;
        DependencyInstallPaths.RejectLinks(this.live);
        if (!replaceRelease && Directory.Exists(this.live)) DependencyInstallPaths.ValidateTree(this.live);
        var parent = Path.GetDirectoryName(this.live)!;
        DependencyInstallPaths.RejectLinks(parent);
        Directory.CreateDirectory(parent);
        stage = Path.Combine(parent, "." + Path.GetFileName(live) + ".stage-" + Guid.NewGuid().ToString("N"));
        if (Directory.Exists(stage) || File.Exists(stage)) throw new IOException("Component staging path already exists.");
        Directory.CreateDirectory(stage);
        DependencyInstallPaths.RejectLinks(stage);
    }

    internal void Prepare(ZipArchive archive, IReadOnlyList<string> declared, IReadOnlyList<string> wanted,
        bool includeContents, DependencyReleaseRecord? record, CancellationToken token, Func<string, Stream>? openStagedFile = null)
    {
        if (prepared) throw new InvalidOperationException("Component payload was already prepared.");
        if (archive.Entries.Count > DependencyInstallPaths.MaximumEntries) throw new InvalidDataException("Component archive has too many entries.");
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            var directory = entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');
            var name = DependencyInstallPaths.Relative(directory ? entry.FullName[..^1] : entry.FullName);
            if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                throw new InvalidDataException("Component archives cannot contain symbolic links.");
            if (!directory && !entries.TryAdd(name, entry)) throw new InvalidDataException($"Duplicate component archive path: {name}");
        }
        var required = (replaceRelease ? declared : wanted).Select(DependencyInstallPaths.Relative).ToArray();
        foreach (var name in required)
            if (!entries.ContainsKey(name)) throw new InvalidDataException($"The selected archive does not provide the declared path {name}.");
        var chosen = replaceRelease && includeContents ? entries.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase)
            : required.ToHashSet(StringComparer.OrdinalIgnoreCase);
        chosen.Remove(DependencyReleaseRecordStore.FileName);
        if (replaceRelease) CopyPreservedFiles(chosen, token, openStagedFile);
        foreach (var name in chosen)
        {
            token.ThrowIfCancellationRequested();
            var destination = DependencyInstallPaths.InRoot(stage, name);
            DependencyInstallPaths.RejectLinks(destination);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            DependencyInstallPaths.RejectLinks(destination);
            using var source = entries[name].Open();
            using (var target = Open(destination, openStagedFile))
            {
                source.CopyTo(target);
                target.Flush();
                if (target is FileStream file) file.Flush(flushToDisk: true);
            }
            selected.Add(name);
        }
        foreach (var name in required)
            if (!File.Exists(DependencyInstallPaths.InRoot(stage, name))) throw new InvalidDataException($"Staged payload is missing {name}.");
        if (replaceRelease && record is not null)
            DependencyReleaseRecordStore.Write(DependencyReleaseRecordStore.InstalledPath(stage), record);
        prepared = true;
    }

    private void CopyPreservedFiles(HashSet<string> replaced, CancellationToken token, Func<string, Stream>? openStagedFile)
    {
        if (!Directory.Exists(live)) return;
        var pending = new Stack<string>();
        pending.Push(live);
        var count = 0;
        while (pending.TryPop(out var directory))
        {
            DependencyInstallPaths.RejectLinks(directory);
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                token.ThrowIfCancellationRequested();
                if (++count > DependencyInstallPaths.MaximumEntries) throw new IOException("Component installation exceeded its entry bound.");
                DependencyInstallPaths.RejectLinks(path);
                if (Directory.Exists(path))
                {
                    Directory.CreateDirectory(DependencyInstallPaths.InRoot(stage, Path.GetRelativePath(live, path)));
                    pending.Push(path);
                    continue;
                }
                var name = DependencyInstallPaths.Relative(Path.GetRelativePath(live, path));
                if (name == DependencyReleaseRecordStore.FileName || replaced.Contains(name)) continue;
                var destination = DependencyInstallPaths.InRoot(stage, name);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                using var source = File.OpenRead(path);
                using var target = Open(destination, openStagedFile);
                source.CopyTo(target);
                target.Flush();
                if (target is FileStream file) file.Flush(flushToDisk: true);
            }
        }
    }

    private static Stream Open(string path, Func<string, Stream>? openStagedFile)
        => openStagedFile is null ? new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)
            : openStagedFile(path);

    internal void Publish(CancellationToken token)
    {
        if (!prepared) throw new InvalidOperationException("Component payload was not prepared.");
        token.ThrowIfCancellationRequested();
        DependencyInstallPaths.RejectLinks(live);
        DependencyInstallPaths.RejectLinks(stage);
        if (!replaceRelease) { PublishMissingFiles(); return; }
        var backup = live + ".previous-" + Guid.NewGuid().ToString("N");
        DependencyInstallPaths.RejectLinks(backup);
        var hadLive = Directory.Exists(live);
        if (hadLive) Directory.Move(live, backup);
        try { Directory.Move(stage, live); }
        catch (Exception publishFailure)
        {
            // Never delete the old root if rollback cannot complete.
            if (hadLive)
            {
                try { Directory.Move(backup, live); }
                catch (Exception rollbackFailure)
                {
                    logger?.LogError(rollbackFailure, "Component rollback could not restore the previous root retained at {BackupPath}", backup);
                    throw new AggregateException($"The previous component payload is retained at {backup}.", publishFailure, rollbackFailure);
                }
            }
            throw;
        }
        if (hadLive)
        {
            try { DependencyInstallPaths.DeleteOwnedTree(backup); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { logger?.LogWarning(error, "Component replacement succeeded; old payload backup remains at {BackupPath}", backup); }
        }
    }

    private void PublishMissingFiles()
    {
        var moved = new List<string>();
        try
        {
            foreach (var name in selected)
            {
                var destination = DependencyInstallPaths.InRoot(live, name);
                DependencyInstallPaths.RejectLinks(destination);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                DependencyInstallPaths.RejectLinks(destination);
                File.Move(DependencyInstallPaths.InRoot(stage, name), destination, overwrite: false);
                moved.Add(name);
            }
        }
        catch
        {
            for (var index = moved.Count - 1; index >= 0; index--)
                File.Move(DependencyInstallPaths.InRoot(live, moved[index]), DependencyInstallPaths.InRoot(stage, moved[index]));
            throw;
        }
    }

    public void Dispose()
    {
        try { DependencyInstallPaths.DeleteOwnedTree(stage); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { logger?.LogWarning(error, "Component staging cleanup could not finish: {StagePath}", stage); }
    }
}
