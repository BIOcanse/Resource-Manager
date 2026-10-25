using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Paths;

namespace ResourceManager.App.Infrastructure.Optimization;

public sealed partial class JsonHostManagerRollbackStateStore : IHostManagerRollbackStateStore, IDisposable
{
    internal static readonly TimeSpan DefaultCheckpointInterval = TimeSpan.FromMinutes(1);
    private const string DurableStoreKind = "rollback-state";

    private static readonly JsonSerializerOptions JsonOptions = new HostManagerRollbackStateStoreJsonContext(new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
        MaxDepth = HostManagerRollbackStateEnvelopeCodec.MaximumJsonDepth
    }).Options;

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string statePath;
    private readonly string manifestPath;
    private readonly string ownerLeasePath;
    private readonly IReadOnlyList<string> legacyPaths;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan checkpointInterval;
    private readonly Action? beforeCommit;
    private readonly IHostManagerRollbackStateFileCommitter committer;
    private readonly HostManagerDurableRootManifest rootManifest;
    private FileStream? ownerLease;
    private HostManagerRollbackStateDocument? cachedDocument;
    private HostManagerRollbackStateDocument? persistedDocument;
    private byte[]? persistedCanonicalSha256;
    private ulong persistedRevision;
    private DateTimeOffset lastPersistedAt;
    private bool canonicalAuthorityObserved;
    private bool dirty;
    private bool disposed;

    public JsonHostManagerRollbackStateStore(IHostEnvironment environment)
        : this(
            ResolveProductionPaths(environment),
            TimeProvider.System,
            DefaultCheckpointInterval)
    {
    }

    internal JsonHostManagerRollbackStateStore(
        string statePath,
        TimeProvider timeProvider,
        TimeSpan checkpointInterval)
        : this(
            ResolveDirectPaths(statePath),
            timeProvider,
            checkpointInterval,
            beforeCommit: null,
            WindowsHostManagerRollbackStateFileCommitter.Instance,
            WindowsHostManagerDurableRootManifestCommitter.Instance)
    {
    }

    internal JsonHostManagerRollbackStateStore(
        string statePath,
        TimeProvider timeProvider,
        TimeSpan checkpointInterval,
        Action? beforeCommit)
        : this(
            ResolveDirectPaths(statePath),
            timeProvider,
            checkpointInterval,
            beforeCommit,
            WindowsHostManagerRollbackStateFileCommitter.Instance,
            WindowsHostManagerDurableRootManifestCommitter.Instance)
    {
    }

    internal JsonHostManagerRollbackStateStore(
        string statePath,
        TimeProvider timeProvider,
        TimeSpan checkpointInterval,
        IHostManagerRollbackStateFileCommitter committer,
        IHostManagerDurableRootManifestCommitter manifestCommitter)
        : this(
            ResolveDirectPaths(statePath),
            timeProvider,
            checkpointInterval,
            beforeCommit: null,
            committer,
            manifestCommitter)
    {
    }

    internal JsonHostManagerRollbackStateStore(
        HostManagerRollbackStateStorePaths paths,
        TimeProvider timeProvider,
        TimeSpan checkpointInterval)
        : this(
            paths,
            timeProvider,
            checkpointInterval,
            beforeCommit: null,
            WindowsHostManagerRollbackStateFileCommitter.Instance,
            WindowsHostManagerDurableRootManifestCommitter.Instance)
    {
    }

    private JsonHostManagerRollbackStateStore(
        HostManagerRollbackStateStorePaths paths,
        TimeProvider timeProvider,
        TimeSpan checkpointInterval,
        Action? beforeCommit,
        IHostManagerRollbackStateFileCommitter committer,
        IHostManagerDurableRootManifestCommitter manifestCommitter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(paths.CanonicalPath);
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (checkpointInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(checkpointInterval));
        }

        statePath = Path.GetFullPath(paths.CanonicalPath);
        manifestPath = Path.GetFullPath(paths.RootManifestPath);
        ownerLeasePath = Path.GetFullPath(paths.OwnerLeasePath);
        legacyPaths = paths.LegacyPaths
            .Select(Path.GetFullPath)
            .Where(path => !path.Equals(statePath, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        this.timeProvider = timeProvider;
        this.checkpointInterval = checkpointInterval;
        this.beforeCommit = beforeCommit;
        this.committer = committer ?? throw new ArgumentNullException(nameof(committer));
        rootManifest = new HostManagerDurableRootManifest(
            statePath,
            paths.RootManifestPath,
            DurableStoreKind,
            manifestCommitter ?? throw new ArgumentNullException(nameof(manifestCommitter)));
    }

    public async Task<HostManagerRollbackStateDocument> LoadAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            await EnsureLoadedAsync(cancellationToken);
            return cachedDocument!;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task SaveAsync(
        HostManagerRollbackStateDocument document,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            await EnsureLoadedAsync(cancellationToken);
            var normalized = NormalizeDocument(document);
            if (normalized.NativeHostSessionIncarnation
                != cachedDocument!.NativeHostSessionIncarnation)
            {
                throw new InvalidOperationException(
                    "Native Host session incarnation can only change through an atomic reservation.");
            }
            var now = timeProvider.GetUtcNow();
            var baseline = persistedDocument ?? HostManagerRollbackStateDocument.Empty;
            var materialChanged = !HasSameRecoveryState(baseline, normalized);
            var recoveryCompleted = baseline.LastRestoreAt != normalized.LastRestoreAt;

            // Only an already committed document can suppress a write. A virgin store,
            // pending transient update or failed/ambiguous commit still needs publication.
            if (!materialChanged && !recoveryCompleted
                && canonicalAuthorityObserved && persistedDocument is not null
                && HasSameCheckpointFields(persistedDocument, normalized))
            {
                cachedDocument = normalized;
                dirty = false;
                return;
            }

            if (materialChanged
                || recoveryCompleted
                || now - lastPersistedAt >= checkpointInterval)
            {
                await PersistAndPublishAsync(normalized, now, cancellationToken);
                return;
            }

            cachedDocument = normalized;
            dirty = true;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<HostManagerRollbackStateDocument> ReserveNativeHostSessionIncarnationAsync(
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            await EnsureLoadedAsync(cancellationToken);
            var nextIncarnation = checked(
                cachedDocument!.NativeHostSessionIncarnation + 1);
            var reserved = cachedDocument with
            {
                NativeHostSessionIncarnation = nextIncarnation
            };
            await PersistAndPublishAsync(
                reserved,
                timeProvider.GetUtcNow(),
                cancellationToken);
            return reserved;
        }
        finally
        {
            gate.Release();
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        gate.Wait();
        try
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            if (dirty && cachedDocument is not null)
            {
                PersistAndPublishAsync(
                        cachedDocument,
                        timeProvider.GetUtcNow(),
                        CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
            }
        }
        finally
        {
            ownerLease?.Dispose();
            ownerLease = null;
            gate.Release();
            gate.Dispose();
        }
    }

    private async Task PersistAndPublishAsync(
        HostManagerRollbackStateDocument document,
        DateTimeOffset persistedAt,
        CancellationToken cancellationToken)
    {
        // Snapshot before encoding/awaiting IO. A caller can retain mutable metadata,
        // but it must never mutate the baseline used to suppress a later write.
        var canonicalDocument = SnapshotDocument(document);
        HostManagerRollbackStatePersistedImage persisted;
        try
        {
            persisted = await PersistAsync(
                canonicalDocument,
                checked(persistedRevision + 1),
                cancellationToken);
        }
        catch (HostManagerRollbackStateCommitException exception) when (
            exception.Outcome == HostManagerRollbackStateCommitOutcome.CommitAmbiguous)
        {
            InvalidateCachedAuthority();
            throw;
        }

        cachedDocument = document;
        persistedDocument = canonicalDocument;
        persistedRevision = persisted.Revision;
        persistedCanonicalSha256 = persisted.CanonicalSha256;
        canonicalAuthorityObserved = true;
        lastPersistedAt = persistedAt;
        dirty = false;
    }

    private async Task<HostManagerRollbackStatePersistedImage> PersistAsync(
        HostManagerRollbackStateDocument document,
        ulong revision,
        CancellationToken cancellationToken)
    {
        ValidateCurrentCanonicalBeforeTransition();
        var canonicalImage = HostManagerRollbackStateEnvelopeCodec.Encode(
            document,
            revision);
        EnsureStateDirectory();
        var tempPath = CreateTempPath();
        try
        {
            await using (var stream = new FileStream(
                tempPath,
                new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    BufferSize = 1,
                    Options = FileOptions.Asynchronous | FileOptions.WriteThrough
                }))
            {
                await stream.WriteAsync(canonicalImage, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            HostManagerDurableRootTransition transition;
            try
            {
                rootManifest.EnsureInitializing();
                transition = rootManifest.BeginCanonicalTransition(canonicalImage);
                beforeCommit?.Invoke();
            }
            catch (Exception exception)
            {
                throw new HostManagerRollbackStateCommitException(
                    HostManagerRollbackStateCommitOutcome.NotCommitted,
                    exception);
            }

            committer.Commit(
                tempPath,
                statePath,
                canonicalImage,
                replaceExisting: canonicalAuthorityObserved);
            try
            {
                rootManifest.FinalizeCanonicalTransition(transition);
            }
            catch (Exception exception)
            {
                throw new HostManagerRollbackStateCommitException(
                    HostManagerRollbackStateCommitOutcome.CommitAmbiguous,
                    exception);
            }

            return new HostManagerRollbackStatePersistedImage(
                revision,
                SHA256.HashData(canonicalImage));
        }
        finally
        {
            DeleteTempFile(tempPath);
        }
    }

    private void EnsureStateDirectory()
    {
        var directory = Path.GetDirectoryName(statePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private string CreateTempPath()
    {
        return $"{statePath}.{Guid.NewGuid():N}.tmp";
    }

    private static void DeleteTempFile(string tempPath)
    {
        if (File.Exists(tempPath))
        {
            File.Delete(tempPath);
        }
    }

    private async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (cachedDocument is not null)
        {
            return;
        }

        AcquireOwnerLease();
        var now = timeProvider.GetUtcNow();
        var canonicalImage = await ReadBoundedImageAsync(statePath, cancellationToken);
        if (canonicalImage is not null)
        {
            if (!IsLegacyStateImage(canonicalImage))
            {
                var decoded = HostManagerRollbackStateEnvelopeCodec.Decode(canonicalImage);
                rootManifest.ValidateExistingCanonical(canonicalImage);
                PublishLoadedAuthority(decoded.State, decoded.Revision, canonicalImage, now);
                return;
            }

            var migrated = MigrateDisplayTextState(canonicalImage, statePath, out var revision, out var converted);
            var backup = PreserveExactFile(statePath);
            var manifestBackup = PreserveExactFile(manifestPath);
            if (converted)
            {
                try { rootManifest.ValidateOrAdoptCanonical(canonicalImage); }
                catch (InvalidDataException error)
                {
                    System.Diagnostics.Trace.TraceWarning("Old rollback root cannot be converted; its exact backup is retained: {0}", error.Message);
                    converted = false;
                    migrated = HostManagerRollbackStateDocument.Empty;
                    revision = 0;
                }
            }
            if (!converted) rootManifest.InitializeAfterPreservedReset(backup, manifestBackup);
            cachedDocument = migrated;
            persistedDocument = migrated;
            persistedRevision = revision;
            persistedCanonicalSha256 = SHA256.HashData(canonicalImage);
            canonicalAuthorityObserved = true;
            lastPersistedAt = now;
            await PersistAndPublishAsync(migrated, now, cancellationToken);
            return;
        }

        rootManifest.RequireVirginOrInitializing();
        var legacy = await LoadLegacyStateAsync(cancellationToken);
        if (legacy is null)
        {
            cachedDocument = HostManagerRollbackStateDocument.Empty;
            persistedDocument = HostManagerRollbackStateDocument.Empty;
            persistedRevision = 0;
            persistedCanonicalSha256 = null;
            canonicalAuthorityObserved = false;
            lastPersistedAt = now;
            return;
        }

        cachedDocument = legacy;
        persistedDocument = legacy;
        persistedRevision = 0;
        persistedCanonicalSha256 = null;
        canonicalAuthorityObserved = false;
        lastPersistedAt = now;
        await PersistAndPublishAsync(legacy, now, cancellationToken);
    }

    private async Task<HostManagerRollbackStateDocument?> LoadLegacyStateAsync(
        CancellationToken cancellationToken)
    {
        HostManagerRollbackStateDocument? selected = null;
        byte[]? selectedIdentity = null;
        foreach (var legacyPath in legacyPaths)
        {
            var image = await ReadBoundedImageAsync(legacyPath, cancellationToken);
            if (image is null)
            {
                continue;
            }

            var candidate = !IsLegacyStateImage(image)
                ? HostManagerRollbackStateEnvelopeCodec.Decode(image).State
                : MigrateDisplayTextState(image, legacyPath, out _, out _);
            PreserveExactFile(legacyPath);
            var identity = HostManagerRollbackStateEnvelopeCodec.Encode(candidate, revision: 1);
            if (selectedIdentity is not null
                && !selectedIdentity.AsSpan().SequenceEqual(identity))
            {
                throw new InvalidDataException(
                    "Conflicting legacy Host Manager rollback authorities were found.");
            }

            selected = candidate;
            selectedIdentity = identity;
        }

        return selected;
    }

    private static async Task<byte[]?> ReadBoundedImageAsync(
        string path,
        CancellationToken cancellationToken)
    {
        FileStream stream;
        try
        {
            stream = WindowsNativeAtomicFileCommitter.OpenCommittedRead(
                path, FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }

        await using (stream)
        {
            var length = stream.Length;
            if (length is < 0
                || length > HostManagerRollbackStateEnvelopeCodec.MaximumCanonicalBytes
                || length > int.MaxValue)
            {
                throw new InvalidDataException(
                    "The Host Manager rollback state has an invalid canonical size.");
            }

            var image = GC.AllocateUninitializedArray<byte>(checked((int)length));
            await stream.ReadExactlyAsync(image, cancellationToken);
            return image;
        }
    }

    private static byte[]? ReadBoundedImage(string path)
    {
        FileStream stream;
        try
        {
            stream = WindowsNativeAtomicFileCommitter.OpenCommittedRead(path, FileOptions.SequentialScan);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }

        using (stream)
        {
            var length = stream.Length;
            if (length is < 0
                || length > HostManagerRollbackStateEnvelopeCodec.MaximumCanonicalBytes
                || length > int.MaxValue)
            {
                throw new InvalidDataException(
                    "The Host Manager rollback state has an invalid canonical size.");
            }

            var image = GC.AllocateUninitializedArray<byte>(checked((int)length));
            stream.ReadExactly(image);
            return image;
        }
    }

    private void ValidateCurrentCanonicalBeforeTransition()
    {
        var image = ReadBoundedImage(statePath);
        if (image is null)
        {
            if (canonicalAuthorityObserved)
            {
                throw new IOException(
                    "The initialized Host Manager rollback authority lost its canonical file.");
            }

            rootManifest.RequireVirginOrInitializing();
            return;
        }

        if (!canonicalAuthorityObserved || persistedCanonicalSha256 is null)
        {
            throw new InvalidDataException(
                "Host Manager rollback state appeared outside its active owner.");
        }

        var currentSha256 = SHA256.HashData(image);
        if (!CryptographicOperations.FixedTimeEquals(
                currentSha256,
                persistedCanonicalSha256))
        {
            throw new InvalidDataException(
                "Host Manager rollback state changed outside its active owner.");
        }

        // The hash binds the exact image/revision decoded on load or verified on commit.
        // Decoding identical JSON cannot establish any additional authority.
        if (persistedRevision == 0) rootManifest.ValidateOrAdoptCanonical(image);
        else rootManifest.ValidateExistingCanonical(image);
    }

    private void PublishLoadedAuthority(
        HostManagerRollbackStateDocument document,
        ulong revision,
        ReadOnlySpan<byte> canonicalImage,
        DateTimeOffset loadedAt)
    {
        cachedDocument = document;
        persistedDocument = SnapshotDocument(document);
        persistedRevision = revision;
        persistedCanonicalSha256 = SHA256.HashData(canonicalImage);
        canonicalAuthorityObserved = true;
        lastPersistedAt = loadedAt;
        dirty = false;
    }

    private void InvalidateCachedAuthority()
    {
        cachedDocument = null;
        persistedDocument = null;
        persistedCanonicalSha256 = null;
        persistedRevision = 0;
        canonicalAuthorityObserved = false;
        dirty = false;
    }

    private void AcquireOwnerLease()
    {
        if (ownerLease is not null)
        {
            return;
        }

        EnsureOwnerLeaseDirectory();
        try
        {
            ownerLease = new FileStream(
                ownerLeasePath,
                new FileStreamOptions
                {
                    Mode = FileMode.OpenOrCreate,
                    Access = FileAccess.ReadWrite,
                    Share = FileShare.None,
                    BufferSize = 1,
                    Options = FileOptions.WriteThrough
                });
        }
        catch (IOException exception)
        {
            throw new HostManagerRollbackStateOwnerLeaseException(
                ownerLeasePath,
                exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new HostManagerRollbackStateOwnerLeaseException(
                ownerLeasePath,
                exception);
        }
    }

    private void EnsureOwnerLeaseDirectory()
    {
        var directory = Path.GetDirectoryName(ownerLeasePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    internal static HostManagerRollbackStateStorePaths ResolveProductionPaths(
        string contentRootPath,
        string stableInstallRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRootPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(stableInstallRoot);
        if (!Path.IsPathFullyQualified(stableInstallRoot))
        {
            throw new ArgumentException(
                "The stable Resource Manager install root must be absolute.",
                nameof(stableInstallRoot));
        }

        var normalizedStableRoot = Path.GetFullPath(stableInstallRoot);
        var packageRoot = Path.GetFullPath(
            PackagePathResolver.ResolvePackageRoot(contentRootPath));
        var storePaths = HostManagerDurableStorePaths.ResolveRollbackState(normalizedStableRoot);
        var rollbackRoot = Path.GetDirectoryName(storePaths.ManifestPath)!;
        var canonicalPath = storePaths.CanonicalPath;
        var ownerLeasePath = Path.Combine(
            rollbackRoot,
            "rollback-state.owner.lock");
        var rootManifestPath = storePaths.ManifestPath;
        var legacyPaths = new[]
            {
                Path.Combine(
                    packageRoot,
                    "Config",
                    "smart-optimization-state.json"),
                Path.Combine(
                    normalizedStableRoot,
                    "Config",
                    "smart-optimization-state.json")
            }
            .Select(Path.GetFullPath)
            .Where(path => !path.Equals(canonicalPath, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new HostManagerRollbackStateStorePaths(
            canonicalPath,
            ownerLeasePath,
            rootManifestPath,
            legacyPaths);
    }

    private static HostManagerRollbackStateStorePaths ResolveDirectPaths(
        string statePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(statePath);
        var canonicalPath = Path.GetFullPath(statePath);
        return new HostManagerRollbackStateStorePaths(
            canonicalPath,
            $"{canonicalPath}.owner.lock",
            $"{canonicalPath}.root.manifest",
            []);
    }

    private static HostManagerRollbackStateStorePaths ResolveProductionPaths(
        IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        return ResolveProductionPaths(
            environment.ContentRootPath,
            HostManagerDurableDataRootResolver.ResolveStableInstallRoot(
                environment.ContentRootPath,
                allowUnregisteredFallback: environment.IsDevelopment()));
    }

    private static bool HasSameRecoveryState(
        HostManagerRollbackStateDocument left,
        HostManagerRollbackStateDocument right)
    {
        return AppliedPlacementsEqual(left.AppliedPlacements, right.AppliedPlacements)
            && left.NativeHostSessionIncarnation == right.NativeHostSessionIncarnation;
    }

    // Recovery fields/record metadata were already compared by HasSameRecoveryState.
    private static bool HasSameCheckpointFields(
        HostManagerRollbackStateDocument left, HostManagerRollbackStateDocument right)
    {
        if (left.Version != right.Version || left.LastRunAt != right.LastRunAt
            || left.LastRestoreAt != right.LastRestoreAt || left.Coordinator != right.Coordinator)
            return false;
        for (var index = 0; index < left.AppliedPlacements.Count; index++)
        {
            var before = left.AppliedPlacements[index];
            var after = right.AppliedPlacements[index];
            if (before.TargetId != after.TargetId || before.SoftwareId != after.SoftwareId
                || before.ResourceKind != after.ResourceKind || before.ProcessId != after.ProcessId
                || before.ProcessStartKey != after.ProcessStartKey || before.ProcessName != after.ProcessName
                || before.AppliedAt != after.AppliedAt || before.UpdatedAt != after.UpdatedAt)
                return false;
            for (var record = 0; record < before.Records.Count; record++)
                if (before.Records[record].Kind != after.Records[record].Kind
                    || before.Records[record].RecordId != after.Records[record].RecordId)
                    return false;
        }
        return true;
    }

    private static HostManagerRollbackStateDocument SnapshotDocument(HostManagerRollbackStateDocument document)
        => document with
        {
            AppliedPlacements = document.AppliedPlacements.Select(placement => placement with
            {
                Records = placement.Records.Select(record => record with
                {
                    Metadata = record.Metadata is null ? null
                        : new Dictionary<string, string>(record.Metadata, StringComparer.Ordinal)
                }).ToArray()
            }).ToArray()
        };

    private static bool AppliedPlacementsEqual(
        IReadOnlyList<HostManagerAppliedPlacementReceipt> left,
        IReadOnlyList<HostManagerAppliedPlacementReceipt> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            var leftItem = left[index];
            var rightItem = right[index];
            if (!string.Equals(leftItem.TargetId, rightItem.TargetId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(leftItem.SoftwareId, rightItem.SoftwareId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(leftItem.ResourceKind, rightItem.ResourceKind, StringComparison.OrdinalIgnoreCase)
                || !RecordsEqual(leftItem.Records, rightItem.Records))
            {
                return false;
            }
        }

        return true;
    }

    private static bool RecordsEqual(
        IReadOnlyList<HostManagerAppliedRecord> left,
        IReadOnlyList<HostManagerAppliedRecord> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            var leftItem = left[index];
            var rightItem = right[index];
            if (!string.Equals(leftItem.Kind, rightItem.Kind, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(leftItem.RecordId, rightItem.RecordId, StringComparison.OrdinalIgnoreCase)
                || !MetadataEqual(leftItem.Metadata, rightItem.Metadata))
            {
                return false;
            }
        }

        return true;
    }

    private static bool MetadataEqual(
        IReadOnlyDictionary<string, string>? left,
        IReadOnlyDictionary<string, string>? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null || left.Count != right.Count)
        {
            return false;
        }

        foreach (var (key, value) in left)
        {
            if (!right.TryGetValue(key, out var rightValue)
                || !string.Equals(value, rightValue, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    internal static HostManagerRollbackStateDocument NormalizeDocument(
        HostManagerRollbackStateDocument? document)
    {
        if (document is null)
        {
            throw new InvalidDataException("Host Manager rollback state document cannot be JSON null.");
        }
        if (document.Version != HostManagerRollbackStateDocument.CurrentVersion)
        {
            throw new InvalidDataException(
                $"Host Manager rollback state version {document.Version} is unsupported.");
        }

        return new HostManagerRollbackStateDocument(
            HostManagerRollbackStateDocument.CurrentVersion,
            document.LastRunAt,
            document.LastRestoreAt,
            NormalizePlacements(document.AppliedPlacements ?? []))
        {
            NativeHostSessionIncarnation = document.NativeHostSessionIncarnation,
            Coordinator = document.Coordinator
        };
    }

    internal static HostManagerRollbackStateDocument ReadStoredDocument(
        HostManagerRollbackStateDocument? document)
    {
        var normalized = NormalizeDocument(document);
        return NormalizeDocument(normalized with
        {
            AppliedPlacements = normalized.AppliedPlacements
                .Select(placement => placement with
                {
                    Records = placement.Records
                        .Select(record => record with
                        {
                            Kind = HostManagerAppliedRecordKinds.ReadStored(record.Kind)
                        })
                        .ToArray()
                })
                .ToArray()
        });
    }

    private static IReadOnlyList<HostManagerAppliedPlacementReceipt> NormalizePlacements(
        IReadOnlyList<HostManagerAppliedPlacementReceipt> placements)
    {
        var normalized = new Dictionary<HostManagerPlacementReceiptKey, HostManagerAppliedPlacementReceipt>();
        foreach (var placement in placements)
        {
            if (string.IsNullOrWhiteSpace(placement.TargetId)
                || string.IsNullOrWhiteSpace(placement.ResourceKind)
                || placement.Records is null
                || placement.Records.Count == 0)
            {
                throw new InvalidDataException("Host Manager placement receipt has an invalid identity or no records.");
            }

            var records = new Dictionary<(string Kind, string RecordId), HostManagerAppliedRecord>();
            foreach (var record in placement.Records)
            {
                if (string.IsNullOrWhiteSpace(record.Kind)
                    || string.IsNullOrWhiteSpace(record.RecordId))
                {
                    throw new InvalidDataException("Host Manager placement record has an invalid identity.");
                }
                var recordKey = (record.Kind.Trim().ToUpperInvariant(), record.RecordId.Trim().ToUpperInvariant());
                if (!records.TryAdd(recordKey, record))
                {
                    throw new InvalidDataException(
                        $"Host Manager placement {placement.ResourceKind}/{placement.TargetId} contains duplicate record {record.Kind}/{record.RecordId}.");
                }
            }

            var key = HostManagerPlacementReceiptKey.Create(placement);
            if (!normalized.TryAdd(
                key,
                placement with
                {
                    Records = records.Values
                        .OrderBy(static record => record.Kind, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(static record => record.RecordId, StringComparer.OrdinalIgnoreCase)
                        .ToArray()
                }))
            {
                throw new InvalidDataException(
                    $"Host Manager rollback state contains duplicate placement {placement.ResourceKind}/{placement.TargetId}.");
            }
        }

        return normalized.Values
            .OrderBy(static placement => placement.ResourceKind, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static placement => placement.TargetId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }
}

internal readonly record struct HostManagerRollbackStateStorePaths(
    string CanonicalPath,
    string OwnerLeasePath,
    string RootManifestPath,
    IReadOnlyList<string> LegacyPaths);

internal readonly record struct HostManagerRollbackStatePersistedImage(
    ulong Revision,
    byte[] CanonicalSha256);
