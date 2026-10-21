using ResourceManager.Subscription.Native;

namespace ResourceManager.Subscription;

public partial class NativeItemSamplingSubscriptionTracker<TRequest, TContext>
{
    private readonly Func<ulong, uint, INativeSamplingOutputScratch>? createOutputScratch;
    private INativeSamplingOutputScratch? outputScratch;
    private INativeSamplingOutputScratch? pendingOutputClose;
    private NativeSamplingOutputScratchLease? outputUse;
    private bool outputOperationActive;
    private bool outputClosing;

    internal NativeItemSamplingSubscriptionTracker(ISamplingSubscriptionSessionOwner<TContext> owner, int roleId,
        Func<TRequest, string> createSourceKey, Func<TRequest, IEnumerable<string>> getItemIds,
        Func<IReadOnlyList<string>, IReadOnlyList<NativeItemSamplingSubscriptionSourceView<TRequest>>, TRequest?> createRequest,
        bool coalesceItemsIntoLatestCapture, Func<ulong, uint, INativeSamplingOutputScratch>? createOutputScratch)
        : this(owner, roleId, createSourceKey, getItemIds, createRequest, coalesceItemsIntoLatestCapture)
        => this.createOutputScratch = createOutputScratch;

    private NativeSamplingOutputView BorrowPlanOutputs(scoped in NativeSamplingSubscriptionCapacity capacity)
    {
        if (createOutputScratch is null)
        {
            EnsureBuffers(capacity);
            return new NativeSamplingOutputView(dueBuffer, sourceBuffer, expiredBuffer, triggerBuffer);
        }
        if (outputUse is not null)
            throw new InvalidOperationException("The prior sampling output use could not be returned; close must be retried.");
        ClosePendingOutputScratch();
        var layout = new NativeSamplingOutputLayout(in capacity);
        INativeSamplingOutputScratch? candidate = null;
        try
        {
            var resource = outputScratch;
            if (resource is null || resource.Length != layout.StorageLength)
            {
                candidate = createOutputScratch(layout.Length, NativeSamplingOutputLayout.Alignment)
                    ?? throw new InvalidOperationException("The sampling output factory returned no resource.");
                if (candidate.Length != layout.StorageLength)
                    throw new InvalidOperationException("The sampling output factory returned a different layout length.");
                resource = candidate;
            }
            outputUse = resource.Acquire();
            if (outputUse.Length != resource.Length)
                throw new InvalidOperationException("The sampling output lease has a different allocation length.");
            var view = new NativeSamplingOutputView(outputUse, in layout);
            if (candidate is not null)
            {
                // No new Plan has run yet. A failed acquisition/validation keeps the old descriptor.
                pendingOutputClose = outputScratch;
                outputScratch = candidate;
                candidate = null;
            }
            return view;
        }
        catch
        {
            // The outer synchronous scope returns any real lease. Retain a failed
            // candidate for the next explicit close; never lose its ownership.
            if (candidate is not null) pendingOutputClose = candidate;
            throw;
        }
    }

    private NativeItemSamplingSubscriptionPlan<TRequest, TContext> FinishOutputPlan(
        NativeItemSamplingSubscriptionPlan<TRequest, TContext> result,
        ref NativeSamplingOutputView view, ref bool outputReleaseAttempted)
    {
        outputUse?.Complete();
        view.Dispose();
        outputReleaseAttempted = true;
        EndOutputUse();
        return result;
    }

    private void EndOutputUse()
    {
        outputUse?.Dispose();
        outputUse = null; // A failed endUse retains the actual lease for explicit close retry.
    }

    private void ClosePendingOutputScratch()
    {
        pendingOutputClose?.Dispose();
        pendingOutputClose = null;
    }

    internal void CloseOutputScratch()
    {
        lock (gate)
        {
            if (outputOperationActive)
                throw new InvalidOperationException("The sampling output operation is still active.");
            outputClosing = true;
            EndOutputUse();
            ClosePendingOutputScratch();
            outputScratch?.Dispose();
            outputScratch = null;
        }
    }
}
