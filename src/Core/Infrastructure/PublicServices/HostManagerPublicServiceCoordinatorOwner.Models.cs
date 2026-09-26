using ResourceManager.App.Application.PublicServices.AiModels;
using ResourceManager.App.Domain.PublicServices.AiModels;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.PublicServices;

public sealed partial class HostManagerPublicServiceCoordinatorOwner
{
    public Task<AiModelRuntimeStatus> GetStatusAsync(CancellationToken cancellationToken)
        => runtimeProvider.GetStatusAsync(cancellationToken);

    public Task<IReadOnlyList<AiModelDescriptor>> ListModelsAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var holder = AcquireCurrent();
        try
        {
            lock (holder.ModelGate)
            {
                var state = holder.Workspace.ReadSnapshot();
                var flags = (NativePublicServiceCoordinatorSnapshotFlags)state.Flags;
                if (!flags.HasFlag(
                        NativePublicServiceCoordinatorSnapshotFlags.ModelCatalogUsable))
                {
                    Signal(modelWake);
                    throw new AiModelRuntimeUnavailableException(
                        "The native local-model catalog has no usable provider snapshot.");
                }
                var payloads = holder.Models.Capture();
                if (payloads.Descriptors.Count != state.ModelCount)
                {
                    throw new InvalidOperationException(
                        "The native local-model catalog and its transport payloads diverged.");
                }
                return Task.FromResult(payloads.Descriptors);
            }
        }
        finally
        {
            holder.Release();
        }
    }

    public Task<bool> IsAllowedAsync(
        string model,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrEmpty(model))
        {
            return Task.FromResult(false);
        }
        var holder = AcquireCurrent();
        try
        {
            lock (holder.ModelGate)
            {
                var output = holder.Workspace.ResolveModel(model);
                return Task.FromResult(
                    (NativePublicServiceModelResolveStatus)output.Status
                        == NativePublicServiceModelResolveStatus.Matched);
            }
        }
        finally
        {
            holder.Release();
        }
    }

    public Task<AiModelLoadResult> LoadAsync(
        AiModelLoadRequest request,
        CancellationToken cancellationToken)
        => EnqueueLoadAsync(request, cancellationToken);

    public Task<AiModelUnloadResult> UnloadAsync(
        AiModelUnloadRequest request,
        CancellationToken cancellationToken)
        => EnqueueUnloadAsync(request, cancellationToken);

    public Task ForwardAsync(
        HttpContext context,
        string relativePath,
        CancellationToken cancellationToken)
        => runtimeProvider.ForwardAsync(context, relativePath, cancellationToken);

    private async Task RunModelAcquisitionLoopAsync(CancellationToken cancellationToken)
    {
        await modelWake.WaitAsync(cancellationToken).ConfigureAwait(false);
        while (!cancellationToken.IsCancellationRequested)
        {
            WorkspaceHolder? holder = null;
            ulong nextWake = 0;
            try
            {
                holder = AcquireCurrent();
                var plan = holder.Workspace.PlanModelAcquisition();
                nextWake = plan.NextWakeMonotonicMilliseconds;
                switch ((NativePublicServiceModelAcquisitionPlanStatus)plan.Status)
                {
                    case NativePublicServiceModelAcquisitionPlanStatus.Start:
                        await AcquireModelCatalogAsync(
                            holder,
                            plan,
                            cancellationToken).ConfigureAwait(false);
                        nextWake = 0;
                        break;
                    case NativePublicServiceModelAcquisitionPlanStatus.NotDue:
                    case NativePublicServiceModelAcquisitionPlanStatus.Running:
                        break;
                    default:
                        throw new InvalidOperationException(
                            $"Unknown native model-acquisition plan status {plan.Status}.");
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogCritical(ex, "The native local-model acquisition loop faulted.");
                throw;
            }
            finally
            {
                holder?.Release();
            }

            await WaitUntilAsync(modelWake, nextWake, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task AcquireModelCatalogAsync(
        WorkspaceHolder holder,
        NativePublicServiceModelAcquisitionPlanOutput plan,
        CancellationToken cancellationToken)
    {
        try
        {
            var descriptors = await runtimeProvider.ListModelsIfRunningAsync(cancellationToken)
                .ConfigureAwait(false);
            var projection = holder.Models.Project(
                descriptors,
                holder.Plan.HotPublish.AiProviderHandle);
            if (plan.ModelGenerationAtStart == ulong.MaxValue)
            {
                throw new InvalidOperationException(
                    "The native local-model catalog generation is exhausted.");
            }
            lock (holder.ModelGate)
            {
                holder.Workspace.PublishModels(
                    plan.AttemptHandle,
                    plan.ModelGenerationAtStart + 1,
                    projection.Models,
                    projection.Aliases,
                    projection.Text);
                holder.Models.Commit(projection.Payloads);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            CompleteFailedAcquisition(
                holder,
                plan.AttemptHandle,
                NativePublicServiceModelAcquisitionCompletionStatus.Failed,
                new OperationCanceledException(
                    "The local-model catalog acquisition was cancelled by Host Manager shutdown.",
                    cancellationToken));
            throw;
        }
        catch (AiModelRuntimeUnavailableException ex)
        {
            CompleteFailedAcquisition(
                holder,
                plan.AttemptHandle,
                NativePublicServiceModelAcquisitionCompletionStatus.Unavailable,
                ex);
        }
        catch (Exception ex)
        {
            CompleteFailedAcquisition(
                holder,
                plan.AttemptHandle,
                NativePublicServiceModelAcquisitionCompletionStatus.Failed,
                ex);
        }
        finally
        {
            RetryDesiredPlan();
        }
    }

    private void CompleteFailedAcquisition(
        WorkspaceHolder holder,
        ulong attemptHandle,
        NativePublicServiceModelAcquisitionCompletionStatus status,
        Exception acquisitionFailure)
    {
        try
        {
            holder.Workspace.CompleteModelAcquisition(attemptHandle, status);
        }
        catch (Exception completionFailure)
        {
            throw new AggregateException(acquisitionFailure, completionFailure);
        }
        logger.LogDebug(
            acquisitionFailure,
            "Local-model provider catalog acquisition completed as {Status}.",
            status);
    }
}
