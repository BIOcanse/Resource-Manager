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
        var (id, confirm) =
            HostManagerOperationRequestCodec.DecodeBooleanRequest(ticket.Request.Payload);
        try
        {
            var result = await softwareOperationManager.UninstallAsync(
                new SoftwareOperationRequest(id, confirm),
                cancellationToken).ConfigureAwait(false);
            return new(HostManagerOperationEffectOutcome.Succeeded, result.Message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(HostManagerOperationEffectOutcome.Canceled, "操作已取消。");
        }
        catch (IOException error)
        {
            return new(HostManagerOperationEffectOutcome.RetryableFailure, error.Message);
        }
        catch (Exception error)
        {
            return new(HostManagerOperationEffectOutcome.TerminalFailure, error.Message);
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
                "尚无卸载动作执行证据，可重新排队。")
            : new HostManagerOperationEffectCompletion(
                HostManagerOperationEffectOutcome.Uncertain,
                "卸载进程可能已启动，无法安全重复执行。"));
    }
}
