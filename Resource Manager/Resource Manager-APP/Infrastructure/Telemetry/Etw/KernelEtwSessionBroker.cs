using Microsoft.Diagnostics.Tracing.Parsers;

namespace ResourceManager.App.Infrastructure.Telemetry.Etw;

public sealed class KernelEtwSessionBroker : BackgroundService, IKernelEtwSessionBroker
{
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StopWait = TimeSpan.FromSeconds(2);
    private static readonly string SessionName = $"ResourceManagerKernelTelemetry-{Environment.ProcessId}";

    private readonly object reconciliationGate = new();
    private readonly object stateGate = new();
    private readonly Dictionary<long, KernelEtwSubscriptionRegistration> subscriptions = [];
    private readonly IKernelEtwSessionRuntimeFactory runtimeFactory;
    private readonly ILogger<KernelEtwSessionBroker> logger;
    private readonly TimeSpan reconciliationInterval;
    private IKernelEtwSessionRuntime? runtime;
    private long nextLeaseId;
    private long configurationVersion;
    private long appliedConfigurationVersion = -1;
    private long generationSequence;
    private long generation;
    private DateTimeOffset startedAt = DateTimeOffset.MinValue;
    private KernelTraceEventParser.Keywords enabledKeywords;
    private string state = "Idle";
    private string message = "共享 kernel ETW 会话等待订阅。";
    private string? lastLoggedFailure;
    private bool restartBlockedByUnreleasedRuntime;
    private int stopping;

    public KernelEtwSessionBroker(ILogger<KernelEtwSessionBroker> logger)
        : this(new TraceEventKernelEtwSessionRuntimeFactory(), logger)
    {
    }

    internal KernelEtwSessionBroker(
        IKernelEtwSessionRuntimeFactory runtimeFactory,
        ILogger<KernelEtwSessionBroker> logger)
        : this(runtimeFactory, logger, RetryInterval)
    {
    }

    internal KernelEtwSessionBroker(
        IKernelEtwSessionRuntimeFactory runtimeFactory,
        ILogger<KernelEtwSessionBroker> logger,
        TimeSpan reconciliationInterval)
    {
        if (reconciliationInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(reconciliationInterval));
        }

