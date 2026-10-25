using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.ReadWrite.Windows.Processes.Etw;

namespace ResourceManager.App.Infrastructure.Telemetry.Etw;

/// <summary>Host retry/lifetime adapter over the single SDK session resource owner.</summary>
public sealed class KernelEtwSessionBroker : BackgroundService, IKernelEtwSessionBroker
{
    private readonly KernelEtwSessionOwner owner;
    private readonly TimeSpan reconciliationInterval;
    private readonly IBusinessBlockDiagnostics? blockDiagnostics;
    public KernelEtwSessionBroker(ILogger<KernelEtwSessionBroker> logger,
        IEtwConsumerThreadObserver? consumerThreadObserver = null,
        IBusinessBlockDiagnostics? blockDiagnostics = null)
    {
        this.blockDiagnostics = blockDiagnostics;
        owner = new(new TraceEventKernelEtwSessionRuntimeFactory(sessionName =>
            ProcessOwnedEtwSessionOrphanReconciler.CreateDefault(logger).Reconcile(
                ResourceManagerEtwSessionNames.KernelPrefix, ResourceManagerEtwSessionNames.CurrentOwner, sessionName), consumerThreadObserver),
            new(value =>
            {
                Report(logger, value);
                if (value.Exception is { } error)
                    blockDiagnostics?.ReportFailure(nameof(KernelEtwSessionBroker), error, "kernel-session");
            }));
        reconciliationInterval = TimeSpan.FromSeconds(5);
    }
    internal KernelEtwSessionBroker(IKernelEtwSessionRuntimeFactory factory, ILogger<KernelEtwSessionBroker> logger)
        : this(factory, logger, TimeSpan.FromSeconds(5)) { }
    internal KernelEtwSessionBroker(IKernelEtwSessionRuntimeFactory factory, ILogger<KernelEtwSessionBroker> logger, TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
        reconciliationInterval = interval;
        owner = new(factory, new(value => Report(logger, value)));
    }
    internal KernelEtwSessionOwner Owner => owner;
    internal static void Report(ILogger logger, KernelEtwDiagnostic value)
        => logger.Log(value.Level switch
        {
            KernelEtwDiagnosticLevel.Debug => LogLevel.Debug,
            KernelEtwDiagnosticLevel.Information => LogLevel.Information,
            KernelEtwDiagnosticLevel.Warning => LogLevel.Warning,
            _ => LogLevel.Error
        }, value.Exception, value.MessageTemplate, value.Arguments.ToArray());
    public IKernelEtwSubscription Subscribe(KernelEtwSubscriptionRequest request) => owner.Subscribe(request);
    public KernelEtwSessionSnapshot GetSnapshot() => owner.GetSnapshot();
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(reconciliationInterval, stoppingToken);
                using var segment = blockDiagnostics?.MeasureSegment(nameof(KernelEtwSessionBroker));
                owner.Reconcile();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { owner.Dispose(); }
    }
    public override Task StopAsync(CancellationToken cancellationToken) { owner.Dispose(); return base.StopAsync(cancellationToken); }
    public override void Dispose() { owner.Dispose(); base.Dispose(); }
}
