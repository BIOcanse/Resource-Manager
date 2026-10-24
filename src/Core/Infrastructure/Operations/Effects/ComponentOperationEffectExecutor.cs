using ResourceManager.App.Domain.Messages;
using ResourceManager.App.Application.Components;
using ResourceManager.App.Domain.Software;

namespace ResourceManager.App.Infrastructure.Operations.Effects;

internal sealed class ComponentOperationEffectExecutor(
    IComponentManager componentManager) : IHostManagerOperationEffectExecutor
{
    public string Kind => HostManagerOperationKinds.ComponentDownload;

    internal static ComponentOperationEffectExecutor ForInstall(
        IComponentManager componentManager)
        => new(componentManager, install: true);

    private ComponentOperationEffectExecutor(
        IComponentManager componentManager,
        bool install)
        : this(componentManager)
    {
        IsInstall = install;
    }

    private bool IsInstall { get; }

    string IHostManagerOperationEffectExecutor.Kind
        => IsInstall
            ? HostManagerOperationKinds.ComponentInstall
            : HostManagerOperationKinds.ComponentDownload;

    public async ValueTask<HostManagerOperationEffectCompletion> ExecuteStartAsync(
        HostManagerOperationActionTicket ticket,
        IHostManagerOperationProgressSink progress,
        CancellationToken cancellationToken)
    {
        var progressAdapter = new DurableDependencyProgress(ticket, progress, cancellationToken);
        SoftwareOperationCaller? caller = null;
        string id;
        bool acknowledge;
        string? versionChoice;
        if (IsInstall && ticket.Request.SchemaId == HostManagerOperationRequestSchemas.UserComponentInstall)
            (id, acknowledge, versionChoice, caller) =
                HostManagerOperationRequestCodec.DecodeUserAcquisitionRequest(ticket.Request.Payload);
        else
            (id, acknowledge, versionChoice) =
                HostManagerOperationRequestCodec.DecodeAcquisitionRequest(ticket.Request.Payload);
        try
        {
            var result = IsInstall
                ? await componentManager.InstallAsync(
                    id,
                    acknowledge,
                    versionChoice,
                    cancellationToken,
                    caller,
                    progressAdapter).ConfigureAwait(false)
                : await componentManager.DownloadAsync(
                    id,
                    acknowledge,
                    versionChoice,
                    cancellationToken,
                    progressAdapter).ConfigureAwait(false);
            return new(HostManagerOperationEffectOutcome.Succeeded, HostManagerOperationFeedback.Result(result.State, result.Message));
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
