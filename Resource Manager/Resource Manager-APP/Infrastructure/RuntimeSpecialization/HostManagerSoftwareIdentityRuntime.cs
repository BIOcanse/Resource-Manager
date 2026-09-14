using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization;

public sealed class HostManagerSoftwareIdentityRuntime(
    IRuntimePlanProvider runtimePlanProvider,
    HostManagerDeploymentState deploymentState)
{
    internal HostManagerSoftwareIdentityRuntimePlan CaptureDesired()
    {
        var hostPlan = runtimePlanProvider.Current.HostManager.RequirePublished();
        var catalog = hostPlan.SoftwareIdentityCatalog;
        var resolution = hostPlan.SoftwareIdentityResolution;
        if (!catalog.IsPublished || !resolution.IsPublished)
        {
            throw new InvalidOperationException(
                "The Host Manager software identity configuration is not published.");
        }
        return new HostManagerSoftwareIdentityRuntimePlan(hostPlan, catalog, resolution);
    }

    internal HostManagerDeploymentAttemptToken Begin(
        HostManagerModuleKind module,
        CompiledHostManagerPlan plan,
        HostManagerDeploymentOperation operation)
    {
        if (module is not HostManagerModuleKind.SoftwareIdentityCatalog
            and not HostManagerModuleKind.SoftwareIdentityResolution)
        {
            throw new ArgumentOutOfRangeException(nameof(module), module, null);
        }
        return deploymentState.BeginAttempt(module, plan, operation);
    }

    internal HostManagerDeploymentAttemptSettlement CompleteSucceeded(
        HostManagerDeploymentAttemptToken token)
        => HostManagerDeploymentAttemptSettlementGuard.RequireApplied(
            deploymentState.CompleteAttemptSucceeded(token),
            token.Module);

    internal HostManagerDeploymentAttemptSettlement CompleteFailed(
        HostManagerDeploymentAttemptToken token,
        string failureCode)
        => HostManagerDeploymentAttemptSettlementGuard.RequireFailed(
            deploymentState.CompleteAttemptFailed(token, failureCode),
            token.Module);
}

internal sealed record HostManagerSoftwareIdentityRuntimePlan(
    CompiledHostManagerPlan HostPlan,
    CompiledHostManagerSoftwareIdentityCatalogPlan Catalog,
    CompiledHostManagerSoftwareIdentityResolutionPlan Resolution);

public sealed class HostManagerSoftwareIdentityOwner : IHostedService, IDisposable
{
    private readonly ReaderWriterLockSlim gate = new(LockRecursionPolicy.NoRecursion);
    private readonly RuntimePlanProvider planProvider;
    private readonly HostManagerSoftwareIdentityRuntime deployment;
    private readonly ILogger<HostManagerSoftwareIdentityOwner> logger;
    private WorkspaceHolder? current;
    private bool started;
    private bool disposed;

    public HostManagerSoftwareIdentityOwner(
        RuntimePlanProvider planProvider,
        HostManagerSoftwareIdentityRuntime deployment,
        ILogger<HostManagerSoftwareIdentityOwner> logger)
    {
        this.planProvider = planProvider;
        this.deployment = deployment;
        this.logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        if (started)
        {
            return Task.CompletedTask;
        }
        planProvider.Published += OnPublished;
        started = true;
        Apply(planProvider.Current.HostManager.RequirePublished(), throwOnFailure: true);
        return Task.CompletedTask;
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

    internal SessionLease Acquire()
    {
        ThrowIfDisposed();
        EnsureReady();
        gate.EnterReadLock();
        try
        {
            return (current
                ?? throw new InvalidOperationException(
                    "The Host Manager software identity workspace is not ready."))
                .Acquire();
        }
        finally
        {
            gate.ExitReadLock();
        }
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
            disposed = true;
        }
        finally
        {
            gate.ExitWriteLock();
        }
    }

    private void EnsureReady()
    {
        gate.EnterReadLock();
        try
        {
            if (current is not null)
            {
                return;
            }
        }
        finally
        {
            gate.ExitReadLock();
        }
        Apply(planProvider.Current.HostManager.RequirePublished(), throwOnFailure: true);
    }

    private void OnPublished(CompiledRuntimePlan plan)
        => Apply(plan.HostManager.RequirePublished(), throwOnFailure: false);

