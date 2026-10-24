using ResourceManager.App.Domain.Dependencies;

namespace ResourceManager.App.Infrastructure.Operations.Effects;

internal sealed class DurableDependencyProgress(
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
