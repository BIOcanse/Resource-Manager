using System.Buffers;
using System.Security.Cryptography;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Paths;

namespace ResourceManager.App.Infrastructure.Optimization.Transactions;

internal enum HostManagerAuthorityRetirementCommitOutcome : byte
{
    NotCommitted = 1,
    CommitAmbiguous = 2
}

internal sealed class HostManagerAuthorityRetirementPrepareException(
    HostManagerAuthorityRetirementCommitOutcome outcome,
    Guid ticketId,
    Exception innerException)
    : IOException(
        outcome == HostManagerAuthorityRetirementCommitOutcome.CommitAmbiguous
            ? "The authority-retirement tombstone may have been committed."
            : "The authority-retirement tombstone was not committed.",
        innerException)
{
    internal HostManagerAuthorityRetirementCommitOutcome Outcome { get; } = outcome;

    internal Guid TicketId { get; } = ticketId;
}

internal interface IHostManagerAuthorityRetirementStorage
{
    void CommitNew(string temporaryPath, string destinationPath);

    WindowsNativeFileDeleteResult DeleteFile(string path);

    void DeleteEmptyDirectory(string path);

    FileStream AcquireDeleteLease(string path);
}

internal sealed class WindowsHostManagerAuthorityRetirementStorage
    : IHostManagerAuthorityRetirementStorage
{
    internal static WindowsHostManagerAuthorityRetirementStorage Instance { get; } = new();

    private WindowsHostManagerAuthorityRetirementStorage()
    {
    }

    public void CommitNew(string temporaryPath, string destinationPath)
        => WindowsNativeAtomicFileCommitter.CommitNew(temporaryPath, destinationPath);

    public WindowsNativeFileDeleteResult DeleteFile(string path)
        => WindowsNativeAtomicFileCommitter.DeleteExact(path);

    public void DeleteEmptyDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: false);
        }
        catch (DirectoryNotFoundException)
        {
            return;
        }

        if (Directory.Exists(path))
        {
            throw new IOException(
                "The retired authority directory delete was not verified.");
        }
    }

    public FileStream AcquireDeleteLease(string path)
        => new(
            path,
            new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.ReadWrite,
                Share = FileShare.Delete,
                BufferSize = 1,
                Options = FileOptions.WriteThrough
            });
}

internal sealed class HostManagerAuthorityRetirementManager : IDisposable
{
    private const int MaximumPendingRetirements = 64;
    private const int MaximumOrphanTemporaryFiles = 64;
    private const int MaximumRetirementDirectoryEntries =
        1 + MaximumPendingRetirements + MaximumOrphanTemporaryFiles;
    private const int MaximumCanonicalTemporaryFiles = 64;
    private const int MaximumPayloadEntries = 4096;
    private const long MaximumPayloadInventoryBytes = 16L * 1024 * 1024 * 1024;
    private const int IoBufferSize = 64 * 1024;
    private const string RetirementDirectoryName =
        "AuthorityRetirement/generation-00000001";
    private const string OwnerLeaseFileName = "retirement.owner.lock";
    private const string AppliedOwnershipStoreKind = "applied-ownership";
    private const string TransactionJournalStoreKind = "transaction-journal";
    private const string PayloadOwnerLockFileName = "payload-store.lock";

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object sync = new();
    private readonly Dictionary<Guid, HostManagerAuthorityRetirementRecord> records = [];
    private readonly Dictionary<Guid, IAsyncDisposable> resources = [];
    private readonly IHostManagerAuthorityRetirementStorage storage;
    private readonly FileStream ownerLease;
    private readonly string storageBoundaryRoot;
    private bool disposed;

    internal HostManagerAuthorityRetirementManager(IHostEnvironment environment)
        : this(
            ResolveProductionPaths(environment),
            WindowsHostManagerAuthorityRetirementStorage.Instance)
    {
    }

    internal HostManagerAuthorityRetirementManager(
        string dataRoot,
        IHostManagerAuthorityRetirementStorage storage)
        : this(
            new HostManagerAuthorityRetirementPaths(
                Path.GetFullPath(dataRoot),
                Path.GetFullPath(dataRoot),
                Path.GetFullPath(Path.Combine(
                    dataRoot,
                    "HostManager",
                    RetirementDirectoryName))),
            storage)
    {
    }

