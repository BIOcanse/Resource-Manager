namespace ResourceManager.NativeUi;

// Used only on the UI thread. An accepted close retires its whole window branch
// before a subsequent show may publish new window demand.
internal sealed class NativeUiWindowRequests
{
    private Task closing = Task.CompletedTask;

    internal Task CloseAsync(Func<Task> retireBranch)
    {
        if (!closing.IsCompleted) return closing;
        return closing = retireBranch();
    }

    internal Task WaitForCloseAsync() => closing;
}
