using ResourceManager.ReadWrite.Windows.Processes.Etw;

namespace ResourceManager.App.Infrastructure.Telemetry.Etw;

/// <summary>Held kernel ETW resource. The first holder opens it and the last holder releases it.</summary>
public sealed class KernelEtwSessionBroker : IKernelEtwSessionBroker, IDisposable
{
    private readonly object lifetimeGate = new();
    private readonly KernelEtwSessionOwner owner;
    private bool disposed;
    private int holders;
    private Timer? reconciliation;
    private readonly TimeSpan reconciliationInterval;
    private readonly ILogger<KernelEtwSessionBroker> logger;
    public KernelEtwSessionBroker(ILogger<KernelEtwSessionBroker> logger)
    {
        this.logger = logger;
        owner = new(new TraceEventKernelEtwSessionRuntimeFactory(sessionName =>
            ProcessOwnedEtwSessionOrphanReconciler.CreateDefault(logger).Reconcile(
                ResourceManagerEtwSessionNames.KernelPrefix, ResourceManagerEtwSessionNames.CurrentOwner, sessionName)),
            new(value => Report(logger, value)));
        reconciliationInterval = TimeSpan.FromSeconds(5);
    }
    internal KernelEtwSessionBroker(IKernelEtwSessionRuntimeFactory factory, ILogger<KernelEtwSessionBroker> logger)
        : this(factory, logger, TimeSpan.FromSeconds(5)) { }
    internal KernelEtwSessionBroker(IKernelEtwSessionRuntimeFactory factory, ILogger<KernelEtwSessionBroker> logger, TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
        this.logger = logger;
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
    public IKernelEtwSubscription Subscribe(KernelEtwSubscriptionRequest request)
    {
        lock (lifetimeGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var subscription = owner.Subscribe(request);
            if (holders++ == 0)
                reconciliation = new Timer(_ => Reconcile(), null, reconciliationInterval, reconciliationInterval);
            return new Holder(this, subscription);
        }
    }
    public KernelEtwSessionSnapshot GetSnapshot() => owner.GetSnapshot();
    private void Release(IKernelEtwSubscription subscription)
    {
        lock (lifetimeGate)
        {
            try { subscription.Dispose(); }
            finally
            {
                if (--holders == 0)
                {
                    reconciliation?.DisposeAsync().AsTask().GetAwaiter().GetResult();
                    reconciliation = null;
                }
            }
        }
    }
    private void Reconcile()
    {
        try { owner.Reconcile(); }
        catch (Exception error) { logger.LogWarning(error, "Shared kernel ETW resource reconciliation failed."); }
    }
    public void Dispose()
    {
        lock (lifetimeGate)
        {
            if (disposed)
            {
                owner.Dispose();
                return;
            }
            disposed = true;
            reconciliation?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            reconciliation = null;
            owner.Dispose();
        }
    }
    private sealed class Holder(KernelEtwSessionBroker broker, IKernelEtwSubscription subscription) : IKernelEtwSubscription
    {
        private KernelEtwSessionBroker? owner = broker;
        public KernelEtwSessionSnapshot GetSnapshot() => subscription.GetSnapshot();
        public void Dispose() => Interlocked.Exchange(ref owner, null)?.Release(subscription);
    }
}
