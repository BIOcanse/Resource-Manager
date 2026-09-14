using ResourceManager.App.Application.PublicServices.AiModels;
using ResourceManager.App.Domain.PublicServices.AiModels;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.PublicServices;

public sealed partial class HostManagerPublicServiceCoordinatorOwner
{
    private Task<AiModelLoadResult> EnqueueLoadAsync(
        AiModelLoadRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrEmpty(request.Model))
        {
            throw new ArgumentException(
                "A model identity is required.",
                nameof(request));
        }

        var operation = new PendingLoadOperation(request);
        EnqueueOperation(
            request.Model,
            operation,
            NativePublicServiceTaskKind.Load,
            static plan => plan.HotPublish.LoadTaskBaseScore);
        return operation.Completion.WaitAsync(cancellationToken);
    }

    private Task<AiModelUnloadResult> EnqueueUnloadAsync(
        AiModelUnloadRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrEmpty(request.InstanceId))
        {
            throw new ArgumentException(
                "A loaded model instance identity is required.",
                nameof(request));
        }

        var operation = new PendingUnloadOperation(request);
        EnqueueOperation(
            request.InstanceId,
            operation,
            NativePublicServiceTaskKind.Unload,
            static plan => plan.HotPublish.UnloadTaskBaseScore);
        return operation.Completion.WaitAsync(cancellationToken);
    }

    private void EnqueueOperation(
        string alias,
        PendingModelOperation operation,
        NativePublicServiceTaskKind kind,
        Func<CompiledHostManagerPublicServiceCoordinatorPlan, long> selectBaseScore)
    {
        gate.EnterReadLock();
        try
        {
            ThrowIfDisposed();
            var holder = current
                ?? throw new InvalidOperationException(
                    "The Host Manager public-service coordinator is not ready.");
            lock (holder.ModelGate)
            {
                var resolved = holder.Workspace.ResolveModel(alias);
                var status = (NativePublicServiceModelResolveStatus)resolved.Status;
                if (status == NativePublicServiceModelResolveStatus.CatalogUnavailable)
                {
                    Signal(modelWake);
                    throw new AiModelRuntimeUnavailableException(
                        "The native local-model catalog has no usable provider snapshot.");
                }
                if (status != NativePublicServiceModelResolveStatus.Matched
                    || resolved.ModelHandle == 0
                    || resolved.PayloadHandle == 0)
                {
                    throw new ArgumentException(
                        $"The local-model catalog does not contain '{alias}'.",
                        nameof(alias));
                }

                var payloads = holder.Models.Capture();
                if (!payloads.ByPayloadHandle.TryGetValue(
                        resolved.PayloadHandle,
                        out var descriptor))
                {
                    throw new InvalidOperationException(
                        "The native model identity has no matching transport payload.");
                }
                operation.BindDescriptor(descriptor);

                var payloadHandle = NextHandle(
                    ref nextOperationPayloadHandle,
                    "public-service task payload");
                if (!pendingModelOperations.TryAdd(payloadHandle, operation))
                {
                    throw new InvalidOperationException(
                        "The public-service task payload handle was reused.");
                }
                try
                {
                    var task = holder.Workspace.EnqueueTask(
                        holder.Plan.HotPublish.AnonymousCallerHandle,
                        resolved.ModelHandle,
                        payloadHandle,
                        selectBaseScore(holder.Plan),
                        kind);
                    if (task.PayloadHandle != payloadHandle
                        || task.ModelHandle != resolved.ModelHandle
                        || task.Kind != (uint)kind
                        || task.State != (uint)NativePublicServiceTaskState.Queued
                        || task.Attempt != 0)
                    {
                        throw new InvalidOperationException(
                            "The native public-service queue returned a non-canonical task.");
                    }
                }
                catch
                {
                    _ = pendingModelOperations.TryRemove(payloadHandle, out _);
                    throw;
                }
            }
        }
        finally
        {
            gate.ExitReadLock();
        }
        Signal(taskWake);
    }

    private async Task RunTaskLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            WorkspaceHolder? holder = null;
            ulong nextWake = 0;
            try
            {
                holder = AcquireCurrent();
                var batch = holder.Workspace.PlanTasks(
                    holder.Plan.Recreate.MaximumConcurrentModelTasks);
                var flags = (NativePublicServiceTaskPlanFlags)batch.Flags;
                ValidateTaskPlan(batch, flags);
                nextWake = batch.NextWakeMonotonicMilliseconds;
                if (batch.Tasks.Length != 0)
                {
                    await Task.WhenAll(batch.Tasks.Select(
                        task => ExecuteTaskAsync(holder, task, cancellationToken)))
                        .ConfigureAwait(false);
                    RetryDesiredPlan();
                    Signal(taskWake);
                }
                else if (flags.HasFlag(NativePublicServiceTaskPlanFlags.MoreReady))
                {
                    Signal(taskWake);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogCritical(ex, "The native local-model task loop faulted.");
                throw;
            }
            finally
            {
                holder?.Release();
            }

            await WaitUntilAsync(taskWake, nextWake, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ExecuteTaskAsync(
        WorkspaceHolder holder,
        NativePublicServiceTaskOutput task,
        CancellationToken cancellationToken)
    {
        ValidateTask(task);
        if (!pendingModelOperations.TryGetValue(task.PayloadHandle, out var operation)
            || operation.Kind != (NativePublicServiceTaskKind)task.Kind)
        {
            throw new InvalidOperationException(
                "The native public-service task has no exact pending operation payload.");
        }

        var effect = new NativeTaskEffectResult(
            NativePublicServiceTaskEffectOutcome.Failed,
            0);
        Exception? failure = null;
        using var effectCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var now = NativePublicServiceCoordinatorWorkspace.MonotonicMilliseconds();
        if (task.DeadlineMonotonicMilliseconds <= now)
        {
            effectCancellation.Cancel();
        }
        else
        {
            effectCancellation.CancelAfter(
                TimeSpan.FromMilliseconds(
                    checked((long)(task.DeadlineMonotonicMilliseconds - now))));
        }
        try
        {
            await operation.ExecuteAsync(runtimeProvider, effectCancellation.Token)
                .ConfigureAwait(false);
            effect = new NativeTaskEffectResult(
                NativePublicServiceTaskEffectOutcome.Succeeded,
                0);
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            failure = ex;
            effect = new NativeTaskEffectResult(
                NativePublicServiceTaskEffectOutcome.Cancelled,
                0);
        }
        catch (OperationCanceledException ex) when (effectCancellation.IsCancellationRequested)
        {
            failure = ex;
            effect = new NativeTaskEffectResult(
                NativePublicServiceTaskEffectOutcome.Timeout,
                0);
        }
        catch (Exception ex)
        {
            failure = ex;
            effect = ClassifyTaskEffectOutcome(ex);
        }

        var completion = holder.Workspace.CompleteTask(
            task.TaskHandle,
            task.Attempt,
            effect.Outcome,
            effect.HttpStatusCode);
        if (completion.Disposition
            == NativePublicServiceTaskCompletionDisposition.RetryScheduled)
        {
            logger.LogDebug(
                failure,
                "Local-model task {TaskHandle} will be retried by the native queue at {NextWake}.",
                task.TaskHandle,
                completion.NextWakeMonotonicMilliseconds);
            return;
        }

        if (!pendingModelOperations.TryRemove(task.PayloadHandle, out var settled)
            || !ReferenceEquals(settled, operation))
        {
            throw new InvalidOperationException(
                "The completed public-service task lost its exact pending payload.");
        }
        if (effect.Outcome == NativePublicServiceTaskEffectOutcome.Cancelled)
        {
            operation.Cancel();
        }
        else if (completion.Disposition
            == NativePublicServiceTaskCompletionDisposition.Succeeded)
        {
            operation.Complete();
            Signal(modelWake);
        }
        else
        {
            operation.Fail(
                failure
                ?? new InvalidOperationException(
                    "The local-model provider task failed without an exception."));
        }
    }

    private static void ValidateTaskPlan(
        NativePublicServiceTaskPlanBatch batch,
        NativePublicServiceTaskPlanFlags flags)
    {
        if ((flags & ~NativePublicServiceTaskPlanFlags.Known) != 0)
        {
            throw new InvalidOperationException(
                "The native task plan contains unknown flags.");
        }
        var hasWake = flags.HasFlag(NativePublicServiceTaskPlanFlags.NextWakeValid);
        if (hasWake != (batch.NextWakeMonotonicMilliseconds != 0))
        {
            throw new InvalidOperationException(
                "The native task plan wake flag and timestamp diverged.");
        }
    }

    private static void ValidateTask(NativePublicServiceTaskOutput task)
    {
        var kind = (NativePublicServiceTaskKind)task.Kind;
        if (task.TaskHandle == 0
            || task.CallerHandle == 0
            || task.ModelHandle == 0
            || task.PayloadHandle == 0
            || task.Attempt == 0
            || task.State != (uint)NativePublicServiceTaskState.Running
            || kind is not (
                NativePublicServiceTaskKind.Load
                or NativePublicServiceTaskKind.Unload))
        {
            throw new InvalidOperationException(
                "The native public-service task is non-canonical.");
        }
    }

    private static NativeTaskEffectResult ClassifyTaskEffectOutcome(
        Exception exception)
        => exception switch
        {
            AiModelRuntimeUnavailableException =>
                new(NativePublicServiceTaskEffectOutcome.ProviderUnavailable, 0),
            TimeoutException =>
                new(NativePublicServiceTaskEffectOutcome.Timeout, 0),
            HttpRequestException { StatusCode: null } =>
                new(NativePublicServiceTaskEffectOutcome.TransportFailure, 0),
            HttpRequestException { StatusCode: not null } http =>
                new(
                    NativePublicServiceTaskEffectOutcome.HttpResponse,
                    checked((uint)(int)http.StatusCode.Value)),
            ArgumentException =>
                new(NativePublicServiceTaskEffectOutcome.Rejected, 0),
            _ => new(NativePublicServiceTaskEffectOutcome.Failed, 0)
        };

    private readonly record struct NativeTaskEffectResult(
        NativePublicServiceTaskEffectOutcome Outcome,
        uint HttpStatusCode);

    private abstract class PendingModelOperation(
        NativePublicServiceTaskKind kind)
    {
        internal NativePublicServiceTaskKind Kind { get; } = kind;

        internal abstract Task ExecuteAsync(
            IAiModelRuntimeProvider provider,
            CancellationToken cancellationToken);

        internal abstract void BindDescriptor(AiModelDescriptor descriptor);

        internal abstract void Complete();

        internal abstract void Fail(Exception exception);

        internal abstract void Cancel();
    }

    private sealed class PendingLoadOperation
        : PendingModelOperation
    {
        private readonly TaskCompletionSource<AiModelLoadResult> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly AiModelLoadRequest request;
        private AiModelLoadRequest effectiveRequest;
        private AiModelLoadResult? result;

        internal PendingLoadOperation(AiModelLoadRequest request)
            : base(NativePublicServiceTaskKind.Load)
        {
            this.request = request;
            effectiveRequest = request;
        }

        internal Task<AiModelLoadResult> Completion => completion.Task;

        internal override void BindDescriptor(AiModelDescriptor descriptor)
            => effectiveRequest = request with { Model = descriptor.Key };

        internal override async Task ExecuteAsync(
            IAiModelRuntimeProvider provider,
            CancellationToken cancellationToken)
            => result = await provider.LoadAsync(effectiveRequest, cancellationToken)
                .ConfigureAwait(false);

        internal override void Complete()
            => completion.TrySetResult(
                result
                ?? throw new InvalidOperationException(
                    "The model load completed without a result."));

        internal override void Fail(Exception exception)
            => completion.TrySetException(exception);

        internal override void Cancel()
            => completion.TrySetCanceled();
    }

    private sealed class PendingUnloadOperation
        : PendingModelOperation
    {
        private readonly TaskCompletionSource<AiModelUnloadResult> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly AiModelUnloadRequest request;
        private AiModelUnloadRequest effectiveRequest;
        private AiModelUnloadResult? result;

        internal PendingUnloadOperation(AiModelUnloadRequest request)
            : base(NativePublicServiceTaskKind.Unload)
        {
            this.request = request;
            effectiveRequest = request;
        }

        internal Task<AiModelUnloadResult> Completion => completion.Task;

        internal override void BindDescriptor(AiModelDescriptor descriptor)
        {
            if (!descriptor.LoadedInstances.Any(instance =>
                    string.Equals(
                        instance.InstanceId,
                        request.InstanceId,
                        StringComparison.Ordinal)))
            {
                throw new ArgumentException(
                    $"The loaded model instance '{request.InstanceId}' is not present in the native catalog.",
                    nameof(request));
            }
            effectiveRequest = request;
        }

        internal override async Task ExecuteAsync(
            IAiModelRuntimeProvider provider,
            CancellationToken cancellationToken)
            => result = await provider.UnloadAsync(effectiveRequest, cancellationToken)
                .ConfigureAwait(false);

        internal override void Complete()
            => completion.TrySetResult(
                result
                ?? throw new InvalidOperationException(
                    "The model unload completed without a result."));

        internal override void Fail(Exception exception)
            => completion.TrySetException(exception);

        internal override void Cancel()
            => completion.TrySetCanceled();
    }
}
