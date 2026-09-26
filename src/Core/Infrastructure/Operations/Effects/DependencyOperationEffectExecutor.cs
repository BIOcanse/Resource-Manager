using ResourceManager.App.Application.Dependencies;
using ResourceManager.App.Domain.Dependencies;

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
        var (id, acknowledge, versionChoice) =
            HostManagerOperationRequestCodec.DecodeAcquisitionRequest(ticket.Request.Payload);
        try
        {
            if (launchInstaller)
            {
                var result = await dependencyManager.LaunchInstallerAsync(
                    id,
                    acknowledge,
                    versionChoice,
                    cancellationToken).ConfigureAwait(false);
                // 这一层的完成消息还没迁到消息码（操作日志是二进制持久化的，见 M5）。
                // 暂时不带文本：前端会用自己的本地化「操作完成」，不会漏出中文。
                return new(HostManagerOperationEffectOutcome.Succeeded, string.Empty);
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
            return new(HostManagerOperationEffectOutcome.Succeeded, string.Empty);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(HostManagerOperationEffectOutcome.Canceled, string.Empty);
        }
        catch (HttpRequestException error)
        {
            return new(HostManagerOperationEffectOutcome.RetryableFailure, error.Message);
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

    private sealed class DurableDependencyProgress(
        HostManagerOperationActionTicket ticket,
        IHostManagerOperationProgressSink sink,
        CancellationToken cancellationToken) : IProgress<DependencyDownloadProgress>
    {
        public void Report(DependencyDownloadProgress value)
        {
            uint? percentMilli = value.Percent is null
                ? null
                : checked((uint)Math.Clamp(
                    Math.Round(value.Percent.Value * 1000),
                    0,
                    100_000));
            sink.ReportAsync(
                    ticket,
                    new HostManagerOperationProgressUpdate(
                        percentMilli,
                        checked((ulong)value.BytesWritten),
                        value.TotalBytes is null
                            ? null
                            : checked((ulong)value.TotalBytes.Value),
                        value.SpeedBytesPerSecond is null
                            ? null
                            : checked((ulong)Math.Max(
                                0,
                                Math.Round(value.SpeedBytesPerSecond.Value))),
                        "download",
                        null),
                    cancellationToken)
                .AsTask()
                .GetAwaiter()
                .GetResult();
        }
    }
}
