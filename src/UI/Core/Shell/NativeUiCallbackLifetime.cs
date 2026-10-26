using System.Diagnostics;

namespace ResourceManager.NativeUi;

/// <summary>One block invocation owns its dispatched callbacks until they finish or are retired.</summary>
internal sealed class NativeUiCallbackLifetime : IAsyncDisposable
{
    private readonly object sync = new();
    private readonly Func<Action, Task> dispatch;
    private readonly Action<Exception> reportError;
    private readonly HashSet<Task> pending = [];
    private bool accepting = true;
    private Task? stopped;

    internal NativeUiCallbackLifetime(Func<Action, Task> dispatch, Action<Exception>? reportError = null)
    {
        this.dispatch = dispatch ?? throw new ArgumentNullException(nameof(dispatch));
        this.reportError = reportError ?? (error => Trace.WriteLine(error));
    }

    internal void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (sync)
        {
            if (!accepting) return;
            // Reserve ownership before dispatch can run synchronously or race StopAsync.
            pending.Add(completion.Task);
        }
        _ = DispatchAsync(action, completion);
    }

    private async Task DispatchAsync(Action action, TaskCompletionSource completion)
    {
        try
        {
            await dispatch(() =>
            {
                lock (sync)
                {
                    if (!accepting) return;
                }
                action();
            }).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            try { reportError(error); }
            catch (Exception reportingError) { Trace.WriteLine(reportingError); }
        }
        finally
        {
            lock (sync)
            {
                completion.TrySetResult();
                pending.Remove(completion.Task);
            }
        }
    }

    internal Task StopAsync()
    {
        lock (sync)
        {
            accepting = false;
            return stopped ??= Task.WhenAll(pending.ToArray());
        }
    }

    public ValueTask DisposeAsync() => new(StopAsync());
}
