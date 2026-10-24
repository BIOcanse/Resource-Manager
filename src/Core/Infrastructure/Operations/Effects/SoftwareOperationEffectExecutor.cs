using ResourceManager.App.Domain.Messages;
using ResourceManager.App.Application.Software;
using ResourceManager.App.Domain.Software;

namespace ResourceManager.App.Infrastructure.Operations.Effects;

internal sealed class SoftwareOperationEffectExecutor(
    ISoftwareOperationManager softwareOperationManager)
    : IHostManagerOperationEffectExecutor
{
    public string Kind => HostManagerOperationKinds.SoftwareUninstall;

    public async ValueTask<HostManagerOperationEffectCompletion> ExecuteStartAsync(
        HostManagerOperationActionTicket ticket,
        IHostManagerOperationProgressSink progress,
        CancellationToken cancellationToken)
    {
        _ = progress;
        try
        {
            SoftwareOperationRequest request;
            SoftwareOperationCaller? caller = null;
            if (ticket.Request.SchemaId == HostManagerOperationRequestSchemas.UserSoftwareUninstall)
                (request, caller) = HostManagerOperationRequestCodec.DecodeUserSoftwareUninstall(ticket.Request.Payload);
            else
            {
                var (id, confirm) = HostManagerOperationRequestCodec.DecodeBooleanRequest(ticket.Request.Payload);
                request = new(id, confirm);
            }
            var result = await softwareOperationManager.UninstallAsync(
                request, caller,
                cancellationToken).ConfigureAwait(false);
            return new(HostManagerOperationEffectOutcome.Succeeded, HostManagerOperationFeedback.Result(result.State, result.Message));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(HostManagerOperationEffectOutcome.Canceled, string.Empty);
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
