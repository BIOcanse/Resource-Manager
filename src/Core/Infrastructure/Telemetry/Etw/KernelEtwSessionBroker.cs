using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.ReadWrite.Windows.Processes.Etw;

namespace ResourceManager.App.Infrastructure.Telemetry.Etw;

/// <summary>Host retry/lifetime adapter over the single SDK session resource owner.</summary>
public sealed class KernelEtwSessionBroker : BackgroundService, IKernelEtwSessionBroker
{
    private readonly object lifetimeGate = new();
    private readonly KernelEtwSessionOwner owner;
    private bool disposed;
    private int stoppingRuns;
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
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        lock (lifetimeGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (stoppingRuns != 0 || ExecuteTask is { IsCompleted: false })
                throw new InvalidOperationException("The previous kernel ETW run has not drained.");
            // BackgroundService can cancel its scheduled delegate before ExecuteAsync
            // ever enters. Finish that run's resources before resuming the owner.
            if (ExecuteTask is not null) owner.StopRun();
            owner.ResumeRun();
            return base.StartAsync(cancellationToken);
        }
    }
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        Task? execution;
        lock (lifetimeGate)
        {
            execution = ExecuteTask;
            stoppingRuns++;
        }
        try { await base.StopAsync(cancellationToken); }
        finally
        {
            bool drainOwner;
            lock (lifetimeGate)
            {
                drainOwner = ReferenceEquals(ExecuteTask, execution) && (execution is null || execution.IsCompleted);
            }
            try
            {
                if (drainOwner) owner.StopRun();
            }
            finally
            {
                lock (lifetimeGate) stoppingRuns--;
            }
        }
    }
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
        finally { owner.StopRun(); }
    }
    public override void Dispose()
    {
        Task? execution;
        lock (lifetimeGate)
        {
            disposed = true;
            base.Dispose();
            execution = ExecuteTask;
        }
        try { execution?.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) when (execution is { IsCanceled: true }) { }
        finally { owner.Dispose(); }
    }
}
