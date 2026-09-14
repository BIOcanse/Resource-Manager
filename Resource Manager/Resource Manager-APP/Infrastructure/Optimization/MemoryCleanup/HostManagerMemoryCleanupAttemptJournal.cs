using System.Security.Cryptography;
using System.Text.Json;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Domain.Optimization.MemoryCleanup;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Paths;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace ResourceManager.App.Infrastructure.Optimization;

internal readonly record struct HostManagerMemoryCleanupAttemptBatch(
    Guid BatchId,
    IReadOnlySet<HostManagerComputeProcessIdentity> Processes,
    ulong AttemptGeneration = 0);

public sealed class HostManagerMemoryCleanupAttemptJournal : IDisposable
{
    private const uint SchemaVersion = 1;
    private const long MaximumDocumentBytes = 4L * 1024 * 1024;
    private const string CanonicalFileName = "host-manager-memory-cleanup-attempts.json";
    private const string DurableStoreKind = "memory-cleanup-attempt-journal";
    private const string StorageGenerationDirectory = "generation-00000001";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly object gate = new();
    private readonly string statePath;
    private readonly string ownerLeasePath;
    private readonly IReadOnlyList<HostManagerMemoryCleanupAttemptLegacySource> legacySources;
    private readonly Func<int> capacityProvider;
    private readonly TimeProvider timeProvider;
    private readonly IHostManagerMemoryCleanupAttemptFileCommitter committer;
    private readonly IHostManagerDurableRootManifestCommitter manifestCommitter;
    private readonly IHostManagerMemoryCleanupLegacyFileRetirer legacyRetirer;
    private readonly HostManagerDurableRootManifest rootManifest;
    private FileStream? ownerLease;
    private IReadOnlyList<FileStream>? legacyOwnerLeases;
    private HostManagerMemoryCleanupAttemptDocument? current;
    private bool canonicalAuthorityObserved;
    private bool disposed;

    public HostManagerMemoryCleanupAttemptJournal(
        IHostEnvironment environment,
        HostManagerMemoryCleanupRuntime runtime,
        TimeProvider timeProvider)
        : this(
            ResolveProductionPaths(environment),
            () => runtime.CaptureDesired().Recreate.StateCapacity,
            timeProvider)
    {
    }

    internal HostManagerMemoryCleanupAttemptJournal(
        HostManagerMemoryCleanupAttemptJournalPaths paths,
        Func<int> capacityProvider,
        TimeProvider timeProvider)
        : this(
            paths.CanonicalPath,
            paths.LegacySources,
            capacityProvider,
            timeProvider,
            WindowsHostManagerMemoryCleanupAttemptFileCommitter.Instance,
            WindowsHostManagerDurableRootManifestCommitter.Instance,
            WindowsHostManagerMemoryCleanupLegacyFileRetirer.Instance)
    {
    }

    internal HostManagerMemoryCleanupAttemptJournal(
        string statePath,
        Func<int> capacityProvider,
        TimeProvider timeProvider)
        : this(
            statePath,
            legacyStatePath: null,
            capacityProvider,
            timeProvider,
            WindowsHostManagerMemoryCleanupAttemptFileCommitter.Instance,
            WindowsHostManagerDurableRootManifestCommitter.Instance)
    {
    }

    internal HostManagerMemoryCleanupAttemptJournal(
        string statePath,
        string? legacyStatePath,
        Func<int> capacityProvider,
        TimeProvider timeProvider,
        IHostManagerMemoryCleanupAttemptFileCommitter committer,
        IHostManagerDurableRootManifestCommitter manifestCommitter)
        : this(
            statePath,
            legacyStatePath is null ? [] : [legacyStatePath],
            capacityProvider,
            timeProvider,
            committer,
            manifestCommitter,
            WindowsHostManagerMemoryCleanupLegacyFileRetirer.Instance)
    {
    }

    internal HostManagerMemoryCleanupAttemptJournal(
        string statePath,
        IReadOnlyList<string> legacyStatePaths,
        Func<int> capacityProvider,
        TimeProvider timeProvider,
        IHostManagerMemoryCleanupAttemptFileCommitter committer,
        IHostManagerDurableRootManifestCommitter manifestCommitter,
        IHostManagerMemoryCleanupLegacyFileRetirer legacyRetirer)
        : this(
            statePath,
            CreateLegacySources(legacyStatePaths),
            capacityProvider,
            timeProvider,
            committer,
            manifestCommitter,
            legacyRetirer)
    {
    }

