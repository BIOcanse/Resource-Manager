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
    IDisposable,
    IAsyncDisposable
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
    private readonly Func<CompiledHostManagerPublicServiceCoordinatorPlan, ulong, ulong, ulong,
        Func<ulong, IComputeScratch>, NativePublicServiceCoordinatorWorkspace> createWorkspace;
    private CancellationTokenSource? workerCancellation;
    private Task? modelWorker;
    private Task? taskWorker;
    private WorkspaceHolder? current;
    private WorkspaceHolder? pendingRetirement;
    private WorkspaceHolder? runHolder;
    private CompiledHostManagerPlan? desiredHostPlan;
    private CompiledHostManagerPublicServiceCoordinatorPlan? appliedPlan;
    private long nextRequestCompletionHandle;
    private long nextOperationPayloadHandle;
    private long nextSessionIncarnation;
    private bool started;
    private bool disposed;
    private bool retiring;
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
        Func<ulong, IComputeScratch> createScratch,
        Func<CompiledHostManagerPublicServiceCoordinatorPlan, ulong, ulong, ulong,
            Func<ulong, IComputeScratch>, NativePublicServiceCoordinatorWorkspace>? createWorkspace = null)
    {
        this.planProvider = planProvider;
        this.deployment = deployment;
        this.runtimeIdentity = runtimeIdentity;
        this.runtimeProvider = runtimeProvider;
        this.logger = logger;
        this.createScratch = createScratch;
        this.createWorkspace = createWorkspace ?? (static (plan, host, incarnation, generation, scratch) =>
            new NativePublicServiceCoordinatorWorkspace(plan, host, incarnation, generation, scratch));
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
                ThrowIfDisposed();
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
        gate.EnterWriteLock();
        try
        {
            ThrowIfDisposed();
            if (started) return;
            if (workerCancellation is not null || modelWorker is not null || taskWorker is not null)
                throw new InvalidOperationException("The prior Host Manager public-service stop has not completed.");
            runHolder = AcquireCurrentLocked();
            var cancellation = new CancellationTokenSource();
            workerCancellation = cancellation;
            modelWorker = Task.Run(
                () => RunModelAcquisitionLoopAsync(cancellation.Token), CancellationToken.None);
            taskWorker = Task.Run(
                () => RunTaskLoopAsync(cancellation.Token), CancellationToken.None);
            started = true;
            Signal(taskWake);
            if (Volatile.Read(ref modelRefreshQueued) != 0) Signal(modelWake);
        }
        finally { gate.ExitWriteLock(); }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        CancellationTokenSource? cancellation;
        Task[] workers;
        gate.EnterWriteLock();
        try
        {
            if (workerCancellation is null && modelWorker is null && taskWorker is null) return;
            started = false;
            cancellation = workerCancellation;
            workers = new[] { modelWorker, taskWorker }
                .Where(static worker => worker is not null).Cast<Task>().ToArray();
        }
        finally { gate.ExitWriteLock(); }
        // Another Stop may already have joined and disposed this same source.
        try { cancellation?.Cancel(); }
        catch (ObjectDisposedException) { }
        Signal(modelWake);
        Signal(taskWake);
        // The caller may stop waiting, but the admitted Stop still joins the
        // actual workers and returns its run reference when they finally exit.
        await DrainStoppedRunAsync(cancellation, workers).WaitAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task DrainStoppedRunAsync(CancellationTokenSource? cancellation, Task[] workers)
    {
        Exception? workerFailure = null;
        try
        {
            await Task.WhenAll(workers).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            cancellation?.IsCancellationRequested == true)
        {
        }
        catch (Exception failure)
        {
            workerFailure = failure;
        }
        gate.EnterWriteLock();
        try
        {
            if (!ReferenceEquals(workerCancellation, cancellation)) return;
            // Queued operations retain their transferred references across Stop
            // and are consumed by the next run. Only final disposal cancels them.
            modelWorker = null;
            taskWorker = null;
            workerCancellation = null;
            cancellation?.Dispose();
            var holder = runHolder;
            runHolder = null;
            holder?.ReleaseLocked();
        }
        finally { gate.ExitWriteLock(); }
        if (workerFailure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(workerFailure).Throw();
    }

    public void Dispose()
    {
        gate.EnterWriteLock();
        try
        {
            if (disposed) return;
            if (workerCancellation is not null || current?.HasReferences == true
                || pendingRetirement?.HasReferences == true)
                throw new InvalidOperationException(
                    "The public-service coordinator is in use; use DisposeAsync to drain it.");
            retiring = true;
            CloseResourcesLocked();
        }
        finally { gate.ExitWriteLock(); }
    }

    public async ValueTask DisposeAsync()
    {
        gate.EnterWriteLock();
        try
        {
            if (disposed) return;
            retiring = true;
        }
        finally { gate.ExitWriteLock(); }
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        Task drain;
        gate.EnterWriteLock();
        try
        {
            // A stopped Load/Unload may still own a queued native task.
            CancelQueuedOperationsLocked();
            drain = Task.WhenAll(current?.Drain ?? Task.CompletedTask,
                pendingRetirement?.Drain ?? Task.CompletedTask);
        }
        finally { gate.ExitWriteLock(); }
        await drain.ConfigureAwait(false);
        gate.EnterWriteLock();
        try { CloseResourcesLocked(); }
        finally { gate.ExitWriteLock(); }
    }

    private void CloseResourcesLocked()
    {
        if (publishedHandler is { } handler) planProvider.Published -= handler;
        publishedHandler = null;
        Interlocked.Exchange(ref modelRefreshQueued, 0);
        if (pendingRetirement?.RetireLocked() == true) pendingRetirement = null;
        if (current?.RetireLocked() == true) current = null;
        appliedPlan = null;
        disposed = true;
        PublishPendingTasks();
        // Managed gates stay usable for admitted completion and callbacks that
        // captured the old publication delegate before unsubscription.
    }

    private void Apply(CompiledHostManagerPlan hostPlan, bool throwOnFailure,
        Action<CompiledRuntimePlan>? expectedHandler = null)
    {
        var desired = hostPlan.PublicServiceCoordinator;
        gate.EnterWriteLock();
        try
        {
            if (expectedHandler is not null && !ReferenceEquals(publishedHandler, expectedHandler)) return;
            if (retiring || disposed) return;
            desiredHostPlan = hostPlan;
            if (pendingRetirement?.RetireLocked() == true) pendingRetirement = null;
            if (current is { HasReferences: false } idle && idle.RetireLocked())
            {
                current = null;
                appliedPlan = null;
            }
            // Preparation and idle publications only record configuration.
            if (current is null) return;
            PublishAdmissionStateLocked(current, desired.HotPublish.ServiceEnabled);
            if (appliedPlan?.ConfigurationSha256 == desired.ConfigurationSha256)
            {
                return;
            }
            if (pendingRetirement is not null)
                throw new InvalidOperationException(
                    "The previous public-service workspace is still in use; replacement must wait for its retirement.");
            if (current is not null)
            {
                if (!IsReplaceable(current.Workspace.ReadSnapshot())) return;
            }

            var token = deployment.BeginHostRecreateAndHotPublish(hostPlan);
            WorkspaceHolder? next = null;
            try
            {
                var models = new NativePublicServiceModelProjection();
                var replacement = createWorkspace(
                    desired,
                    runtimeIdentity.InstanceId,
                    NextHandle(
                        ref nextSessionIncarnation,
                        "public-service session incarnation"),
                    desired.HotPublish.ConfigurationGeneration,
                    createScratch);
                next = new WorkspaceHolder(this, replacement, desired, models);
                deployment.CompleteSucceeded(token);
            }
            catch (Exception ex)
            {
                var failures = new List<Exception> { ex };
                if (next is not null)
                {
                    pendingRetirement = next;
                    try { if (next.RetireLocked()) pendingRetirement = null; }
                    catch (Exception close) { failures.Add(close); }
                }
                try
                {
                    deployment.CompleteFailed(token, "public-service-coordinator-apply-failed");
                }
                catch (Exception settlement)
                {
                    failures.Add(settlement);
                }
                if (failures.Count > 1) throw new AggregateException(failures);
                throw;
            }
            // Publication succeeded; retirement failures retain the old holder
            // without changing the already settled deployment attempt.
            var previous = current;
            current = next!;
            appliedPlan = desired;
            current.AddReferenceLocked();
            pendingRetirement = previous;
            if (ReferenceEquals(runHolder, previous))
            {
                runHolder = current;
                previous!.ReleaseLocked();
            }
            else
            {
                // A stopped finite call still owns the predecessor. Its final
                // return releases this transferred reference to the replacement;
                // current itself never receives an idle baseline reference.
                previous!.ReplacementHold = current;
            }
            Signal(modelWake);
            Signal(taskWake);
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
        gate.EnterWriteLock();
        try { return AcquireCurrentLocked(); }
        finally { gate.ExitWriteLock(); }
    }

    private WorkspaceHolder AcquireCurrentLocked()
    {
        ThrowIfDisposed();
        ThrowIfWorkerFaulted();
        if (pendingRetirement?.RetireLocked() == true) pendingRetirement = null;
        if (current is { HasReferences: false } idle)
        {
            // A failed last close remains owned here until a later entry or
            // disposal retries it successfully; never overwrite that obligation.
            if (idle.RetireLocked()) current = null;
        }
        if (current is null)
        {
            if (pendingRetirement is not null)
                throw new InvalidOperationException(
                    "The previous public-service workspace must finish retirement before another workspace is opened.");
            var hostPlan = desiredHostPlan ?? throw new InvalidOperationException(
                "The Host Manager public-service coordinator is not ready.");
            var desired = hostPlan.PublicServiceCoordinator;
            var token = deployment.BeginInitialCreate(hostPlan);
            WorkspaceHolder? candidate = null;
            try
            {
                candidate = new WorkspaceHolder(this, createWorkspace(desired,
                    runtimeIdentity.InstanceId,
                    NextHandle(ref nextSessionIncarnation, "public-service session incarnation"),
                    desired.HotPublish.ConfigurationGeneration, createScratch),
                    desired, new NativePublicServiceModelProjection());
                deployment.CompleteSucceeded(token);
                current = candidate;
                appliedPlan = desired;
            }
            catch (Exception failure)
            {
                var failures = new List<Exception> { failure };
                if (candidate is not null)
                {
                    pendingRetirement = candidate;
                    try { if (candidate.RetireLocked()) pendingRetirement = null; }
                    catch (Exception close) { failures.Add(close); }
                }
                try { deployment.CompleteFailed(token, "public-service-coordinator-create-failed"); }
                catch (Exception settlement) { failures.Add(settlement); }
                if (failures.Count > 1) throw new AggregateException(failures);
                throw;
            }
        }
        current.AddReferenceLocked();
        return current;
    }

    private WorkspaceHolder AcquireRunWorkspace()
    {
        gate.EnterWriteLock();
        try
        {
            var holder = runHolder ?? throw new InvalidOperationException(
                "The public-service worker has no running workspace reference.");
            holder.AddReferenceLocked();
            return holder;
        }
        finally { gate.ExitWriteLock(); }
    }

    private void PublishAdmissionStateLocked(WorkspaceHolder holder, bool enabled)
    {
        if (holder.Plan.HotPublish.ServiceEnabled == enabled) return;
        var hot = holder.Plan.HotPublish with
        {
            ServiceEnabled = enabled,
            ConfigurationGeneration = checked(holder.Plan.HotPublish.ConfigurationGeneration + 1)
        };
        // Native retains its caller windows and admitted requests while a full
        // replacement waits for those requests/tasks to settle.
        holder.Workspace.PublishCatalog(hot, hot.ConfigurationGeneration);
        holder.Plan = holder.Plan with { HotPublish = hot };
    }

    private void ReleaseHolderLocked(WorkspaceHolder holder)
    {
        List<Exception>? failures = null;
        WorkspaceHolder? releasing = holder;
        // The single pending-retirement slot permits at most one predecessor
        // and its replacement. Drain both iteratively, including failed closes.
        var remaining = 2;
        while (releasing is not null)
        {
            if (--remaining < 0)
                throw new InvalidOperationException("Public-service replacement references exceeded their bound.");
            WorkspaceHolder? next = null;
            try
            {
                if (releasing.ReleaseReferenceLocked())
                {
                    if (ReferenceEquals(current, releasing)) { current = null; appliedPlan = null; }
                    if (ReferenceEquals(pendingRetirement, releasing)) pendingRetirement = null;
                }
            }
            catch (Exception failure) { (failures ??= []).Add(failure); }
            finally
            {
                if (!releasing.HasReferences)
                {
                    next = releasing.ReplacementHold;
                    releasing.ReplacementHold = null;
                }
            }
            releasing = next;
        }
        if (failures is { Count: 1 })
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures is { Count: > 1 }) throw new AggregateException(failures);
    }

    private void RetryDesiredPlan()
    {
        if (disposed || retiring)
        {
            return;
        }
        Apply(planProvider.Current.HostManager.RequirePublished(), throwOnFailure: false);
    }

    private static bool IsReplaceable(
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
            return false;
        }
        return true;
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

    private void CancelQueuedOperationsLocked()
    {
        var holder = current;
        if (holder is null) return;
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
                operation.ReleaseHolderLocked();
            }
            if (!pendingModelOperations.IsEmpty)
                throw new InvalidOperationException(
                    "Host Manager shutdown left public-service operation payloads unsettled.");
        }
        finally { PublishPendingTasks(); }
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
        => ObjectDisposedException.ThrowIf(disposed || retiring, this);

    private sealed class WorkspaceHolder(
        HostManagerPublicServiceCoordinatorOwner owner,
        NativePublicServiceCoordinatorWorkspace workspace,
        CompiledHostManagerPublicServiceCoordinatorPlan plan,
        NativePublicServiceModelProjection models)
    {
        private int referenceCount;
        private readonly TaskCompletionSource drained =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool closed;

        internal NativePublicServiceCoordinatorWorkspace Workspace { get; } = workspace;

        internal CompiledHostManagerPublicServiceCoordinatorPlan Plan { get; set; } = plan;

        internal object ModelGate { get; } = new();

        internal NativePublicServiceModelProjection Models { get; } = models;

        internal WorkspaceHolder? ReplacementHold { get; set; }

        internal bool HasReferences => referenceCount != 0;

        internal Task Drain => referenceCount == 0 ? Task.CompletedTask : drained.Task;

        internal void AddReferenceLocked()
        {
            ObjectDisposedException.ThrowIf(closed, this);
            referenceCount++;
        }

        internal bool RetireLocked()
        {
            if (referenceCount != 0) return false;
            if (!closed) Workspace.Dispose();
            closed = true;
            return true;
        }

        internal void Release()
        {
            owner.gate.EnterWriteLock();
            try { ReleaseLocked(); }
            finally { owner.gate.ExitWriteLock(); }
        }

        internal void ReleaseLocked() => owner.ReleaseHolderLocked(this);

        internal bool ReleaseReferenceLocked()
        {
            if (referenceCount <= 0)
                throw new InvalidOperationException("A public-service workspace reference was released twice.");
            if (--referenceCount != 0) return false;
            try
            {
                return RetireLocked();
            }
            finally { drained.TrySetResult(); }
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
