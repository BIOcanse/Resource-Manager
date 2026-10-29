using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using ResourceManager.App.Application.Notifications;
using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Paths;

namespace ResourceManager.App.Infrastructure.Notifications;

// The single owner of the notification list (docs/notifications_2026-10-29.md). Business owners post a kind and its
// facts; this owner numbers, retains, persists and publishes them and never knows who listens.
public sealed class NotificationOwner(
    IRuntimePlanProvider plans,
    IHostEnvironment environment,
    RuntimePersistenceCapabilityPolicy persistence,
    TimeProvider time,
    ILogger<NotificationOwner> logger) : INotificationOwner
{
    private const int MaximumSubscribers = 64;
    private static readonly JsonSerializerOptions StoreJsonOptions = new(JsonSerializerDefaults.Web)
        { WriteIndented = true, RespectRequiredConstructorParameters = true };
    private sealed record StoredNotifications(int Version, long NextSequence, IReadOnlyList<NotificationItem> Notifications);
    private readonly object gate = new();
    private readonly HashSet<Channel<NotificationList>> readers = [];
    private NotificationList current = new([]);
    private long nextSequence = 1;
    private bool restored;
    private string StorePath => Path.Combine(PackagePathResolver.ResolvePackageRoot(environment.ContentRootPath),
        "UserData", "Notifications", "notifications.json");

    public NotificationList Read()
    {
        lock (gate)
        {
            RestoreOnce();
            return current;
        }
    }

    public async IAsyncEnumerable<NotificationList> SubscribeAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Every frame is the whole list, so a slow reader only ever needs the newest one.
        var channel = Channel.CreateBounded<NotificationList>(new BoundedChannelOptions(1)
            { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = false });
        lock (gate)
        {
            if (readers.Count == MaximumSubscribers) throw new InvalidOperationException("Notification subscriber capacity exceeded.");
            RestoreOnce();
            channel.Writer.TryWrite(current);
            readers.Add(channel);
        }
        try { await foreach (var list in channel.Reader.ReadAllAsync(cancellationToken)) yield return list; }
        finally { lock (gate) readers.Remove(channel); }
    }

    public NotificationItem Post(string kind, JsonElement facts)
    {
        if (NotificationKinds.ScopeOf(kind) is null)
            throw new ArgumentException("The notification kind is not registered.", nameof(kind));
        if (facts.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Notification facts must be one JSON object.", nameof(facts));
        var capacity = plans.Current.HostManager.Notifications?.RetainedCount
            ?? throw new InvalidOperationException("The notification retained count is unavailable.");
        lock (gate)
        {
            RestoreOnce();
            var item = new NotificationItem(nextSequence, kind, time.GetUtcNow(), facts.Clone());
            var previous = current.Notifications;
            // A new notification beyond the retained count evicts the oldest ones; their events stay in their reports.
            var evicted = Math.Max(0, previous.Count + 1 - capacity);
            var next = new NotificationItem[previous.Count - evicted + 1];
            for (var index = evicted; index < previous.Count; index++) next[index - evicted] = previous[index];
            next[^1] = item;
            nextSequence++;
            try { Save(next); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Delivery does not wait for the disk; the notification still publishes from memory.
                logger.LogError(error, "Notification owner could not write {Path}.", StorePath);
            }
            current = new(next);
            foreach (var reader in readers) reader.Writer.TryWrite(current);
            return item;
        }
    }

    // Caller holds gate.
    private void RestoreOnce()
    {
        if (restored) return;
        restored = true;
        if (!persistence.MutablePersistenceEnabled) return;
        try
        {
            using var stream = WindowsNativeAtomicFileCommitter.OpenCommittedRead(StorePath);
            var stored = JsonSerializer.Deserialize<StoredNotifications>(stream, StoreJsonOptions);
            if (stored?.Version != 1 || stored.Notifications is null
                || stored.Notifications.Count > NotificationOptions.MaximumRetainedCount
                || !ValidList(stored.Notifications, stored.NextSequence))
                throw new InvalidDataException("The stored notifications are incomplete or inconsistent.");
            // A smaller configured retained count trims on the next notification, never on restore.
            current = new(stored.Notifications.ToArray());
            nextSequence = stored.NextSequence;
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { }
        catch (Exception error) when (error is JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            logger.LogError(error, "Notification owner could not restore notifications from {Path}.", StorePath);
        }
    }

    private static bool ValidList(IReadOnlyList<NotificationItem> items, long storedNextSequence)
    {
        var last = 0L;
        foreach (var item in items)
        {
            if (item is null || item.Sequence <= last || string.IsNullOrWhiteSpace(item.Kind)
                || item.OccurredAt == default || item.Facts.ValueKind != JsonValueKind.Object) return false;
            last = item.Sequence;
        }
        return storedNextSequence > last && storedNextSequence < long.MaxValue;
    }

    private void Save(IReadOnlyList<NotificationItem> items)
    {
        if (!persistence.MutablePersistenceEnabled) return;
        var path = StorePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, new StoredNotifications(1, nextSequence, items), StoreJsonOptions);
                stream.Flush(flushToDisk: true);
            }
            WindowsNativeAtomicFileCommitter.CommitReplace(temporaryPath, path);
        }
        finally { WindowsNativeAtomicFileCommitter.DeleteExact(temporaryPath); }
    }
}