    private void Apply(CompiledHostManagerPlan hostPlan, bool throwOnFailure)
    {
        gate.EnterWriteLock();
        try
        {
            var catalogPlan = hostPlan.SoftwareIdentityCatalog;
            var resolutionPlan = hostPlan.SoftwareIdentityResolution;
            if (current is not null
                && current.CatalogPlan == catalogPlan
                && current.ResolutionPlan == resolutionPlan)
            {
                return;
            }
            if (current is not null)
            {
                ValidateGeneration(
                    current.CatalogPlan.ConfigurationGeneration,
                    current.CatalogPlan.ConfigurationSha256,
                    catalogPlan.ConfigurationGeneration,
                    catalogPlan.ConfigurationSha256,
                    "catalog");
                ValidateGeneration(
                    current.ResolutionPlan.ConfigurationGeneration,
                    current.ResolutionPlan.ConfigurationSha256,
                    resolutionPlan.ConfigurationGeneration,
                    resolutionPlan.ConfigurationSha256,
                    "resolution");
            }

            NativeSoftwareIdentityCatalogWorkspace? catalogValidation = null;
            NativeSoftwareIdentityResolutionWorkspace? resolution = null;
            HostManagerDeploymentAttemptToken? catalogAttempt = null;
            HostManagerDeploymentAttemptToken? resolutionAttempt = null;
            try
            {
                catalogValidation = new NativeSoftwareIdentityCatalogWorkspace(catalogPlan);
                resolution = new NativeSoftwareIdentityResolutionWorkspace(resolutionPlan);

                var initial = current is null;
                var catalogOperation = ResolveOperation(
                    initial,
                    current?.CatalogPlan,
                    catalogPlan);
                var resolutionOperation = ResolveOperation(
                    initial,
                    current?.ResolutionPlan,
                    resolutionPlan);
                catalogAttempt = deployment.Begin(
                    HostManagerModuleKind.SoftwareIdentityCatalog,
                    hostPlan,
                    catalogOperation);
                resolutionAttempt = deployment.Begin(
                    HostManagerModuleKind.SoftwareIdentityResolution,
                    hostPlan,
                    resolutionOperation);

                deployment.CompleteSucceeded(catalogAttempt.Value);
                catalogAttempt = null;
                deployment.CompleteSucceeded(resolutionAttempt.Value);
                resolutionAttempt = null;

                var next = new WorkspaceHolder(catalogPlan, resolutionPlan, resolution);
                resolution = null;
                var previous = current;
                current = next;
                previous?.Retire();
            }
            catch (Exception applyFailure)
            {
                resolution?.Dispose();
                Exception? settlementFailure = null;
                if (resolutionAttempt is { } resolutionToken)
                {
                    settlementFailure = TrySettleFailed(
                        resolutionToken,
                        "software-identity-resolution-apply-failed");
                }
                if (catalogAttempt is { } catalogToken)
                {
                    var catalogSettlement = TrySettleFailed(
                        catalogToken,
                        "software-identity-catalog-apply-failed");
                    settlementFailure = Combine(settlementFailure, catalogSettlement);
                }
                if (settlementFailure is not null)
                {
                    throw new AggregateException(applyFailure, settlementFailure);
                }
                throw;
            }
            finally
            {
                catalogValidation?.Dispose();
            }
        }
        catch (Exception ex) when (!throwOnFailure)
        {
            logger.LogError(
                ex,
                "Host Manager software identity plan apply failed; the prior catalog/resolution workspace remains active.");
        }
        finally
        {
            gate.ExitWriteLock();
        }
    }

