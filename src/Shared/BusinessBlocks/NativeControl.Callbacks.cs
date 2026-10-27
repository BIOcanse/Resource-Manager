using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;

namespace ResourceManager.Shared.BusinessBlocks;

public sealed partial class NativeControl
{
    private sealed record Run(NativeControl Owner, int Block, ulong Number, CancellationTokenSource Stop)
    {
        internal TaskCompletionSource StopDelivery { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int StopDeliveryQueued;
    }
    private async Task ExecuteAsync(Run run)
    {
        try
        {
            await create!(run.Block).RunAsync(new(run.Stop.Token,
                () => Check(RunRunning(handle, (uint)run.Block, run.Number)))).ConfigureAwait(false);
        }
        catch (Exception error) { SafeLog(LogLevel.Debug, error, "Business block run returned exceptionally; diagnostics belong to its owner."); }
        finally
        {
            try { Check(RunReturned(handle, (uint)run.Block, run.Number)); }
            catch (Exception error) { SafeLog(LogLevel.Error, error, "Failed to report a returned run."); }
            runs.TryRemove((run.Block, run.Number), out _);
            run.Stop.Dispose();
        }
    }
    private static NativeControl Owner(nint value) => (NativeControl)GCHandle.FromIntPtr(value).Target!;
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnStart(nint context, uint block, ulong number)
    {
        NativeControl? owner = null;
        Run? run = null;
        try
        {
            owner = Owner(context);
            run = new(owner, checked((int)block), number, new());
            if (!owner.runs.TryAdd((run.Block, number), run)) throw new InvalidOperationException("Duplicate native run callback.");
            if (!ThreadPool.UnsafeQueueUserWorkItem(static (Run value) => { _ = value.Owner.ExecuteAsync(value); }, run, false))
                throw new InvalidOperationException("The run entry could not be queued.");
        }
        catch (Exception error)
        {
            owner?.SafeLog(LogLevel.Error, error, "The native start callback could not queue the run.");
            if (owner is not null)
            {
                if (run is not null) { owner.runs.TryRemove((run.Block, number), out _); run.Stop.Dispose(); }
                try { Check(RunReturned(owner.handle, block, number)); }
                catch (Exception failure) { owner.SafeLog(LogLevel.Error, failure, "Failed to return an unqueued run."); }
            }
        }
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnStop(nint context, uint block, ulong number)
    {
        NativeControl? owner = null;
        Run? run = null;
        try
        {
            owner = Owner(context);
            if (!owner.runs.TryGetValue((checked((int)block), number), out run)) return;
            if (Interlocked.Exchange(ref run.StopDeliveryQueued, 1) != 0) return;
            if (!ThreadPool.UnsafeQueueUserWorkItem(static (Run value) =>
            {
                try { _ = CancelAsync(value); }
                catch (Exception error) { value.Owner.SafeLog(LogLevel.Error, error, "Stop notification failed."); }
            }, run, false))
            {
                var error = new InvalidOperationException("The stop notification could not be queued.");
                run.StopDelivery.TrySetException(error);
                throw error;
            }
        }
        catch (Exception error)
        {
            run?.StopDelivery.TrySetException(error);
            owner?.SafeLog(LogLevel.Error, error, "The native stop callback failed.");
        }
    }
    private static async Task CancelAsync(Run run)
    {
        Task callbackDrain;
        try { callbackDrain = run.Stop.CancelAsync(); }
        catch (ObjectDisposedException)
        {
            // The run already returned and released its source; no token owner remains to notify.
            run.StopDelivery.TrySetResult();
            return;
        }
        catch (Exception error)
        {
            run.StopDelivery.TrySetException(error);
            run.Owner.SafeLog(LogLevel.Error, error, "A block stop request could not be delivered.");
            return;
        }

        // CancelAsync sets IsCancellationRequested synchronously. Round completion waits only for
        // this delivery boundary, not for registered callbacks or the run to return.
        run.StopDelivery.TrySetResult();
        try { await callbackDrain.ConfigureAwait(false); }
        catch (Exception error) { run.Owner.SafeLog(LogLevel.Error, error, "A block stop handler failed."); }
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnComplete(nint context, ulong token)
    {
        NativeControl? owner = null;
        TaskCompletionSource? completion = null;
        try
        {
            owner = Owner(context);
            if (!owner.completions.TryRemove(token, out completion)) return;
            var stopDeliveries = owner.runs.Values
                .Where(run => Volatile.Read(ref run.StopDeliveryQueued) != 0)
                .Select(run => run.StopDelivery.Task)
                .ToArray();
            _ = CompleteRoundAsync(owner, completion, stopDeliveries);
        }
        catch (Exception error)
        {
            completion?.TrySetException(error);
            owner?.SafeLog(LogLevel.Error, error, "The native round completion callback failed.");
        }
    }
    private static async Task CompleteRoundAsync(NativeControl owner, TaskCompletionSource completion, Task[] stopDeliveries)
    {
        try
        {
            await Task.WhenAll(stopDeliveries).ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (Exception error)
        {
            completion.TrySetException(error);
            owner.SafeLog(LogLevel.Error, error, "A native round completed before its stop request was delivered.");
        }
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnLog(nint context, uint level, byte* text, nuint length)
    {
        try { Owner(context).logger.Log((LogLevel)level, "{ControlMessage}", Encoding.UTF8.GetString(text, checked((int)length))); }
        catch (Exception) { /* Host logging cannot fault a controller worker. */ }
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnItemChanged(nint context, ulong id, ItemKey key, ReasonView* reason)
    {
        NativeControl? owner = null;
        try
        {
            owner = Owner(context);
            Func<BusinessBlockReason, Task>? reader;
            lock (owner.readersSync) owner.readers.TryGetValue(id, out reader);
            if (reader is null) return;
            Task task;
            try
            {
                task = reader(new((BusinessBlockRequestKind)reason->Kind,
                    Encoding.UTF8.GetString(reason->Owner, checked((int)reason->OwnerLength)),
                    Encoding.UTF8.GetString(reason->Cause, checked((int)reason->CauseLength)), unchecked((long)reason->PlanVersion)))
                    ?? Task.FromException(new InvalidOperationException("A state reader returned no Task."));
            }
            catch (Exception error) { task = Task.FromException(error); }
            if (publication is { } current && ReferenceEquals(current.Owner, owner)) current.Add(task);
            else owner.SafeLog(LogLevel.Error, null, "A runtime-reader event has no publishing host call.");
        }
        catch (Exception error)
        {
            if (publication is { } current && ReferenceEquals(current.Owner, owner)) current.Add(Task.FromException(error));
            owner?.SafeLog(LogLevel.Error, error, "The runtime-reader callback failed.");
        }
    }
    private void SafeLog(LogLevel level, Exception? error, string message)
    {
        try { logger.Log(level, error, "{ControlMessage}", message); } catch (Exception) { }
    }
    /// <summary>
    /// After the host has quiesced its callers and awaited their publication/reader tasks,
    /// stop and drain owned run entries, then destroy and join the native controller.
    /// No caller may publish, read, or invoke this handle concurrently with destruction.
    /// </summary>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    /// <summary>Asynchronous form of the same quiescent-host disposal contract.</summary>
    public ValueTask DisposeAsync()
    {
        lock (disposeSync)
        {
            if (disposeTask is not null) return new(disposeTask);
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            disposeTask = completion.Task;
            _ = DisposeCoreAsync(completion);
            return new(disposeTask);
        }
    }

    private async Task DisposeCoreAsync(TaskCompletionSource completion)
    {
        try
        {
            if (Volatile.Read(ref disposed) == 0)
            {
                // The service provider may already be disposing. Teardown has no active plan lookup.
                await RequestSchedulerShutdownAsync().ConfigureAwait(false);
                while (Graph.BlockIds.Any(id => ReadState(id) != BusinessBlockState.Stopped) || !runs.IsEmpty)
                    await Task.Delay(1).ConfigureAwait(false);
                if (Interlocked.Exchange(ref disposed, 1) == 0)
                {
                    // Every host run has returned; destroy joins the controller workers.
                    Check(Destroy(handle.DangerousGetHandle()));
                    handle.SetHandleAsInvalid();
                    handle.Dispose();
                    context.Free();
                }
            }
            completion.TrySetResult();
        }
        catch (Exception error) { completion.TrySetException(error); }
    }
}
