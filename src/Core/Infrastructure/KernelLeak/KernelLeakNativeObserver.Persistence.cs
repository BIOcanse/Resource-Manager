using System.Text.Json;
using ResourceManager.App.Domain.KernelLeak;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Paths;

namespace ResourceManager.App.Infrastructure.KernelLeak;

public sealed partial class KernelLeakObserver
{
    // Z-codec/kernel-leak removes this format-only CLR codec; P-file uses the
    // existing native atomic file owner. No report decisions are made here.
    private static readonly JsonSerializerOptions ItemJsonOptions = new(JsonSerializerDefaults.Web)
        { WriteIndented = true, RespectRequiredConstructorParameters = true };
    private sealed record StoredItems(int Version, IReadOnlyList<KernelLeakSuspect> Items);
    private bool itemsRestored;
    private string ItemsPath => Path.Combine(PackagePathResolver.ResolvePackageRoot(environment.ContentRootPath),
        "UserData", "KernelLeaks", "reported-items.json");
    private void RestoreItemsOnce()
    {
        if (itemsRestored) return;
        itemsRestored = true;
        if (!persistence.MutablePersistenceEnabled) return;
        try
        {
            using var stream = WindowsNativeAtomicFileCommitter.OpenCommittedRead(ItemsPath);
            var stored = JsonSerializer.Deserialize<StoredItems>(stream, ItemJsonOptions);
            if (stored?.Version != 1 || stored.Items is null) throw new InvalidDataException("Invalid kernel leak stored format.");
            Items.Restore(stored.Items);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { }
        catch (Exception error) when (error is JsonException or InvalidDataException or IOException or UnauthorizedAccessException or OverflowException)
        { logger.LogError(error, "Kernel leak observer could not restore reported items from {Path}.", ItemsPath); }
    }
    private void SaveItems(IReadOnlyList<KernelLeakSuspect> values)
    {
        if (!persistence.MutablePersistenceEnabled) return;
        var path = ItemsPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(stream, new StoredItems(1, values), ItemJsonOptions); stream.Flush(flushToDisk: true); }
            WindowsNativeAtomicFileCommitter.CommitReplace(temporary, path);
        }
        finally { WindowsNativeAtomicFileCommitter.DeleteExact(temporary); }
    }
}
