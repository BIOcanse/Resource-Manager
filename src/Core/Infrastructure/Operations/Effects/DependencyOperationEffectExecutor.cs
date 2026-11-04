using ResourceManager.App.Domain.Messages;
using ResourceManager.App.Application.Dependencies;
using ResourceManager.App.Domain.Dependencies;
using ResourceManager.App.Domain.Software;

namespace ResourceManager.App.Infrastructure.Operations.Effects;

internal sealed class DependencyOperationEffectExecutor(
    IOptionalDependencyManager dependencyManager,
    bool launchInstaller) : IHostManagerOperationEffectExecutor
{
    public string Kind => launchInstaller
        ? HostManagerOperationKinds.DependencyLaunchInstaller
        : HostManagerOperationKinds.DependencyDownload;

    public async ValueTask<HostManagerOperationEffectCompletion> ExecuteStartAsync(
        HostManagerOperationActionTicket ticket,
        IHostManagerOperationProgressSink progress,
        CancellationToken cancellationToken)
    {
        SoftwareOperationCaller? caller = null;
        string id;
        bool acknowledge;
        string? versionChoice;
        if (launchInstaller && ticket.Request.SchemaId == HostManagerOperationRequestSchemas.UserDependencyInstaller)
            (id, acknowledge, versionChoice, caller) =
                HostManagerOperationRequestCodec.DecodeUserAcquisitionRequest(ticket.Request.Payload);
        else
            (id, acknowledge, versionChoice) =
                HostManagerOperationRequestCodec.DecodeAcquisitionRequest(ticket.Request.Payload);
        try
        {
            if (launchInstaller)
            {
                var result = await dependencyManager.LaunchInstallerAsync(
                    id,
                    acknowledge,
                    versionChoice,
                    cancellationToken,
                    caller).ConfigureAwait(false);
                return new(HostManagerOperationEffectOutcome.Succeeded, HostManagerOperationFeedback.Result(result.State, result.Message));
            }

            var progressAdapter = new DurableDependencyProgress(
                ticket,
                progress,
                cancellationToken);
            var download = await dependencyManager.DownloadAsync(
                id,
                acknowledge,
                versionChoice,
                cancellationToken,
                progressAdapter).ConfigureAwait(false);
            return new(HostManagerOperationEffectOutcome.Succeeded, HostManagerOperationFeedback.Result(download.State, download.Message));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(HostManagerOperationEffectOutcome.Canceled, string.Empty);
        }
        catch (HttpRequestException error)
        {
            return new(HostManagerOperationEffectOutcome.RetryableFailure, HostManagerOperationFeedback.Failure(error));
        }
        catch (IOException error)
        {
            return new(HostManagerOperationEffectOutcome.RetryableFailure, HostManagerOperationFeedback.Failure(error));
        }
        catch (Exception error)
        {
            return new(HostManagerOperationEffectOutcome.TerminalFailure, HostManagerOperationFeedback.Failure(error));
        }
    }

    public ValueTask<HostManagerOperationEffectCompletion> ReadbackAsync(
        HostManagerOperationActionTicket ticket,
        HostManagerOperationEffectReceipt receipt,
        CancellationToken cancellationToken)
    {
        _ = ticket;
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(receipt.State == HostManagerOperationEffectReceiptState.Prepared
            ? new HostManagerOperationEffectCompletion(
                HostManagerOperationEffectOutcome.RetryableFailure,
                HostManagerOperationFeedback.Reason(BackendMessageCodes.Operation.NoExecutionEvidence))
            : new HostManagerOperationEffectCompletion(
                HostManagerOperationEffectOutcome.Uncertain,
                HostManagerOperationFeedback.Reason(BackendMessageCodes.Operation.OutcomeUncertain)));
    }

}
