namespace ResourceManager.ReadWrite.Windows.Processes.Etw;

public static class DedicatedEtwConsumerThread
{
    public static Task Start(
        int? blockId,
        string sessionName,
        Action process,
        IEtwConsumerThreadObserver? observer = null)
        => Start(blockId, sessionName, process, out _, observer);

    public static Task Start(
        int? blockId,
        string sessionName,
        Action process,
        out Thread consumerThread,
        IEtwConsumerThreadObserver? observer = null)
    {
        ValidateBlockId(blockId, observer);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionName);
        ArgumentNullException.ThrowIfNull(process);

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                using (observer?.RegisterCurrentThread(blockId!.Value, sessionName))
                {
                    process();
                }

                completion.TrySetResult();
            }
            catch (Exception error)
            {
                try
                {
                    observer?.ReportFailure(blockId!.Value, error);
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
            Name = blockId is { } uid
                ? uid.ToString(System.Globalization.CultureInfo.InvariantCulture) + " / " + sessionName
                : sessionName
        };

        consumerThread = thread;
        thread.Start();
        return completion.Task;
    }

    internal static int? ValidateBlockId(int? blockId, IEtwConsumerThreadObserver? observer)
    {
        if (blockId is <= 0) throw new ArgumentOutOfRangeException(nameof(blockId));
        if (observer is not null && blockId is null)
            throw new ArgumentException("A block UID is required when a consumer observer is supplied.", nameof(blockId));
        return blockId;
    }
}
