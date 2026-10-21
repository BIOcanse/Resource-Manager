using ResourceManager.ReadWrite.Windows.Devices;
using ResourceManager.ReadWrite.Windows.Metrics.Resources;

namespace ResourceManager.App.Infrastructure.Monitoring;

public sealed partial class WindowsStorageSensorsMonitoringZone
{
    private readonly WindowsDiskPerformanceCaptureReader performanceReader = new(
        new WindowsDiskLayoutInformationReader(), TimeProvider.System,
        new WindowsMetricResourceOptions().DiskPerformanceSampleWindow);
    private readonly Func<IReadOnlyList<WindowsDiskPerformanceTarget>, CancellationToken,
        Task<WindowsDiskPerformanceCaptureResult>>? performanceProducer;

    internal WindowsStorageSensorsMonitoringZone(Func<IReadOnlyList<WindowsDiskPerformanceTarget>, CancellationToken,
        Task<WindowsDiskPerformanceCaptureResult>> performanceProducer) : this()
        => this.performanceProducer = performanceProducer ?? throw new ArgumentNullException(nameof(performanceProducer));

    internal Task<WindowsDiskPerformanceCaptureResult> ReadDiskPerformanceAsync(
        IReadOnlyList<WindowsDiskPerformanceTarget> targets, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!CanRead) return Task.FromResult(WindowsDiskPerformanceCaptureResult.Empty(targets));
        return performanceProducer is not null ? performanceProducer(targets, token)
            : performanceReader.ReadAsync(targets, owner.ReadInformationDeclarations(), token);
    }
}
