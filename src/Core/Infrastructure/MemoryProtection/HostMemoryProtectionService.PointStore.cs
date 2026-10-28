using System.Text.Json;
using ResourceManager.App.Application.MemoryProtection;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Paths;

namespace ResourceManager.App.Infrastructure.MemoryProtection;

// Normal running points are user settings (user 10-28): every created point is saved, the user picks the one in
// effect, and the list and the choice are restored when the service starts, so a restart or an install keeps them.
// The whole list is one file replaced atomically, written before a change takes effect; a change that cannot be
// saved fails and changes nothing.
public sealed partial class HostMemoryProtectionService
{
    // Each point holds every process (about 100 KB at 500 processes); creation past the limit is refused, never evicts.
    public const int PointLimit = 32;
    private static readonly JsonSerializerOptions PointJsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private sealed record StoredPoints(string? ActiveId, IReadOnlyList<NormalRunningPoint> Points);
    private IReadOnlyList<NormalRunningPoint> points = [];
    // True until the restored points have been published once; every later change publishes itself.
    private bool pointRestorePending = true;
    // The point the next publication announces with normal-running-point-created; selecting a point announces only a change.
    private NormalRunningPoint? createdPoint;

    private string PointsPath => Path.Combine(PackagePathResolver.ResolvePackageRoot(environment.ContentRootPath),
        "UserData", "MemoryProtection", "normal-running-points.json");

    public NormalRunningPoint? ReadPoint() { lock (gate) return point; }

    public NormalRunningPointList ListPoints()
    {
        lock (gate) return PointList(points, point);
    }

    private static NormalRunningPointList PointList(IReadOnlyList<NormalRunningPoint> saved, NormalRunningPoint? active)
        => new(active?.Id, saved.Select(item => new NormalRunningPointSummary(item.Id, item.At, item.ProcessCount, item.Commit)).ToArray(), PointLimit);

    public async Task<NormalRunningPoint> CreatePointAsync(CancellationToken cancellationToken)
    {
        await pointCreation.WaitAsync(cancellationToken);
        try
        {
            HostMemoryProtectionSettings current;
            lock (gate)
            {
                if (points.Count >= PointLimit) throw new NormalRunningPointException("normal-running-point-limit");
                current = settings;
            }
            await foreach (var reading in source.SubscribeAsync("host-memory-protection:normal-running-point",
                TimeSpan.FromSeconds(current.CheckIntervalSeconds), cancellationToken))
            {
                if (reading.Commit is null || reading.Processes is null) continue;
                var next = new NormalRunningPoint(reading.At, reading.Commit, reading.Processes.ToArray()) { Id = Guid.NewGuid().ToString("N") };
                IReadOnlyList<NormalRunningPoint> saved;
                lock (gate) saved = [.. points, next];
                await ApplyPointsAsync(saved, next, "normal-running-point-created");
                PublishStatus(reading.Commit, useReading: true, reading.At);
                return next;
            }
            throw new InvalidOperationException("normal-running-point-source-ended");
        }
        finally { pointCreation.Release(); }
    }

    // Returns null when no saved point has this id.
    public async Task<NormalRunningPoint?> SelectPointAsync(string id)
    {
        await pointCreation.WaitAsync();
        try
        {
            IReadOnlyList<NormalRunningPoint> saved;
            lock (gate) saved = points;
            var selected = saved.FirstOrDefault(item => item.Id == id);
            if (selected is null) return null;
            await ApplyPointsAsync(saved, selected, "normal-running-point-selected");
            PublishStatus();
            return selected;
        }
        finally { pointCreation.Release(); }
    }

    // Deleting the point in effect leaves no point in effect; returns false when no saved point has this id.
    public async Task<bool> DeletePointAsync(string id)
    {
        await pointCreation.WaitAsync();
        try
        {
            IReadOnlyList<NormalRunningPoint> saved;
            NormalRunningPoint? active;
            lock (gate) { saved = points; active = point; }
            if (!saved.Any(item => item.Id == id)) return false;
            await ApplyPointsAsync(saved.Where(item => item.Id != id).ToArray(), active?.Id == id ? null : active, "normal-running-point-deleted");
            PublishStatus();
            return true;
        }
        finally { pointCreation.Release(); }
    }

    // Called with pointCreation held.
    private async Task ApplyPointsAsync(IReadOnlyList<NormalRunningPoint> saved, NormalRunningPoint? active, string cause)
    {
        SavePoints(new(active?.Id, saved));
        pointRestorePending = false;
        lock (gate)
        {
            points = saved;
            if (!ReferenceEquals(point, active)) { point = active; cannotOpen.Clear(); exclusions = []; }
            if (cause == "normal-running-point-created") createdPoint = active;
        }
        await pointExists.PublishAsync(active is not null, Reason(cause));
    }

    private async Task RestorePointsOnceAsync()
    {
        await pointCreation.WaitAsync();
        try
        {
            if (!pointRestorePending) return;
            pointRestorePending = false;
            var stored = ReadStoredPoints();
            if (stored is null) return;
            var active = stored.Points.FirstOrDefault(item => item.Id == stored.ActiveId);
            lock (gate) { points = stored.Points; point = active; cannotOpen.Clear(); exclusions = []; }
            await pointExists.PublishAsync(active is not null, Reason("normal-running-point-restored"));
            logger.LogInformation("Host memory protection restored {Count} normal running points; in effect: {At}.", stored.Points.Count, active?.At);
        }
        finally { pointCreation.Release(); }
    }

    private StoredPoints? ReadStoredPoints()
    {
        try
        {
            using var stream = WindowsNativeAtomicFileCommitter.OpenCommittedRead(PointsPath);
            var stored = JsonSerializer.Deserialize<StoredPoints>(stream, PointJsonOptions);
            if (stored?.Points is null || stored.Points.Any(item => item?.Commit is null || item.Processes is null || string.IsNullOrEmpty(item.Id))
                || stored.Points.Select(item => item.Id).Distinct().Count() != stored.Points.Count
                || stored.ActiveId is { } id && stored.Points.All(item => item.Id != id))
                throw new InvalidDataException("The stored normal running points are incomplete or inconsistent.");
            return stored;
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return null; }
        catch (Exception error) when (error is JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            // The file stays for inspection; status shows no-normal-running-point and the next saved change replaces it.
            logger.LogError(error, "Host memory protection could not restore the normal running points from {Path}.", PointsPath);
            return null;
        }
    }

    private void SavePoints(StoredPoints value)
    {
        var path = PointsPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + $".{Guid.NewGuid():N}.tmp";
        using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, value, PointJsonOptions);
            stream.Flush(flushToDisk: true);
        }
        WindowsNativeAtomicFileCommitter.CommitReplace(temporaryPath, path);
    }
}