    private HostManagerAuthorityRetirementManager(
        HostManagerAuthorityRetirementPaths paths,
        IHostManagerAuthorityRetirementStorage storage)
    {
        if (!Path.IsPathFullyQualified(paths.StorageBoundaryRoot)
            || !Path.IsPathFullyQualified(paths.RuntimeDataRoot)
            || !Path.IsPathFullyQualified(paths.RetirementDirectory))
        {
            throw new ArgumentException(
                "Host Manager authority-retirement roots must be absolute.",
                nameof(paths));
        }

        storageBoundaryRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(paths.StorageBoundaryRoot));
        DataRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(paths.RuntimeDataRoot));
        this.storage = storage ?? throw new ArgumentNullException(nameof(storage));
        RetirementDirectory = Path.GetFullPath(paths.RetirementDirectory);
        RequireWithinStorageBoundary(DataRoot);
        RequireWithinStorageBoundary(RetirementDirectory);
        Directory.CreateDirectory(RetirementDirectory);
        RequireNotReparsePoint(RetirementDirectory);
        ownerLease = new FileStream(
            Path.Combine(RetirementDirectory, OwnerLeaseFileName),
            new FileStreamOptions
            {
                Mode = FileMode.OpenOrCreate,
                Access = FileAccess.ReadWrite,
                Share = FileShare.None,
                BufferSize = 1,
                Options = FileOptions.WriteThrough
            });
        try
        {
            LoadRecords();
        }
        catch
        {
            ownerLease.Dispose();
            throw;
        }
    }

    internal string DataRoot { get; }

    internal string RetirementDirectory { get; }

    internal Exception? LastFailure { get; private set; }

    internal int PendingCount(HostManagerAuthorityKind kind)
    {
        lock (sync)
        {
            return records.Values.Count(record => record.Kind == kind);
        }
    }

    internal async Task<HostManagerAuthorityRetirementTicket> PrepareAppliedOwnershipAsync(
        string canonicalPath,
        CancellationToken cancellationToken)
        => await PrepareAsync(
            HostManagerAuthorityKind.AppliedOwnership,
            canonicalPath,
            payloadDirectory: null,
            cancellationToken).ConfigureAwait(false);

    internal async Task<HostManagerAuthorityRetirementTicket> PrepareTransactionJournalAsync(
        string canonicalPath,
        string payloadDirectory,
        CancellationToken cancellationToken)
        => await PrepareAsync(
            HostManagerAuthorityKind.TransactionJournal,
            canonicalPath,
            payloadDirectory,
            cancellationToken).ConfigureAwait(false);

    internal void Attach(
        HostManagerAuthorityRetirementTicket ticket,
        IAsyncDisposable resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        lock (sync)
        {
            ThrowIfDisposed();
            var record = RequireRecord(ticket);
            if (resources.ContainsKey(record.TicketId))
            {
                throw new InvalidOperationException(
                    "The retired authority already has an attached owner.");
            }
            resources.Add(record.TicketId, resource);
        }
    }

    internal async Task CancelAsync(
        HostManagerAuthorityRetirementTicket ticket,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            HostManagerAuthorityRetirementRecord record;
            lock (sync)
            {
                ThrowIfDisposed();
                record = RequireRecord(ticket);
                resources.Remove(record.TicketId);
            }

            _ = storage.DeleteFile(GetTombstonePath(record.TicketId));
            lock (sync)
            {
                records.Remove(record.TicketId);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    internal async Task RetryAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            Exception? failures = null;
            HostManagerAuthorityRetirementRecord[] pending;
            lock (sync)
            {
                pending = records.Values
                    .OrderBy(static record => record.TicketId)
                    .ToArray();
            }

            foreach (var record in pending)
            {
                IAsyncDisposable? resource;
                lock (sync)
                {
                    resources.TryGetValue(record.TicketId, out resource);
                }
                if (resource is not null)
                {
                    try
                    {
                        await resource.DisposeAsync().ConfigureAwait(false);
                        lock (sync)
                        {
                            resources.Remove(record.TicketId);
                        }
                    }
                    catch (Exception exception)
                    {
                        failures = Append(failures, exception);
                        continue;
                    }
                }

                try
                {
                    Cleanup(record);
                    _ = storage.DeleteFile(GetTombstonePath(record.TicketId));
                    lock (sync)
                    {
                        records.Remove(record.TicketId);
                    }
                }
                catch (Exception exception)
                {
                    failures = Append(failures, exception);
                }
            }

            LastFailure = failures;
        }
        finally
        {
            gate.Release();
        }
    }

    internal void RequireAvailable(params string[] candidatePaths)
    {
        ArgumentNullException.ThrowIfNull(candidatePaths);
        var relativePaths = candidatePaths.Select(ToRelativePath).ToArray();
        lock (sync)
        {
            ThrowIfDisposed();
            foreach (var record in records.Values)
            {
                foreach (var candidate in relativePaths)
                {
                    if (HostManagerAuthorityRetirementRecord.PathsOverlap(
                            candidate,
                            record.CanonicalRelativePath)
                        || record.PayloadRelativeDirectory is not null
                        && HostManagerAuthorityRetirementRecord.PathsOverlap(
                            candidate,
                            record.PayloadRelativeDirectory))
                    {
                        throw new InvalidOperationException(
                            $"The Host Manager path is reserved by pending authority retirement {record.TicketId:N}.");
                    }
                }
            }
        }
    }

    internal Exception? CreatePendingFailure(string message)
    {
        lock (sync)
        {
            if (records.Count == 0)
            {
                return null;
            }

            return new InvalidOperationException(
                message,
                LastFailure ?? new IOException(
                    "One or more durable authority retirements remain pending."));
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
        }

        ownerLease.Dispose();
        gate.Dispose();
    }

    private static HostManagerAuthorityRetirementPaths ResolveProductionPaths(
        IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var stableInstallRoot = HostManagerDurableDataRootResolver.ResolveStableInstallRoot(
            environment.ContentRootPath,
            allowUnregisteredFallback: environment.IsDevelopment());
        return CompileProductionPaths(stableInstallRoot);
    }

    internal static HostManagerAuthorityRetirementPaths CompileProductionPaths(
        string stableInstallRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stableInstallRoot);
        if (!Path.IsPathFullyQualified(stableInstallRoot))
        {
            throw new ArgumentException(
                "The stable Resource Manager install root must be absolute.",
                nameof(stableInstallRoot));
        }

        var normalizedStableRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(stableInstallRoot));
        return new HostManagerAuthorityRetirementPaths(
            normalizedStableRoot,
            Path.GetFullPath(Path.Combine(normalizedStableRoot, "UserData")),
            Path.GetFullPath(Path.Combine(
                normalizedStableRoot,
                "UserData",
                "HostManager",
                RetirementDirectoryName)));
    }

    private async Task<HostManagerAuthorityRetirementTicket> PrepareAsync(
        HostManagerAuthorityKind kind,
        string canonicalPath,
        string? payloadDirectory,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var canonicalRelativePath = ToRelativePath(canonicalPath);
            var payloadRelativePath = payloadDirectory is null
                ? null
                : ToRelativePath(payloadDirectory);
            RequirePathsAvailable(canonicalRelativePath, payloadRelativePath);
            RequireNoReparseAncestors(Path.GetFullPath(canonicalPath));
            if (payloadDirectory is not null)
            {
                RequireNoReparseAncestors(Path.GetFullPath(payloadDirectory));
            }

            lock (sync)
            {
                if (records.Count >= MaximumPendingRetirements)
                {
                    throw new InvalidOperationException(
                        "The durable authority-retirement capacity is exhausted.");
                }
            }

            var storeKind = kind switch
            {
                HostManagerAuthorityKind.AppliedOwnership => AppliedOwnershipStoreKind,
                HostManagerAuthorityKind.TransactionJournal => TransactionJournalStoreKind,
                _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
            };
            var identity = new HostManagerDurableRootManifest(
                    Path.GetFullPath(canonicalPath),
                    storeKind)
                .CaptureInitializedIdentity();
            var ticketId = Guid.NewGuid();
            var record = new HostManagerAuthorityRetirementRecord(
                ticketId,
                kind,
                canonicalRelativePath,
                payloadRelativePath,
                identity.RootIncarnation,
                identity.CanonicalLength,
                identity.CanonicalSha256,
                identity.ManifestLength,
                identity.ManifestSha256);
            var image = record.Encode();
            try
            {
                CommitRecord(record, image);
            }
            catch (HostManagerAuthorityRetirementPrepareException exception) when (
                exception.Outcome == HostManagerAuthorityRetirementCommitOutcome.CommitAmbiguous)
            {
                lock (sync)
                {
                    records.Add(ticketId, record);
                }
                throw;
            }
            lock (sync)
            {
                records.Add(ticketId, record);
            }
            return new HostManagerAuthorityRetirementTicket(ticketId, kind);
        }
        finally
        {
            gate.Release();
        }
    }

    private void CommitRecord(
        HostManagerAuthorityRetirementRecord record,
        ReadOnlySpan<byte> image)
    {
        var destinationPath = GetTombstonePath(record.TicketId);
        var temporaryPath = Path.Combine(
            RetirementDirectory,
            $".{Path.GetFileNameWithoutExtension(destinationPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            try
            {
                using var stream = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    4096,
                    FileOptions.WriteThrough);
                stream.Write(image);
                stream.Flush(flushToDisk: true);
            }
            catch (Exception exception)
            {
                throw new HostManagerAuthorityRetirementPrepareException(
                    HostManagerAuthorityRetirementCommitOutcome.NotCommitted,
                    record.TicketId,
                    exception);
            }

            try
            {
                storage.CommitNew(temporaryPath, destinationPath);
            }
            catch (Exception exception)
            {
                throw new HostManagerAuthorityRetirementPrepareException(
                    HostManagerAuthorityRetirementCommitOutcome.CommitAmbiguous,
                    record.TicketId,
                    exception);
            }

            try
            {
                VerifyExactImage(destinationPath, image);
            }
            catch (Exception exception)
            {
                throw new HostManagerAuthorityRetirementPrepareException(
                    HostManagerAuthorityRetirementCommitOutcome.CommitAmbiguous,
                    record.TicketId,
                    exception);
            }
        }
        finally
        {
            try
            {
                _ = storage.DeleteFile(temporaryPath);
            }
            catch
            {
            }
        }
    }

    private void LoadRecords()
    {
        var loaded = new Dictionary<Guid, HostManagerAuthorityRetirementRecord>();
        var entries = Directory.EnumerateFileSystemEntries(
                RetirementDirectory,
                "*",
                SearchOption.TopDirectoryOnly)
            .Take(MaximumRetirementDirectoryEntries + 1)
            .ToArray();
        if (entries.Length > MaximumRetirementDirectoryEntries)
        {
            throw new InvalidDataException(
                "The authority-retirement root exceeds its entry limit.");
        }

        var orphanTemporaryFiles = new List<string>();
        foreach (var entry in entries)
        {
            RequireNotReparsePoint(entry);
            if (Directory.Exists(entry))
            {
                throw new InvalidDataException(
                    "The authority-retirement root contains an unexpected directory.");
            }

            var name = Path.GetFileName(entry);
            if (name.Equals(OwnerLeaseFileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (TryParseTemporaryFileName(name))
            {
                orphanTemporaryFiles.Add(entry);
                if (orphanTemporaryFiles.Count > MaximumOrphanTemporaryFiles)
                {
                    throw new InvalidDataException(
                        "The authority-retirement root has too many orphan temporary files.");
                }
                continue;
            }
            if (!TryParseTombstoneFileName(name, out var fileTicket))
            {
                throw new InvalidDataException(
                    "The authority-retirement root contains an unexpected file.");
            }
            if (new FileInfo(entry).Length > HostManagerAuthorityRetirementRecord.MaximumImageLength)
            {
                throw new InvalidDataException(
                    "An authority-retirement tombstone exceeds its image limit.");
            }

            var record = HostManagerAuthorityRetirementRecord.Decode(File.ReadAllBytes(entry));
            if (record.TicketId != fileTicket || !loaded.TryAdd(record.TicketId, record))
            {
                throw new InvalidDataException(
                    "An authority-retirement tombstone has a duplicate or mismatched ticket.");
            }
            if (loaded.Count > MaximumPendingRetirements)
            {
                throw new InvalidDataException(
                    "The durable authority-retirement capacity is exceeded.");
            }
        }

        var values = loaded.Values.ToArray();
        for (var left = 0; left < values.Length; left++)
        {
            for (var right = left + 1; right < values.Length; right++)
            {
                if (RecordsOverlap(values[left], values[right]))
                {
                    throw new InvalidDataException(
                        "Durable authority-retirement tombstones reserve overlapping paths.");
                }
            }
        }

        foreach (var temporaryPath in orphanTemporaryFiles)
        {
            _ = storage.DeleteFile(temporaryPath);
        }

        lock (sync)
        {
            foreach (var pair in loaded)
            {
                records.Add(pair.Key, pair.Value);
            }
        }
    }

    private void Cleanup(HostManagerAuthorityRetirementRecord record)
    {
        var canonicalPath = ResolveRelativePath(record.CanonicalRelativePath);
        var manifestPath = $"{canonicalPath}.root.manifest";
        var ownerLockPath = $"{canonicalPath}.owner.lock";
        var payloadDirectory = record.PayloadRelativeDirectory is null
            ? null
            : ResolveRelativePath(record.PayloadRelativeDirectory);
        var payloadLockPath = payloadDirectory is null
            ? null
            : Path.Combine(payloadDirectory, PayloadOwnerLockFileName);

        RequireNoReparseAncestors(canonicalPath);
        if (payloadDirectory is not null)
        {
            RequireNoReparseAncestors(payloadDirectory);
        }

        var canonicalExists = File.Exists(canonicalPath);
        var manifestExists = File.Exists(manifestPath);
        var payloadExists = payloadDirectory is not null && Directory.Exists(payloadDirectory);
        var ownerLockExists = File.Exists(ownerLockPath);
        var payloadLockExists = payloadLockPath is not null && File.Exists(payloadLockPath);
        if ((canonicalExists || manifestExists || payloadExists) && !ownerLockExists)
        {
            throw new InvalidDataException(
                "The retired authority lost its canonical owner lock before cleanup.");
        }

        FileStream? ownerLease = null;
        FileStream? payloadLease = null;
        try
        {
            if (ownerLockExists)
            {
                ownerLease = storage.AcquireDeleteLease(ownerLockPath);
            }
            if (payloadExists && payloadLockExists)
            {
                payloadLease = storage.AcquireDeleteLease(payloadLockPath!);
            }

            VerifyRetiredIdentity(record, canonicalPath, manifestPath);
            DeleteCanonicalTemporaryFiles(canonicalPath);
            if (payloadDirectory is not null && payloadExists)
            {
                DeletePayloadContents(
                    payloadDirectory,
                    payloadLockPath!,
                    allowMissingLock: !canonicalExists && manifestExists);
            }

            VerifyRetiredIdentity(record, canonicalPath, manifestPath);
            _ = storage.DeleteFile(canonicalPath);
            if (payloadDirectory is not null && Directory.Exists(payloadDirectory))
            {
                if (payloadLease is not null)
                {
                    _ = storage.DeleteFile(payloadLockPath!);
                    payloadLease.Dispose();
                    payloadLease = null;
                }
                storage.DeleteEmptyDirectory(payloadDirectory);
            }
            _ = storage.DeleteFile(manifestPath);
            if (ownerLease is not null)
            {
                _ = storage.DeleteFile(ownerLockPath);
                ownerLease.Dispose();
                ownerLease = null;
            }
        }
        finally
        {
            payloadLease?.Dispose();
            ownerLease?.Dispose();
        }
    }

    private static void VerifyRetiredIdentity(
        HostManagerAuthorityRetirementRecord record,
        string canonicalPath,
        string manifestPath)
    {
        var canonicalExists = File.Exists(canonicalPath);
        var manifestExists = File.Exists(manifestPath);
        if (canonicalExists && !manifestExists)
        {
            throw new InvalidDataException(
                "The retired authority canonical exists without its root manifest.");
        }
        if (canonicalExists)
        {
            VerifyFileIdentity(
                canonicalPath,
                record.CanonicalLength,
                record.CanonicalSha256,
                "canonical");
        }
        if (manifestExists)
        {
            VerifyFileIdentity(
                manifestPath,
                record.ManifestLength,
                record.ManifestSha256,
                "manifest");
        }
    }

    private static void VerifyFileIdentity(
        string path,
        long expectedLength,
        ReadOnlySpan<byte> expectedSha256,
        string artifact)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            IoBufferSize,
            FileOptions.SequentialScan);
        if (stream.Length != expectedLength)
        {
            throw new InvalidDataException(
                $"The retired authority {artifact} length changed.");
        }
        var actualSha256 = SHA256.HashData(stream);
        if (!CryptographicOperations.FixedTimeEquals(actualSha256, expectedSha256))
        {
            throw new InvalidDataException(
                $"The retired authority {artifact} digest changed.");
        }
    }

    private void DeleteCanonicalTemporaryFiles(string canonicalPath)
    {
        var directory = Path.GetDirectoryName(canonicalPath)
            ?? throw new InvalidOperationException(
                "The retired canonical has no parent directory.");
        if (!Directory.Exists(directory))
        {
            return;
        }

        var canonicalName = Path.GetFileName(canonicalPath);
        var manifestName = $"{canonicalName}.root.manifest";
        var matchingCount = 0;
        foreach (var path in Directory.EnumerateFiles(
                     directory,
                     ".*.tmp",
                     SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(path);
            if (!IsCanonicalTemporaryFile(name, canonicalName)
                && !IsCanonicalTemporaryFile(name, manifestName))
            {
                continue;
            }
            matchingCount++;
            if (matchingCount > MaximumCanonicalTemporaryFiles)
            {
                throw new InvalidDataException(
                    "The retired authority has too many canonical temporary files.");
            }
            RequireNotReparsePoint(path);
            _ = storage.DeleteFile(path);
        }
    }

    private void DeletePayloadContents(
        string payloadDirectory,
        string payloadLockPath,
        bool allowMissingLock)
    {
        RequireNotReparsePoint(payloadDirectory);
        var entries = Directory.EnumerateFileSystemEntries(
                payloadDirectory,
                "*",
                SearchOption.TopDirectoryOnly)
            .ToArray();
        if (entries.Length > MaximumPayloadEntries)
        {
            throw new InvalidDataException(
                "The retired payload root exceeds its entry limit.");
        }

        long totalBytes = 0;
        var deletable = new List<string>(entries.Length);
        foreach (var entry in entries)
        {
            RequireNotReparsePoint(entry);
            if (Directory.Exists(entry))
            {
                throw new InvalidDataException(
                    "The retired payload root contains an unexpected directory.");
            }
            if (string.Equals(entry, payloadLockPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var name = Path.GetFileName(entry);
            if (!IsOwnedPayloadFile(name))
            {
                throw new InvalidDataException(
                    "The retired payload root contains an unknown file.");
            }
            totalBytes = checked(totalBytes + new FileInfo(entry).Length);
            if (totalBytes > MaximumPayloadInventoryBytes)
            {
                throw new InvalidDataException(
                    "The retired payload root exceeds its byte limit.");
            }
            deletable.Add(entry);
        }
        foreach (var entry in deletable)
        {
            _ = storage.DeleteFile(entry);
        }

        if (!File.Exists(payloadLockPath)
            && (!allowMissingLock || Directory.EnumerateFileSystemEntries(
                    payloadDirectory,
                    "*",
                    SearchOption.TopDirectoryOnly).Any()))
        {
            throw new InvalidDataException(
                "The retired payload root lost its owner lock before cleanup.");
        }
    }

    private void RequirePathsAvailable(
        string canonicalRelativePath,
        string? payloadRelativePath)
    {
        lock (sync)
        {
            foreach (var record in records.Values)
            {
                if (HostManagerAuthorityRetirementRecord.PathsOverlap(
                        canonicalRelativePath,
                        record.CanonicalRelativePath)
                    || record.PayloadRelativeDirectory is not null
                    && HostManagerAuthorityRetirementRecord.PathsOverlap(
                        canonicalRelativePath,
                        record.PayloadRelativeDirectory)
                    || payloadRelativePath is not null
                    && HostManagerAuthorityRetirementRecord.PathsOverlap(
                        payloadRelativePath,
                        record.CanonicalRelativePath)
                    || payloadRelativePath is not null
                    && record.PayloadRelativeDirectory is not null
                    && HostManagerAuthorityRetirementRecord.PathsOverlap(
                        payloadRelativePath,
                        record.PayloadRelativeDirectory))
                {
                    throw new InvalidOperationException(
                        "The authority being retired overlaps an existing durable retirement.");
                }
            }
        }
    }

    private HostManagerAuthorityRetirementRecord RequireRecord(
        HostManagerAuthorityRetirementTicket ticket)
    {
        if (!records.TryGetValue(ticket.TicketId, out var record)
            || record.Kind != ticket.Kind)
        {
            throw new InvalidOperationException(
                "The authority-retirement ticket is not current.");
        }
        return record;
    }

    private string ToRelativePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException(
                "An authority-retirement path must be absolute.",
                nameof(path));
        }

        var fullPath = Path.GetFullPath(path);
        RequireWithinStorageBoundary(fullPath);

        var relativePath = Path.GetRelativePath(storageBoundaryRoot, fullPath)
            .Replace('\\', '/');
        HostManagerAuthorityRetirementRecord.ValidateRelativePath(relativePath);
        return relativePath;
    }

    private string ResolveRelativePath(string relativePath)
    {
        HostManagerAuthorityRetirementRecord.ValidateRelativePath(relativePath);
        var resolved = Path.GetFullPath(Path.Combine(
            storageBoundaryRoot,
            Path.Combine(relativePath.Split('/'))));
        RequireWithinStorageBoundary(resolved);
        return resolved;
    }

    private void RequireNoReparseAncestors(string path)
    {
        var relativePath = ToRelativePath(path);
        var current = storageBoundaryRoot;
        RequireNotReparsePoint(current);
        foreach (var segment in relativePath.Split('/'))
        {
            current = Path.Combine(current, segment);
            if (File.Exists(current) || Directory.Exists(current))
            {
                RequireNotReparsePoint(current);
            }
        }
    }

    private static void RequireNotReparsePoint(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                "A Host Manager durable authority path is a reparse point.");
        }
    }

    private static void VerifyExactImage(
        string path,
        ReadOnlySpan<byte> expectedImage)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            IoBufferSize,
            FileOptions.SequentialScan);
        if (stream.Length != expectedImage.Length)
        {
            throw new InvalidDataException(
                "The committed authority-retirement tombstone has an unexpected length.");
        }

        var buffer = ArrayPool<byte>.Shared.Rent(IoBufferSize);
        try
        {
            var offset = 0;
            while (offset < expectedImage.Length)
            {
                var length = Math.Min(buffer.Length, expectedImage.Length - offset);
                var read = stream.Read(buffer, 0, length);
                if (read == 0
                    || !buffer.AsSpan(0, read).SequenceEqual(
                        expectedImage.Slice(offset, read)))
                {
                    throw new InvalidDataException(
                        "The committed authority-retirement tombstone differs from its expected image.");
                }
                offset += read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private string GetTombstonePath(Guid ticketId)
        => Path.Combine(RetirementDirectory, $"retirement-{ticketId:N}.bin");

    private static bool TryParseTombstoneFileName(string name, out Guid ticketId)
    {
        const string prefix = "retirement-";
        const string suffix = ".bin";
        if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            && Guid.TryParseExact(
                name.AsSpan(prefix.Length, name.Length - prefix.Length - suffix.Length),
                "N",
                out ticketId))
        {
            return true;
        }
        ticketId = Guid.Empty;
        return false;
    }

    private static bool TryParseTemporaryFileName(string name)
    {
        const string prefix = ".retirement-";
        const string suffix = ".tmp";
        if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || !name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var body = name.AsSpan(prefix.Length, name.Length - prefix.Length - suffix.Length);
        var separator = body.IndexOf('.');
        return separator == 32
            && Guid.TryParseExact(body[..separator], "N", out _)
            && body.Length == 65
            && Guid.TryParseExact(body[(separator + 1)..], "N", out _);
    }

    private static bool IsCanonicalTemporaryFile(string name, string canonicalName)
    {
        var prefix = $".{canonicalName}.";
        const string suffix = ".tmp";
        return name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            && Guid.TryParseExact(
                name.AsSpan(prefix.Length, name.Length - prefix.Length - suffix.Length),
                "N",
                out _);
    }

    private static bool IsOwnedPayloadFile(string name)
    {
        const string sequenceName = "payload-sequence.bin";
        if (name.Equals(sequenceName, StringComparison.OrdinalIgnoreCase)
            || IsPayloadTemporaryFile(name, sequenceName))
        {
            return true;
        }
        if (!TryGetPayloadCanonicalName(name, out var canonicalName))
        {
            return false;
        }
        return name.Equals(canonicalName, StringComparison.OrdinalIgnoreCase)
            || IsPayloadTemporaryFile(name, canonicalName);
    }

    private static bool TryGetPayloadCanonicalName(
        string name,
        out string canonicalName)
    {
        const int canonicalLength = 33;
        if (name.Length < canonicalLength
            || !name.StartsWith("payload-", StringComparison.OrdinalIgnoreCase))
        {
            canonicalName = string.Empty;
            return false;
        }

        var candidate = name[..canonicalLength];
        var valid = candidate[8..18].All(char.IsAsciiDigit)
            && candidate[18] == '-'
            && candidate[19..29].All(char.IsAsciiDigit)
            && candidate.EndsWith(".bin", StringComparison.OrdinalIgnoreCase);
        canonicalName = valid ? candidate : string.Empty;
        return valid;
    }

    private static bool IsPayloadTemporaryFile(string name, string canonicalName)
    {
        var prefix = canonicalName + '.';
        const string suffix = ".tmp";
        return name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            && Guid.TryParseExact(
                name.AsSpan(prefix.Length, name.Length - prefix.Length - suffix.Length),
                "N",
                out _);
    }

    private static bool RecordsOverlap(
        HostManagerAuthorityRetirementRecord first,
        HostManagerAuthorityRetirementRecord second)
    {
        var firstPaths = first.PayloadRelativeDirectory is null
            ? [first.CanonicalRelativePath]
            : new[] { first.CanonicalRelativePath, first.PayloadRelativeDirectory };
        var secondPaths = second.PayloadRelativeDirectory is null
            ? [second.CanonicalRelativePath]
            : new[] { second.CanonicalRelativePath, second.PayloadRelativeDirectory };
        return firstPaths.Any(firstPath => secondPaths.Any(secondPath =>
            HostManagerAuthorityRetirementRecord.PathsOverlap(firstPath, secondPath)));
    }

    private static Exception Append(Exception? current, Exception next)
        => current is null ? next : new AggregateException(current, next);

    private void RequireWithinStorageBoundary(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (string.Equals(
                fullPath,
                storageBoundaryRoot,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        var prefix = Path.EndsInDirectorySeparator(storageBoundaryRoot)
            ? storageBoundaryRoot
            : storageBoundaryRoot + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "A Host Manager authority path escapes the registered install root.");
        }
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(disposed, this);
}

internal readonly record struct HostManagerAuthorityRetirementPaths(
    string StorageBoundaryRoot,
    string RuntimeDataRoot,
    string RetirementDirectory);
