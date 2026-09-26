using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Paths;

namespace ResourceManager.App.Infrastructure.Optimization;

public sealed class JsonHostManagerRollbackStateStore : IHostManagerRollbackStateStore, IDisposable
{
    internal static readonly TimeSpan DefaultCheckpointInterval = TimeSpan.FromMinutes(1);
    private const string DurableStoreKind = "rollback-state";
    private const string StorageGenerationDirectory = "generation-00000001";
    private const string CanonicalFileName = "rollback-state.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = HostManagerRollbackStateEnvelopeCodec.MaximumJsonDepth
    };

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string statePath;
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
                NativeHostSessionIncarnation = nextIncarnation,
                Message = $"Host Manager reserved native host session incarnation {nextIncarnation}."
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
        HostManagerRollbackStatePersistedImage persisted;
        try
        {
            persisted = await PersistAsync(
                document,
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
        persistedDocument = document;
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
                    BufferSize = 64 * 1024,
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
            if (IsEnvelopeImage(canonicalImage))
            {
                var decoded = HostManagerRollbackStateEnvelopeCodec.Decode(canonicalImage);
                rootManifest.ValidateExistingCanonical(canonicalImage);
                PublishLoadedAuthority(
                    decoded.State,
                    decoded.Revision,
                    canonicalImage,
                    now);
                return;
            }

            var migrated = DecodeLegacyState(canonicalImage);
            rootManifest.ValidateOrAdoptCanonical(canonicalImage);
            cachedDocument = migrated;
            persistedDocument = migrated;
            persistedRevision = 0;
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

            var candidate = IsEnvelopeImage(image)
                ? HostManagerRollbackStateEnvelopeCodec.Decode(image).State
                : DecodeLegacyState(image);
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

    private static HostManagerRollbackStateDocument DecodeLegacyState(byte[] image)
    {
        using var json = JsonDocument.Parse(
            image.AsMemory(),
            new JsonDocumentOptions
            {
                MaxDepth = HostManagerRollbackStateEnvelopeCodec.MaximumJsonDepth
            });
        var version = ReadVersion(json.RootElement);
        return version switch
        {
            HostManagerRollbackStateDocument.CurrentVersion => NormalizeDocument(
                json.RootElement.Deserialize<HostManagerRollbackStateDocument>(JsonOptions)),
            1 => MigrateVersion1(
                json.RootElement.Deserialize<LegacyHostManagerRollbackStateDocumentV1>(JsonOptions)),
            _ => throw new InvalidDataException(
                $"Host Manager rollback state version {version} is unsupported.")
        };
    }

    private static bool IsEnvelopeImage(byte[] image)
    {
        using var json = JsonDocument.Parse(
            image.AsMemory(),
            new JsonDocumentOptions
            {
                MaxDepth = HostManagerRollbackStateEnvelopeCodec.MaximumJsonDepth
            });
        return json.RootElement.ValueKind == JsonValueKind.Object
            && json.RootElement.TryGetProperty("schemaVersion", out _);
    }

    private static async Task<byte[]?> ReadBoundedImageAsync(
        string path,
        CancellationToken cancellationToken)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(
                path,
                new FileStreamOptions
                {
                    Mode = FileMode.Open,
                    Access = FileAccess.Read,
                    Share = FileShare.Read,
                    BufferSize = 64 * 1024,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan
                });
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
            if (length is <= 0
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
            stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.SequentialScan);
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
            if (length is <= 0
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

        if (persistedRevision == 0)
        {
            _ = DecodeLegacyState(image);
            rootManifest.ValidateOrAdoptCanonical(image);
        }
        else
        {
            var decoded = HostManagerRollbackStateEnvelopeCodec.Decode(image);
            if (decoded.Revision != persistedRevision)
            {
                throw new InvalidDataException(
                    "Host Manager rollback state revision changed outside its active owner.");
            }

            rootManifest.ValidateExistingCanonical(image);
        }
    }

    private void PublishLoadedAuthority(
        HostManagerRollbackStateDocument document,
        ulong revision,
        ReadOnlySpan<byte> canonicalImage,
        DateTimeOffset loadedAt)
    {
        cachedDocument = document;
        persistedDocument = document;
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
        var rollbackRoot = Path.GetFullPath(Path.Combine(
            normalizedStableRoot,
            "UserData",
            "HostManager",
            "RollbackState"));
        var canonicalPath = Path.GetFullPath(Path.Combine(
            rollbackRoot,
            StorageGenerationDirectory,
            CanonicalFileName));
        var ownerLeasePath = Path.Combine(
            rollbackRoot,
            "rollback-state.owner.lock");
        var rootManifestPath = Path.Combine(
            rollbackRoot,
            "rollback-state.root.manifest");
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
            string.IsNullOrWhiteSpace(document.Message) ? HostManagerRollbackStateDocument.Empty.Message : document.Message,
            NormalizePlacements(document.AppliedPlacements ?? []))
        {
            NativeHostSessionIncarnation = document.NativeHostSessionIncarnation
        };
    }

    private static int ReadVersion(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("version", out var versionElement)
            || versionElement.ValueKind != JsonValueKind.Number
            || !versionElement.TryGetInt32(out var version))
        {
            throw new InvalidDataException("Host Manager rollback state has no valid version.");
        }

        return version;
    }

    private static HostManagerRollbackStateDocument MigrateVersion1(
        LegacyHostManagerRollbackStateDocumentV1? legacy)
    {
        if (legacy is null)
        {
            throw new InvalidDataException("Host Manager rollback state document cannot be JSON null.");
        }
        if (legacy.Version != 1)
        {
            throw new InvalidDataException(
                $"Host Manager rollback state version {legacy.Version} is unsupported.");
        }
        if (HasItems(legacy.PendingChanges)
            || HasItems(legacy.AppliedTargets)
            || HasItems(legacy.NativeActionAttempts))
        {
            throw new InvalidDataException(
                "Host Manager rollback state v1 contains retired action authority and cannot be upgraded safely.");
        }

        return NormalizeDocument(new HostManagerRollbackStateDocument(
            HostManagerRollbackStateDocument.CurrentVersion,
            legacy.LastRunAt,
            legacy.LastRestoreAt,
            legacy.Message ?? HostManagerRollbackStateDocument.Empty.Message,
            legacy.AppliedPlacements ?? [])
        {
            NativeHostSessionIncarnation = legacy.NativeHostSessionIncarnation
        });
    }

    private static bool HasItems(JsonElement[]? values)
        => values is { Length: > 0 };

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed class LegacyHostManagerRollbackStateDocumentV1
    {
        public int Version { get; init; }

        public JsonElement[]? PendingChanges { get; init; }

        public JsonElement[]? AppliedTargets { get; init; }

        public DateTimeOffset? LastRunAt { get; init; }

        public DateTimeOffset? LastRestoreAt { get; init; }

        public string? Message { get; init; }

        public IReadOnlyList<HostManagerAppliedPlacementReceipt>? AppliedPlacements { get; init; }

        public JsonElement[]? NativeActionAttempts { get; init; }

        public ulong NativeHostSessionIncarnation { get; init; }
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
