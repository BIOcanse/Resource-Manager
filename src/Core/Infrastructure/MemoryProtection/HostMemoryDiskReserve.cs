using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using ResourceManager.App.Application.MemoryProtection;

namespace ResourceManager.App.Infrastructure.MemoryProtection;

/// <summary>
/// Owns only its manifest, lock and named reserve file. Dumps remain the caller's files.
/// Requested capacity must contain whole allocation units; remaining capacity counts real clusters.
/// File data, including dump headers and metadata, is charged before allocation. Filesystem MFT,
/// directory and journal overhead is outside file-data allocation; filesystem/concurrent-user IO
/// failures remain possible and must be handled as incomplete dumps by the executor.
/// </summary>
public sealed class HostMemoryDiskReserve : IHostMemoryProtectionReserveEntry
{
    private const string Prefix = ".rm-host-memory-reserve";
    private const string ManifestName = Prefix + ".json";
    private readonly SemaphoreSlim _operation = new(1, 1);
    private HostMemoryReserveEntryStatus _status = new("", 0, false, "reserve-not-configured", 0);
    private FileStream? _reserve;
    private FileStream? _lock;
    private List<SafeFileHandle> _directories = [];
    private Manifest? _manifest;
    private string _location = "";
    private ulong _configured;
    private ulong _cluster;
    private bool _prepared;
    private bool _disposed;

    public HostMemoryReserveEntryStatus ReadStatus() => Volatile.Read(ref _status);

