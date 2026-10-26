using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Domain.ResourceBreakdown;

namespace ResourceManager.App.Infrastructure.ResourceBreakdown;

/// <summary>Composes physical-memory capture with the existing GPU residual provider.</summary>
public sealed class WindowsResourceResidualBreakdownProvider : IResourceResidualBreakdownProvider,
    IResourceResidualCaptureSource, IGpuVramResidualObservationSource
{
    private readonly IResourceResidualBreakdownProvider gpu;
    private readonly IGpuVramResidualObservationSource? gpuObservation;
    private readonly ILogger<WindowsResourceResidualBreakdownProvider> logger;
    private readonly Func<CancellationToken, PhysicalMemoryResidualCapture> capture;
    private readonly object stateGate = new();
    private string? captureState;
    private string? projectionState;

    public WindowsResourceResidualBreakdownProvider(DxgkrnlVidMmEtwResidualBreakdownProvider gpu,
        ILogger<WindowsResourceResidualBreakdownProvider> logger)
        : this(gpu, logger, new WindowsPhysicalMemoryResidualReader().Capture) { }

    internal WindowsResourceResidualBreakdownProvider(IResourceResidualBreakdownProvider gpu,
        ILogger<WindowsResourceResidualBreakdownProvider> logger,
        Func<CancellationToken, PhysicalMemoryResidualCapture> capture)
    {
        this.gpu = gpu;
        gpuObservation = gpu as IGpuVramResidualObservationSource;
        this.logger = logger;
        this.capture = capture;
    }

    public IResourceResidualBreakdownProvider CaptureResiduals(CancellationToken cancellationToken)
    {
        var current = capture(cancellationToken);
        ReportState(current.State, isCapture: true);
        return new PhysicalMemoryResidualBreakdownProvider(current, gpu,
            state => ReportState(state, isCapture: false));
    }

    // Used without a capture only for other metrics. A read never performs physical-memory capture.
    public IReadOnlyList<ResourceProcessSegment> CreateResidualSegments(ResourceResidualBreakdownRequest request)
        => request.MetricId.Equals(ResourceBreakdownMetricIds.MemoryUsage, StringComparison.OrdinalIgnoreCase)
            ? new PhysicalMemoryResidualBreakdownProvider(new(null, null, 0, "not-captured"), gpu,
                static _ => { }).CreateResidualSegments(request)
            : gpu.CreateResidualSegments(request);

    public IDisposable AcquireSubscription(string owner, TimeSpan interval)
        => gpuObservation?.AcquireSubscription(owner, interval) ?? EmptyLease.Instance;

    private void ReportState(string state, bool isCapture)
    {
        lock (stateGate)
        {
            ref var previous = ref (isCapture ? ref captureState : ref projectionState);
            if (previous == state) return;
            previous = state;
            if (state == "ready") logger.LogInformation("Physical memory residual {Stage}: {State}",
                isCapture ? "capture" : "partition", state);
            else logger.LogWarning("Physical memory residual {Stage}: {State}; unavailable detail is withheld",
                isCapture ? "capture" : "partition", state);
        }
    }

    private sealed class EmptyLease : IDisposable
    {
        internal static readonly EmptyLease Instance = new();
        public void Dispose() { }
    }
}
