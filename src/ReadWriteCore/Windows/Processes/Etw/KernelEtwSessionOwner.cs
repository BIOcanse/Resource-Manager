using Microsoft.Diagnostics.Tracing.Parsers;

namespace ResourceManager.App.Infrastructure.Telemetry.Etw;

public sealed class KernelEtwSessionOwner : IKernelEtwSessionBroker, IDisposable
{
    private static readonly TimeSpan StopWait = TimeSpan.FromSeconds(2);
    private static readonly string SessionName = ResourceManagerEtwSessionNames.KernelSessionName;

    private readonly object reconciliationGate = new();
    private readonly object stateGate = new();
    private readonly Dictionary<long, KernelEtwSubscriptionRegistration> subscriptions = [];
    private readonly IKernelEtwSessionRuntimeFactory runtimeFactory;
    private readonly KernelEtwDiagnostics logger;
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
    private bool disposed;
    private int stopping;

    public KernelEtwSessionOwner(IKernelEtwSessionRuntimeFactory runtimeFactory, KernelEtwDiagnostics? diagnostics = null)
    {
        this.runtimeFactory = runtimeFactory ?? throw new ArgumentNullException(nameof(runtimeFactory));
        logger = diagnostics ?? new();
    }

    /// <summary>Explicit retry; caller owns scheduling. Subscribe/release still reconcile synchronously.</summary>
    public void Reconcile()
    {
        lock (reconciliationGate)
        {
            if (NeedsReconciliation()) ReconcileCore();
        }
    }

    /// <summary>
    /// Registers a managed reader. Before the first host run, SDK consumers retain
    /// eager reconciliation; after StopRun, changes wait for explicit ResumeRun.
    /// </summary>
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
            ObjectDisposedException.ThrowIf(disposed, this);

