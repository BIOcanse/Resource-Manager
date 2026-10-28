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

    internal HostManagerDeploymentAttemptToken BeginCreate(
        HostManagerModuleKind module, CompiledHostManagerPlan plan)
    {
        if (module is not HostManagerModuleKind.SoftwareIdentityCatalog
            and not HostManagerModuleKind.SoftwareIdentityResolution)
        {
            throw new ArgumentOutOfRangeException(nameof(module), module, null);
        }
        return deploymentState.BeginResourceCreate(module, plan,
            HostManagerDeploymentOperation.HostRecreateAndHotPublish);
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

public sealed class HostManagerSoftwareIdentityOwner : IDisposable
{
    private readonly ReaderWriterLockSlim gate = new(LockRecursionPolicy.NoRecursion);
    private readonly RuntimePlanProvider planProvider;
    private readonly HostManagerSoftwareIdentityRuntime deployment;
    private readonly ILogger<HostManagerSoftwareIdentityOwner> logger;
    private WorkspaceHolder? current;
    private CompiledHostManagerSoftwareIdentityCatalogPlan? appliedCatalogPlan;
    private CompiledHostManagerSoftwareIdentityResolutionPlan? appliedResolutionPlan;
    private readonly List<WorkspaceHolder> retiredWorkspaces = [];
    private readonly object lifetimeGate = new();
    private int holders;
    private bool disposed;
    private Action<CompiledRuntimePlan>? publishedHandler;

    public HostManagerSoftwareIdentityOwner(
        RuntimePlanProvider planProvider,
        HostManagerSoftwareIdentityRuntime deployment,
        ILogger<HostManagerSoftwareIdentityOwner> logger)
    {
        this.planProvider = planProvider;
        this.deployment = deployment;
        this.logger = logger;
    }

    internal SessionLease Acquire()
    {
        lock (lifetimeGate)
        {
            ThrowIfDisposed();
            RetryRetiredWorkspaces();
            if (holders == 0)
            {
                Action<CompiledRuntimePlan> handler = null!;
                handler = plan =>
                {
                    lock (lifetimeGate)
                        Apply(plan.HostManager.RequirePublished(), throwOnFailure: false, handler);
                };
                publishedHandler = handler;
                planProvider.Published += handler;
                try { Apply(planProvider.Current.HostManager.RequirePublished(), throwOnFailure: true, handler); }
                catch
                {
                    planProvider.Published -= handler;
                    publishedHandler = null;
                    throw;
                }
            }
            holders++;
            return current!.Acquire(Release);
        }
    }

    private void Release(WorkspaceHolder holder)
    {
        lock (lifetimeGate)
        {
            try { holder.Release(); }
            finally
            {
                if (--holders == 0)
                {
                    if (publishedHandler is { } handler) planProvider.Published -= handler;
                    publishedHandler = null;
                    var previous = current;
                    current = null;
                    RetireWorkspace(previous);
                }
            }
        }
    }

    public void Dispose()
    {
        lock (lifetimeGate)
        {
            if (disposed) { RetryRetiredWorkspaces(); return; }
            gate.EnterWriteLock();
            try
            {
                if (publishedHandler is { } handler) planProvider.Published -= handler;
                publishedHandler = null;
                disposed = true;
                var previous = current;
                current = null;
                RetireWorkspace(previous);
                RetryRetiredWorkspaces();
            }
            finally { gate.ExitWriteLock(); }
        }
    }

    private void RetireWorkspace(WorkspaceHolder? holder)
    {
        if (holder is null) return;
        retiredWorkspaces.Add(holder);
        holder.Retire();
        if (holder.TryClose()) retiredWorkspaces.Remove(holder);
    }

    private void RetryRetiredWorkspaces()
    {
        for (var index = retiredWorkspaces.Count - 1; index >= 0; index--)
            if (retiredWorkspaces[index].TryClose()) retiredWorkspaces.RemoveAt(index);
    }

    private void Apply(CompiledHostManagerPlan hostPlan, bool throwOnFailure,
        Action<CompiledRuntimePlan>? expectedHandler = null)
    {
        gate.EnterWriteLock();
        try
        {
            if (expectedHandler is not null && !ReferenceEquals(publishedHandler, expectedHandler)) return;
            var catalogPlan = hostPlan.SoftwareIdentityCatalog;
            var resolutionPlan = hostPlan.SoftwareIdentityResolution;
            if (current is not null
                && current.CatalogPlan == catalogPlan
                && current.ResolutionPlan == resolutionPlan)
            {
                return;
            }
            if (appliedCatalogPlan is not null && appliedResolutionPlan is not null)
            {
                ValidateGeneration(
                    appliedCatalogPlan.ConfigurationGeneration,
                    appliedCatalogPlan.ConfigurationSha256,
                    catalogPlan.ConfigurationGeneration,
                    catalogPlan.ConfigurationSha256,
                    "catalog");
                ValidateGeneration(
                    appliedResolutionPlan.ConfigurationGeneration,
                    appliedResolutionPlan.ConfigurationSha256,
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
                catalogAttempt = initial
                    ? deployment.BeginCreate(HostManagerModuleKind.SoftwareIdentityCatalog, hostPlan)
                    : deployment.Begin(HostManagerModuleKind.SoftwareIdentityCatalog, hostPlan, catalogOperation);
                resolutionAttempt = initial
                    ? deployment.BeginCreate(HostManagerModuleKind.SoftwareIdentityResolution, hostPlan)
                    : deployment.Begin(HostManagerModuleKind.SoftwareIdentityResolution, hostPlan, resolutionOperation);

                deployment.CompleteSucceeded(catalogAttempt.Value);
                catalogAttempt = null;
                deployment.CompleteSucceeded(resolutionAttempt.Value);
                resolutionAttempt = null;

                var next = new WorkspaceHolder(catalogPlan, resolutionPlan, resolution);
                resolution = null;
                var previous = current;
                current = next;
                appliedCatalogPlan = catalogPlan;
                appliedResolutionPlan = resolutionPlan;
                RetireWorkspace(previous);
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

        private readonly Action<WorkspaceHolder>? release;

        internal SessionLease(WorkspaceHolder holder, Action<WorkspaceHolder>? release = null)
        {
            this.holder = holder;
            this.release = release;
            CatalogPlan = holder.CatalogPlan;
            ResolutionPlan = holder.ResolutionPlan;
            ResolutionSession = holder.ResolutionWorkspace.Session;
            PolicyGeneration = holder.ResolutionWorkspace.PolicyGeneration;
        }

        internal CompiledHostManagerSoftwareIdentityCatalogPlan CatalogPlan { get; }

        internal CompiledHostManagerSoftwareIdentityResolutionPlan ResolutionPlan { get; }

        internal NativeSoftwareIdentityResolutionSession ResolutionSession { get; }

        internal ulong PolicyGeneration { get; }

        // Every resolver borrowing this native session must allocate and submit
        // its frame under the same lock, including observation construction.
        internal object ResolutionGate => (holder
            ?? throw new ObjectDisposedException(nameof(SessionLease))).ResolutionGate;

        internal HostManagerSoftwareIdentityFrameStamp NextFrame(DateTimeOffset now)
            => (holder
                ?? throw new ObjectDisposedException(nameof(SessionLease)))
                .NextFrame(now);

        public void Dispose()
        {
            var previous = Interlocked.Exchange(ref holder, null);
            if (previous is null) return;
            if (release is null) previous.Release();
            else release(previous);
        }
    }

    internal sealed class WorkspaceHolder(
        CompiledHostManagerSoftwareIdentityCatalogPlan catalogPlan,
        CompiledHostManagerSoftwareIdentityResolutionPlan resolutionPlan,
        NativeSoftwareIdentityResolutionWorkspace resolutionWorkspace)
    {
        private int referenceCount = 1;
        private int retired;
        private readonly object closeGate = new();
        private bool closed;
        internal object ResolutionGate { get; } = new();
        private ulong frameEpoch;
        private long lastCommandUtcMilliseconds;

        internal CompiledHostManagerSoftwareIdentityCatalogPlan CatalogPlan { get; } = catalogPlan;

        internal CompiledHostManagerSoftwareIdentityResolutionPlan ResolutionPlan { get; } =
            resolutionPlan;

        internal NativeSoftwareIdentityResolutionWorkspace ResolutionWorkspace { get; } =
            resolutionWorkspace;

        internal SessionLease Acquire(Action<WorkspaceHolder>? release = null)
        {
            if (Volatile.Read(ref retired) != 0)
            {
                throw new ObjectDisposedException(nameof(NativeSoftwareIdentityResolutionWorkspace));
            }
            Interlocked.Increment(ref referenceCount);
            return new SessionLease(this, release);
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
            lock (ResolutionGate)
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
                _ = TryClose();
            }
        }

        internal bool TryClose()
        {
            lock (closeGate)
            {
                if (closed) return true;
                if (Volatile.Read(ref referenceCount) != 0) return false;
                ResolutionWorkspace.Dispose();
                closed = true;
                return true;
            }
        }

    }
}

internal readonly record struct HostManagerSoftwareIdentityFrameStamp(
    ulong FrameEpoch,
    long CommandUtcMilliseconds);
