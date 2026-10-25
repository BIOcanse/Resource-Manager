using ResourceManager.App.Domain.CpuTopology;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Publications;
using ResourceManager.ReadWrite.Windows.Cpu.Models;
using ResourceManager.ReadWrite.Windows.Devices.Models;

namespace ResourceManager.ReadWrite.Windows.Cpu.Snapshots;

public sealed record CpuTopologyCapture(CpuTopologySnapshot? Snapshot,
    CpuOsTopologyInformation? Information, DateTimeOffset CapturedAt);
public sealed record CpuTopologyPublishedState(CpuTopologySnapshot? Snapshot, DeviceInformationSnapshot InformationFields);
public enum CpuTopologyCaptureAdmission { Capture, Skip, SkipAndResetBaseline }
public enum CpuTopologyCaptureCompletion { Skipped, Failed, Sampled }
/// <summary>Caller admission and synchronous atomic retirement commit. TryPublish must execute its action before returning true.</summary>
public sealed record CpuTopologyCapturePermission(Func<CpuTopologyCaptureAdmission> Admit, Func<Action, bool> TryPublish)
{
    /// <summary>Runs only in an accepted commit, after the exact current state is written. The flag records this attempt's source success.</summary>
    public Action<CpuTopologyPublishedState, bool>? CaptureCommitted { get; init; }

    public CpuTopologyCapturePermission(Func<CpuTopologyCaptureAdmission> admit,
        Func<Action, bool> tryPublish, Action<CpuTopologyPublishedState, bool>? captureCommitted)
        : this(admit, tryPublish)
        => CaptureCommitted = captureCommitted;
}

/// <summary>Original serialized capture, paired current and publication owner. No worker or demand policy.
/// The default source reads actual Windows facts and never constructs a product fallback snapshot.
/// Stop admitting work before Dispose; callbacks must not dispose this owner. Synchronous OS reads cannot be interrupted by cancellation.</summary>
public sealed class CpuTopologyCaptureOwner : IDisposable
{
    private readonly SemaphoreSlim sampleGate = new(1, 1);
    private readonly CurrentValuePublicationSignal publicationSignal = new();
    private readonly Func<CpuTopologyCapture> capture;
    private readonly Action resetUsageBaseline;
    private readonly Action<Exception>? logFailure;
    private CpuTopologyPublishedState current = new(null, DeviceInformationSnapshot.Empty);
    private int usageBaselineResetPending;
    private int disposed;

    public CpuTopologyCaptureOwner() : this(new WindowsCpuOsTopologyReader()) { }
    private CpuTopologyCaptureOwner(WindowsCpuOsTopologyReader reader)
        : this(() => new(null, reader.ReadInformation(includeUsage: true), DateTimeOffset.UtcNow), reader.ResetUsageBaseline) { }
    public CpuTopologyCaptureOwner(Func<CpuTopologyCapture> capture, Action resetUsageBaseline, Action<Exception>? logFailure = null)
    {
        this.capture = capture ?? throw new ArgumentNullException(nameof(capture));
        this.resetUsageBaseline = resetUsageBaseline ?? throw new ArgumentNullException(nameof(resetUsageBaseline));
        this.logFailure = logFailure;
    }
    public CpuTopologyPublishedState ReadState() => Volatile.Read(ref current);
    public DeviceInformationSnapshot ReadInformation() => ReadState().InformationFields;
    public CurrentValuePublicationSignal.Subscription SubscribePublications() => publicationSignal.Subscribe();
    /// <summary>Canonical catalog/support binding over this same current and explicit capture; caller supplies support policy.</summary>
    public InformationReadWriteBackend CreateBackend(Func<DeviceInformationDefinition, OperationSupport> readSupport)
        => new(ReadInformation, token => CaptureInformationAsync(token), readSupport);

    public async ValueTask<DeviceInformationSnapshot> CaptureInformationAsync(CancellationToken cancellationToken,
        CpuTopologyCapturePermission? permission = null)
    {
        await CaptureAsync(cancellationToken, permission);
        cancellationToken.ThrowIfCancellationRequested();
        return ReadInformation();
    }
    public async Task<CpuTopologyCaptureCompletion> CaptureAsync(CancellationToken cancellationToken,
        CpuTopologyCapturePermission? permission = null)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        await sampleGate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            ApplyPendingUsageBaselineReset();
            var admission = permission?.Admit() ?? CpuTopologyCaptureAdmission.Capture;
            if (admission != CpuTopologyCaptureAdmission.Capture)
            {
                if (admission == CpuTopologyCaptureAdmission.SkipAndResetBaseline) resetUsageBaseline();
                return CpuTopologyCaptureCompletion.Skipped;
            }
            CpuTopologyPublishedState value;
            var sourceQuerySucceeded = false;
            try
            {
                var sample = capture();
                value = new(sample.Snapshot, CpuInformationFieldProjection.Create(sample.Information, sample.CapturedAt, ReadInformation()));
                sourceQuerySucceeded = true;
            }
            catch (Exception error)
            {
                resetUsageBaseline();
                value = new(null, CpuInformationFieldProjection.ClearValues(ReadInformation()));
                logFailure?.Invoke(error);
            }
            cancellationToken.ThrowIfCancellationRequested();
            void Publish()
            {
                Volatile.Write(ref current, value);
                try { permission?.CaptureCommitted?.Invoke(value, sourceQuerySucceeded); }
                catch (Exception error) { logFailure?.Invoke(error); }
                publicationSignal.Publish();
            }
            var published = true;
            if (permission is null) Publish();
            else published = permission.TryPublish(Publish);
            return !published ? CpuTopologyCaptureCompletion.Skipped : sourceQuerySucceeded
                ? CpuTopologyCaptureCompletion.Sampled : CpuTopologyCaptureCompletion.Failed;
        }
        finally { sampleGate.Release(); }
    }
    /// <summary>Discards the usage baseline without waiting: now when no capture holds the gate, otherwise inside the gate
    /// before the next capture reads, so a capture running when demand ended cannot carry its baseline across the idle period.</summary>
    public void ResetUsageBaselineBeforeNextCapture()
    {
        Volatile.Write(ref usageBaselineResetPending, 1);
        if (!sampleGate.Wait(0)) return;
        try { ApplyPendingUsageBaselineReset(); }
        finally { sampleGate.Release(); }
    }
    public async Task DrainAndResetUsageBaselineAsync()
    {
        await sampleGate.WaitAsync();
        try
        {
            Volatile.Write(ref usageBaselineResetPending, 0);
            resetUsageBaseline();
        }
        finally { sampleGate.Release(); }
    }
    private void ApplyPendingUsageBaselineReset()
    {
        if (Interlocked.Exchange(ref usageBaselineResetPending, 0) != 0) resetUsageBaseline();
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        sampleGate.Wait();
        sampleGate.Release();
        // Retain the managed semaphore until GC: previously queued callers must wake, observe disposal and release.
        // No AvailableWaitHandle/native session is allocated by this owner; disposal does not clear current or reset baseline.
    }
}

