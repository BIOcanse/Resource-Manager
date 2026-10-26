namespace ResourceManager.NativeUi;

internal sealed record NativeUiShutdownActions(
    Action MarkExiting,
    Func<Task> StopTrees,
    Action ExitMessageLoop)
{
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(MarkExiting);
        ArgumentNullException.ThrowIfNull(StopTrees);
        ArgumentNullException.ThrowIfNull(ExitMessageLoop);
    }
}

internal sealed class NativeUiShutdownCoordinator
{
    private readonly object sync = new();
    private Task? completion;

    public Task? Completion { get { lock (sync) return completion; } }

    public Task ShutdownAsync(NativeUiShutdownActions actions)
    {
        ArgumentNullException.ThrowIfNull(actions);
        actions.Validate();
        TaskCompletionSource finished;
        lock (sync)
        {
            if (completion is not null) return completion;
            finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
            completion = finished.Task;
        }

        // Publish completion before calling user code: hiding a window or cancellation can reenter.
        _ = FinishAsync(actions, finished);
        return finished.Task;
    }

    private static async Task FinishAsync(NativeUiShutdownActions actions, TaskCompletionSource finished)
    {
        try
        {
            actions.MarkExiting();
            await actions.StopTrees();
            actions.ExitMessageLoop();
            finished.TrySetResult();
        }
        catch (OperationCanceledException error)
        {
            finished.TrySetCanceled(error.CancellationToken);
        }
        catch (Exception error)
        {
            finished.TrySetException(error);
        }
    }
}
