using ResourceManager.App.Application.Components;

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
        _ = progress;
        var (id, acknowledge, versionChoice) =
            HostManagerOperationRequestCodec.DecodeAcquisitionRequest(ticket.Request.Payload);
        try
        {
            var result = IsInstall
                ? await componentManager.InstallAsync(
                    id,
                    acknowledge,
                    versionChoice,
                    cancellationToken).ConfigureAwait(false)
                : await componentManager.DownloadAsync(
                    id,
                    acknowledge,
                    versionChoice,
                    cancellationToken).ConfigureAwait(false);
            // 这一层的完成消息还没迁到消息码（操作日志是二进制持久化的，见 M5）。
            // 暂时不带文本：前端会用自己的本地化「操作完成」，不会漏出中文。
            return new(HostManagerOperationEffectOutcome.Succeeded, string.Empty);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(HostManagerOperationEffectOutcome.Canceled, string.Empty);
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
                string.Empty)
            : new HostManagerOperationEffectCompletion(
                HostManagerOperationEffectOutcome.Uncertain,
                string.Empty));
    }
}
