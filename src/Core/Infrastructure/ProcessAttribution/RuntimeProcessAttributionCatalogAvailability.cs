using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.ProcessAttribution;

// This state belongs to the process-lifetime catalog owner, independently of its consumers.
internal sealed class RuntimeProcessAttributionCatalogAvailability(
    Func<CancellationToken, Task> checkResource,
    RuntimeProcessAttributionCatalogAvailabilityOptions options,
    TimeProvider clock) : IDisposable
{
    private readonly object gate = new();
    private readonly CancellationTokenSource stopping = new();
    private readonly SemaphoreSlim checkGate = new(1, 1);
    private Task? recoveryTask;
    private bool available = true;
    private bool recoverable;
    private bool disposed;
    private long failureRevision;

    internal bool IsAvailable => Volatile.Read(ref available);
    internal event Action? AvailabilityChanged;

    internal bool ReportFailure(Exception error)
    {
        var handled = Classify(error) != FailureKind.Unknown;
        ReportFailure(error, null);
        return handled;
    }

    private void ReportFailure(Exception error, long? expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(error);
        var kind = Classify(error);
        if (kind == FailureKind.Unknown) return;
        bool changed;
        lock (gate)
        {
            if (disposed || (expectedRevision.HasValue && expectedRevision.Value != failureRevision)) return;
            changed = available;
            Volatile.Write(ref available, false);
            // Permission/ABI failures require a corrected owner, not a subsequent incidental IO error.
            recoverable = kind == FailureKind.Transient && (changed || recoverable);
            failureRevision++;
            if (recoverable && options.RecoveryCheckIntervalSeconds > 0 && recoveryTask is null)
                recoveryTask = Task.Run(MonitorAsync);
        }
        if (changed) AvailabilityChanged?.Invoke();
    }

    internal async Task CheckAvailabilityAsync(CancellationToken cancellationToken = default)
    {
        if (!await checkGate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return;
        try
        {
            long revision;
            lock (gate)
            {
                if (disposed || available || !recoverable || options.RecoveryCheckIntervalSeconds <= 0) return;
                revision = failureRevision;
            }
            try
            {
                await checkResource(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (Classify(error) != FailureKind.Unknown)
            {
                ReportFailure(error, revision);
                return;
            }
            lock (gate)
            {
                if (disposed || revision != failureRevision) return;
                Volatile.Write(ref available, true);
                recoverable = false;
            }
            AvailabilityChanged?.Invoke();
        }
        finally { checkGate.Release(); }
    }

    private async Task MonitorAsync()
    {
        try
        {
            while (true)
            {
                int seconds;
                lock (gate)
                {
                    if (disposed || available || !recoverable || options.RecoveryCheckIntervalSeconds <= 0)
                    {
                        recoveryTask = null;
                        return;
                    }
                    seconds = options.RecoveryCheckIntervalSeconds;
                }
                await Task.Delay(TimeSpan.FromSeconds(seconds), clock, stopping.Token).ConfigureAwait(false);
                await CheckAvailabilityAsync(stopping.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            lock (gate) recoveryTask = null;
        }
        catch (Exception error)
        {
            // A programming defect is not a resource availability transition or a reason to retry.
            System.Diagnostics.Trace.TraceError("Catalog availability resource check failed: {0}", error);
            lock (gate)
            {
                recoverable = false;
                recoveryTask = null;
            }
        }
    }

    public void Dispose()
    {
        Task? running;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            running = recoveryTask;
        }
        stopping.Cancel();
        running?.GetAwaiter().GetResult();
        stopping.Dispose();
    }

    private enum FailureKind { Unknown, Transient, Permanent }

    private static FailureKind Classify(Exception error) => error switch
    {
        UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException
            or BadImageFormatException => FailureKind.Permanent,
        NativeSoftwareIdentityException { Status: NativeSoftwareIdentityStatus.AbiMismatch
            or NativeSoftwareIdentityStatus.Unavailable } => FailureKind.Permanent,
        IOException => FailureKind.Transient,
        // A reused catalog cannot demonstrate that the failed native allocation now succeeds.
        // OutOfMemory (managed or native) therefore does not acquire an automatic recovery policy.
        _ => FailureKind.Unknown
    };
}
