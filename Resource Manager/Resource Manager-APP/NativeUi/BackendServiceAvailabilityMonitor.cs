using System.Diagnostics;

namespace ResourceManager.NativeUi;

internal enum BackendServiceUnavailableReason
{
    OwnedProcessExited,
    ConsecutiveProbeFailures,
    StartupFailed,
    MonitorFault
}

internal sealed class BackendServiceUnavailableEventArgs : EventArgs
{
    private BackendServiceUnavailableEventArgs(
        BackendServiceUnavailableReason reason,
        string message,
        int? exitCode)
    {
        Reason = reason;
        Message = message;
        ExitCode = exitCode;
    }

    public BackendServiceUnavailableReason Reason { get; }

    public string Message { get; }

    public int? ExitCode { get; }

    public static BackendServiceUnavailableEventArgs OwnedProcessExited(int exitCode) =>
        new(
            BackendServiceUnavailableReason.OwnedProcessExited,
            $"本地服务进程已退出，退出码 {exitCode}。",
            exitCode);

    public static BackendServiceUnavailableEventArgs ConsecutiveProbeFailures(
        int failureCount) =>
        new(
            BackendServiceUnavailableReason.ConsecutiveProbeFailures,
            $"本地服务连续 {failureCount} 次健康探测失败。",
            exitCode: null);

    public static BackendServiceUnavailableEventArgs StartupFailed(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return new BackendServiceUnavailableEventArgs(
            BackendServiceUnavailableReason.StartupFailed,
            $"本地服务启动失败：{exception.Message}",
            exitCode: null);
    }

    public static BackendServiceUnavailableEventArgs MonitorFault(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return new BackendServiceUnavailableEventArgs(
            BackendServiceUnavailableReason.MonitorFault,
            $"本地服务生命周期监视失败：{exception.Message}",
            exitCode: null);
    }
}

internal sealed record BackendServiceAvailabilityMonitorOptions(
    TimeSpan ProbeInterval,
    int ConsecutiveProbeFailureLimit)
{
    public static BackendServiceAvailabilityMonitorOptions Default { get; } = new(
        TimeSpan.FromSeconds(2),
        ConsecutiveProbeFailureLimit: 3);

    public void Validate()
    {
        if (ProbeInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ProbeInterval),
                ProbeInterval,
                "Probe interval must be positive.");
        }

        if (ConsecutiveProbeFailureLimit <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ConsecutiveProbeFailureLimit),
                ConsecutiveProbeFailureLimit,
                "Consecutive probe failure limit must be positive.");
        }
    }
}

internal sealed class BackendServiceAvailabilityTerminalGate
{
    private const int Active = 0;
    private const int UnavailablePublished = 1;
    private const int Stopped = 2;

    private int state;

    public bool TryPublishUnavailable() =>
        Interlocked.CompareExchange(ref state, UnavailablePublished, Active) == Active;

    public bool TryStop() =>
        Interlocked.CompareExchange(ref state, Stopped, Active) == Active;
}

internal sealed class BackendServiceAvailabilityMonitor : IDisposable
{
    private readonly object sync = new();
    private readonly Func<CancellationToken, Task<bool>> probeAsync;
    private readonly Func<CancellationToken, Task<BackendServiceUnavailableEventArgs>>?
        waitForOwnedProcessExitAsync;
    private readonly Func<TimeSpan, CancellationToken, Task> delayAsync;
    private readonly BackendServiceAvailabilityMonitorOptions options;
    private readonly BackendServiceAvailabilityTerminalGate terminalGate = new();
    private readonly CancellationTokenSource shutdown = new();
    private Task? completion;
    private bool disposed;

    public BackendServiceAvailabilityMonitor(
        Func<CancellationToken, Task<bool>> probeAsync,
        Func<CancellationToken, Task<BackendServiceUnavailableEventArgs>>?
            waitForOwnedProcessExitAsync,
        BackendServiceAvailabilityMonitorOptions? options = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        this.probeAsync = probeAsync ?? throw new ArgumentNullException(nameof(probeAsync));
        this.waitForOwnedProcessExitAsync = waitForOwnedProcessExitAsync;
        this.options = options ?? BackendServiceAvailabilityMonitorOptions.Default;
        this.options.Validate();
        this.delayAsync = delayAsync ?? Task.Delay;
    }

    public event EventHandler<BackendServiceUnavailableEventArgs>? AvailabilityLost;

    internal Task Completion
    {
        get
        {
            lock (sync)
            {
                return completion ?? Task.CompletedTask;
            }
        }
    }

    public void Start()
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            completion ??= RunAsync(shutdown.Token);
        }
    }

    public void Dispose()
    {
        Task? completionToObserve;
        lock (sync)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            completionToObserve = completion;
        }

        terminalGate.TryStop();
        shutdown.Cancel();
        if (completionToObserve is null || completionToObserve.IsCompleted)
        {
            shutdown.Dispose();
            return;
        }

        _ = completionToObserve.ContinueWith(
            static (_, state) => ((CancellationTokenSource)state!).Dispose(),
            shutdown,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var raceCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var healthTask = WaitForHealthFailureAsync(raceCancellation.Token);
            var processTask = waitForOwnedProcessExitAsync?.Invoke(raceCancellation.Token);

            BackendServiceUnavailableEventArgs unavailable;
            if (processTask is null)
            {
                unavailable = await healthTask.ConfigureAwait(false);
            }
            else
            {
                var winner = await Task.WhenAny(healthTask, processTask).ConfigureAwait(false);
                unavailable = await winner.ConfigureAwait(false);
                raceCancellation.Cancel();
                ObserveCanceledRaceTask(ReferenceEquals(winner, healthTask) ? processTask : healthTask);
            }

            cancellationToken.ThrowIfCancellationRequested();
            PublishAvailabilityLost(unavailable);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                PublishAvailabilityLost(BackendServiceUnavailableEventArgs.MonitorFault(ex));
            }
        }
    }

    private async Task<BackendServiceUnavailableEventArgs> WaitForHealthFailureAsync(
        CancellationToken cancellationToken)
    {
        var consecutiveFailures = 0;
        while (true)
        {
            await delayAsync(options.ProbeInterval, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            var healthy = await probeAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (healthy)
            {
                consecutiveFailures = 0;
                continue;
            }

            consecutiveFailures++;
            if (consecutiveFailures >= options.ConsecutiveProbeFailureLimit)
            {
                return BackendServiceUnavailableEventArgs.ConsecutiveProbeFailures(
                    consecutiveFailures);
            }
        }
    }

    private void PublishAvailabilityLost(BackendServiceUnavailableEventArgs unavailable)
    {
        if (!terminalGate.TryPublishUnavailable())
        {
            return;
        }

        var handlers = AvailabilityLost;
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler<BackendServiceUnavailableEventArgs> handler
                 in handlers.GetInvocationList())
        {
            try
            {
                handler(this, unavailable);
            }
            catch (Exception ex)
            {
                Trace.WriteLine(ex);
            }
        }
    }

    private static void ObserveCanceledRaceTask(Task task)
    {
        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