    internal HostManagerMemoryCleanupAttemptJournal(
        string statePath,
        IReadOnlyList<HostManagerMemoryCleanupAttemptLegacySource> legacySources,
        Func<int> capacityProvider,
        TimeProvider timeProvider,
        IHostManagerMemoryCleanupAttemptFileCommitter committer,
        IHostManagerDurableRootManifestCommitter manifestCommitter,
        IHostManagerMemoryCleanupLegacyFileRetirer legacyRetirer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(statePath);
        ArgumentNullException.ThrowIfNull(legacySources);
        ArgumentNullException.ThrowIfNull(capacityProvider);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(committer);
        ArgumentNullException.ThrowIfNull(manifestCommitter);
        ArgumentNullException.ThrowIfNull(legacyRetirer);
        if (!Path.IsPathFullyQualified(statePath))
        {
            throw new ArgumentException(
                "The memory-cleanup attempt journal path must be absolute.",
                nameof(statePath));
        }

        this.statePath = Path.GetFullPath(statePath);
        var normalizedLegacySources = new List<HostManagerMemoryCleanupAttemptLegacySource>(
            legacySources.Count);
        var sourceIndexes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var legacySource in legacySources)
        {
            var legacyStatePath = legacySource.Path;
            ArgumentException.ThrowIfNullOrWhiteSpace(legacyStatePath);
            if (!Path.IsPathFullyQualified(legacyStatePath))
            {
                throw new ArgumentException(
                    "The legacy memory-cleanup attempt journal path must be absolute.",
                    nameof(legacyStatePath));
            }

            var normalizedLegacyPath = Path.GetFullPath(legacyStatePath);
            if (string.Equals(
                normalizedLegacyPath,
                this.statePath,
                StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    "The legacy and canonical memory-cleanup attempt journal paths must differ.",
                    nameof(legacySources));
            }

            if (sourceIndexes.TryGetValue(normalizedLegacyPath, out var existingIndex))
            {
                if (legacySource.UsesDurableRootManifest
                    && !normalizedLegacySources[existingIndex].UsesDurableRootManifest)
                {
                    normalizedLegacySources[existingIndex] = normalizedLegacySources[existingIndex] with
                    {
                        UsesDurableRootManifest = true
                    };
                }
                continue;
            }

            sourceIndexes.Add(normalizedLegacyPath, normalizedLegacySources.Count);
            normalizedLegacySources.Add(legacySource with { Path = normalizedLegacyPath });
        }
        this.legacySources = normalizedLegacySources;
        ownerLeasePath = $"{this.statePath}.owner.lock";
        this.capacityProvider = capacityProvider;
        this.timeProvider = timeProvider;
        this.committer = committer;
        this.manifestCommitter = manifestCommitter;
        this.legacyRetirer = legacyRetirer;
        rootManifest = new HostManagerDurableRootManifest(
            this.statePath,
            DurableStoreKind,
            manifestCommitter);
    }

    internal static HostManagerMemoryCleanupAttemptJournalPaths ResolveProductionPaths(
        string contentRootPath)
        => ResolveProductionPaths(
            contentRootPath,
            HostManagerDurableDataRootResolver.ResolveStableInstallRoot(
                contentRootPath,
                registeredInstallRoot: null));

    internal static HostManagerMemoryCleanupAttemptJournalPaths ResolveProductionPaths(
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
        var canonicalPath = Path.GetFullPath(Path.Combine(
            normalizedStableRoot,
            "UserData",
            "HostManager",
            "MemoryCleanup",
            StorageGenerationDirectory,
            CanonicalFileName));
        var legacySources = new List<HostManagerMemoryCleanupAttemptLegacySource>(capacity: 4);
        var distinctPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            canonicalPath
        };

        AddLegacyPath(normalizedStableRoot, "UserData");
        AddLegacyPath(packageRoot, "UserData");
        AddLegacyPath(normalizedStableRoot, "Config");
        AddLegacyPath(packageRoot, "Config");
        return new(canonicalPath, legacySources);

        void AddLegacyPath(string root, string topLevelDirectory)
        {
            var path = topLevelDirectory.Equals("UserData", StringComparison.Ordinal)
                ? Path.GetFullPath(Path.Combine(
                    root,
                    topLevelDirectory,
                    "HostManager",
                    "MemoryCleanup",
                    CanonicalFileName))
                : Path.GetFullPath(Path.Combine(
                    root,
                    topLevelDirectory,
                    CanonicalFileName));
            if (distinctPaths.Add(path))
            {
                legacySources.Add(new(
                    path,
                    UsesDurableRootManifest: topLevelDirectory.Equals(
                        "UserData",
                        StringComparison.Ordinal)));
            }
        }
    }

    internal IReadOnlySet<HostManagerComputeProcessIdentity> ReconcileAndCaptureBlocked(
        Func<int, RecoveryReadResult<ProcessInstanceRecoverySnapshot>> readProcess)
    {
        ArgumentNullException.ThrowIfNull(readProcess);
        lock (gate)
        {
            EnsureAvailable();
            var document = LoadAndValidate();
            var next = new List<HostManagerMemoryCleanupAttemptEntry>(document.Entries.Count);
            var changed = false;
            foreach (var entry in document.Entries)
            {
                if (entry.Phase ==
                    HostManagerMemoryCleanupAttemptPhase.PreparedBeforeWriter)
                {
                    changed = true;
                    continue;
                }
                var read = readProcess(entry.ProcessId);
                if (read.Status == RecoveryReadStatus.NotFoundOrExited)
                {
                    changed = true;
                    continue;
                }
                if (read.Status == RecoveryReadStatus.Found
                    && read.Value is not null)
                {
                    ulong startKey;
                    try
                    {
                        startKey = checked((ulong)read.Value.StartedAt.ToFileTime());
                    }
                    catch (ArgumentOutOfRangeException)
                    {
                        throw new InvalidDataException(
                            "The memory-cleanup attempt journal received an invalid process start time.");
                    }
                    if (read.Value.ProcessId != entry.ProcessId
                        || startKey != entry.ProcessStartKey)
                    {
                        changed = true;
                        continue;
                    }
                }

                var normalized = entry.Phase == HostManagerMemoryCleanupAttemptPhase.Applying
                    ? entry with { Phase = HostManagerMemoryCleanupAttemptPhase.Unknown }
                    : entry;
                changed |= normalized != entry;
                next.Add(normalized);
            }

            if (changed)
            {
                document = Persist(next);
            }

            return document.Entries
                .Select(static entry => new HostManagerComputeProcessIdentity(
                    entry.ProcessId,
                    entry.ProcessStartKey))
                .ToHashSet();
        }
    }

    internal HostManagerMemoryCleanupAttemptBatch Prepare(
        ulong attemptGeneration,
        IReadOnlyList<AutomaticMemoryCleanupDecision> decisions)
        => PrepareCore(
            Guid.NewGuid(),
            attemptGeneration,
            decisions,
            HostManagerMemoryCleanupAttemptPhase.Applying);

    internal HostManagerMemoryCleanupAttemptBatch PrepareBeforeWriter(
        ulong attemptGeneration,
        IReadOnlyList<AutomaticMemoryCleanupDecision> decisions)
        => PrepareCore(
            Guid.NewGuid(),
            attemptGeneration,
            decisions,
            HostManagerMemoryCleanupAttemptPhase.PreparedBeforeWriter);

    internal HostManagerMemoryCleanupAttemptBatch PrepareBeforeWriter(
        Guid batchId,
        ulong attemptGeneration,
        IReadOnlyList<AutomaticMemoryCleanupDecision> decisions)
        => PrepareCore(
            batchId,
            attemptGeneration,
            decisions,
            HostManagerMemoryCleanupAttemptPhase.PreparedBeforeWriter);

    private HostManagerMemoryCleanupAttemptBatch PrepareCore(
        Guid batchId,
        ulong attemptGeneration,
        IReadOnlyList<AutomaticMemoryCleanupDecision> decisions,
        HostManagerMemoryCleanupAttemptPhase phase)
    {
        ArgumentNullException.ThrowIfNull(decisions);
        if (batchId == Guid.Empty || attemptGeneration == 0 || decisions.Count == 0)
        {
            throw new ArgumentOutOfRangeException(
                attemptGeneration == 0 ? nameof(attemptGeneration) : nameof(decisions));
        }

        lock (gate)
        {
            EnsureAvailable();
            var document = LoadAndValidate();
            var capacity = RequireCapacity();
            if (document.Entries.Count > capacity - decisions.Count)
            {
                throw new InvalidOperationException(
                    "The memory-cleanup attempt journal has no free capacity.");
            }

            var identities = new HashSet<HostManagerComputeProcessIdentity>();
            var occupied = document.Entries
                .Select(static entry => new HostManagerComputeProcessIdentity(
                    entry.ProcessId,
                    entry.ProcessStartKey))
                .ToHashSet();
            var additions = new HostManagerMemoryCleanupAttemptEntry[decisions.Count];
            for (var index = 0; index < decisions.Count; index++)
            {
                var candidate = decisions[index].Candidate;
                ulong processStartKey;
                try
                {
                    processStartKey = checked((ulong)candidate.ProcessStartedAt.ToFileTime());
                }
                catch (ArgumentOutOfRangeException)
                {
                    throw new InvalidDataException(
                        "A memory-cleanup decision has an invalid process start time.");
                }
                var identity = new HostManagerComputeProcessIdentity(
                    candidate.ProcessId,
                    processStartKey);
                var targetKey = NativeStableIdentity.CreateCaseInsensitiveKey(candidate.TargetId);
                if (candidate.ProcessId <= 0
                    || processStartKey == 0
                    || targetKey == 0
                    || !identities.Add(identity)
                    || occupied.Contains(identity))
                {
                    throw new InvalidDataException(
                        "A memory-cleanup attempt must contain unique unblocked process identities.");
                }

                additions[index] = new(
                    batchId,
                    candidate.ProcessId,
                    processStartKey,
                    targetKey,
                    attemptGeneration,
                    phase,
                    timeProvider.GetUtcNow().UtcTicks);
            }

            _ = Persist([.. document.Entries, .. additions]);
            return new(batchId, identities, attemptGeneration);
        }
    }

    internal void ArmForWriter(HostManagerMemoryCleanupAttemptBatch batch)
    {
        lock (gate)
        {
            EnsureAvailable();
            var document = LoadAndValidate();
            RequireExactBatch(
                document,
                batch,
                HostManagerMemoryCleanupAttemptPhase.PreparedBeforeWriter);
            var next = document.Entries
                .Select(entry => entry.BatchId == batch.BatchId
                    ? entry with { Phase = HostManagerMemoryCleanupAttemptPhase.Applying }
                    : entry)
                .ToArray();
            _ = Persist(next);
        }
    }

    internal void RetirePreparedBeforeWriter(HostManagerMemoryCleanupAttemptBatch batch)
    {
        lock (gate)
        {
            EnsureAvailable();
            var document = LoadAndValidate();
            RequireExactBatch(
                document,
                batch,
                HostManagerMemoryCleanupAttemptPhase.PreparedBeforeWriter);
            _ = Persist(document.Entries
                .Where(entry => entry.BatchId != batch.BatchId)
                .ToArray());
        }
    }

    internal IReadOnlySet<HostManagerComputeProcessIdentity> CapturePendingIdentities()
    {
        lock (gate)
        {
            EnsureAvailable();
            return LoadAndValidate().Entries
                .Select(static entry => new HostManagerComputeProcessIdentity(
                    entry.ProcessId,
                    entry.ProcessStartKey))
                .ToHashSet();
        }
    }

    internal IReadOnlyList<HostManagerMemoryCleanupAttemptBatch> CapturePendingBatches()
    {
        lock (gate)
        {
            EnsureAvailable();
            return LoadAndValidate().Entries
                .GroupBy(static entry => new
                {
                    entry.BatchId,
                    entry.AttemptGeneration
                })
                .OrderBy(static group => group.Key.BatchId)
                .Select(static group => new HostManagerMemoryCleanupAttemptBatch(
                    group.Key.BatchId,
                    group.Select(static entry => new HostManagerComputeProcessIdentity(
                        entry.ProcessId,
                        entry.ProcessStartKey)).ToHashSet(),
                    group.Key.AttemptGeneration))
                .ToArray();
        }
    }

    internal void Settle(
        HostManagerMemoryCleanupAttemptBatch batch,
        IReadOnlySet<HostManagerComputeProcessIdentity> terminalProcesses)
    {
        ArgumentNullException.ThrowIfNull(batch.Processes);
        ArgumentNullException.ThrowIfNull(terminalProcesses);
        if (batch.BatchId == Guid.Empty
            || batch.Processes.Count == 0
            || terminalProcesses.Any(identity => !batch.Processes.Contains(identity)))
        {
            throw new ArgumentException("The memory-cleanup settlement is not bound to its prepared batch.");
        }

        lock (gate)
        {
            EnsureAvailable();
            var document = LoadAndValidate();
            RequireExactBatch(
                document,
                batch,
                HostManagerMemoryCleanupAttemptPhase.Applying,
                HostManagerMemoryCleanupAttemptPhase.Unknown);
            var next = new List<HostManagerMemoryCleanupAttemptEntry>(document.Entries.Count);
            foreach (var entry in document.Entries)
            {
                if (entry.BatchId != batch.BatchId)
                {
                    next.Add(entry);
                    continue;
                }

                var identity = new HostManagerComputeProcessIdentity(
                    entry.ProcessId,
                    entry.ProcessStartKey);
                if (!terminalProcesses.Contains(identity))
                {
                    next.Add(entry with { Phase = HostManagerMemoryCleanupAttemptPhase.Unknown });
                }
            }
            _ = Persist(next);
        }
    }

    internal void MarkUnknown(HostManagerMemoryCleanupAttemptBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch.Processes);
        if (batch.BatchId == Guid.Empty || batch.Processes.Count == 0)
        {
            throw new ArgumentException("The memory-cleanup batch identity is invalid.", nameof(batch));
        }

        lock (gate)
        {
            EnsureAvailable();
            var document = LoadAndValidate();
            RequireExactBatch(
                document,
                batch,
                HostManagerMemoryCleanupAttemptPhase.Applying,
                HostManagerMemoryCleanupAttemptPhase.Unknown);
            var next = document.Entries
                .Select(entry => entry.BatchId == batch.BatchId
                    ? entry with { Phase = HostManagerMemoryCleanupAttemptPhase.Unknown }
                    : entry)
                .ToArray();
            _ = Persist(next);
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            ownerLease?.Dispose();
            ownerLease = null;
            if (legacyOwnerLeases is not null)
            {
                foreach (var legacyOwnerLease in legacyOwnerLeases)
                {
                    legacyOwnerLease.Dispose();
                }
                legacyOwnerLeases = null;
            }
            current = null;
            canonicalAuthorityObserved = false;
        }
    }

    private HostManagerMemoryCleanupAttemptDocument LoadAndValidate()
    {
        if (current is not null)
        {
            return current;
        }

        AcquireOwnerLease();
        if (!TryReadAndValidateDocument(
            statePath,
            out var document,
            out var canonicalImage))
        {
            rootManifest.RequireVirginOrInitializing();
            var migrated = TryMigrateLegacyCanonical();
            if (migrated is not null)
            {
                current = migrated;
                canonicalAuthorityObserved = true;
                return current;
            }

            current = CreateDocument([]);
            return current;
        }

        rootManifest.ValidateOrAdoptCanonical(canonicalImage);
        RetireLegacyCanonicalIfPresent();
        current = document;
        canonicalAuthorityObserved = true;
        return document;
    }

    private HostManagerMemoryCleanupAttemptDocument Persist(
        IReadOnlyList<HostManagerMemoryCleanupAttemptEntry> entries)
    {
        try
        {
            ValidateCurrentCanonicalBeforeTransition();
            var document = CommitDocument(entries);
            current = document;
            canonicalAuthorityObserved = true;
            return document;
        }
        catch
        {
            current = null;
            throw;
        }
    }

    private HostManagerMemoryCleanupAttemptDocument CommitDocument(
        IReadOnlyList<HostManagerMemoryCleanupAttemptEntry> entries)
    {
        var document = CreateDocument(entries);
        ValidateDocument(document);
        var canonicalImage = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
        var directory = Path.GetDirectoryName(statePath)
            ?? throw new InvalidOperationException(
                "The memory-cleanup attempt journal has no parent directory.");
        Directory.CreateDirectory(directory);
        AcquireOwnerLease();
        var tempPath = Path.Combine(
            directory,
            $".{Path.GetFileName(statePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                tempPath,
                new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    BufferSize = 64 * 1024,
                    Options = FileOptions.WriteThrough
                }))
            {
                stream.Write(canonicalImage);
                stream.Flush(flushToDisk: true);
            }

            HostManagerDurableRootTransition transition;
            try
            {
                rootManifest.EnsureInitializing();
                transition = rootManifest.BeginCanonicalTransition(canonicalImage);
            }
            catch (Exception exception)
            {
                throw new HostManagerMemoryCleanupAttemptCommitException(
                    HostManagerMemoryCleanupAttemptCommitOutcome.NotCommitted,
                    exception);
            }

            try
            {
                committer.Commit(tempPath, statePath, canonicalImage);
            }
            catch (HostManagerMemoryCleanupAttemptCommitException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new HostManagerMemoryCleanupAttemptCommitException(
                    HostManagerMemoryCleanupAttemptCommitOutcome.CommitAmbiguous,
                    exception);
            }

            try
            {
                rootManifest.FinalizeCanonicalTransition(transition);
            }
            catch (Exception exception)
            {
                throw new HostManagerMemoryCleanupAttemptCommitException(
                    HostManagerMemoryCleanupAttemptCommitOutcome.CommitAmbiguous,
                    exception);
            }

            return document;
        }
        finally
        {
            TryDeleteTemporaryFile(tempPath);
        }
    }

    private void ValidateCurrentCanonicalBeforeTransition()
    {
        if (current is null)
        {
            return;
        }

        if (!TryReadAndValidateDocument(
            statePath,
            out var persisted,
            out var canonicalImage))
        {
            if (canonicalAuthorityObserved)
            {
                throw new IOException(
                    "The initialized memory-cleanup attempt journal lost its canonical file.");
            }

            rootManifest.RequireVirginOrInitializing();
            return;
        }

        rootManifest.ValidateOrAdoptCanonical(canonicalImage);
        if (persisted.SchemaVersion != current.SchemaVersion
            || persisted.Entries.Count != current.Entries.Count
            || !string.Equals(
                persisted.ChecksumSha256,
                current.ChecksumSha256,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The memory-cleanup attempt journal changed outside its active owner.");
        }
    }

    private HostManagerMemoryCleanupAttemptDocument? TryMigrateLegacyCanonical()
    {
        if (legacySources.Count == 0)
        {
            return null;
        }

        AcquireLegacyOwnerLeases();
        var userDataCandidates = ReadLegacyCandidates(usesDurableRootManifest: true);
        var selectedCandidates = userDataCandidates.Count > 0
            ? userDataCandidates
            : ReadLegacyCandidates(usesDurableRootManifest: false);
        if (selectedCandidates.Count == 0)
        {
            return null;
        }

        var selected = selectedCandidates[0];
        foreach (var candidate in selectedCandidates.Skip(1))
        {
            if (!candidate.CanonicalImage.AsSpan().SequenceEqual(selected.CanonicalImage))
            {
                throw new InvalidDataException(
                    "The memory-cleanup attempt journal found conflicting same-tier legacy authorities.");
            }
        }

        var migrated = CommitDocument(selected.Document.Entries);
        RetireLegacyCanonicals();
        return migrated;
    }

    private IReadOnlyList<HostManagerMemoryCleanupAttemptLegacyCandidate>
        ReadLegacyCandidates(bool usesDurableRootManifest)
    {
        var candidates = new List<HostManagerMemoryCleanupAttemptLegacyCandidate>();
        foreach (var legacySource in legacySources.Where(
            source => source.UsesDurableRootManifest == usesDurableRootManifest))
        {
            if (TryReadAndValidateDocument(
                legacySource.Path,
                out var document,
                out var canonicalImage))
            {
                if (usesDurableRootManifest)
                {
                    CreateLegacyRootManifest(legacySource.Path)
                        .ValidateOrAdoptCanonical(canonicalImage);
                }
                candidates.Add(new(document, canonicalImage));
                continue;
            }

            if (usesDurableRootManifest)
            {
                CreateLegacyRootManifest(legacySource.Path)
                    .RequireVirginOrInitializing();
            }
        }
        return candidates;
    }

    private void RetireLegacyCanonicalIfPresent()
    {
        if (legacySources.Count == 0)
        {
            return;
        }

        AcquireLegacyOwnerLeases();
        RetireLegacyCanonicals();
    }

    private bool TryReadAndValidateDocument(
        string path,
        out HostManagerMemoryCleanupAttemptDocument document,
        out byte[] canonicalImage)
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
            document = null!;
            canonicalImage = [];
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            document = null!;
            canonicalImage = [];
            return false;
        }

        using (stream)
        {
            if (stream.Length <= 0
                || stream.Length > MaximumDocumentBytes
                || stream.Length > int.MaxValue)
            {
                throw new InvalidDataException(
                    "The memory-cleanup attempt journal has an invalid file length.");
            }

            canonicalImage = GC.AllocateUninitializedArray<byte>(checked((int)stream.Length));
            stream.ReadExactly(canonicalImage);
        }

        document = JsonSerializer.Deserialize<HostManagerMemoryCleanupAttemptDocument>(
            canonicalImage,
            JsonOptions)
            ?? throw new InvalidDataException(
                "The memory-cleanup attempt journal is empty.");
        ValidateDocument(document);
        return true;
    }

    private void RetireLegacyCanonicals()
    {
        foreach (var legacySource in legacySources)
        {
            _ = legacyRetirer.Retire(legacySource.Path);
        }
    }

    private HostManagerDurableRootManifest CreateLegacyRootManifest(string canonicalPath)
        => new(
            canonicalPath,
            DurableStoreKind,
            manifestCommitter);

    private void AcquireLegacyOwnerLeases()
    {
        if (legacyOwnerLeases is not null || legacySources.Count == 0)
        {
            return;
        }

        var acquired = new List<FileStream>(legacySources.Count);
        try
        {
            foreach (var legacySource in legacySources)
            {
                acquired.Add(AcquirePathOwnerLease(legacySource.Path));
            }
            legacyOwnerLeases = acquired;
        }
        catch
        {
            foreach (var ownerLease in acquired)
            {
                ownerLease.Dispose();
            }
            throw;
        }
    }

    private static FileStream AcquirePathOwnerLease(string canonicalPath)
    {
        var leasePath = $"{canonicalPath}.owner.lock";
        var directory = Path.GetDirectoryName(leasePath)
            ?? throw new InvalidOperationException(
                "The legacy memory-cleanup attempt journal has no parent directory.");
        Directory.CreateDirectory(directory);
        return new FileStream(
            leasePath,
            new FileStreamOptions
            {
                Mode = FileMode.OpenOrCreate,
                Access = FileAccess.ReadWrite,
                Share = FileShare.None,
                BufferSize = 1,
                Options = FileOptions.WriteThrough
            });
    }

    private static IReadOnlyList<HostManagerMemoryCleanupAttemptLegacySource>
        CreateLegacySources(IReadOnlyList<string> legacyStatePaths)
    {
        ArgumentNullException.ThrowIfNull(legacyStatePaths);
        return legacyStatePaths
            .Select(static path => new HostManagerMemoryCleanupAttemptLegacySource(
                path,
                UsesDurableRootManifest: false))
            .ToArray();
    }

    private static void TryDeleteTemporaryFile(string temporaryPath)
    {
        try
        {
            File.Delete(temporaryPath);
        }
        catch (FileNotFoundException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private HostManagerMemoryCleanupAttemptDocument CreateDocument(
        IReadOnlyList<HostManagerMemoryCleanupAttemptEntry> entries)
    {
        var canonical = entries
            .OrderBy(static entry => entry.ProcessId)
            .ThenBy(static entry => entry.ProcessStartKey)
            .ToArray();
        return new(
            SchemaVersion,
            canonical,
            ComputeChecksum(canonical));
    }

    private void ValidateDocument(HostManagerMemoryCleanupAttemptDocument document)
    {
        if (document.SchemaVersion != SchemaVersion
            || document.Entries is null
            || document.Entries.Count > RequireCapacity()
            || !string.Equals(
                document.ChecksumSha256,
                ComputeChecksum(document.Entries),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The memory-cleanup attempt journal failed schema, capacity, or checksum validation.");
        }

        var identities = new HashSet<HostManagerComputeProcessIdentity>();
        foreach (var entry in document.Entries)
        {
            if (entry.BatchId == Guid.Empty
                || entry.ProcessId <= 0
                || entry.ProcessStartKey == 0
                || entry.TargetKey == 0
                || entry.AttemptGeneration == 0
                || entry.PreparedAtUtcTicks <= 0
                || entry.Phase is not HostManagerMemoryCleanupAttemptPhase.Applying
                    and not HostManagerMemoryCleanupAttemptPhase.Unknown
                    and not HostManagerMemoryCleanupAttemptPhase.PreparedBeforeWriter
                || !identities.Add(new(entry.ProcessId, entry.ProcessStartKey)))
            {
                throw new InvalidDataException(
                    "The memory-cleanup attempt journal contains a noncanonical entry.");
            }
        }
    }

    private static string ComputeChecksum(
        IReadOnlyList<HostManagerMemoryCleanupAttemptEntry> entries)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(SchemaVersion);
            writer.Write(entries.Count);
            foreach (var entry in entries
                .OrderBy(static item => item.ProcessId)
                .ThenBy(static item => item.ProcessStartKey))
            {
                writer.Write(entry.BatchId.ToByteArray());
                writer.Write(entry.ProcessId);
                writer.Write(entry.ProcessStartKey);
                writer.Write(entry.TargetKey);
                writer.Write(entry.AttemptGeneration);
                writer.Write((byte)entry.Phase);
                writer.Write(entry.PreparedAtUtcTicks);
            }
        }
        return Convert.ToHexString(SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length))));
    }

    private void AcquireOwnerLease()
    {
        if (ownerLease is not null) return;
        var directory = Path.GetDirectoryName(statePath)
            ?? throw new InvalidOperationException(
                "The memory-cleanup attempt journal has no parent directory.");
        Directory.CreateDirectory(directory);
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

    private int RequireCapacity()
    {
        var capacity = capacityProvider();
        if (capacity <= 0)
        {
            throw new InvalidOperationException(
                "The memory-cleanup attempt journal requires a positive fixed capacity.");
        }
        return capacity;
    }

    private static void RequireExactBatch(
        HostManagerMemoryCleanupAttemptDocument document,
        HostManagerMemoryCleanupAttemptBatch batch,
        params HostManagerMemoryCleanupAttemptPhase[] allowedPhases)
    {
        var entries = document.Entries
            .Where(entry => entry.BatchId == batch.BatchId)
            .ToArray();
        var persisted = entries.Select(static entry =>
                new HostManagerComputeProcessIdentity(
                    entry.ProcessId,
                    entry.ProcessStartKey))
            .ToHashSet();
        if (batch.AttemptGeneration == 0
            || entries.Any(entry =>
                entry.AttemptGeneration != batch.AttemptGeneration
                || allowedPhases.Length != 0
                    && !allowedPhases.Contains(entry.Phase))
            || !persisted.SetEquals(batch.Processes))
        {
            throw new InvalidDataException(
                "The memory-cleanup attempt batch does not match its durable records.");
        }
    }

    private void EnsureAvailable()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    private static HostManagerMemoryCleanupAttemptJournalPaths ResolveProductionPaths(
        IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        return ResolveProductionPaths(
            environment.ContentRootPath,
            HostManagerDurableDataRootResolver.ResolveStableInstallRoot(
                environment.ContentRootPath,
                allowUnregisteredFallback: environment.IsDevelopment()));
    }
}

