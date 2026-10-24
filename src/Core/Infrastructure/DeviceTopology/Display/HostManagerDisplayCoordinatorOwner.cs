using ResourceManager.App.Infrastructure.Adaptation;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Domain.Messages;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.Paths;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace ResourceManager.App.Infrastructure.DeviceTopology;

public sealed class HostManagerDisplayCoordinatorOwner :
    IHostedService,
    IDisposable
{
    private readonly ReaderWriterLockSlim gate = new(LockRecursionPolicy.NoRecursion);
    private readonly RuntimePlanProvider planProvider;
    private readonly HostManagerDisplayCoordinatorRuntime deployment;
    private readonly RuntimePersistenceCapabilityPolicy persistenceCapability;
    private readonly string hostManagerDataRoot;
    private readonly ILogger<HostManagerDisplayCoordinatorOwner> logger;
    private readonly Func<NativeDisplayCoordinatorConfiguration, NativeDisplayCoordinatorPersistenceCallbacks?, NativeDisplayCoordinatorWorkspace> createWorkspace;
    private readonly Func<string, IDisplayCoordinatorCheckpointStore> createCheckpointStore;
    // This gate drains actual checkpoint I/O, independently of native reader leases.
    private readonly object checkpointGate = new();
    private readonly List<CheckpointStore> pendingCheckpointCloses = [];
    private CheckpointBinding? allowedBinding;
    private CheckpointStore? currentCheckpointStore;
    private readonly object retiredWorkspaceGate = new();
    private readonly List<WorkspaceHolder> retiredWorkspaces = [];
    private int disposeStarted;
    private WorkspaceHolder? current;
    private CompiledHostManagerDisplayCoordinatorPlan? appliedPlan;
    private IReadOnlyList<DeviceTopologyOemDisplayConnectorProfile> profiles = [];
    private bool started;
    private bool disposed;

    internal bool PersistenceEnabled =>
        persistenceCapability.MutablePersistenceEnabled;

    public HostManagerDisplayCoordinatorOwner(
        RuntimePlanProvider planProvider,
        HostManagerDisplayCoordinatorRuntime deployment,
        RuntimePersistenceCapabilityPolicy persistenceCapability,
        IHostEnvironment environment,
        ILogger<HostManagerDisplayCoordinatorOwner> logger,
        ResourceManagerLedgerOwner ledgerOwner)
        : this(planProvider, deployment, persistenceCapability, environment, logger,
            (configuration, callbacks) => new NativeDisplayCoordinatorWorkspace(in configuration, callbacks,
                bytes => new DisplayNormalizationScratch(ledgerOwner.CreateScratch(bytes))),
            static path => new NativeDisplayCoordinatorPersistenceStore(path,
                Path.Combine(AppContext.BaseDirectory, "Native", "DiskCheckpoint", "native.json")))
    {
    }

    internal HostManagerDisplayCoordinatorOwner(
        RuntimePlanProvider planProvider,
        HostManagerDisplayCoordinatorRuntime deployment,
        RuntimePersistenceCapabilityPolicy persistenceCapability,
        IHostEnvironment environment,
        ILogger<HostManagerDisplayCoordinatorOwner> logger,
        Func<NativeDisplayCoordinatorConfiguration, NativeDisplayCoordinatorPersistenceCallbacks?, NativeDisplayCoordinatorWorkspace> createWorkspace,
        Func<string, IDisplayCoordinatorCheckpointStore> createCheckpointStore)
    {
        this.createWorkspace = createWorkspace;
        this.createCheckpointStore = createCheckpointStore;
        this.planProvider = planProvider;
        this.deployment = deployment;
        this.persistenceCapability = persistenceCapability;
        this.logger = logger;
        hostManagerDataRoot = Path.GetFullPath(Path.Combine(
            PackagePathResolver.ResolvePackageRoot(environment.ContentRootPath),
            "UserData",
            "HostManager"));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        if (started)
        {
            return Task.CompletedTask;
        }

        var notes = new List<BackendMessage>();
        DeviceTopologySystemIdentity system;
        try
        {
            system = WindowsDeviceTopologyReader.ReadSystemIdentity(notes);
            profiles = DeviceTopologyOemDisplayConnectorCatalog.Resolve(system);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Display OEM profile discovery failed; native display facts will omit OEM profile rows.");
            profiles = [];
        }

        planProvider.Published += OnPublished;
        try
        {
            Apply(planProvider.Current.HostManager.RequirePublished(), throwOnFailure: true);
            started = true;
            return Task.CompletedTask;
        }
        catch
        {
            planProvider.Published -= OnPublished;
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (started)
        {
            planProvider.Published -= OnPublished;
            started = false;
        }
        return Task.CompletedTask;
    }

    internal HostManagerDisplayCoordinatorReadModel Refresh()
    {
        using var lease = Acquire();
        return lease.Workspace.Refresh(profiles);
    }

    public void Dispose()
    {
        // Repeated disposal may retry a failed normal Close, but H is already disposed.
        if (Interlocked.Exchange(ref disposeStarted, 1) != 0)
        {
            lock (checkpointGate) RetryPendingCheckpointCloses();
            RetryRetiredWorkspaces();
            return;
        }
        if (started)
        {
            planProvider.Published -= OnPublished;
            started = false;
        }
        gate.EnterWriteLock();
        try
        {
            lock (checkpointGate)
            {
                if (allowedBinding is not null) allowedBinding.Store = null;
                allowedBinding = null;
                RetryPendingCheckpointCloses();
                var previousStore = currentCheckpointStore;
                currentCheckpointStore = null;
                if (previousStore is not null) CloseCheckpointStore(previousStore);
            }
            // Native leases can complete later. Never wait for W while holding P.
            var previous = current;
            current = null;
            appliedPlan = null;
            disposed = true;
            RetireWorkspace(previous);
            RetryRetiredWorkspaces();
        }
        finally
        {
            gate.ExitWriteLock();
            gate.Dispose();
        }
    }

    internal static unsafe NativeDisplayCoordinatorConfiguration CreateConfiguration(CompiledHostManagerDisplayCoordinatorPlan plan)
    {
        var recreate = plan.Recreate;
        var capacity = recreate.Capacity;
        return new NativeDisplayCoordinatorConfiguration
        {
            AbiVersion = NativeDisplayCoordinatorAbi.Version,
            StructSize = checked((uint)System.Runtime.CompilerServices.Unsafe.SizeOf<NativeDisplayCoordinatorConfiguration>()),
            Generation = recreate.ConfigurationGeneration,
            MaximumSourceCount = capacity.MaximumSourceCount,
            MaximumObservationCount = capacity.MaximumObservationCount,
            MaximumNodeCount = capacity.MaximumNodeCount,
            MaximumEdgeCount = capacity.MaximumEdgeCount,
            MaximumCapabilityCount = capacity.MaximumCapabilityCount,
            MaximumDiffEntryCount = capacity.MaximumDiffEntryCount,
            MaximumTextBindingCount = capacity.MaximumTextBindingCount,
            MaximumTextByteCount = capacity.MaximumTextByteCount,
            MaximumUnresolvedCount = capacity.MaximumUnresolvedCount,
            MaximumSourceBatchCount = capacity.MaximumSourceBatchCount,
            IdentityIndexCapacity = capacity.IdentityIndexCapacity,
            TextIndexCapacity = capacity.TextIndexCapacity,
            ResidentByteBudget = capacity.ResidentByteBudget,
            RequiredSourceMask = recreate.RequiredSourceMask,
            OptionalSourceMask = recreate.OptionalSourceMask,
            IdentitySourcePriorityOrder = recreate.IdentitySourcePriorityOrder,
            FriendlyNameSourcePriorityOrder = recreate.FriendlyNameSourcePriorityOrder,
            CapabilitySourcePriorityOrder = recreate.CapabilitySourcePriorityOrder,
            ProjectionSourcePriorityOrder = recreate.ProjectionSourcePriorityOrder,
            IdentityContractVersion = recreate.IdentityContractVersion,
            CapabilityContractVersion = recreate.CapabilityContractVersion,
            MaximumFutureSkewMilliseconds = recreate.MaximumFutureSkewMilliseconds,
        };
    }

    private void OnPublished(CompiledRuntimePlan plan)
        => Apply(plan.HostManager.RequirePublished(), throwOnFailure: false);

    internal void Apply(CompiledHostManagerPlan hostPlan, bool throwOnFailure)
    {
        ThrowIfDisposed();
        var desired = hostPlan.DisplayCoordinator;
        gate.EnterWriteLock();
        try
        {
            RetryRetiredWorkspaces();
            if (appliedPlan?.ConfigurationSha256 == desired.ConfigurationSha256) return;
            var token = current is null
                ? deployment.BeginInitialCreate(hostPlan)
                : deployment.BeginHostRecreate(hostPlan);
            NativeDisplayCoordinatorWorkspace? replacement = null;
            WorkspaceHolder? next = null;
            CheckpointBinding? nextBinding = null;
            CheckpointStore? candidateStore = null;
            try
            {
                if (!desired.IsPublished)
                    throw new ArgumentException("The display-coordinator plan must be published.", "plan");
                if (persistenceCapability.MutablePersistenceEnabled)
                {
                    var legacyPath = ResolvePersistencePath(desired.Recreate.PersistenceRelativePath);
                    var rootPath = NativeDisplayCoordinatorPersistenceStore.ResolveRootPath(legacyPath);
                    lock (checkpointGate)
                    {
                        if (currentCheckpointStore is not null
                            && string.Equals(currentCheckpointStore.RootPath, rootPath, StringComparison.OrdinalIgnoreCase))
                        {
                            candidateStore = currentCheckpointStore;
                        }
                        else
                        {
                            // Never open a second writer beside a failed, still-valid Close.
                            RetryPendingCheckpointCloses(rootPath, throwOnFailure: true);
                            candidateStore = new(rootPath, createCheckpointStore(legacyPath));
                        }
                    }
                    nextBinding = new(desired.Recreate.ConfigurationGeneration, candidateStore);
                }
                var binding = nextBinding;
                var callbacks = binding is null ? null : new NativeDisplayCoordinatorPersistenceCallbacks(
                    generation => LoadCheckpoint(binding, generation),
                    (generation, image) => SaveCheckpoint(binding, generation, image));
                replacement = createWorkspace(CreateConfiguration(desired), callbacks);
                next = new WorkspaceHolder(replacement);
                replacement = null;
            }
            catch (Exception ex)
            {
                var cleanupFailure = CleanupCandidate(next, replacement, nextBinding, candidateStore);
                Exception? settlementFailure = null;
                try { deployment.CompleteFailed(token, "display-coordinator-apply-failed"); }
                catch (Exception settlement) { settlementFailure = settlement; }
                throw (cleanupFailure, settlementFailure) switch
                {
                    (null, null) => ex,
                    (not null, null) => new AggregateException(ex, cleanupFailure),
                    (null, not null) => new AggregateException(ex, settlementFailure),
                    _ => new AggregateException(ex, cleanupFailure!, settlementFailure!)
                };
            }

            var previous = current;
            var previousPlan = appliedPlan;
            CheckpointStore? retiredStore = null;
            try
            {
                lock (checkpointGate)
                {
                    current = next;
                    appliedPlan = desired;
                    try { deployment.CompleteSucceeded(token); }
                    catch
                    {
                        current = previous;
                        appliedPlan = previousPlan;
                        throw;
                    }
                    // Only nonthrowing reference assignments follow successful settlement.
                    var previousBinding = allowedBinding;
                    var previousStore = currentCheckpointStore;
                    allowedBinding = nextBinding;
                    currentCheckpointStore = candidateStore;
                    if (previousBinding is not null) previousBinding.Store = null;

                    // Acquiring P drained entered Saves; revoked callbacks cannot touch this store.
                    if (previousStore is not null && !ReferenceEquals(previousStore, candidateStore))
                        retiredStore = previousStore;
                }
            }
            catch (Exception ex)
            {
                // Only settlement can fail before switching the permission. Close reports separately.
                var cleanup = CleanupCandidate(next, null, nextBinding, candidateStore);
                if (cleanup is not null) throw new AggregateException(ex, cleanup);
                throw;
            }
            // Keep post-publication Close failures out of candidate rollback.
            if (retiredStore is not null)
            {
                lock (checkpointGate) CloseCheckpointStore(retiredStore);
            }
            try { RetireWorkspace(previous); RetryRetiredWorkspaces(); }
            catch (Exception ex)
            {
                logger.LogError(ex, "The retired display-coordinator workspace could not be disposed.");
            }
        }
        catch (Exception ex) when (!throwOnFailure)
        {
            logger.LogError(ex, "Host Manager display-coordinator apply failed; the prior workspace remains active.");
        }
        finally { gate.ExitWriteLock(); }
    }

    private NativeDisplayCoordinatorPersistenceImage? LoadCheckpoint(CheckpointBinding binding, ulong generation)
    {
        lock (checkpointGate)
        {
            // Load is called only synchronously inside candidate construction while H is held.
            if (generation != binding.ConfigurationGeneration)
                throw new InvalidOperationException("The display checkpoint Load generation does not match its workspace.");
            return (binding.Store ?? throw new ObjectDisposedException(nameof(CheckpointBinding))).Store.Load(generation);
        }
    }

    private void SaveCheckpoint(CheckpointBinding binding, ulong generation, NativeDisplayCoordinatorPersistenceImage image)
    {
        lock (checkpointGate)
        {
            if (!ReferenceEquals(allowedBinding, binding)) return;
            if (generation != binding.ConfigurationGeneration)
                throw new InvalidOperationException("The display checkpoint Save generation does not match its workspace.");
            binding.Store!.Store.Save(generation, image);
        }
    }

    private Exception? CleanupCandidate(WorkspaceHolder? holder, NativeDisplayCoordinatorWorkspace? workspace,
        CheckpointBinding? binding, CheckpointStore? candidate)
    {
        lock (checkpointGate)
        {
            if (binding is not null) binding.Store = null;
            if (candidate is not null && !ReferenceEquals(candidate, currentCheckpointStore))
                CloseCheckpointStore(candidate);
        }
        Exception? failure = null;
        try { RetireWorkspace(holder); }
        catch (Exception ex) { failure = ex; }
        try { if (workspace is not null) RetireWorkspace(new WorkspaceHolder(workspace)); }
        catch (Exception ex) { failure = failure is null ? ex : new AggregateException(failure, ex); }
        return failure;
    }

    // Caller holds P. Failed Close objects alone remain reachable for explicit normal retry.
    private void CloseCheckpointStore(CheckpointStore checkpoint)
    {
        try { checkpoint.Store.Dispose(); }
        catch (Exception ex)
        {
            if (!pendingCheckpointCloses.Contains(checkpoint)) pendingCheckpointCloses.Add(checkpoint);
            logger.LogError(ex, "The display checkpoint writer could not be closed for {RootPath}; normal Close remains pending.", checkpoint.RootPath);
        }
    }

    private void RetryPendingCheckpointCloses(string? rootPath = null, bool throwOnFailure = false)
    {
        for (var index = pendingCheckpointCloses.Count - 1; index >= 0; index--)
        {
            var pending = pendingCheckpointCloses[index];
            if (rootPath is not null && !string.Equals(rootPath, pending.RootPath, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                pending.Store.Dispose();
                pendingCheckpointCloses.RemoveAt(index);
            }
            catch (Exception ex)
            {
                if (throwOnFailure) throw;
                logger.LogError(ex, "The display checkpoint writer Close retry failed for {RootPath}.", pending.RootPath);
            }
        }
    }

    private void RetireWorkspace(WorkspaceHolder? holder)
    {
        if (holder is null) return;
        lock (retiredWorkspaceGate)
        {
            if (!retiredWorkspaces.Contains(holder)) retiredWorkspaces.Add(holder);
            holder.Retire();
        }
    }

    private void RetryRetiredWorkspaces()
    {
        lock (retiredWorkspaceGate)
        {
            for (var i = retiredWorkspaces.Count - 1; i >= 0; i--)
                if (retiredWorkspaces[i].TryClose()) retiredWorkspaces.RemoveAt(i);
        }
    }

    private sealed record CheckpointStore(string RootPath, IDisplayCoordinatorCheckpointStore Store);

    private sealed class CheckpointBinding(ulong configurationGeneration, CheckpointStore store)
    {
        internal ulong ConfigurationGeneration { get; } = configurationGeneration;
        internal CheckpointStore? Store { get; set; } = store;
    }

    internal WorkspaceLease Acquire()
    {
        ThrowIfDisposed();
        gate.EnterReadLock();
        try
        {
            var holder = current
                ?? throw new InvalidOperationException(
                    "The Host Manager display-coordinator workspace is not ready.");
            holder.AddReference();
            return new WorkspaceLease(holder);
        }
        finally
        {
            gate.ExitReadLock();
        }
    }

    private string ResolvePersistencePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)
            || Path.IsPathRooted(relativePath)
            || relativePath.Contains('\\'))
        {
            throw new InvalidOperationException(
                "The compiled display-coordinator persistence path is not canonical.");
        }
        var segments = relativePath.Split('/', StringSplitOptions.None);
        if (segments.Any(static segment =>
                string.IsNullOrEmpty(segment) || segment is "." or ".."))
        {
            throw new InvalidOperationException(
                "The compiled display-coordinator persistence path contains an invalid segment.");
        }
        var result = Path.GetFullPath(Path.Combine(hostManagerDataRoot, Path.Combine(segments)));
        var prefix = Path.EndsInDirectorySeparator(hostManagerDataRoot)
            ? hostManagerDataRoot
            : hostManagerDataRoot + Path.DirectorySeparatorChar;
        if (!result.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The compiled display-coordinator persistence path escapes the Host Manager data root.");
        }
        return result;
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(disposed || Volatile.Read(ref disposeStarted) != 0, this);

    internal sealed class WorkspaceHolder(NativeDisplayCoordinatorWorkspace workspace)
    {
        private int referenceCount = 1;
        private int retired;
        private readonly object closeGate = new();
        private bool closed;

        internal NativeDisplayCoordinatorWorkspace Workspace { get; } = workspace;

        internal void AddReference()
        {
            if (Volatile.Read(ref retired) != 0)
            {
                throw new ObjectDisposedException(nameof(NativeDisplayCoordinatorWorkspace));
            }
            Interlocked.Increment(ref referenceCount);
        }

        internal void Retire()
        {
            if (Interlocked.Exchange(ref retired, 1) == 0)
            {
                Release();
            }
        }

        internal bool TryClose()
        {
            lock (closeGate)
            {
                if (closed) return true;
                if (Volatile.Read(ref referenceCount) != 0 || Volatile.Read(ref retired) == 0) return false;
                Workspace.Dispose();
                closed = true;
                return true;
            }
        }

        internal void Release()
        {
            if (Interlocked.Decrement(ref referenceCount) == 0) _ = TryClose();
        }
    }

    internal sealed class WorkspaceLease(WorkspaceHolder holder) : IDisposable
    {
        private WorkspaceHolder? holder = holder;

        internal NativeDisplayCoordinatorWorkspace Workspace
            => holder?.Workspace
                ?? throw new ObjectDisposedException(nameof(WorkspaceLease));

        public void Dispose()
        {
            Interlocked.Exchange(ref holder, null)?.Release();
        }
    }
}