    public HostMemoryReserveEntryStatus Prepare(string location, ulong sizeBytes)
    {
        _operation.Wait();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            string normalized;
            try { normalized = string.IsNullOrWhiteSpace(location) ? "" : Path.GetFullPath(location); }
            catch (Exception error) when (Expected(error))
            { return PublishFailure(location ?? "", sizeBytes, error); }
            if (_prepared && string.Equals(_location, normalized, StringComparison.OrdinalIgnoreCase) && _configured == sizeBytes)
                return ReadStatus(); // Publishing unchanged settings must never refill consumed space.
            try
            {
                Retire();
                _location = normalized;
                _configured = sizeBytes;
                _cluster = 0;
                _prepared = true;
                return OpenOrBuild(false);
            }
            catch (Exception error) when (Expected(error))
            { return PublishFailure(_location, sizeBytes, error); }
        }
        finally { _operation.Release(); }
    }

    public HostMemoryReserveEntryStatus Rebuild()
    {
        _operation.Wait();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            try { return OpenOrBuild(true); }
            catch (Exception error) when (Expected(error))
            { return PublishFailure(_location, _configured, error); }
        }
        finally { _operation.Release(); }
    }

    public IHostMemoryProtectionReserveLease? TryAcquire()
    {
        if (!_operation.Wait(0)) return null;
        if (_disposed || !ReadStatus().Built || _reserve is null)
        { _operation.Release(); return null; }
        return new Lease(this);
    }

    private HostMemoryReserveEntryStatus OpenOrBuild(bool rebuild)
    {
        Publish(_location, _configured, false, "reserve-building", ReadOwnedAllocation());
        if (_configured == 0 || string.IsNullOrWhiteSpace(_location))
            return Publish(_location, _configured, false, "reserve-not-configured", 0);
        if (!OperatingSystem.IsWindows()) throw new IOException("reserve-requires-windows");
        if (_lock is null)
        {
            _directories = HostMemoryDiskReserveNative.HoldDirectories(_location);
            _lock = HostMemoryDiskReserveNative.OpenFile(Path.Combine(_location, Prefix + ".lock"), FileMode.OpenOrCreate, FileShare.None);
        }
        _manifest ??= ReadManifest();
        var volume = HostMemoryDiskReserveNative.Volume(_location);
        _cluster = volume.Cluster;
        if (_configured % _cluster != 0)
            throw new IOException($"reserve-size-must-be-a-whole-allocation-unit; cluster-bytes={_cluster}");
        if (_manifest is not null && (rebuild || _manifest.ConfiguredSizeBytes != _configured))
            DeleteOwnedReserve();
        if (_manifest is not null)
        {
            if (!_manifest.Complete) throw new IOException("reserve-build-incomplete; request an explicit rebuild");
            if (_cluster != _manifest.ClusterBytes) throw new IOException("reserve-volume-allocation-unit-changed");
            _reserve ??= HostMemoryDiskReserveNative.OpenFile(ReservePath, FileMode.Open, FileShare.Read);
            var remaining = HostMemoryDiskReserveNative.Verify(_reserve, ReservePath);
            if (remaining > _configured || remaining % _cluster != 0 ||
                (ulong)_reserve.Length != remaining)
                throw new IOException("reserve-allocation-does-not-match-completed-build");
            return Publish(_location, _configured, true, null, remaining);
        }

        var allocation = _configured;
        // Reject impossible settings before any large allocation; real control-file and filesystem
        // allocation failures still report their actual IO error instead of imposing hidden slack.
        if (allocation > long.MaxValue || volume.Available < allocation)
            throw new IOException("insufficient-disk-space-for-reserve");
        var initial = new Manifest(1, Guid.NewGuid().ToString("N"), _configured, _cluster, false);
        using (var stream = HostMemoryDiskReserveNative.OpenFile(Path.Combine(_location, ManifestName), FileMode.CreateNew, FileShare.None))
        {
            // Claim ownership only after CreateNew succeeded; a failed open must never grant
            // permission to retire an existing, invalid or otherwise unowned manifest.
            _manifest = initial;
            JsonSerializer.Serialize(stream, initial);
            stream.Flush(true);
        }
        StageCompletionManifest();
        // Allocate both proof files before the large reserve so proof publication cannot require
        // an extra data cluster after reserving the last available cluster on the volume.
        if (HostMemoryDiskReserveNative.Volume(_location).Available < allocation)
            throw new IOException("insufficient-disk-space-for-reserve");
        _reserve = HostMemoryDiskReserveNative.OpenFile(ReservePath, FileMode.CreateNew, FileShare.Read);
        HostMemoryDiskReserveNative.DisableCompression(_reserve);
        HostMemoryDiskReserveNative.Allocate(_reserve, (long)allocation);
        _reserve.SetLength((long)allocation);
        _reserve.Flush(true);
        if (HostMemoryDiskReserveNative.Verify(_reserve, ReservePath) != allocation)
            throw new IOException("reserve-physical-allocation-does-not-match-request");
        File.Move(TemporaryPath, Path.Combine(_location, ManifestName), true);
        _manifest = _manifest! with { Complete = true };
        return Publish(_location, _configured, true, null, allocation);
    }

    private string ReservePath => Path.Combine(_location, Prefix + "-" + _manifest!.Id + ".bin");

    private Manifest? ReadManifest()
    {
        var path = Path.Combine(_location, ManifestName);
        if (!File.Exists(path)) return null;
        using var stream = HostMemoryDiskReserveNative.OpenFile(path, FileMode.Open, FileShare.Read);
        if (stream.Length > 4096) throw new IOException("invalid-reserve-ownership-manifest");
        Manifest? manifest;
        try { manifest = JsonSerializer.Deserialize<Manifest>(stream); }
        catch (JsonException error) { throw new IOException("invalid-reserve-ownership-manifest", error); }
        if (manifest is null || manifest.Version != 1 || !Guid.TryParseExact(manifest.Id, "N", out _) ||
            manifest.ConfiguredSizeBytes == 0 || manifest.ClusterBytes == 0 || manifest.ClusterBytes > long.MaxValue)
            throw new IOException("invalid-reserve-ownership-manifest");
        return manifest;
    }

    private void StageCompletionManifest()
    {
        var manifest = _manifest! with { Complete = true };
        // A crash can leave an incomplete manifest, but can never promote a partial reserve.
        using var stream = HostMemoryDiskReserveNative.OpenFile(TemporaryPath, FileMode.CreateNew, FileShare.None);
        JsonSerializer.Serialize(stream, manifest);
        stream.Flush(true);
    }

    private string TemporaryPath => Path.Combine(_location, Prefix + "-" + _manifest!.Id + ".tmp");

    private void DeleteOwnedReserve()
    {
        _reserve?.Dispose();
        _reserve = null;
        if (_manifest is null) return;
        // No wildcard cleanup and no slice deletion. Open first so a substituted reparse/hardlink
        // is rejected instead of following it or treating somebody else's file as our reserve.
        if (File.Exists(ReservePath))
        {
            using (HostMemoryDiskReserveNative.OpenFile(ReservePath, FileMode.Open, FileShare.None)) { }
            File.Delete(ReservePath);
        }
        if (File.Exists(TemporaryPath))
        {
            using (HostMemoryDiskReserveNative.OpenFile(TemporaryPath, FileMode.Open, FileShare.None)) { }
            File.Delete(TemporaryPath);
        }
        File.Delete(Path.Combine(_location, ManifestName));
        _manifest = null;
    }

    private void Retire()
    {
        // Retain ownership and the protected path on a failed retirement so its remaining
        // allocation stays observable and a later explicit retry can retire the same file.
        DeleteOwnedReserve();
        ReleaseHandles();
    }

    private void ReleaseHandles()
    {
        _reserve?.Dispose();
        _reserve = null;
        _lock?.Dispose();
        _lock = null;
        foreach (var directory in _directories) directory.Dispose();
        _directories.Clear();
    }

    private HostMemoryReserveEntryStatus Publish(string location, ulong configured, bool built, string? reason, ulong remaining)
    {
        var status = new HostMemoryReserveEntryStatus(location, configured, built, reason, remaining) { ClusterBytes = _cluster };
        Volatile.Write(ref _status, status);
        return status;
    }

    private ulong ReadOwnedAllocation()
    {
        if (_reserve is not null)
            return checked((ulong)HostMemoryDiskReserveNative.Information(_reserve.SafeFileHandle).AllocationSize);
        if (_manifest is null || _lock is null || !File.Exists(ReservePath)) return 0;
        // A validated ownership manifest can describe an incomplete build after restart. It is
        // still disk occupation, but never a usable reserve. The protected ancestor handles and
        // native regular-file checks prevent following a substituted link or claiming unknown data.
        using var file = HostMemoryDiskReserveNative.OpenFile(ReservePath, FileMode.Open, FileShare.ReadWrite, FileAccess.Read);
        return checked((ulong)HostMemoryDiskReserveNative.Information(file.SafeFileHandle).AllocationSize);
    }

    private HostMemoryReserveEntryStatus PublishFailure(string location, ulong configured, Exception failure)
    {
        ulong remaining;
        var reason = failure.Message;
        try { remaining = ReadOwnedAllocation(); }
        catch (Exception error) when (Expected(error))
        {
            // Device/access failures can prevent a physical query. Surface that limitation rather
            // than claiming an unverified file or enabling protection from a guessed capacity.
            remaining = 0;
            reason += "; reserve-allocation-unavailable: " + error.Message;
        }
        return Publish(location, configured, false, reason, remaining);
    }

    private void Charge(ulong bytes)
    {
        var status = ReadStatus();
        if (!status.Built) throw new IOException(status.Reason ?? "reserve-is-not-built");
        if (bytes > status.RemainingBytes) throw new IOException("memory-slice-exceeds-remaining-reserve");
        var next = status.RemainingBytes - bytes;
        try
        {
            _reserve!.SetLength((long)next);
            HostMemoryDiskReserveNative.Allocate(_reserve, (long)next);
            _reserve.Flush(true);
            var actual = HostMemoryDiskReserveNative.Verify(_reserve, ReservePath);
            if (actual != next) throw new IOException("reserve-clusters-were-not-released");
            Publish(_location, _configured, true, null, actual);
        }
        catch
        {
            // Shrinks survive failed writes and restart; a failed physical verification disables use.
            var remaining = checked((ulong)HostMemoryDiskReserveNative.Information(_reserve!.SafeFileHandle).AllocationSize);
            Publish(_location, _configured, false, "reserve-release-failed", remaining);
            throw;
        }
    }

    internal static ulong RoundUp(ulong bytes, ulong cluster)
    {
        if (bytes == 0) return 0;
        if (bytes > ulong.MaxValue - (cluster - 1)) throw new IOException("reserve-size-is-too-large");
        return ((bytes + cluster - 1) / cluster) * cluster;
    }

    private static bool Expected(Exception error) => error is IOException or UnauthorizedAccessException or
        ArgumentException or NotSupportedException or System.Security.SecurityException or OverflowException;

    public void Dispose()
    {
        _operation.Wait();
        try { if (!_disposed) { _disposed = true; ReleaseHandles(); } }
        finally { _operation.Release(); }
    }

    private sealed record Manifest(int Version, string Id, ulong ConfiguredSizeBytes, ulong ClusterBytes, bool Complete);

    private sealed class Lease(HostMemoryDiskReserve owner) : IHostMemoryProtectionReserveLease
    {
        private readonly List<Stream> _streams = [];
        private readonly List<string> _paths = [];
        private bool _disposed;
        internal object Sync { get; } = new();
        public string Location => owner._location;
        public ulong RemainingBytes => owner.ReadStatus().RemainingBytes;
        public IReadOnlyList<string> SlicePaths { get { lock (Sync) return _paths.ToArray(); } }
        public bool CanFit(ulong bytes)
        {
            lock (Sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return bytes <= RemainingBytes && RoundUp(bytes, owner._cluster) <= RemainingBytes;
            }
        }

        public Stream CreateSlice(string fileName)
        {
            lock (Sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (string.IsNullOrWhiteSpace(fileName) || fileName != Path.GetFileName(fileName) ||
                    fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || fileName is "." or ".." ||
                    fileName.EndsWith('.') || fileName.EndsWith(' ') || fileName.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("Slice name must be a plain file name.", nameof(fileName));
                var path = Path.Combine(Location, fileName);
                var file = HostMemoryDiskReserveNative.OpenFile(path, FileMode.CreateNew, FileShare.Read);
                try
                {
                    HostMemoryDiskReserveNative.DisableCompression(file);
                    var stream = new HostMemoryDiskReserveStream(file, path, owner._cluster, Sync, owner.Charge,
                        () => ObjectDisposedException.ThrowIf(_disposed, this));
                    _streams.Add(stream);
                    _paths.Add(path);
                    return stream;
                }
                catch { file.Dispose(); throw; }
            }
        }

        public void Dispose()
        {
            lock (Sync)
            {
                if (_disposed) return;
                try { foreach (var stream in _streams) stream.Dispose(); }
                finally { _disposed = true; owner._operation.Release(); }
            }
        }
    }
}
