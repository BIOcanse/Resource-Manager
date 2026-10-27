using ResourceManager.ReadWrite.Windows.Processes;

namespace ResourceManager.App.Infrastructure.CpuTopology;

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
        using var demand = AcquireSubscription($"thread-cpu:{holder}", TimeSpan.FromSeconds(1));
        while (true)
        {
            await publication.WaitAsync(cancellationToken);
            var snapshot = Read();
            if (snapshot is null)
            {
                if (!Capture.State.Equals("Warming", StringComparison.OrdinalIgnoreCase)) return null;
                continue;
            }
            if (snapshot.MeasuredFrom < request.RequestedAt) continue;
            return request.ReadWindow(snapshot);
        }
    }
}
