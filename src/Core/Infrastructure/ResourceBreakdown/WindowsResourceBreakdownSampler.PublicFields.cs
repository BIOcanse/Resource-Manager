using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.ReadWrite.ProcessFields;

namespace ResourceManager.App.Infrastructure.ResourceBreakdown;

public sealed partial class WindowsResourceBreakdownSampler
{
    private ProcessMetricFieldDefinition[] gpuResourceFieldCatalog = [];
    public IReadOnlyList<ProcessMetricFieldDefinition> ResourceFactFields => Volatile.Read(ref gpuResourceFieldCatalog);
    internal long ProcessFieldRevision => snapshotState.Revision;
    internal ProcessMetricFieldSnapshot[] ReadProcessFieldParts() => snapshotState.ReadProcessFieldParts();
    internal ProcessMetricFieldSnapshot? ReadProcessFieldPart(string dataset) => snapshotState.ReadProcessFieldPart(dataset);

    /// <summary>Explicit public-field capture shares the original current field
    /// datasets. Legacy resource-table direct capture retains its separate result.</summary>
    internal async Task<ResourceBreakdownSnapshot> CaptureProcessFieldsAsync(ResourceBreakdownSampleRequest request,
        CancellationToken cancellationToken)
    {
        if (request.MetricIds.Count == 0 || request.MetricIds.Any(static name => ProcessMetricFieldRegistry.Find(name) is null))
            throw new ArgumentException("Select canonical process fields.", nameof(request));
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        using var workspace = samplingSubscriptionOwner.Acquire(SamplingRoleId);
        var owner = hostedPublicationState.CaptureDirectOwner();
        await sampleGate.WaitAsync(cancellationToken);
        var attemptedAt = DateTimeOffset.UtcNow;
        try
        {
            var capture = await ReadSnapshotCoreAsync(request, directProcessCpuDeltaTracker, cancellationToken,
                requireHostedHardwareObservation: false);
            cancellationToken.ThrowIfCancellationRequested();
            var timing = CurrentDirectCaptureTiming();
            if (hostedPublicationState.TryApplyDirectProcessFields(owner, workspace.OwnerToken, request, capture.ResourceSnapshot,
                attemptedAt, timing.DefaultInterval, timing.FreshnessGrace,
                () => { if (capture.IoRates is not null) processFieldOwner.DirectIo.Commit(capture.IoRates); }))
                publicationSignal.Publish(GetPublicationDatasetIds(request));
            return capture.ResourceSnapshot;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            if (hostedPublicationState.TryFailDirectProcessFields(owner, workspace.OwnerToken, request, attemptedAt, error,
                () => ClearIoHistory(request, processFieldOwner.DirectIo)))
                publicationSignal.Publish(GetPublicationDatasetIds(request));
            throw;
        }
        finally { sampleGate.Release(); }
    }
}
