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
        var (id, acknowledge) =
            HostManagerOperationRequestCodec.DecodeBooleanRequest(ticket.Request.Payload);
        try
        {
            var result = IsInstall
                ? await componentManager.InstallAsync(
                    id,
                    acknowledge,
                    cancellationToken).ConfigureAwait(false)
                : await componentManager.DownloadAsync(
                    id,
                    acknowledge,
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
                "尚无组件动作执行证据，可重新排队。")
            : new HostManagerOperationEffectCompletion(
                HostManagerOperationEffectOutcome.Uncertain,
                "组件动作可能已发生，无法安全重复执行。"));
    }
}
