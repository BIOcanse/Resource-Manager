using Microsoft.Extensions.Logging;
using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;

namespace ResourceManager.App.Infrastructure.Telemetry.Etw;

// A held resource has a measurable thread but no control node. Reuse the thread
// measurement mechanism without registering it in the business-block graph.
internal sealed class KernelEtwConsumerThreadDiagnostics(
    ILogger<KernelEtwConsumerThreadDiagnostics> logger) : IKernelEtwConsumerThreadObserver
{
    private readonly BusinessBlockMetrics metrics = new();
    public IDisposable RegisterCurrentThread(string sessionName)
        => metrics.RegisterCurrentThread(sessionName);
    public void ReportFailure(Exception error)
        => logger.LogError(error, "Kernel ETW consumer flow failed.");
    internal BusinessBlockMetricFacts Read() => metrics.Read();
}
