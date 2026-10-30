using Microsoft.Diagnostics.Tracing.Parsers;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Infrastructure.Telemetry.Etw;

namespace ResourceManager.App.Infrastructure.ResourceBreakdown;

/// <summary>Owns the explicitly running network capture hold and closed native publications.</summary>
public sealed partial class EtwNetworkTcpIpCaptureOwner : IDisposable
{
    // UDPv0 can omit the header PID; the native thread history resolves its
    // header TID. Process snapshots verify birth directly and need no Process events.
    private const KernelTraceEventParser.Keywords RequiredKeywords = KernelTraceEventParser.Keywords.NetworkTCPIP | KernelTraceEventParser.Keywords.Thread;
    private readonly IKernelEtwSessionBroker kernelEtwBroker;
    private readonly KernelEtwDiagnostics logger;
    private readonly object subscriptionGate = new(), resourceGate = new(), eventGate = new();
    private readonly Func<long> readTickMilliseconds;
    private readonly Func<DateTimeOffset> utcNow;
    private IKernelEtwSubscription? kernelSubscription;
    private long sessionGeneration;
    private bool disposed;
    private volatile string state = NetworkAttributionProviderStates.Idle;
    private volatile string message = "ETW 网络事件归因按需启动。";

    /// <summary>The capture hold follows the host's explicit run until Stop.</summary>
    public EtwNetworkTcpIpCaptureOwner(IKernelEtwSessionBroker broker, KernelEtwDiagnostics? diagnostics = null)
        : this(broker, diagnostics ?? new(), static () => Environment.TickCount64) { }

    internal EtwNetworkTcpIpCaptureOwner(IKernelEtwSessionBroker broker,
        KernelEtwDiagnostics diagnostics, Func<long> clock, Func<DateTimeOffset>? utcNow = null)
    {
        kernelEtwBroker = broker ?? throw new ArgumentNullException(nameof(broker));
        readTickMilliseconds = clock ?? throw new ArgumentNullException(nameof(clock));
        this.utcNow = utcNow ?? (static () => DateTimeOffset.UtcNow); logger = diagnostics;
    }

    public string State => state;
    public string Message => message;
    public bool IsRunning
    {
        get
        {
            lock (subscriptionGate) if (kernelSubscription is null) return false;
            var snapshot = Volatile.Read(ref kernelSubscription)?.GetSnapshot() ?? kernelEtwBroker.GetSnapshot();
            return snapshot.IsRunning && snapshot.Generation == Volatile.Read(ref sessionGeneration);
        }
    }

    /// <summary>Returns the current closed native publication without acquiring resources.</summary>
    public NetworkAttributionSnapshot Read(bool includeObservedTraffic = false)
    {
        lock (subscriptionGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            lock (eventGate)
            {
                if (nativeSession is not null) return ReadNative(includeObservedTraffic);
                if (kernelSubscription is not null)
                {
                    var snapshot = CaptureSnapshot();
                    state = snapshot.IsRunning ? NetworkAttributionProviderStates.Unavailable : NetworkAttributionProviderStates.FromKernelEtwState(snapshot.State);
                    message = snapshot.IsRunning ? "Network capture has no native shared session binding." : snapshot.Message;
                }
                return new(utcNow(), new Dictionary<int, NetworkProcessAttribution>(), new("etw-network-tcpip", state, message),
                    new Dictionary<int, NetworkProcessAttribution>());
            }
        }
    }

    public void Start()
    {
        lock (resourceGate)
        {
            lock (subscriptionGate) ObjectDisposedException.ThrowIf(disposed, this);
            AcquireCaptureHold();
        }
    }

    private void AcquireCaptureHold()
    {
        if (kernelSubscription is not null) return;
        try
        {
            kernelSubscription = kernelEtwBroker.Acquire(new("network-tcpip-attribution", RequiredKeywords, BindKernelEvents)
                { NativeOutputs = KernelEtwNativeOutputs.Network });
            RefreshBrokerState();
        }
        catch (Exception error)
        {
            state = NetworkAttributionProviderStates.Unavailable; message = $"ETW 网络事件归因启动失败：{error.Message}";
            logger.LogWarning(error, "Failed to start ETW TCP/IP network attribution reader.");
        }
    }

    private void BindKernelEvents(KernelEtwEventSource source, KernelEtwSessionContext context)
    {
        var session = source.NativeSession ?? throw new InvalidOperationException("Network capture requires a native shared kernel ETW session.");
        source.ConsumerCounter("network-transfer");
        source.ConsumerRejectionCounter("network-transfer", "invalid-payload");
        source.ConsumerRejectionCounter("network-transfer", "incomplete-window");
        source.ConsumerRejectionCounter("network-transfer", "stale-binding");
        source.ConsumerRejectionCounter("network-transfer", "capacity");
        source.ConfigureAdmission(windowActive: true, incompleteThrough: readTickMilliseconds());
        lock (eventGate) { sessionGeneration = context.Generation; BindNative(session); }
        state = NetworkAttributionProviderStates.Warming; message = "ETW 网络事件归因正在采集进程收发事件。";
    }

    private KernelEtwSessionSnapshot CaptureSnapshot()
    {
        return Volatile.Read(ref kernelSubscription)?.GetSnapshot() ?? kernelEtwBroker.GetSnapshot();
    }

    private void RefreshBrokerState()
    {
        var snapshot = CaptureSnapshot();
        state = snapshot.IsRunning && snapshot.Generation == sessionGeneration
            ? NetworkAttributionProviderStates.Warming : NetworkAttributionProviderStates.FromKernelEtwState(snapshot.State);
        message = snapshot.Message;
    }

    public void Stop(string idleMessage = "ETW network capture stopped.")
    {
        lock (resourceGate)
        {
            ReleaseCaptureHold();
            state = NetworkAttributionProviderStates.Idle; message = idleMessage;
        }
    }

    private void ReleaseCaptureHold()
    {
        IKernelEtwSubscription? retired;
        lock (subscriptionGate)
        {
            retired = kernelSubscription; kernelSubscription = null; sessionGeneration = 0;
        }
        try { retired?.Dispose(); }
        finally { lock (eventGate) BindNative(null); }
    }

    public void Dispose() => Dispose("ETW network capture stopped.");
    internal void Dispose(string stoppedMessage)
    {
        lock (subscriptionGate) { if (disposed) return; disposed = true; }
        Stop(stoppedMessage);
    }

    // Scalar helper still used by the independent VidMm ETW source.
    internal static ulong? ToEventFileTime(DateTime timestamp)
    {
        try { return checked((ulong)timestamp.ToUniversalTime().ToFileTimeUtc()); }
        catch (ArgumentOutOfRangeException) { return null; }
        catch (OverflowException) { return null; }
    }
}
