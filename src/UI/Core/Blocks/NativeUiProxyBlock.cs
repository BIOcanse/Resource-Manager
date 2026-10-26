using ResourceManager.NativeUi.WebView;
using ResourceManager.Shared.BusinessBlocks;

namespace ResourceManager.NativeUi.Blocks;

// A failed start is still this run's serial work. Only the existing explicit
// native retry action permits another attempt; no scheduler recovery policy.
internal sealed class NativeUiProxyBlock(
    FrontendLoopbackProxy proxy,
    Action<Exception>? recordFailure = null) : IBusinessBlock
{
    private readonly object sync = new();
    private TaskCompletionSource retry = NewSignal();
    private bool stopped;

    internal bool RequestRetry()
    {
        lock (sync)
        {
            if (stopped || !proxy.ResetFailedStart()) return false;
            retry.TrySetResult();
            return true;
        }
    }

    public async Task RunAsync(BusinessBlockRunContext context)
    {
        Task? starting = null;
        try
        {
            while (!context.StopNotification.IsCancellationRequested)
            {
                Task requestedRetry;
                lock (sync) requestedRetry = retry.Task;
                try
                {
                    starting = proxy.StartAsync();
                    await starting.WaitAsync(context.StopNotification);
                }
                catch (OperationCanceledException) when (context.StopNotification.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception failure)
                {
                    if (recordFailure is not null) recordFailure(failure);
                    else System.Diagnostics.Trace.WriteLine(failure);
                    await requestedRetry.WaitAsync(context.StopNotification);
                    lock (sync) retry = NewSignal();
                    continue;
                }
                context.ReportRunning();
                await Task.Delay(Timeout.InfiniteTimeSpan, context.StopNotification);
            }
        }
        catch (OperationCanceledException) when (context.StopNotification.IsCancellationRequested) { }
        finally
        {
            lock (sync) stopped = true;
            try { await proxy.DisposeAsync(); }
            finally
            {
                // Dispose joins the underlying listener startup; also join its public
                // caller continuation when cancellation interrupted our local wait.
                if (starting is not null)
                {
                    try { await starting; }
                    catch (Exception) { } // Already published as endpoint failure, or canceled by disposal.
                }
            }
        }
    }

    private static TaskCompletionSource NewSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
