using ResourceManager.App.Application.DiskUsage;

namespace ResourceManager.App.Infrastructure.Operations.Effects;

/// <summary>
/// 磁盘占用扫描接进操作协调器：进度条、取消、任务中心里的条目都由协调器现成提供，
/// 这里只负责把扫描跑起来并把进度转成协调器认识的形状。
/// </summary>
internal sealed class DiskUsageScanEffectExecutor(
    IDiskUsageScanner scanner,
    IDiskUsageTreeStore store) : IHostManagerOperationEffectExecutor
{
    public string Kind => HostManagerOperationKinds.DiskUsageScan;

    public async ValueTask<HostManagerOperationEffectCompletion> ExecuteStartAsync(
        HostManagerOperationActionTicket ticket,
        IHostManagerOperationProgressSink progress,
        CancellationToken cancellationToken)
    {
        var request = HostManagerOperationRequestCodec.DecodeDiskUsageScan(
            ticket.Request.Payload);
        var sink = new ProgressBridge(ticket, progress, cancellationToken);
        try
        {
            var result = await scanner
                .ScanAsync(request, sink, cancellationToken)
                .ConfigureAwait(false);
            // 扫完才整棵替换：读取端不会看到扫到一半的树。
            store.Replace(result);
            // 这一层不带文本：结果的措辞由前端按扫描概况自己出。
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
        // 扫描结果只在内存里，进程重启后不存在，所以重启前没结算的扫描一律作废重来。
        return ValueTask.FromResult(new HostManagerOperationEffectCompletion(
            HostManagerOperationEffectOutcome.RetryableFailure,
            string.Empty));
    }

    /// <summary>
    /// 把扫描器的进度转成协调器的进度。遍历式扫描算不出比例时就不报比例，
    /// 界面据此显示不确定进度，而不是拿一个编出来的数字糊弄。
    /// </summary>
    private sealed class ProgressBridge(
        HostManagerOperationActionTicket ticket,
        IHostManagerOperationProgressSink sink,
        CancellationToken cancellationToken) : IProgress<DiskUsageScanProgress>
    {
        public void Report(DiskUsageScanProgress value)
        {
            var update = new HostManagerOperationProgressUpdate(
                PercentMilli: value.Fraction is { } fraction
                    ? (uint)Math.Clamp(Math.Round(fraction * 100_000), 0, 100_000)
                    : null,
                BytesDone: value.BytesSeen > 0 ? (ulong)value.BytesSeen : 0,
                BytesTotal: null,
                SpeedBytesPerSecond: null);
            sink.ReportAsync(ticket, update, cancellationToken)
                .AsTask()
                .GetAwaiter()
                .GetResult();
        }
    }
}
