using System.Collections.Immutable;
using ResourceManager.ReadWrite.Native.DataArea;

namespace ResourceManager.ReadWrite.Windows.DataArea;

/// <summary>
/// Run-local admission/lifetime wrapper around the one native region owner.
/// The caller must await StopAsync in its run's finally, with an explicit time.
/// Native code owns publication, layout and both mappings; this wrapper retains
/// only its owner handle, never a region address, borrow or current-value copy.
/// </summary>
internal sealed class BackendDataAreaOwner
{
    private readonly object state = new();
    private IntPtr handle;
    private int inFlight;
    private bool stopping;
    private TaskCompletionSource? drained;
    private TaskCompletionSource? stopCompletion;

    internal BackendDataAreaOwner(DataAreaSettings settings)
        => handle = BackendDataAreaNative.Create(settings);

    internal void Apply(ImmutableArray<BackendDataAreaDeclaration> declarations, long nowUnixMilliseconds)
    {
        var current = Enter();
        try { BackendDataAreaNative.Apply(current, declarations, nowUnixMilliseconds); }
        finally { Exit(); }
    }

    // Bindings/views are non-owning descriptors. Acquire checks their native
    // generation at each callback entry, including after a plan replacement.
    internal DataAreaBinding Binding(uint declaration)
    {
        var current = Enter();
        try { return BackendDataAreaNative.Binding(current, declaration); }
        finally { Exit(); }
    }

    internal DataAreaStatus ReadView(ReadOnlySpan<uint> declarations, Span<uint> sources,
        out DataAreaReadView view)
    {
        var current = Enter();
        try { return BackendDataAreaNative.ReadView(current, declarations, sources, out view); }
        finally { Exit(); }
    }

    internal ValueTask WithRegionAsync(DataAreaBinding binding, BackendDataAreaCallback callback,
        CancellationToken cancellationToken)
        => WithRegionAsync(binding.Region, binding.LayoutGeneration, callback, cancellationToken);

    internal ValueTask WithRegionAsync(DataAreaReadView view, BackendDataAreaCallback callback,
        CancellationToken cancellationToken)
        => WithRegionAsync(view.Region, view.LayoutGeneration, callback, cancellationToken);

    private async ValueTask WithRegionAsync(DataAreaRegion region, ulong generation,
        BackendDataAreaCallback callback, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(callback);
        cancellationToken.ThrowIfCancellationRequested();
        var current = Enter();
        try
        {
            var borrow = BackendDataAreaNative.Acquire(current, region, generation);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                await BackendDataAreaNative.Invoke(callback, in borrow, cancellationToken).ConfigureAwait(false);
            }
            finally { BackendDataAreaNative.Release(current, borrow); }
        }
        finally { Exit(); }
    }

    /// <summary>
    /// Closes admission immediately. Every admitted native call and callback
    /// finishes before native Stop/Destroy. Cancellation of a callback still
    /// runs its release; stopping itself cannot abandon lifetime settlement.
    /// </summary>
    internal Task StopAsync(long nowUnixMilliseconds)
    {
        Task waitForCalls;
        TaskCompletionSource completion;
        IntPtr current;
        lock (state)
        {
            if (stopCompletion is not null) return stopCompletion.Task;
            if (handle == IntPtr.Zero) return Task.CompletedTask;
            stopping = true;
            current = handle;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            stopCompletion = completion;
            if (inFlight == 0) waitForCalls = Task.CompletedTask;
            else
            {
                drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
                waitForCalls = drained.Task;
            }
        }
        // Starts on the caller's execution flow. No worker, timer or task-run
        // thread; all native work and continuations are outside the state lock.
        _ = FinishStopAsync(current, waitForCalls, nowUnixMilliseconds, completion);
        return completion.Task;
    }

    private async Task FinishStopAsync(IntPtr current, Task waitForCalls, long nowUnixMilliseconds,
        TaskCompletionSource completion)
    {
        try
        {
            await waitForCalls.ConfigureAwait(false);
            BackendDataAreaNative.Check("stop", DataAreaAbi.Stop(current, nowUnixMilliseconds));
            BackendDataAreaNative.Check("destroy", DataAreaAbi.Destroy(current));
            lock (state) handle = IntPtr.Zero;
            completion.SetResult();
        }
        catch (Exception error)
        {
            // Native destroy refusal leaves its handle valid. Keep admission
            // closed but let an explicit later StopAsync retry cleanup.
            lock (state) stopCompletion = null;
            completion.SetException(error);
        }
    }

    private IntPtr Enter()
    {
        lock (state)
        {
            if (stopping) throw new BackendDataAreaException("admission", DataAreaStatus.Stopped);
            inFlight = checked(inFlight + 1);
            return handle;
        }
    }

    private void Exit()
    {
        TaskCompletionSource? notify = null;
        lock (state)
        {
            inFlight--;
            if (inFlight == 0 && stopping) notify = drained;
        }
        notify?.TrySetResult();
    }
}
