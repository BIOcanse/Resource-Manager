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
        ILogger<HostManagerDisplayCoordinatorOwner> logger)
    {
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
        if (disposed)
        {
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
            current?.Retire();
            current = null;
            appliedPlan = null;
            disposed = true;
        }
        finally
        {
            gate.ExitWriteLock();
            gate.Dispose();
        }
    }

    private void OnPublished(CompiledRuntimePlan plan)
        => Apply(plan.HostManager.RequirePublished(), throwOnFailure: false);

    private void Apply(CompiledHostManagerPlan hostPlan, bool throwOnFailure)
    {
        var desired = hostPlan.DisplayCoordinator;
        gate.EnterWriteLock();
        try
        {
            if (appliedPlan?.ConfigurationSha256 == desired.ConfigurationSha256)
            {
                return;
            }
            var initial = current is null;
            var token = initial
                ? deployment.BeginInitialCreate(hostPlan)
                : deployment.BeginHostRecreate(hostPlan);
            NativeDisplayCoordinatorWorkspace? replacement = null;
            WorkspaceHolder? next = null;
            try
            {
                replacement = new NativeDisplayCoordinatorWorkspace(
                    desired,
                    persistenceCapability.MutablePersistenceEnabled
                        ? ResolvePersistencePath(desired.Recreate.PersistenceRelativePath)
                        : null);
                next = new WorkspaceHolder(replacement);
                replacement = null;
            }
            catch (Exception ex)
            {
                Exception? cleanupFailure = null;
                try
                {
                    next?.Retire();
                }
                catch (Exception cleanup)
                {
                    cleanupFailure = cleanup;
                }
                try
                {
                    replacement?.Dispose();
                }
                catch (Exception cleanup)
                {
                    cleanupFailure = cleanupFailure is null
                        ? cleanup
                        : new AggregateException(cleanupFailure, cleanup);
                }

                Exception? settlementFailure = null;
                try
                {
                    deployment.CompleteFailed(
                        token,
                        "display-coordinator-apply-failed");
                }
                catch (Exception settlement)
                {
                    settlementFailure = settlement;
                }

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
            current = next;
            appliedPlan = desired;
            try
            {
                deployment.CompleteSucceeded(token);
            }
            catch
            {
                current = previous;
                appliedPlan = previousPlan;
                next.Retire();
                throw;
            }

            next = null;
            try
            {
                previous?.Retire();
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "The retired display-coordinator workspace could not be disposed.");
            }
        }
        catch (Exception ex) when (!throwOnFailure)
        {
            logger.LogError(
                ex,
                "Host Manager display-coordinator apply failed; the prior workspace remains active.");
        }
        finally
        {
            gate.ExitWriteLock();
        }
    }

    private WorkspaceLease Acquire()
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
        => ObjectDisposedException.ThrowIf(disposed, this);

    private sealed class WorkspaceHolder(NativeDisplayCoordinatorWorkspace workspace)
    {
        private int referenceCount = 1;
        private int retired;

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

        internal void Release()
        {
            if (Interlocked.Decrement(ref referenceCount) == 0)
            {
                Workspace.Dispose();
            }
        }
    }

    private sealed class WorkspaceLease(WorkspaceHolder holder) : IDisposable
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