    private Exception? TrySettleFailed(
        HostManagerDeploymentAttemptToken token,
        string failureCode)
    {
        try
        {
            deployment.CompleteFailed(token, failureCode);
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static Exception? Combine(Exception? first, Exception? second)
        => first is null
            ? second
            : second is null
                ? first
                : new AggregateException(first, second);

    private static HostManagerDeploymentOperation ResolveOperation<TPlan>(
        bool initial,
        TPlan? currentPlan,
        TPlan nextPlan)
        where TPlan : class
    {
        if (initial)
        {
            return HostManagerDeploymentOperation.InitialCreate;
        }
        return Equals(currentPlan, nextPlan)
            ? HostManagerDeploymentOperation.HotPublish
            : ResolveRecreateOrHot(currentPlan!, nextPlan);
    }

    private static void ValidateGeneration(
        ulong currentGeneration,
        string currentDigest,
        ulong nextGeneration,
        string nextDigest,
        string module)
    {
        if (nextGeneration < currentGeneration)
        {
            throw new InvalidOperationException(
                $"Software identity {module} configuration generation cannot move backwards.");
        }
        if (nextGeneration == currentGeneration
            && !currentDigest.Equals(nextDigest, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Software identity {module} configuration cannot drift within one generation.");
        }
    }

    private static HostManagerDeploymentOperation ResolveRecreateOrHot<TPlan>(
        TPlan currentPlan,
        TPlan nextPlan)
        where TPlan : class
    {
        return (currentPlan, nextPlan) switch
        {
            (CompiledHostManagerSoftwareIdentityCatalogPlan currentCatalog,
             CompiledHostManagerSoftwareIdentityCatalogPlan nextCatalog)
                when currentCatalog.Build == nextCatalog.Build
                    && currentCatalog.Recreate == nextCatalog.Recreate
                => HostManagerDeploymentOperation.HotPublish,
            (CompiledHostManagerSoftwareIdentityResolutionPlan currentResolution,
             CompiledHostManagerSoftwareIdentityResolutionPlan nextResolution)
                when currentResolution.Build == nextResolution.Build
                    && currentResolution.Recreate == nextResolution.Recreate
                => HostManagerDeploymentOperation.HotPublish,
            _ => HostManagerDeploymentOperation.HostRecreateAndHotPublish
        };
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(disposed, this);

    internal sealed class SessionLease : IDisposable
    {
        private WorkspaceHolder? holder;

        internal SessionLease(WorkspaceHolder holder)
        {
            this.holder = holder;
            CatalogPlan = holder.CatalogPlan;
            ResolutionPlan = holder.ResolutionPlan;
            ResolutionSession = holder.ResolutionWorkspace.Session;
            PolicyGeneration = holder.ResolutionWorkspace.PolicyGeneration;
        }

        internal CompiledHostManagerSoftwareIdentityCatalogPlan CatalogPlan { get; }

        internal CompiledHostManagerSoftwareIdentityResolutionPlan ResolutionPlan { get; }

        internal NativeSoftwareIdentityResolutionSession ResolutionSession { get; }

        internal ulong PolicyGeneration { get; }

        internal HostManagerSoftwareIdentityFrameStamp NextFrame(DateTimeOffset now)
            => (holder
                ?? throw new ObjectDisposedException(nameof(SessionLease)))
                .NextFrame(now);

        public void Dispose()
            => Interlocked.Exchange(ref holder, null)?.Release();
    }

    internal sealed class WorkspaceHolder(
        CompiledHostManagerSoftwareIdentityCatalogPlan catalogPlan,
        CompiledHostManagerSoftwareIdentityResolutionPlan resolutionPlan,
        NativeSoftwareIdentityResolutionWorkspace resolutionWorkspace)
    {
        private int referenceCount = 1;
        private int retired;
        private readonly object frameGate = new();
        private ulong frameEpoch;
        private long lastCommandUtcMilliseconds;

        internal CompiledHostManagerSoftwareIdentityCatalogPlan CatalogPlan { get; } = catalogPlan;

        internal CompiledHostManagerSoftwareIdentityResolutionPlan ResolutionPlan { get; } =
            resolutionPlan;

        internal NativeSoftwareIdentityResolutionWorkspace ResolutionWorkspace { get; } =
            resolutionWorkspace;

        internal SessionLease Acquire()
        {
            if (Volatile.Read(ref retired) != 0)
            {
                throw new ObjectDisposedException(nameof(NativeSoftwareIdentityResolutionWorkspace));
            }
            Interlocked.Increment(ref referenceCount);
            return new SessionLease(this);
        }

        internal HostManagerSoftwareIdentityFrameStamp NextFrame(DateTimeOffset now)
        {
            var command = now.ToUnixTimeMilliseconds();
            if (command < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(now),
                    "Software identity frame time must not precede the Unix epoch.");
            }
            lock (frameGate)
            {
                if (frameEpoch == ulong.MaxValue)
                {
                    throw new InvalidOperationException(
                        "Software identity resolution frame epoch is exhausted.");
                }
                frameEpoch++;
                lastCommandUtcMilliseconds = Math.Max(
                    lastCommandUtcMilliseconds,
                    command);
                return new HostManagerSoftwareIdentityFrameStamp(
                    frameEpoch,
                    lastCommandUtcMilliseconds);
            }
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
                ResolutionWorkspace.Dispose();
            }
        }
    }
}

internal readonly record struct HostManagerSoftwareIdentityFrameStamp(
    ulong FrameEpoch,
    long CommandUtcMilliseconds);