internal readonly record struct HostManagerMemoryCleanupAttemptJournalPaths(
    string CanonicalPath,
    IReadOnlyList<HostManagerMemoryCleanupAttemptLegacySource> LegacySources)
{
    internal IReadOnlyList<string> LegacyPaths { get; } = LegacySources
        .Select(static source => source.Path)
        .ToArray();
}

internal readonly record struct HostManagerMemoryCleanupAttemptLegacySource(
    string Path,
    bool UsesDurableRootManifest);

internal readonly record struct HostManagerMemoryCleanupAttemptLegacyCandidate(
    HostManagerMemoryCleanupAttemptDocument Document,
    byte[] CanonicalImage);

internal enum HostManagerMemoryCleanupAttemptPhase : byte
{
    Applying = 1,
    Unknown = 2,
    PreparedBeforeWriter = 3
}

internal sealed record HostManagerMemoryCleanupAttemptEntry(
    Guid BatchId,
    int ProcessId,
    ulong ProcessStartKey,
    ulong TargetKey,
    ulong AttemptGeneration,
    HostManagerMemoryCleanupAttemptPhase Phase,
    long PreparedAtUtcTicks);

internal sealed record HostManagerMemoryCleanupAttemptDocument(
    uint SchemaVersion,
    IReadOnlyList<HostManagerMemoryCleanupAttemptEntry> Entries,
    string ChecksumSha256);
