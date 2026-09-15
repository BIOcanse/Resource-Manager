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
                return new(HostManagerOperationEffectOutcome.Succeeded, result.Message);
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
            return new(HostManagerOperationEffectOutcome.Succeeded, download.Message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(HostManagerOperationEffectOutcome.Canceled, "操作已取消。");
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
                "尚无依赖动作执行证据，可重新排队。")
            : new HostManagerOperationEffectCompletion(
                HostManagerOperationEffectOutcome.Uncertain,
                "依赖动作可能已发生，无法安全重复执行。"));
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
