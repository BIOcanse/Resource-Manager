namespace ResourceManager.ReadWrite.Windows.Processes.Etw;

public static class DedicatedEtwConsumerThread
{
    public static Task Start(
        string blockId,
        string sessionName,
        Action process,
        IEtwConsumerThreadObserver? observer = null)
        => Start(blockId, sessionName, process, out _, observer);

    public static Task Start(
        string blockId,
        string sessionName,
        Action process,
        out Thread consumerThread,
        IEtwConsumerThreadObserver? observer = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(blockId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionName);
        ArgumentNullException.ThrowIfNull(process);

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                using (observer?.RegisterCurrentThread(blockId, sessionName))
                {
                    process();
                }

                completion.TrySetResult();
            }
            catch (Exception error)
            {
                try
                {
                    observer?.ReportFailure(blockId, error);
                }
                catch (Exception reportingError)
                {
                    completion.TrySetException(new AggregateException(error, reportingError));
                    return;
                }

                completion.TrySetException(error);
            }
        })
        {
            IsBackground = true,
            Name = $"{blockId} / {sessionName}"
        };

        consumerThread = thread;
        thread.Start();
        return completion.Task;
    }
}
