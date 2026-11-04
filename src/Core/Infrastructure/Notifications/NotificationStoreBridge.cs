using System.Collections;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using ResourceManager.App.Application.Notifications;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.Notifications;

public sealed partial class NotificationOwner
{
    // Codec and committed-file leaves only. Native owns whether/when to invoke
    // these and whether a decoded store may replace its state. P-file-commit removes
    // the atomic-file leaf; H3 removes the CLR codec with the last typed callers.
    private static readonly JsonSerializerOptions StoreJsonOptions = new(JsonSerializerDefaults.Web)
        { WriteIndented = true, RespectRequiredConstructorParameters = true };
    private sealed record StoredNotifications(int Version, long NextSequence, IReadOnlyList<NotificationItem> Notifications);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int Load(nint context, NativeNotificationMethods.Store* store)
    {
        var call = Borrow(context);
        try
        {
            using var stream = WindowsNativeAtomicFileCommitter.OpenCommittedRead(call.Owner.storePath);
            call.Loaded = JsonSerializer.Deserialize<StoredNotifications>(stream, StoreJsonOptions);
            if (call.Loaded?.Notifications is null) return 2;
            *store = new() { Version = call.Loaded.Version, NextSequence = call.Loaded.NextSequence,
                Count = checked((nuint)call.Loaded.Notifications.Count) };
            return 0;
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return 1; }
        catch (Exception error) when (error is JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
        { call.StoreFailure = error; return 2; }
        catch (Exception error) { call.Failure = ExceptionDispatchInfo.Capture(error); return 7; }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nint LoadedItem(nint context, nuint index)
    {
        var call = Borrow(context);
        try
        {
            var item = call.Loaded!.Notifications[checked((int)index)];
            return item is null ? 0 : GCHandle.ToIntPtr(GCHandle.Alloc(item));
        }
        catch (Exception error) { call.Failure = ExceptionDispatchInfo.Capture(error); return 0; }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void LoadedEnd(nint context) => Borrow(context).Loaded = null;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int Save(nint context, long nextSequence, nint* items, nuint count, nint appended)
    {
        var call = Borrow(context);
        var path = call.Owner.storePath;
        var temporaryPath = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            try
            {
                using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(stream, new StoredNotifications(1, nextSequence,
                        new BorrowedItems(items, checked((int)count), appended)), StoreJsonOptions);
                    stream.Flush(flushToDisk: true);
                }
                WindowsNativeAtomicFileCommitter.CommitReplace(temporaryPath, path);
            }
            finally { WindowsNativeAtomicFileCommitter.DeleteExact(temporaryPath); }
            return 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { call.StoreFailure = error; return 1; }
        catch (Exception error) { call.Failure = ExceptionDispatchInfo.Capture(error); return 7; }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int LogError(nint context, uint writing)
    {
        var call = Borrow(context);
        try
        {
            call.Owner.logger.LogError(call.StoreFailure ?? new InvalidDataException("The stored notifications are incomplete or inconsistent."),
                writing != 0 ? "Notification owner could not write {Path}." : "Notification owner could not restore notifications from {Path}.", call.Owner.storePath);
            call.StoreFailure = null;
            return 0;
        }
        catch (Exception error) { call.Failure = ExceptionDispatchInfo.Capture(error); return 7; }
    }

    // This synchronous view exists only inside the save callback under the
    // native lock. Serialization borrows original payloads; no list graph copy.
    private sealed unsafe class BorrowedItems(nint* items, int kept, nint appended) : IReadOnlyList<NotificationItem>
    {
        public int Count => kept + 1;
        public NotificationItem this[int index] => (uint)index < (uint)Count
            ? Payload(index == kept ? appended : items[index]) : throw new ArgumentOutOfRangeException(nameof(index));
        public IEnumerator<NotificationItem> GetEnumerator() => new Enumerator(this);
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        private sealed class Enumerator(BorrowedItems owner) : IEnumerator<NotificationItem>
        {
            private int index = -1;
            public NotificationItem Current => owner[index];
            object IEnumerator.Current => Current;
            public bool MoveNext() => ++index < owner.Count;
            public void Reset() => index = -1;
            public void Dispose() { }
        }
    }
}
