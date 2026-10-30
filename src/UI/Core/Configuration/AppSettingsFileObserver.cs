using System.Text.Json;

namespace ResourceManager.NativeUi.Configuration;

/// <summary>One read of app-settings.json shared by every native consumer of that read.</summary>
internal sealed class AppSettingsFileRead
{
    private AppSettingsFileRead(AppSettingsFileReadState state, JsonElement root)
    {
        State = state;
        Root = root;
    }

    internal AppSettingsFileReadState State { get; }

    /// <summary>The document root; only meaningful when <see cref="State"/> is Document.</summary>
    internal JsonElement Root { get; }

    internal static AppSettingsFileRead Missing { get; } = new(AppSettingsFileReadState.Missing, default);
    internal static AppSettingsFileRead Unreadable { get; } = new(AppSettingsFileReadState.Unreadable, default);
    internal static AppSettingsFileRead Document(JsonElement root) => new(AppSettingsFileReadState.Document, root);
}

internal enum AppSettingsFileReadState { Missing, Unreadable, Document }

/// <summary>
/// Reads app-settings.json once at start, then again only when the file changes (one
/// FileSystemWatcher on its directory, shared by all consumers) or when a caller asks.
/// Every event that arrives before the queued read runs is merged into that one read, and
/// a read whose file version (last write time, length) is unchanged is skipped. Consumers
/// run on the thread <c>post</c> dispatches to and must not keep <see cref="AppSettingsFileRead.Root"/>.
/// </summary>
internal sealed class AppSettingsFileObserver : IDisposable
{
    private readonly string settingsPath;
    private readonly Action<Action> post;
    private readonly Func<string, Stream> open;
    private readonly List<Action<AppSettingsFileRead>> consumers = [];
    private FileSystemWatcher? watcher;
    private (long Ticks, long Length)? readVersion;
    private bool readOnce;
    private int pending;
    private int forcePending;
    private bool disposed;

    internal AppSettingsFileObserver(string settingsPath, Action<Action> post, Func<string, Stream>? open = null)
    {
        this.settingsPath = Path.GetFullPath(settingsPath);
        this.post = post;
        this.open = open ?? SettingsFileReader.Open;
    }

    /// <summary>Number of times the file content was read; tests use it to prove that no read happened.</summary>
    internal int ReadCount { get; private set; }

    internal void Subscribe(Action<AppSettingsFileRead> consumer)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        consumers.Add(consumer);
    }

    /// <summary>Reads once for all consumers, then watches the file.</summary>
    internal void Start()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ReadIfChanged(force: true);
        if (watcher is not null) return;
        var directory = Path.GetDirectoryName(settingsPath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return;
        try
        {
            watcher = new FileSystemWatcher(directory, Path.GetFileName(settingsPath))
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
                InternalBufferSize = 4096
            };
            watcher.Changed += OnFileEvent;
            watcher.Created += OnFileEvent;
            watcher.Deleted += OnFileEvent;
            watcher.Renamed += OnFileEvent;
            // An overflowed buffer may have lost the save itself: read once to be sure.
            watcher.Error += (_, _) => RequestRead(force: true);
            watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or PlatformNotSupportedException)
        {
            watcher?.Dispose();
            watcher = null;
            System.Diagnostics.Trace.WriteLine(ex);
        }
    }

    /// <summary>
    /// Queues one read on the consumer thread. Requests that arrive before it runs are merged.
    /// <paramref name="force"/> reads even when the file version is unchanged (an explicit reload).
    /// </summary>
    internal void RequestRead(bool force = false)
    {
        if (force) Volatile.Write(ref forcePending, 1);
        if (Volatile.Read(ref disposed) || Interlocked.Exchange(ref pending, 1) != 0) return;
        try
        {
            post(() =>
            {
                Volatile.Write(ref pending, 0);
                ReadIfChanged(Interlocked.Exchange(ref forcePending, 0) != 0);
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // The consumer thread is gone; nothing is left to apply the read to.
            Volatile.Write(ref pending, 0);
        }
    }

    // Runs on the consumer thread.
    [System.ComponentModel.Description("RuntimeRead: EventDriven(\"app-settings.json changed or reload requested\")")]
    internal void ReadIfChanged(bool force)
    {
        if (disposed) return;
        var file = new FileInfo(settingsPath);
        (long Ticks, long Length)? version = file.Exists ? (file.LastWriteTimeUtc.Ticks, file.Length) : null;
        if (!force && readOnce && version == readVersion) return;
        readOnce = true;
        readVersion = version;
        if (version is null)
        {
            Publish(AppSettingsFileRead.Missing);
            return;
        }

        JsonDocument? document = null;
        try
        {
            ReadCount++;
            using var stream = open(settingsPath);
            document = JsonDocument.Parse(stream);
        }
        catch (FileNotFoundException)
        {
            readVersion = null;
            Publish(AppSettingsFileRead.Missing);
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A half-replaced or locked file: the next change event reads it again.
            readVersion = null;
            Publish(AppSettingsFileRead.Unreadable);
            return;
        }

        using (document)
            Publish(AppSettingsFileRead.Document(document.RootElement));
    }

    public void Dispose()
    {
        if (disposed) return;
        Volatile.Write(ref disposed, true);
        watcher?.Dispose();
        watcher = null;
        consumers.Clear();
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        if (e is RenamedEventArgs renamed
            && !IsSettingsPath(renamed.FullPath) && !IsSettingsPath(renamed.OldFullPath)) return;
        if (e is not RenamedEventArgs && !IsSettingsPath(e.FullPath)) return;
        RequestRead();
    }

    private bool IsSettingsPath(string? path) =>
        path is not null && string.Equals(Path.GetFullPath(path), settingsPath, StringComparison.OrdinalIgnoreCase);

    private void Publish(AppSettingsFileRead read)
    {
        foreach (var consumer in consumers.ToArray())
        {
            try
            {
                consumer(read);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine(ex);
            }
        }
    }
}
