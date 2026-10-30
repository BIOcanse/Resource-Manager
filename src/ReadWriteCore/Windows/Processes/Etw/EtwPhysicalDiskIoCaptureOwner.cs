using Microsoft.Diagnostics.Tracing.Parsers;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Infrastructure.Telemetry.Etw;

namespace ResourceManager.ReadWrite.Windows.Processes;

/// <summary>Owns an explicit DiskIO hold and copies closed native publications.</summary>
public sealed partial class EtwPhysicalDiskIoCaptureOwner : IDisposable
{
    // Completion records may name only the issuing thread. The native decoder
    // uses event-time Thread metadata to resolve its PID; Process invalidates
    // the verified PID/birth cache.
    // DiskIOInit is not requested: this owner consumes completion records only.
    private const KernelTraceEventParser.Keywords RequiredKeywords = KernelTraceEventParser.Keywords.DiskIO
        | KernelTraceEventParser.Keywords.Process | KernelTraceEventParser.Keywords.Thread;
    private const long WindowMilliseconds = 2000;
    private readonly IKernelEtwSessionBroker broker;
    private readonly KernelEtwDiagnostics diagnostics;
    private readonly Func<long> clock;
    private readonly Func<DateTimeOffset> utcNow;
    private readonly object subscriptionGate = new();
    private readonly object eventGate = new();
    private readonly PhysicalDiskIoCaptureConfiguration? requestedNativeConfiguration;
    private PhysicalDiskIoCaptureConfiguration selectedConfiguration;
    private IKernelEtwSubscription? subscription;
    private long generation, startedTick = long.MinValue;
    private bool disposed, disposeCompleted;
    private volatile string state = PhysicalDiskIoProviderStates.Idle;
    private volatile string message = "ETW DiskIO 物理磁盘归因按需启动。";

    /// <summary>An omitted configuration adopts the native runtime's immutable disk limits at binding.
    /// An explicit configuration must match; conflicts report requested and actual values.</summary>
    public EtwPhysicalDiskIoCaptureOwner(IKernelEtwSessionBroker broker, KernelEtwDiagnostics? diagnostics = null,
        PhysicalDiskIoCaptureConfiguration? configuration = null)
        : this(broker, diagnostics ?? new(), static () => Environment.TickCount64, configuration) { }

    internal EtwPhysicalDiskIoCaptureOwner(IKernelEtwSessionBroker broker, KernelEtwDiagnostics diagnostics,
        Func<long> clock, PhysicalDiskIoCaptureConfiguration? configuration = null, Func<DateTimeOffset>? utcNow = null)
    {
        ArgumentNullException.ThrowIfNull(broker);
        ArgumentNullException.ThrowIfNull(clock);
        if (configuration is { BucketWidthMilliseconds: <= 0 }) throw new ArgumentOutOfRangeException(nameof(configuration));
        if (configuration is { MaximumActivePairs: <= 0 }) throw new ArgumentOutOfRangeException(nameof(configuration));
        this.broker = broker; this.diagnostics = diagnostics; this.clock = clock;
        this.utcNow = utcNow ?? (static () => DateTimeOffset.UtcNow);
        requestedNativeConfiguration = configuration;
        selectedConfiguration = configuration ?? PhysicalDiskIoCaptureConfiguration.Default;
    }

    public string State => state;
    public string Message => message;
    public PhysicalDiskIoCaptureConfiguration Configuration => Volatile.Read(ref selectedConfiguration);
    public bool IsRunning
    {
        get
        {
            lock (subscriptionGate)
                return subscription is not null && subscription.GetSnapshot() is { IsRunning: true } snapshot
                    && snapshot.Generation == Volatile.Read(ref generation);
        }
    }

    /// <summary>Copies the current closed native publication. Reading never acquires a hold or starts capture.</summary>
    public PhysicalDiskIoAttributionSnapshot Read(bool includeObservedTraffic = false)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        lock (eventGate)
        {
            if (nativeSession is not null) return ReadNative(includeObservedTraffic);
            if (subscription is { } held)
            {
                var snapshot = held.GetSnapshot();
                state = snapshot.IsRunning ? PhysicalDiskIoProviderStates.Unavailable : PhysicalDiskIoProviderStates.FromKernelEtwState(snapshot.State);
                message = snapshot.IsRunning ? "DiskIO capture has no native shared session binding." : snapshot.Message;
            }
            return new(utcNow(), new Dictionary<int, PhysicalDiskProcessAttribution>(), new("etw-fileio-disk", state, message));
        }
    }

    public void Start()
    {
        lock (subscriptionGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (subscription is not null) return;
            try
            {
                subscription = broker.Acquire(new("physical-disk-io-attribution", RequiredKeywords, BindKernelEvents)
                    { NativeOutputs = KernelEtwNativeOutputs.Disk });
            }
            catch (Exception error)
            {
                state = PhysicalDiskIoProviderStates.Unavailable; message = error.Message;
                diagnostics.LogWarning(error, "Failed to start shared DiskIO source.");
            }
        }
    }

    private void BindKernelEvents(KernelEtwEventSource source, KernelEtwSessionContext context)
    {
        var session = source.NativeSession ?? throw new InvalidOperationException("DiskIO capture requires a native shared kernel ETW session.");
        source.ConsumerCounter("disk-transfer");
        source.ConsumerRejectionCounter("disk-transfer", "invalid-payload");
        source.ConsumerRejectionCounter("disk-transfer", "stale-binding");
        source.ConsumerRejectionCounter("disk-transfer", "before-process-birth");
        source.ConsumerCounter("process-identity");
        source.ConsumerRejectionCounter("process-identity", "stale-binding");
        source.ConfigureAdmission(windowActive: true);
        lock (eventGate)
        {
            generation = context.Generation; startedTick = clock();
            BindNative(session);
            state = PhysicalDiskIoProviderStates.Warming; message = "ETW DiskIO 采样窗口不完整。";
        }
    }

    public void Stop(string idleMessage = "ETW DiskIO 物理磁盘归因已停止。")
    {
        IKernelEtwSubscription? retired;
        lock (subscriptionGate) { retired = subscription; subscription = null; Volatile.Write(ref generation, 0); }
        try { retired?.Dispose(); }
        finally
        {
            lock (subscriptionGate)
            {
                if (subscription is null)
                {
                    lock (eventGate) { BindNative(null); startedTick = long.MinValue; }
                    state = PhysicalDiskIoProviderStates.Idle; message = idleMessage;
                }
            }
        }
    }

    public void Dispose()
    {
        lock (subscriptionGate) { if (disposeCompleted) return; disposed = true; }
        Stop("ETW DiskIO 物理磁盘归因正在随程序停止。");
        lock (subscriptionGate) disposeCompleted = true;
    }
}
