using ResourceManager.ReadWrite.Windows.Processes;

namespace ResourceManager.App.Infrastructure.CpuTopology;

internal sealed record ThreadCpuWindowBoundary(DateTimeOffset MeasuredFrom, DateTimeOffset MeasuredThrough,
    long SessionGeneration);
internal sealed record ThreadCpuWindowWaitDiagnostics(string WaitingFor, int ProcessId, long ProcessStartKey,
    DateTimeOffset RequestedAt, int PublicationsObserved, ThreadCpuWindowBoundary? LastWindow,
    long RevisionAtRequest, long RevisionAtCancellation, TimeSpan CaptureInterval, bool HasSessionLease,
    TaskStatus? WorkerStatus, EtwCpuCoreResidencyDiagnostics Residency);
internal sealed class ThreadCpuWindowWaitCanceledException(ThreadCpuWindowWaitDiagnostics diagnostics,
    OperationCanceledException inner, CancellationToken cancellationToken)
    : OperationCanceledException("Thread CPU capture was canceled while waiting for a fresh closed residency window.",
        inner, cancellationToken)
{
    internal ThreadCpuWindowWaitDiagnostics Diagnostics { get; } = diagnostics;
}

public sealed partial class EtwCpuCoreResidencyReader
{
    /// <summary>One fresh closed window, held by this consumer through the original subscription source.</summary>
    public async Task<CpuProcessThreadWindow?> CaptureThreadWindowAsync(string holder, int processId,
        long processCreationFileTime100Ns, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(holder);
        cancellationToken.ThrowIfCancellationRequested();
        using var request = Capture.RequestThreadDiagnostics(processId, processCreationFileTime100Ns);
        using var publication = publicationSignal.Subscribe();
        var revisionAtRequest = Capture.Revision;
        var publicationsObserved = 0;
        var lastWindow = SummarizeThreadWindow(Read());
        using var demand = AcquireSubscription($"thread-cpu:{holder}", TimeSpan.FromSeconds(1));
        try
        {
            while (true)
            {
                await publication.WaitAsync(cancellationToken);
                publicationsObserved++;
                var snapshot = Read();
                lastWindow = SummarizeThreadWindow(snapshot);
                if (snapshot is null)
                {
                    if (!Capture.State.Equals("Warming", StringComparison.OrdinalIgnoreCase)) return null;
                    continue;
                }
                if (snapshot.MeasuredFrom < request.RequestedAt) continue;
                return request.ReadWindow(snapshot);
            }
        }
        catch (OperationCanceledException error) when (cancellationToken.IsCancellationRequested)
        {
            // Keep request-local evidence before releasing this consumer's demand and native handles.
            // No extra capture, timer or retry is started to diagnose a canceled wait.
            throw new ThreadCpuWindowWaitCanceledException(new("fresh-closed-window", processId,
                processCreationFileTime100Ns, request.RequestedAt, publicationsObserved, lastWindow,
                revisionAtRequest, Capture.Revision, captureInterval, Capture.HasSessionLease,
                ExecuteTask?.Status, GetDiagnostics()), error, cancellationToken);
        }
    }

    private static ThreadCpuWindowBoundary? SummarizeThreadWindow(
        Domain.CpuTopology.CpuCoreResidencySnapshot? snapshot)
        => snapshot is null ? null : new(snapshot.MeasuredFrom, snapshot.MeasuredThrough, snapshot.SessionGeneration);
}
