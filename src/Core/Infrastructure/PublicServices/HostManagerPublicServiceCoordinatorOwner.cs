using System.Collections.Concurrent;
using ResourceManager.App.Application.PublicServices;
using ResourceManager.App.Application.PublicServices.AiGateway;
using ResourceManager.App.Application.PublicServices.AiModels;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

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
    private readonly ConcurrentDictionary<ulong, RequestCompletion> requestCompletions = new();
    private readonly ConcurrentDictionary<ulong, PendingModelOperation> pendingModelOperations = new();
    private readonly SemaphoreSlim modelWake = new(0, 1);
    private readonly SemaphoreSlim taskWake = new(0, 1);
    private readonly RuntimePlanProvider planProvider;
    private readonly HostManagerPublicServiceCoordinatorRuntime deployment;
    private readonly HostManagerRuntimeIdentity runtimeIdentity;
    private readonly IAiModelRuntimeProvider runtimeProvider;
    private readonly ILogger<HostManagerPublicServiceCoordinatorOwner> logger;
    private CancellationTokenSource? workerCancellation;
    private Task? modelWorker;
    private Task? taskWorker;
    private WorkspaceHolder? current;
    private CompiledHostManagerPublicServiceCoordinatorPlan? appliedPlan;
    private long nextRequestCompletionHandle;
    private long nextOperationPayloadHandle;
    private long nextSessionIncarnation;
    private bool started;
    private bool disposed;

    public HostManagerPublicServiceCoordinatorOwner(
        RuntimePlanProvider planProvider,
        HostManagerPublicServiceCoordinatorRuntime deployment,
        HostManagerRuntimeIdentity runtimeIdentity,
        IAiModelRuntimeProvider runtimeProvider,
        ILogger<HostManagerPublicServiceCoordinatorOwner> logger)
    {
        this.planProvider = planProvider;
        this.deployment = deployment;
        this.runtimeIdentity = runtimeIdentity;
        this.runtimeProvider = runtimeProvider;
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
        if (workerCancellation is not null
            || modelWorker is not null
            || taskWorker is not null)
        {
            throw new InvalidOperationException(
                "The prior Host Manager public-service stop has not completed.");
        }

        planProvider.Published += OnPublished;
        try
        {
            Apply(planProvider.Current.HostManager.RequirePublished(), throwOnFailure: true);
            workerCancellation = new CancellationTokenSource();
            modelWorker = Task.Run(
                () => RunModelAcquisitionLoopAsync(workerCancellation.Token),
                CancellationToken.None);
            taskWorker = Task.Run(
                () => RunTaskLoopAsync(workerCancellation.Token),
                CancellationToken.None);
            started = true;
            Signal(taskWake);
            return Task.CompletedTask;
        }
        catch
        {
            planProvider.Published -= OnPublished;
            throw;
        }
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

        if (started)
        {
            planProvider.Published -= OnPublished;
            started = false;
        }
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
        CancelQueuedOperations();
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

        if (started)
        {
            planProvider.Published -= OnPublished;
            started = false;
        }
        workerCancellation?.Cancel();
        workerCancellation?.Dispose();
        workerCancellation = null;
        CancelPendingOperations();
        ReleaseRequestCompletions();
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
        modelWake.Dispose();
        taskWake.Dispose();
    }

    private void OnPublished(CompiledRuntimePlan plan)
        => Apply(plan.HostManager.RequirePublished(), throwOnFailure: false);

    private void Apply(CompiledHostManagerPlan hostPlan, bool throwOnFailure)
    {
        var desired = hostPlan.PublicServiceCoordinator;
        gate.EnterWriteLock();
        try
        {
            if (appliedPlan?.ConfigurationSha256 == desired.ConfigurationSha256)
            {
                return;
            }
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
                replacement = new NativePublicServiceCoordinatorWorkspace(
                    desired,
                    runtimeIdentity.InstanceId,
                    NextHandle(
                        ref nextSessionIncarnation,
                        "public-service session incarnation"),
                    desired.HotPublish.ConfigurationGeneration);
                next = new WorkspaceHolder(replacement, desired);
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
                previous?.Retire();
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
        }
        catch (Exception ex) when (!throwOnFailure)
        {
            logger.LogError(
                ex,
                "Host Manager public-service coordinator apply failed; the prior workspace remains active.");
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
        CompiledHostManagerPublicServiceCoordinatorPlan plan)
    {
        private int referenceCount = 1;
        private int retired;

        internal NativePublicServiceCoordinatorWorkspace Workspace { get; } = workspace;

        internal CompiledHostManagerPublicServiceCoordinatorPlan Plan { get; } = plan;

        internal object ModelGate { get; } = new();

        internal NativePublicServiceModelProjection Models { get; } = new();

        internal void AddReference()
        {
            if (Volatile.Read(ref retired) != 0)
            {
                throw new ObjectDisposedException(nameof(NativePublicServiceCoordinatorWorkspace));
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
