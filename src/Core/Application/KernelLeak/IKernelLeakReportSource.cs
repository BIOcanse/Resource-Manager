using ResourceManager.App.Domain.KernelLeak;

namespace ResourceManager.App.Application.KernelLeak;

public interface IKernelLeakReportSource
{
    // Reads and report subscriptions never start, stop or reset the backend-owned observation demand.
    KernelLeakReport Read();
    KernelLeakDiagnostics ReadDiagnostics();
    IAsyncEnumerable<KernelLeakReport> SubscribeAsync(CancellationToken cancellationToken);
}
public interface IKernelLeakSampleSource
{
    IAsyncEnumerable<KernelLeakSampleFrame> SubscribeAsync(string holder, TimeSpan interval, CancellationToken cancellationToken);
}
public interface IKernelLeakAttributionReader
{
    IReadOnlyList<KernelLeakHolder> ReadBatch(IReadOnlyList<KernelLeakHolder> holders, CancellationToken cancellationToken = default);
}
