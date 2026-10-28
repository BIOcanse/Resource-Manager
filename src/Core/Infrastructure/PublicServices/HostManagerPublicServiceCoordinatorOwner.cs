using System.Collections.Concurrent;
using ResourceManager.App.Application.PublicServices;
using ResourceManager.App.Application.PublicServices.AiGateway;
using ResourceManager.App.Application.PublicServices.AiModels;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.Adaptation;

namespace ResourceManager.App.Infrastructure.PublicServices;

public sealed partial class HostManagerPublicServiceCoordinatorOwner :
    IHostedService,
    ILocalServiceCatalogQueries,
    ILocalPublicServiceAccessPolicy,
    ILocalAiModelService,
    IAiGatewayLocalModelPolicy,
    IDisposable
{
    private readonly ReaderWriterLockSlim gate = new(LockRecursionPolicy.NoRecursion);
    private readonly SemaphoreSlim initializationGate = new(1, 1);
    private readonly ConcurrentDictionary<ulong, RequestCompletion> requestCompletions = new();
    private readonly ConcurrentDictionary<ulong, PendingModelOperation> pendingModelOperations = new();
    private readonly SemaphoreSlim modelWake = new(0, 1);
    private readonly SemaphoreSlim taskWake = new(0, 1);
    private readonly RuntimePlanProvider planProvider;
    private readonly HostManagerPublicServiceCoordinatorRuntime deployment;
    private readonly HostManagerRuntimeIdentity runtimeIdentity;
    private readonly IAiModelRuntimeProvider runtimeProvider;
    private readonly ILogger<HostManagerPublicServiceCoordinatorOwner> logger;
    private readonly Func<ulong, IComputeScratch> createScratch;
    private CancellationTokenSource? workerCancellation;
    private Task? modelWorker;
    private Task? taskWorker;
    private WorkspaceHolder? current;
    private WorkspaceHolder? pendingRetirement;
    private CompiledHostManagerPublicServiceCoordinatorPlan? appliedPlan;
    private long nextRequestCompletionHandle;
    private long nextOperationPayloadHandle;
    private long nextSessionIncarnation;
    private bool started;
    private bool disposed;
    private Action<CompiledRuntimePlan>? publishedHandler;
    private readonly object activityGate = new();
    private bool hasPendingTasks;
    private int activeModelAcquisitions;
    private int modelRefreshQueued;

    internal bool HasPendingTasks => Volatile.Read(ref hasPendingTasks);
    internal event Action? PendingTasksChanged;

    private void PublishPendingTasks()
    {
        lock (activityGate)
        {
            var pending = !requestCompletions.IsEmpty || !pendingModelOperations.IsEmpty
                || Volatile.Read(ref activeModelAcquisitions) != 0
                || Volatile.Read(ref modelRefreshQueued) != 0;
            if (hasPendingTasks == pending) return;
            Volatile.Write(ref hasPendingTasks, pending);
            PendingTasksChanged?.Invoke();
        }
    }

    private void QueueModelRefresh()
    {
        Interlocked.Exchange(ref modelRefreshQueued, 1);
        PublishPendingTasks();
        Signal(modelWake);
    }

    public HostManagerPublicServiceCoordinatorOwner(
        RuntimePlanProvider planProvider,
        HostManagerPublicServiceCoordinatorRuntime deployment,
        HostManagerRuntimeIdentity runtimeIdentity,
        IAiModelRuntimeProvider runtimeProvider,
        ILogger<HostManagerPublicServiceCoordinatorOwner> logger,
        ResourceManagerLedgerOwner ledger)
        : this(planProvider, deployment, runtimeIdentity, runtimeProvider, logger,
            length => ledger.CreateScratch(length)) { }

    internal HostManagerPublicServiceCoordinatorOwner(
        RuntimePlanProvider planProvider,
        HostManagerPublicServiceCoordinatorRuntime deployment,
        HostManagerRuntimeIdentity runtimeIdentity,
        IAiModelRuntimeProvider runtimeProvider,
        ILogger<HostManagerPublicServiceCoordinatorOwner> logger,
        Func<ulong, IComputeScratch> createScratch)
    {
        this.planProvider = planProvider;
        this.deployment = deployment;
        this.runtimeIdentity = runtimeIdentity;
        this.runtimeProvider = runtimeProvider;
        this.logger = logger;
        this.createScratch = createScratch;
    }

    internal async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfDisposed();
            if (publishedHandler is not null) return;

            Action<CompiledRuntimePlan> handler = null!;
            handler = plan => Apply(plan.HostManager.RequirePublished(), throwOnFailure: false, handler);
            gate.EnterWriteLock();
            try
            {
                publishedHandler = handler;
                planProvider.Published += handler;
            }
            finally { gate.ExitWriteLock(); }
            try
            {
                Apply(planProvider.Current.HostManager.RequirePublished(), throwOnFailure: true, handler);
                return;
            }
            catch
            {
                gate.EnterWriteLock();
                try
                {
                    planProvider.Published -= handler;
                    if (ReferenceEquals(publishedHandler, handler)) publishedHandler = null;
                }
                finally { gate.ExitWriteLock(); }
                throw;
            }
        }
        finally { initializationGate.Release(); }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (started) return;
        if (workerCancellation is not null || modelWorker is not null || taskWorker is not null)
            throw new InvalidOperationException("The prior Host Manager public-service stop has not completed.");
        workerCancellation = new CancellationTokenSource();
        modelWorker = Task.Run(
            () => RunModelAcquisitionLoopAsync(workerCancellation.Token), CancellationToken.None);
        taskWorker = Task.Run(
            () => RunTaskLoopAsync(workerCancellation.Token), CancellationToken.None);
        started = true;
        Signal(taskWake);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (!started
            && workerCancellation is null
            && modelWorker is null
            && taskWorker is null)
        {
            return;
        }

        gate.EnterWriteLock();
        try
        {
            started = false;
        }
        finally { gate.ExitWriteLock(); }
        var cancellation = workerCancellation;
        cancellation?.Cancel();
        Signal(modelWake);
        Signal(taskWake);
        var workers = new[] { modelWorker, taskWorker }
            .Where(static worker => worker is not null)
            .Cast<Task>()
            .ToArray();
        try
        {
            await Task.WhenAll(workers).WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            !cancellationToken.IsCancellationRequested
            && cancellation?.IsCancellationRequested == true)
        {
        }
        ThrowIfWorkerFaulted();
        modelWorker = null;
        taskWorker = null;
        workerCancellation = null;
        cancellation?.Dispose();
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        workerCancellation?.Cancel();
        workerCancellation?.Dispose();
        workerCancellation = null;
        Interlocked.Exchange(ref modelRefreshQueued, 0);
        CancelPendingOperations();
        ReleaseRequestCompletions();
        gate.EnterWriteLock();
        try
        {
            if (publishedHandler is { } handler) planProvider.Published -= handler;
            publishedHandler = null;
            started = false;
            if (pendingRetirement?.Retire() == true) pendingRetirement = null;
            if (current?.Retire() == true) current = null;
            if (pendingRetirement is not null || current is not null)
                throw new InvalidOperationException(
                    "Public-service workspaces are still in use; disposal must be retried after their retirement.");
            appliedPlan = null;
            disposed = true;
        }
        finally
        {
            gate.ExitWriteLock();
        }
        // A publication may still hold the old delegate after unsubscription.
        // Keep its admission gate usable so that callback can enter and reject it.
        modelWake.Dispose();
        taskWake.Dispose();
    }

    private void Apply(CompiledHostManagerPlan hostPlan, bool throwOnFailure,
        Action<CompiledRuntimePlan>? expectedHandler = null)
    {
        var desired = hostPlan.PublicServiceCoordinator;
        gate.EnterWriteLock();
        try
        {
            if (expectedHandler is not null && !ReferenceEquals(publishedHandler, expectedHandler)) return;
            if (pendingRetirement?.Retire() == true) pendingRetirement = null;
            if (appliedPlan?.ConfigurationSha256 == desired.ConfigurationSha256)
            {
                return;
            }
            if (pendingRetirement is not null)
                throw new InvalidOperationException(
                    "The previous public-service workspace is still in use; replacement must wait for its retirement.");
            if (current is not null)
            {
                RequireReplaceable(current.Workspace.ReadSnapshot());
            }

            var initial = current is null;
            var token = initial
                ? deployment.BeginInitialCreate(hostPlan)
                : deployment.BeginHostRecreateAndHotPublish(hostPlan);
            NativePublicServiceCoordinatorWorkspace? replacement = null;
            WorkspaceHolder? next = null;
            try
            {
                var models = new NativePublicServiceModelProjection();
                replacement = new NativePublicServiceCoordinatorWorkspace(
                    desired,
                    runtimeIdentity.InstanceId,
                    NextHandle(
                        ref nextSessionIncarnation,
                        "public-service session incarnation"),
                    desired.HotPublish.ConfigurationGeneration,
                    createScratch);
                next = new WorkspaceHolder(replacement, desired, models);
                replacement = null;
                var previous = current;
                var previousPlan = appliedPlan;
                current = next;
                appliedPlan = desired;
                next = null;
                try
                {
                    deployment.CompleteSucceeded(token);
                }
                catch
                {
                    var rejected = current;
                    current = previous;
                    appliedPlan = previousPlan;
                    rejected?.Retire();
                    throw;
                }
                pendingRetirement = previous;
                if (!initial)
                {
                    Signal(modelWake);
                }
                Signal(taskWake);
            }
            catch (Exception ex)
            {
                next?.Retire();
                replacement?.Dispose();
                try
                {
                    deployment.CompleteFailed(token, "public-service-coordinator-apply-failed");
                }
                catch (Exception settlement)
                {
                    throw new AggregateException(ex, settlement);
                }
                throw;
            }
            // Publication succeeded; retirement failures retain the old holder
            // without changing the already settled deployment attempt.
            if (pendingRetirement?.Retire() == true) pendingRetirement = null;
        }
        catch (Exception ex) when (!throwOnFailure)
        {
            logger.LogError(
                ex,
                "Host Manager public-service coordinator apply or workspace retirement failed.");
        }
        finally
        {
            gate.ExitWriteLock();
        }
    }

    private WorkspaceHolder AcquireCurrent()
    {
        ThrowIfDisposed();
        ThrowIfWorkerFaulted();
        gate.EnterReadLock();
        try
        {
            var holder = current
                ?? throw new InvalidOperationException(
                    "The Host Manager public-service coordinator is not ready.");
            holder.AddReference();
            return holder;
        }
        finally
        {
            gate.ExitReadLock();
        }
    }

    private void RetryDesiredPlan()
    {
        if (disposed || !started)
        {
            return;
        }
        Apply(planProvider.Current.HostManager.RequirePublished(), throwOnFailure: false);
    }

    private static void RequireReplaceable(
        NativePublicServiceCoordinatorSnapshot snapshot)
    {
        var flags = (NativePublicServiceCoordinatorSnapshotFlags)snapshot.Flags;
        if (snapshot.LeaseCount != 0
            || snapshot.SubscriptionCount != 0
            || snapshot.RequestCount != 0
            || snapshot.QueuedTaskCount != 0
            || snapshot.RunningTaskCount != 0
            || flags.HasFlag(
                NativePublicServiceCoordinatorSnapshotFlags.ModelAcquisitionRunning))
        {
            throw new InvalidOperationException(
                "The current public-service workspace still owns active model work or references.");
        }
    }

    private static async Task WaitUntilAsync(
        SemaphoreSlim signal,
        ulong nextWakeMonotonicMilliseconds,
        CancellationToken cancellationToken)
    {
        if (nextWakeMonotonicMilliseconds == 0)
        {
            await signal.WaitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        var now = NativePublicServiceCoordinatorWorkspace.MonotonicMilliseconds();
        if (nextWakeMonotonicMilliseconds <= now)
        {
            await Task.Yield();
            return;
        }
        var delay = nextWakeMonotonicMilliseconds - now;
        await signal.WaitAsync(
            TimeSpan.FromMilliseconds(Math.Min(delay, int.MaxValue)),
            cancellationToken).ConfigureAwait(false);
    }

    private static void Signal(SemaphoreSlim signal)
    {
        if (signal.CurrentCount == 0)
        {
            try
            {
                signal.Release();
            }
            catch (SemaphoreFullException)
            {
            }
        }
    }

    private static ulong NextHandle(ref long value, string name)
    {
        var next = Interlocked.Increment(ref value);
        return next > 0
            ? checked((ulong)next)
            : throw new InvalidOperationException($"{name} handle space is exhausted.");
    }

    private void CancelPendingOperations()
    {
        foreach (var entry in pendingModelOperations.ToArray())
        {
            if (pendingModelOperations.TryRemove(entry.Key, out var operation))
            {
                operation.Cancel();
            }
        }
        PublishPendingTasks();
    }

    private void CancelQueuedOperations()
    {
        var holder = AcquireCurrent();
        try
        {
            foreach (var task in holder.Workspace.CancelQueuedTasks())
            {
                ValidateCancelledTask(task);
                if (!pendingModelOperations.TryRemove(task.PayloadHandle, out var operation)
                    || operation.Kind != (NativePublicServiceTaskKind)task.Kind)
                {
                    throw new InvalidOperationException(
                        "A cancelled native task lost its exact pending operation payload.");
                }
                operation.Cancel();
            }
            PublishPendingTasks();
            if (!pendingModelOperations.IsEmpty)
            {
                throw new InvalidOperationException(
                    "Host Manager shutdown left public-service operation payloads unsettled.");
            }
        }
        finally
        {
            holder.Release();
        }
    }

    private void ReleaseRequestCompletions()
    {
        foreach (var entry in requestCompletions.ToArray())
        {
            if (requestCompletions.TryRemove(entry.Key, out var completion))
            {
                completion.Holder.Release();
            }
        }
        PublishPendingTasks();
    }

    private void ThrowIfWorkerFaulted()
    {
        var modelFailure = modelWorker is { IsFaulted: true } model
            ? model.Exception
            : null;
        var taskFailure = taskWorker is { IsFaulted: true } task
            ? task.Exception
            : null;
        if (modelFailure is not null || taskFailure is not null)
        {
            throw new AggregateException(
                "The Host Manager public-service coordinator worker faulted.",
                new[] { modelFailure, taskFailure }
                    .Where(static failure => failure is not null)
                    .Cast<Exception>());
        }
    }

    private static void ValidateCancelledTask(NativePublicServiceTaskOutput task)
    {
        var kind = (NativePublicServiceTaskKind)task.Kind;
        if (task.TaskHandle == 0
            || task.PayloadHandle == 0
            || task.State != (uint)NativePublicServiceTaskState.Queued
            || kind is not (
                NativePublicServiceTaskKind.Load
                or NativePublicServiceTaskKind.Unload))
        {
            throw new InvalidOperationException(
                "The native coordinator returned a non-canonical cancelled task.");
        }
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(disposed, this);

    private sealed class WorkspaceHolder(
        NativePublicServiceCoordinatorWorkspace workspace,
        CompiledHostManagerPublicServiceCoordinatorPlan plan,
        NativePublicServiceModelProjection models)
    {
        private readonly object lifecycleGate = new();
        private int referenceCount = 1;
        private int retired;

        internal NativePublicServiceCoordinatorWorkspace Workspace { get; } = workspace;

        internal CompiledHostManagerPublicServiceCoordinatorPlan Plan { get; } = plan;

        internal object ModelGate { get; } = new();

        internal NativePublicServiceModelProjection Models { get; } = models;

        internal void AddReference()
        {
            lock (lifecycleGate)
            {
                if (retired != 0)
                    throw new ObjectDisposedException(nameof(NativePublicServiceCoordinatorWorkspace));
                referenceCount++;
            }
        }

        internal bool Retire()
        {
            lock (lifecycleGate)
            {
                if (retired == 0)
                {
                    retired = 1;
                    try { Release(); }
                    catch { retired = 0; throw; }
                }
                return referenceCount == 0;
            }
        }

        internal void Release()
        {
            lock (lifecycleGate)
            {
                if (--referenceCount == 0)
                {
                    try { Workspace.Dispose(); }
                    catch
                    {
                        referenceCount++;
                        retired = 0;
                        throw;
                    }
                }
            }
        }
    }

    private sealed class RequestCompletion(
        WorkspaceHolder holder,
        ulong nativeRequestHandle)
    {
        private int completing;

        internal WorkspaceHolder Holder { get; } = holder;

        internal ulong NativeRequestHandle { get; } = nativeRequestHandle;

        internal bool TryBegin()
            => Interlocked.CompareExchange(ref completing, 1, 0) == 0;

        internal void Retry()
            => Volatile.Write(ref completing, 0);
    }
}