        this.runtimeFactory = runtimeFactory;
        this.logger = logger;
        this.reconciliationInterval = reconciliationInterval;
    }

    public IKernelEtwSubscription Subscribe(KernelEtwSubscriptionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Id);
        ArgumentNullException.ThrowIfNull(request.Bind);
        if (request.Keywords == KernelTraceEventParser.Keywords.None)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "kernel ETW 订阅必须声明至少一个 keyword。");
        }

        lock (reconciliationGate)
        {
            if (Volatile.Read(ref stopping) != 0)
            {
                throw new ObjectDisposedException(
                    nameof(KernelEtwSessionBroker),
                    "The shared kernel ETW broker has stopped.");
            }

            var leaseId = Interlocked.Increment(ref nextLeaseId);
            lock (stateGate)
            {
                if (Volatile.Read(ref stopping) != 0)
                {
                    throw new ObjectDisposedException(
                        nameof(KernelEtwSessionBroker),
                        "The shared kernel ETW broker has stopped.");
                }

                subscriptions.Add(leaseId, new KernelEtwSubscriptionRegistration(
                    leaseId,
                    request.Id.Trim(),
                    request.Keywords,
                    request.Bind));
                configurationVersion++;
            }

            ReconcileCore();
            return new SubscriptionLease(this, leaseId);
        }
    }

    public KernelEtwSessionSnapshot GetSnapshot()
    {
        lock (stateGate)
        {
            var eventsLost = runtime?.EventsLost ?? 0;
            return new KernelEtwSessionSnapshot(
                state,
                message,
                generation,
                startedAt,
                enabledKeywords,
                subscriptions.Count,
                eventsLost);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(reconciliationInterval, stoppingToken);
                lock (reconciliationGate)
                {
                    if (NeedsReconciliation())
                    {
                        ReconcileCore();
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            StopBroker();
        }
    }

    public override void Dispose()
    {
        StopBroker();
        base.Dispose();
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        StopBroker();
        return base.StopAsync(cancellationToken);
    }

    private bool NeedsReconciliation()
    {
        lock (stateGate)
        {
            if (restartBlockedByUnreleasedRuntime)
            {
                return false;
            }

            if (Volatile.Read(ref stopping) != 0)
            {
                return runtime is not null;
            }

            if (subscriptions.Count == 0)
            {
                return runtime is not null;
            }

            return runtime is null
                || !runtime.IsProcessing
                || runtime.EventsLost > 0
                || appliedConfigurationVersion != configurationVersion;
        }
    }

    private void ReconcileCore()
    {
        KernelEtwSubscriptionRegistration[] currentSubscriptions;
        long currentConfigurationVersion;
        IKernelEtwSessionRuntime? previousRuntime;
        lock (stateGate)
        {
            if (restartBlockedByUnreleasedRuntime)
            {
                return;
            }

            currentSubscriptions = subscriptions.Values
                .OrderBy(static subscription => subscription.LeaseId)
                .ToArray();
            currentConfigurationVersion = configurationVersion;
            if (Volatile.Read(ref stopping) == 0
                && currentSubscriptions.Length > 0
                && runtime is not null
                && runtime.IsProcessing
                && runtime.EventsLost == 0
                && appliedConfigurationVersion == currentConfigurationVersion)
            {
                return;
            }

            previousRuntime = runtime;
            runtime = null;
            appliedConfigurationVersion = -1;
            enabledKeywords = KernelTraceEventParser.Keywords.None;
            startedAt = DateTimeOffset.MinValue;
            state = currentSubscriptions.Length == 0 ? "Idle" : "Starting";
            message = currentSubscriptions.Length == 0
                ? "共享 kernel ETW 会话等待订阅。"
                : "共享 kernel ETW 会话正在合并订阅并启动。";
        }

        var previousStop = previousRuntime?.Stop(StopWait);
        if (previousStop is not null && !previousStop.IsReleased)
        {
            RecordStopFailure(previousRuntime!, previousStop, "restart");
            return;
        }

        if (Volatile.Read(ref stopping) != 0 || currentSubscriptions.Length == 0)
        {
            return;
        }

        if (!runtimeFactory.TryGetAvailability(out var availabilityState, out var availabilityMessage))
        {
            lock (stateGate)
            {
                state = availabilityState;
                message = availabilityMessage;
            }

            return;
        }

        var keywords = currentSubscriptions.Aggregate(
            KernelTraceEventParser.Keywords.None,
            static (value, subscription) => value | subscription.Keywords);
        var nextGeneration = Interlocked.Increment(ref generationSequence);
        var context = new KernelEtwSessionContext(nextGeneration, DateTimeOffset.UtcNow);
        IKernelEtwSessionRuntime? nextRuntime = null;
        try
        {
            nextRuntime = runtimeFactory.Create(
                SessionName,
                keywords,
                context,
                currentSubscriptions);
            lock (stateGate)
            {
                runtime = nextRuntime;
                appliedConfigurationVersion = currentConfigurationVersion;
                generation = nextGeneration;
                startedAt = context.StartedAt;
                enabledKeywords = keywords;
                state = "Running";
                message = $"共享 kernel ETW 会话正在服务 {currentSubscriptions.Length} 个订阅。";
                lastLoggedFailure = null;
            }

            nextRuntime.StartProcessing(failure => OnProcessingStopped(nextRuntime, failure));
        }
        catch (Exception ex)
        {
            lock (stateGate)
            {
                if (ReferenceEquals(runtime, nextRuntime))
                {
                    runtime = null;
                }

                appliedConfigurationVersion = -1;
                enabledKeywords = KernelTraceEventParser.Keywords.None;
                startedAt = DateTimeOffset.MinValue;
                state = "Unavailable";
                message = $"共享 kernel ETW 会话启动失败：{ex.Message}";
            }

            var failedStartStop = nextRuntime?.Stop(StopWait);
            if (failedStartStop is not null && !failedStartStop.IsReleased)
            {
                RecordStopFailure(nextRuntime!, failedStartStop, "failed-start cleanup");
            }
            else if (ex is KernelEtwSessionCreationCleanupException creationCleanupFailure)
            {
                RecordCreationCleanupFailure(creationCleanupFailure);
            }
            LogStartFailure(ex);
        }
    }

    private void OnProcessingStopped(IKernelEtwSessionRuntime stoppedRuntime, Exception? failure)
    {
        lock (stateGate)
        {
            if (!ReferenceEquals(runtime, stoppedRuntime)
                || restartBlockedByUnreleasedRuntime)
            {
                return;
            }

            state = "Unavailable";
            message = failure is null
                ? "共享 kernel ETW 事件处理意外停止，等待重新启动。"
                : $"共享 kernel ETW 事件处理失败：{failure.Message}";
        }

        if (failure is null)
        {
            logger.LogDebug("Shared kernel ETW processing stopped unexpectedly.");
        }
        else
        {
            logger.LogWarning(failure, "Shared kernel ETW processing failed.");
        }
    }

    private void Release(long leaseId)
    {
        lock (reconciliationGate)
        {
            if (Volatile.Read(ref stopping) != 0)
            {
                return;
            }

            lock (stateGate)
            {
                if (!subscriptions.Remove(leaseId))
                {
                    return;
                }

                configurationVersion++;
            }

            ReconcileCore();
        }
    }

    private void StopBroker()
    {
        if (Interlocked.Exchange(ref stopping, 1) != 0)
        {
            return;
        }

        lock (reconciliationGate)
        {
            IKernelEtwSessionRuntime? runtimeToStop;
            bool releaseAlreadyUnproven;
            string? existingReleaseFailure;
            lock (stateGate)
            {
                runtimeToStop = runtime;
                releaseAlreadyUnproven = restartBlockedByUnreleasedRuntime;
                existingReleaseFailure = releaseAlreadyUnproven ? message : null;
                runtime = null;
                subscriptions.Clear();
                configurationVersion++;
                enabledKeywords = KernelTraceEventParser.Keywords.None;
                startedAt = DateTimeOffset.MinValue;
                appliedConfigurationVersion = -1;
                state = "Stopping";
                message = "共享 kernel ETW 会话正在释放。";
            }

            var stopResult = runtimeToStop?.Stop(StopWait)
                ?? (releaseAlreadyUnproven
                    ? new KernelEtwSessionStopResult(
                        SessionStopSucceeded: false,
                        SessionDisposeSucceeded: false,
                        ProcessingCompleted: true,
                        Failures: [existingReleaseFailure ?? "A prior kernel ETW release remained unproven."])
                    : KernelEtwSessionStopResult.Released);
            lock (stateGate)
            {
                if (stopResult.IsReleased)
                {
                    state = "Stopped";
                    message = "共享 kernel ETW 会话已随程序停止。";
                }
                else
                {
                    state = "StopFailed";
                    message = CreateStopFailureMessage(stopResult, "shutdown");
                }
            }

            if (!stopResult.IsReleased)
            {
                logger.LogError(
                    "Shared kernel ETW shutdown did not prove release: {Failures}",
                    string.Join(" | ", stopResult.Failures));
            }
        }
    }

    private void RecordStopFailure(
        IKernelEtwSessionRuntime unreleasedRuntime,
        KernelEtwSessionStopResult result,
        string operation)
    {
        lock (stateGate)
        {
            runtime = unreleasedRuntime;
            restartBlockedByUnreleasedRuntime = true;
            appliedConfigurationVersion = -1;
            enabledKeywords = KernelTraceEventParser.Keywords.None;
            startedAt = DateTimeOffset.MinValue;
            state = "StopFailed";
            message = CreateStopFailureMessage(result, operation);
        }

        logger.LogError(
            "Shared kernel ETW {Operation} did not prove release: {Failures}",
            operation,
            string.Join(" | ", result.Failures));
    }

    private void RecordCreationCleanupFailure(
        KernelEtwSessionCreationCleanupException exception)
    {
        lock (stateGate)
        {
            runtime = null;
            restartBlockedByUnreleasedRuntime = true;
            appliedConfigurationVersion = -1;
            enabledKeywords = KernelTraceEventParser.Keywords.None;
            startedAt = DateTimeOffset.MinValue;
            state = "StopFailed";
            message = "共享 kernel ETW 会话创建失败，且部分创建的会话未能证明完整释放。";
        }

        logger.LogError(
            exception,
            "Shared kernel ETW creation cleanup did not prove release; restart is blocked.");
    }

    private static string CreateStopFailureMessage(
        KernelEtwSessionStopResult result,
        string operation)
        => $"共享 kernel ETW 会话在 {operation} 时未能证明完整释放：{string.Join(" | ", result.Failures)}";

    private void LogStartFailure(Exception exception)
    {
        var fingerprint = $"{exception.GetType().FullName}:{exception.Message}";
        var shouldWarn = false;
        lock (stateGate)
        {
            if (!string.Equals(lastLoggedFailure, fingerprint, StringComparison.Ordinal))
            {
                lastLoggedFailure = fingerprint;
                shouldWarn = true;
            }
        }

        if (shouldWarn)
        {
            logger.LogWarning(exception, "Failed to start shared kernel ETW session.");
        }
        else
        {
            logger.LogDebug(exception, "Shared kernel ETW session retry failed.");
        }
    }

    private sealed class SubscriptionLease(KernelEtwSessionBroker owner, long leaseId) : IKernelEtwSubscription
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                owner.Release(leaseId);
            }
        }
    }
}
