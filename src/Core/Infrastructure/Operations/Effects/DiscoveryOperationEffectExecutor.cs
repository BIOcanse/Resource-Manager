using ResourceManager.App.Application.Migration;

namespace ResourceManager.App.Infrastructure.Operations.Effects;

internal sealed class DiscoveryOperationEffectExecutor(
    ISoftwareDataDiscoveryManager discoveryManager)
    : IHostManagerOperationEffectExecutor
{
    public string Kind => HostManagerOperationKinds.DiscoveryStart;

    public async ValueTask<HostManagerOperationEffectCompletion> ExecuteStartAsync(
        HostManagerOperationActionTicket ticket,
        IHostManagerOperationProgressSink progress,
        CancellationToken cancellationToken)
    {
        _ = progress;
        var request =
            HostManagerOperationRequestCodec.DecodeDiscoveryStart(ticket.Request.Payload);
        string? sessionId = null;
        try
        {
            var session = await discoveryManager.StartSessionAsync(
                request,
                cancellationToken).ConfigureAwait(false);
            sessionId = session.Id;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
                .ConfigureAwait(false);
            return new(
                HostManagerOperationEffectOutcome.Uncertain,
                "发现会话在没有取消信号的情况下退出等待。");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (sessionId is not null)
            {
                _ = discoveryManager.StopSession(sessionId);
            }
            return new(HostManagerOperationEffectOutcome.Canceled, "发现会话已停止。");
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
                "尚无发现会话启动证据，可重新排队。")
            : new HostManagerOperationEffectCompletion(
                HostManagerOperationEffectOutcome.Uncertain,
                "旧发现会话无法跨进程证明存活状态。"));
    }
}
