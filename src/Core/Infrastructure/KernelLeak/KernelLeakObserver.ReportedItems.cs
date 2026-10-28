using System.Text.Json;
using ResourceManager.App.Domain.KernelLeak;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Paths;

namespace ResourceManager.App.Infrastructure.KernelLeak;

public sealed partial class KernelLeakObserver
{
    private static readonly JsonSerializerOptions ItemJsonOptions = new(JsonSerializerDefaults.Web)
        { WriteIndented = true, RespectRequiredConstructorParameters = true };
    private sealed record StoredItems(int Version, IReadOnlyList<KernelLeakSuspect> Items);
    private IReadOnlyList<KernelLeakSuspect> reportedItems = [];
    private bool itemsRestored;
    private string ItemsPath => Path.Combine(PackagePathResolver.ResolvePackageRoot(environment.ContentRootPath),
        "UserData", "KernelLeaks", "reported-items.json");
    private static (int Pid, string Start, KernelLeakResourceKind Kind) Key(KernelLeakSuspect item)
        => (item.Holder.ProcessId, item.Holder.StartKey, item.ResourceKind);

    // Caller holds gate. Restore and every committed change belong to the observer, never to a block run.
    private void RestoreItemsOnce()
    {
        if (itemsRestored) return;
        itemsRestored = true;
        if (!persistence.MutablePersistenceEnabled) return;
        try
        {
            using var stream = WindowsNativeAtomicFileCommitter.OpenCommittedRead(ItemsPath);
            var stored = JsonSerializer.Deserialize<StoredItems>(stream, ItemJsonOptions);
            if (stored?.Version != 1 || stored.Items is null || stored.Items.Count > 4096
                || stored.Items.Any(item => !ValidItem(item))
                || stored.Items.Select(Key).Distinct().Count() != stored.Items.Count)
                throw new InvalidDataException("The stored kernel leak reported items are incomplete or inconsistent.");
            // A smaller configured capacity never discards already recorded identities.
            reportedItems = stored.Items.OrderBy(item => item.FirstObservedAt).ToArray();
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { }
        catch (Exception error) when (error is JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            logger.LogError(error, "Kernel leak observer could not restore reported items from {Path}.", ItemsPath);
        }
    }

    private static bool ValidItem(KernelLeakSuspect? item)
    {
        if (item is null || item.ResourceKind != KernelLeakResourceKind.ProcessHandles
            || !Enum.IsDefined(item.State) || item.FirstObservedAt == default || item.LastObservedAt < item.FirstObservedAt
            || !double.IsFinite(item.GrowthPerSecond) || item.GrowthPerSecond <= 0
            || !double.IsFinite(item.ObservationSeconds) || item.ObservationSeconds <= 0
            || item.SampleCount < 2 || item.Growth == 0 || item.Growth > item.CurrentValue
            || item.Holder is not { ProcessId: > 0, StartKey: { Length: > 0 and <= 20 }, Image: { } image, Services: { } services } holder
            || !ulong.TryParse(holder.StartKey, out var birth) || birth == 0) return false;
        if (!Enum.IsDefined(image.Status) || !Enum.IsDefined(services.Status) || services.Names is null
            || services.Names.Any(string.IsNullOrWhiteSpace)) return false;
        return (image.Status == KernelLeakMetadataStatus.Current
                ? !string.IsNullOrWhiteSpace(image.Name) && !string.IsNullOrWhiteSpace(image.Path)
                : image.Name is null && image.Path is null && image.ProductName is null)
            && (services.Status == KernelLeakMetadataStatus.Current || services.Names.Count == 0);
    }

    private int ApplyJudgment(KernelLeakJudgment judgment, KernelLeakSampleFrame frame, int capacity)
    {
        RestoreItemsOnce();
        var next = reportedItems.ToDictionary(Key);
        var live = frame.Samples.Where(sample => sample.ResourceKind == KernelLeakSampleKind.ProcessHandles)
            .Select(sample => (sample.ProcessId, sample.StartKey, KernelLeakResourceKind.ProcessHandles)).ToHashSet();
        var unavailable = frame.UnavailableHolders.ToHashSet();
        var suspected = judgment.Suspects.Select(Key).ToHashSet();
        foreach (var item in reportedItems)
        {
            var key = Key(item);
            var present = live.Contains(key) || unavailable.Contains(new(key.Pid, key.Start)) || unavailable.Contains(new(key.Pid, null));
            if (item.State == KernelLeakItemState.Active && (!present || judgment.Judged.Contains(key) && !suspected.Contains(key)))
                next[key] = item with { State = KernelLeakItemState.History };
        }
        var unrecorded = 0;
        foreach (var suspect in judgment.Suspects)
        {
            var key = Key(suspect);
            if (!next.TryGetValue(key, out var previous) && next.Count >= capacity) { unrecorded++; continue; }
            next[key] = suspect with { State = KernelLeakItemState.Active,
                FirstObservedAt = previous?.FirstObservedAt ?? frame.CapturedAt, LastObservedAt = frame.CapturedAt };
        }
        var saved = next.Values.OrderBy(item => item.FirstObservedAt).ToArray();
        if (!reportedItems.SequenceEqual(saved))
        {
            SaveItems(saved); // A failed write leaves the published items and their evidence unchanged.
            reportedItems = saved;
        }
        return unrecorded;
    }

    private void SaveItems(IReadOnlyList<KernelLeakSuspect> items)
    {
        if (!persistence.MutablePersistenceEnabled) return;
        var path = ItemsPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, new StoredItems(1, items), ItemJsonOptions);
                stream.Flush(flushToDisk: true);
            }
            WindowsNativeAtomicFileCommitter.CommitReplace(temporaryPath, path);
        }
        finally { WindowsNativeAtomicFileCommitter.DeleteExact(temporaryPath); }
    }
}