            var leaseId = Interlocked.Increment(ref nextLeaseId);
            lock (stateGate)
            {
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

    private KernelEtwSessionSnapshot GetSnapshot(long leaseId)
    {
        lock (stateGate)
        {
            var snapshot = GetSnapshot();
            if (!subscriptions.TryGetValue(leaseId, out var subscription))
                return snapshot with { State = "Idle", Message = "共享 kernel ETW 订阅已释放。" };
            return snapshot.IsRunning
                && (subscription.Keywords & ~(enabledKeywords | KernelTraceEventParser.Keywords.Process
                    | KernelTraceEventParser.Keywords.Thread)) != 0
                ? snapshot with { State = "Unavailable" }
                : snapshot;
        }
    }

    /// <summary>Drains this run's native resources while retaining managed subscriptions.</summary>
    public void StopRun() => StopBroker(dispose: false);

    /// <summary>Resumes retained subscriptions after the preceding run has drained.</summary>
    public void ResumeRun()
    {
        lock (reconciliationGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (Interlocked.Exchange(ref stopping, 0) == 0) return;
            ReconcileCore();
        }
    }

    /// <summary>Drains native resources and permanently releases all managed subscriptions.</summary>
    public void Dispose() => StopBroker(dispose: true);

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
                || appliedConfigurationVersion != configurationVersion;
        }
    }

    private void ReconcileCore()
    {
        KernelEtwSubscriptionRegistration[] currentSubscriptions;
        long currentConfigurationVersion;
        IKernelEtwSessionRuntime? previousRuntime;
        long currentGeneration;
        DateTimeOffset currentStartedAt;
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
                && appliedConfigurationVersion == currentConfigurationVersion)
            {
                return;
            }

            previousRuntime = runtime;
            currentGeneration = generation;
            currentStartedAt = startedAt;
        }

        Exception? failedUpdate = null;
        var keywords = currentSubscriptions.Aggregate(
            KernelTraceEventParser.Keywords.None,
            static (value, subscription) => value | subscription.Keywords);
        if (Volatile.Read(ref stopping) == 0
            && currentSubscriptions.Length > 0
            && previousRuntime is { IsProcessing: true })
        {
            try
            {
                previousRuntime.UpdateSubscriptions(currentSubscriptions,
                    new KernelEtwSessionContext(currentGeneration, currentStartedAt));
                if (previousRuntime.IsProcessing)
                {
                    lock (stateGate)
                    {
                        appliedConfigurationVersion = currentConfigurationVersion;
                        enabledKeywords = keywords;
                        state = "Running";
                        message = $"共享 kernel ETW 会话正在服务 {currentSubscriptions.Length} 个订阅。";
                        lastLoggedFailure = null;
                    }
                    return;
                }
            }
            catch (Exception updateFailure) when (KernelEtwSessionKeywordControl.IsAccessDenied(updateFailure)
                && previousRuntime.IsProcessing)
            {
                lock (stateGate)
                {
                    state = "Running";
                    message = $"共享 kernel ETW 会话继续使用已启用的 keywords；订阅更新被拒绝：{updateFailure.Message}";
                }
                if (ShouldLogFailure($"update:{updateFailure.GetType().FullName}:{updateFailure.Message}"))
                    logger.LogWarning(updateFailure, "Shared kernel ETW keyword update was denied; retaining the running session and retrying reconciliation.");
                else
                    logger.LogDebug(updateFailure, "Shared kernel ETW keyword update was denied again; retaining the running session.");
                return;
            }
            catch (Exception updateFailure)
            {
                // A partially applied update must go through the same proven-release
                // path as a processing failure, rather than retrying an uncertain runtime.
                failedUpdate = updateFailure;
            }
        }

        // Do not drop ownership or enabled flags until stop has actually released them.
        if (currentSubscriptions.Length == 0 && previousRuntime is { IsProcessing: true })
        {
            previousRuntime.UpdateSubscriptions(currentSubscriptions,
                new KernelEtwSessionContext(currentGeneration, currentStartedAt));
        }
        var previousStop = previousRuntime?.Stop(StopWait);
        if (previousStop is not null && !previousStop.IsReleased)
        {
            if (failedUpdate is not null)
                logger.LogDebug(failedUpdate, "Shared kernel ETW subscription update failed; runtime release was not proven.");
            RecordStopFailure(previousRuntime!, previousStop,
                Volatile.Read(ref stopping) != 0 ? "shutdown"
                    : currentSubscriptions.Length == 0 ? "last subscription release" : "restart");
            return;
        }

        if (failedUpdate is not null)
            logger.LogWarning(failedUpdate, "Shared kernel ETW subscription update failed; released the runtime.");

        lock (stateGate)
        {
            runtime = null;
            appliedConfigurationVersion = -1;
            enabledKeywords = KernelTraceEventParser.Keywords.None;
            startedAt = DateTimeOffset.MinValue;
            state = Volatile.Read(ref stopping) != 0 ? "Stopped" : currentSubscriptions.Length == 0 ? "Idle" : "Starting";
            message = Volatile.Read(ref stopping) != 0 ? "共享 kernel ETW 会话已随程序停止。" : currentSubscriptions.Length == 0
                ? "共享 kernel ETW 会话等待订阅。"
                : "共享 kernel ETW 会话正在合并订阅并启动。";
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
                || restartBlockedByUnreleasedRuntime
                || Volatile.Read(ref stopping) != 0)
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

    private void StopBroker(bool dispose)
    {
        lock (reconciliationGate)
        {
            if (disposed) return;
            if (dispose)
            {
                disposed = true;
                lock (stateGate)
                {
                    subscriptions.Clear();
                    configurationVersion++;
                }
            }
            // A concurrent caller waits on the same gate until the actual consumer
            // disposal has completed instead of returning on the stopping flag.
            if (Interlocked.Exchange(ref stopping, 1) != 0) return;
            IKernelEtwSessionRuntime? stoppedRuntime;
            bool releaseFailed;
            lock (stateGate)
            {
                stoppedRuntime = runtime;
                releaseFailed = restartBlockedByUnreleasedRuntime;
                state = "Stopping";
            }

            try
            {
                if (stoppedRuntime is not null && !releaseFailed)
                {
                    var result = stoppedRuntime.Stop(StopWait);
                    if (!result.IsReleased)
                    {
                        releaseFailed = true;
                        RecordStopFailure(stoppedRuntime, result, "shutdown");
                    }
                }
            }
            catch (Exception ex)
            {
                releaseFailed = true;
                lock (stateGate) message = $"共享 kernel ETW 会话停止失败：{ex.Message}";
                logger.LogError(ex, "Shared kernel ETW shutdown failed before local consumer disposal.");
            }

            try
            {
                // Stop has a bounded reconciliation budget; terminal Dispose owns
                // the local handles and waits for the task and consumer thread.
                stoppedRuntime?.Dispose();
            }
            catch (Exception ex)
            {
                releaseFailed = true;
                lock (stateGate) message = $"{message} 本地消费者释放失败：{ex.Message}";
                logger.LogError(ex, "Shared kernel ETW local consumer disposal failed.");
                throw;
            }
            finally
            {
                lock (stateGate)
                {
                    runtime = null;
                    // Local consumer disposal does not prove native session release.
                    // A later run must retain the existing refusal to replace it.
                    restartBlockedByUnreleasedRuntime |= releaseFailed;
                    appliedConfigurationVersion = -1;
                    enabledKeywords = KernelTraceEventParser.Keywords.None;
                    startedAt = DateTimeOffset.MinValue;
                    state = releaseFailed ? "StopFailed" : "Stopped";
                    if (!releaseFailed) message = "共享 kernel ETW 会话已随程序停止。";
                }
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
            restartBlockedByUnreleasedRuntime = !result.ControlDenied || !unreleasedRuntime.IsProcessing;
            appliedConfigurationVersion = -1;
            if (restartBlockedByUnreleasedRuntime)
            {
                enabledKeywords = KernelTraceEventParser.Keywords.None;
                startedAt = DateTimeOffset.MinValue;
            }
            state = Volatile.Read(ref stopping) != 0 ? "Stopping"
                : restartBlockedByUnreleasedRuntime ? "StopFailed" : "Running";
            message = CreateStopFailureMessage(result, operation);
        }

        var failures = string.Join(" | ", result.Failures);
        if (ShouldLogFailure($"stop:{operation}:{failures}"))
            logger.LogError(new InvalidOperationException(CreateStopFailureMessage(result, operation)),
                "Shared kernel ETW {Operation} did not prove release: {Failures}", operation, failures);
        else
            logger.LogDebug("Shared kernel ETW {Operation} still did not prove release: {Failures}", operation, failures);
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
        if (ShouldLogFailure($"start:{exception.GetType().FullName}:{exception.Message}"))
        {
            logger.LogWarning(exception, "Failed to start shared kernel ETW session.");
        }
        else
        {
            logger.LogDebug(exception, "Shared kernel ETW session retry failed.");
        }
    }

    private bool ShouldLogFailure(string fingerprint)
    {
        lock (stateGate)
        {
            if (string.Equals(lastLoggedFailure, fingerprint, StringComparison.Ordinal)) return false;
            lastLoggedFailure = fingerprint;
            return true;
        }
    }

    private sealed class SubscriptionLease(KernelEtwSessionOwner owner, long leaseId) : IKernelEtwSubscription
    {
        private int disposed;

        public KernelEtwSessionSnapshot GetSnapshot() => owner.GetSnapshot(leaseId);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                owner.Release(leaseId);
            }
        }
    }
}
