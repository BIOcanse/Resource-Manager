using ResourceManager.App.Domain.CpuTopology;
using ResourceManager.App.Infrastructure.Telemetry.Etw;

namespace ResourceManager.ReadWrite.Windows.Processes;

public sealed partial class EtwCpuResidencyCaptureOwner
{
    private readonly object diagnosticGate = new();
    private readonly HashSet<ThreadDiagnosticRequest> diagnosticRequests = [];
    private ResidencySessionState? diagnosticSession;
    internal int DiagnosticRequestCount { get { lock (diagnosticGate) return diagnosticRequests.Count; } }

    private void BindDiagnosticSource(ResidencySessionState session)
    {
        lock (diagnosticGate)
        {
            diagnosticSession = session;
            foreach (var request in diagnosticRequests)
                request.BindNative(session.Native.Session, session.Generation, session.Plan.ObservationWindow, session.EventSource);
        }
    }

    private void ClearDiagnosticSource()
    {
        lock (diagnosticGate)
        {
            foreach (var request in diagnosticRequests) request.UnbindNative();
            diagnosticSession = null;
        }
    }

    // Metadata demand does not acquire any kernel hold or start capture.
    public ThreadDiagnosticRequest RequestThreadDiagnostics(int processId, long processCreationFileTime100Ns)
    {
        if (processId <= 0) throw new ArgumentOutOfRangeException(nameof(processId));
        if (processCreationFileTime100Ns <= 0) throw new ArgumentOutOfRangeException(nameof(processCreationFileTime100Ns));
        lock (diagnosticGate)
        {
            ObjectDisposedException.ThrowIf(stopped, this);
            var request = new ThreadDiagnosticRequest(this, processId, processCreationFileTime100Ns);
            if (diagnosticSession is { } session)
                request.BindNative(session.Native.Session, session.Generation, session.Plan.ObservationWindow, session.EventSource);
            diagnosticRequests.Add(request);
            return request;
        }
    }

    private void DisposeDiagnosticRequests()
    {
        lock (diagnosticGate)
            foreach (var request in diagnosticRequests.ToArray()) request.Dispose();
    }

    public sealed class ThreadDiagnosticRequest : IDisposable
    {
        private readonly EtwCpuResidencyCaptureOwner owner;
        private NativeThreadDiagnosticRequest? nativeRequest;
        private bool disposed;
        public int ProcessId { get; }
        public long ProcessCreationFileTime100Ns { get; }
        public DateTimeOffset RequestedAt { get; } = DateTimeOffset.UtcNow;
        internal ThreadDiagnosticRequest(EtwCpuResidencyCaptureOwner owner, int pid, long birth)
        { this.owner = owner; ProcessId = pid; ProcessCreationFileTime100Ns = birth; }
        internal void BindNative(NativeKernelEtwSession session, long generation, TimeSpan sampleBudget, KernelEtwEventSource source)
        {
            if (nativeRequest?.Session == session && ReferenceEquals(nativeRequest.EventSource, source)) return;
            UnbindNative();
            nativeRequest = new(session, generation, ProcessId, ProcessCreationFileTime100Ns, RequestedAt, sampleBudget, source);
        }
        internal void UnbindNative() { nativeRequest?.Dispose(); nativeRequest = null; }
        public CpuProcessThreadWindow? ReadWindow(CpuCoreResidencySnapshot? snapshot)
        {
            NativeThreadDiagnosticRequest? native;
            lock (owner.diagnosticGate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (snapshot is null || snapshot.MeasuredFrom < RequestedAt) return null;
                native = nativeRequest;
            }
            return native?.ReadWindow(snapshot);
        }
        public Task<CpuProcessThreadWindow?> ReadWindowAsync(CpuCoreResidencySnapshot? snapshot,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            NativeThreadDiagnosticRequest? native;
            lock (owner.diagnosticGate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (snapshot is null || snapshot.MeasuredFrom < RequestedAt) return Task.FromResult<CpuProcessThreadWindow?>(null);
                native = nativeRequest;
            }
            return native is null ? Task.FromResult<CpuProcessThreadWindow?>(null)
                : native.ReadWindowAsync(snapshot, cancellationToken);
        }
        public void Dispose()
        {
            lock (owner.diagnosticGate)
            {
                if (disposed) return;
                UnbindNative(); disposed = true; owner.diagnosticRequests.Remove(this);
            }
        }
    }
}
