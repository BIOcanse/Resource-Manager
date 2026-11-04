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
    public IKernelEtwSubscription Acquire(KernelEtwSubscriptionRequest request)
    {
        lock (lifetimeGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var subscription = owner.Acquire(request);
            if (holders++ == 0)
            {
                var previous = Interlocked.Exchange(ref reconciliation, null);
                previous?.Dispose();
                Timer? next = null;
                next = new Timer(_ => Reconcile(next), null, reconciliationInterval, reconciliationInterval);
                reconciliation = next;
            }
            return new Holder(this, subscription);
        }
    }
    public KernelEtwSessionSnapshot GetSnapshot() => owner.GetSnapshot();
    internal KernelEtwHolderView[] ReadHolders() => owner.ReadHolders();
    private void Release(IKernelEtwSubscription subscription)
    {
        lock (lifetimeGate)
        {
            var released = false;
            try { subscription.Dispose(); released = true; }
            finally
            {
                if (--holders == 0 && released)
                {
                    reconciliation?.DisposeAsync().AsTask().GetAwaiter().GetResult();
                    reconciliation = null;
                }
            }
        }
    }
    private void Reconcile(Timer? expected)
    {
        try
        {
            owner.Reconcile();
            // Last-return cleanup may need a later retry after a published read
            // reference returns. Stop this cleanup timer once release is proven.
            if (Volatile.Read(ref holders) == 0 && owner.GetSnapshot().State is "Idle" or "Stopped"
                && ReferenceEquals(Interlocked.CompareExchange(ref reconciliation, null, expected), expected))
                expected?.Dispose();
        }
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
