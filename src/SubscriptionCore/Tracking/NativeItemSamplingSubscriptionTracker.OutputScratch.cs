using ResourceManager.Subscription.Native;

namespace ResourceManager.Subscription;

public partial class NativeItemSamplingSubscriptionTracker<TRequest, TContext>
{
    private readonly Func<ulong, uint, INativeSamplingOutputScratch>? createOutputScratch;
    private INativeSamplingOutputScratch? outputScratch;
    private INativeSamplingOutputScratch? pendingOutputClose;
    private NativeSamplingOutputScratchLease? outputUse;
    private NativeSamplingSubscriptionCapacity outputRows;
    internal Action<string>? ResizeLog { get; set; }
    private bool outputOperationActive;
    private bool outputClosing;

    internal bool HasOutstandingOutputResources
    {
        get
        {
            lock (gate) return outputScratch is not null || pendingOutputClose is not null || outputUse is not null;
        }
    }

    // Internal observation only. The host exposes this solely in its named development build.
    internal (int SourceRegistrations, bool PendingSessionLease, ulong OutputScratchBytes,
        ulong PendingOutputCloseBytes, bool OutputUseActive, int ManagedOutputRows) CaptureResourceDiagnostics()
    {
        lock (gate)
        {
            return (sourcePayloads.Count, pendingSessionLease is not null, outputScratch?.Length ?? 0,
                pendingOutputClose?.Length ?? 0, outputUse is not null,
                checked(dueBuffer.Length + sourceBuffer.Length + expiredBuffer.Length + triggerBuffer.Length));
        }
    }

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
        // These maps contain the real tracked rows, including pending item
        // identities. The native session remains the owner of trigger state.
        var rows = capacity;
        uint memberships = 0;
        foreach (var payload in sourcePayloads.Values)
            memberships = checked(memberships + (coalesceItemsIntoLatestCapture ? 1U : checked((uint)payload.ItemIds.Count)));
        rows.DueItemCapacity = Math.Max(outputRows.DueItemCapacity, Math.Max(1U, checked((uint)itemIds.Count)));
        rows.SourceViewCapacity = Math.Max(outputRows.SourceViewCapacity, Math.Max(1U, checked((uint)sourcePayloads.Count)));
        rows.ExpiredSourceCapacity = Math.Max(outputRows.ExpiredSourceCapacity, Math.Max(1U, checked((uint)sourcePayloads.Count)));
        rows.TriggerCapacity = Math.Max(outputRows.TriggerCapacity, Math.Max(1U, memberships));
        // A replacement session may lower its configured admission limits.
        rows.DueItemCapacity = Math.Min(rows.DueItemCapacity, capacity.DueItemCapacity);
        rows.SourceViewCapacity = Math.Min(rows.SourceViewCapacity, capacity.SourceViewCapacity);
        rows.ExpiredSourceCapacity = Math.Min(rows.ExpiredSourceCapacity, capacity.ExpiredSourceCapacity);
        rows.TriggerCapacity = Math.Min(rows.TriggerCapacity, capacity.TriggerCapacity);
        var layout = new NativeSamplingOutputLayout(in rows);
        INativeSamplingOutputScratch? candidate = null;
        try
        {
            var resource = outputScratch;
            if (resource is null || resource.Length < layout.StorageLength)
            {
                candidate = createOutputScratch(layout.Length, NativeSamplingOutputLayout.Alignment)
                    ?? throw new InvalidOperationException("The sampling output factory returned no resource.");
                if (candidate.Length != layout.StorageLength)
                    throw new InvalidOperationException("The sampling output factory returned a different layout length.");
                // Every live source can expire in one plan, so sources are the live expired rows.
                candidate.Describe($"sampling-output-role{roleId}",
                    $"due={rows.DueItemCapacity}/live={itemIds.Count}, " +
                    $"sources={rows.SourceViewCapacity}/live={sourcePayloads.Count}, " +
                    $"expired={rows.ExpiredSourceCapacity}/live={sourcePayloads.Count}, " +
                    $"triggers={rows.TriggerCapacity}/live={memberships}");
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
                var message = $"Sampling output workspace resize role={roleId} old_rows={outputRows.DueItemCapacity}/{outputRows.SourceViewCapacity}/{outputRows.ExpiredSourceCapacity}/{outputRows.TriggerCapacity} new_rows={rows.DueItemCapacity}/{rows.SourceViewCapacity}/{rows.ExpiredSourceCapacity}/{rows.TriggerCapacity} old_bytes={pendingOutputClose?.Length ?? 0} new_bytes={resource.Length} reason=tracked-demand";
                if (ResizeLog is { } report) report(message);
                else System.Diagnostics.Trace.TraceInformation(message);
            }
            outputRows = rows;
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
