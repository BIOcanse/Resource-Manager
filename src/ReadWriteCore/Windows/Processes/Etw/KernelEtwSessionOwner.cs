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
    private bool runtimeDisposalPending;
    private bool pendingNativeReleaseConfirmed;
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
            if (RetryPendingDisposal()) return;
            if (NeedsReconciliation()) ReconcileCore();
        }
    }

    /// <summary>Acquires a reader and reconciles the held resource synchronously.</summary>
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

            try
            {
                ReconcileCore();
                return new SubscriptionLease(this, leaseId);
            }
            catch (Exception acquisitionFailure)
            {
                try { RollbackAcquisition(leaseId); }
                catch (Exception cleanupFailure)
                {
                    throw new AggregateException("Kernel ETW acquisition and registration rollback failed.",
                        acquisitionFailure, cleanupFailure);
                }
                throw;
            }
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

    /// <summary>Drains native resources and permanently releases all managed subscriptions.</summary>
    public void Dispose() => StopBroker();

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
            try
            {
                previousRuntime.UpdateSubscriptions(currentSubscriptions,
                    new KernelEtwSessionContext(currentGeneration, currentStartedAt));
            }
            catch (Exception updateFailure)
            {
                // There are no readers left to keep this session alive. Failure
                // to unregister callbacks cannot prevent stopping the resource.
                failedUpdate = updateFailure;
            }
        }
        KernelEtwSessionStopResult? previousStop;
        try { previousStop = previousRuntime?.Stop(StopWait); }
        catch (Exception stopFailure)
        {
            RecordStopException(previousRuntime!, stopFailure);
            if (currentSubscriptions.Length == 0) DisposeRuntime(previousRuntime!, false);
            throw;
        }
        if (previousStop is not null && !previousStop.IsReleased)
        {
            try
            {
                RecordStopFailure(previousRuntime!, previousStop,
                    Volatile.Read(ref stopping) != 0 ? "shutdown"
                        : currentSubscriptions.Length == 0 ? "last subscription release" : "restart");
                if (failedUpdate is not null)
                    logger.LogDebug(failedUpdate, "Shared kernel ETW subscription update failed; runtime release was not proven.");
            }
            finally
            {
                // With no holder, even a diagnostics failure cannot leave the
                // local consumer running. Native replacement stays blocked.
                if (currentSubscriptions.Length == 0) DisposeRuntime(previousRuntime!, false);
            }
            return;
        }

        if (previousRuntime is not null) DisposeRuntime(previousRuntime, true);

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
                appliedConfigurationVersion = -1;
                enabledKeywords = KernelTraceEventParser.Keywords.None;
                startedAt = DateTimeOffset.MinValue;
                state = "Unavailable";
                message = $"共享 kernel ETW 会话启动失败：{ex.Message}";
            }

            KernelEtwSessionStopResult? failedStartStop;
            try { failedStartStop = nextRuntime?.Stop(StopWait); }
            catch (Exception stopFailure)
            {
                RecordStopException(nextRuntime!, stopFailure);
                throw;
            }
            if (failedStartStop is not null && !failedStartStop.IsReleased)
            {
                RecordStopFailure(nextRuntime!, failedStartStop, "failed-start cleanup");
            }
            else if (ex is KernelEtwSessionCreationCleanupException creationCleanupFailure)
            {
                RecordCreationCleanupFailure(creationCleanupFailure);
            }
            if (failedStartStop?.IsReleased == true)
            {
                DisposeRuntime(nextRuntime!, true);
                lock (stateGate)
                {
                    state = "Unavailable";
                    message = $"共享 kernel ETW 会话启动失败：{ex.Message}";
                }
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
            bool lastHolder;
            lock (stateGate)
            {
                if (!subscriptions.Remove(leaseId)) return;
                configurationVersion++;
                lastHolder = subscriptions.Count == 0;
            }
            if (RetryPendingDisposal()) return;
            if (lastHolder && restartBlockedByUnreleasedRuntime && runtime is not null)
            {
                DisposeRuntime(runtime, false);
                return;
            }
            ReconcileCore();
        }
    }

    private void StopBroker()
    {
        lock (reconciliationGate)
        {
            if (disposed)
            {
                RetryPendingDisposal();
                return;
            }
            disposed = true;
            lock (stateGate)
            {
                subscriptions.Clear();
                configurationVersion++;
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

            // Retain the runtime until its local consumer actually confirms release.
            if (stoppedRuntime is not null)
            {
                DisposeRuntime(stoppedRuntime, runtimeDisposalPending
                    ? pendingNativeReleaseConfirmed : !releaseFailed);
            }
            else
            {
                lock (stateGate)
                    state = releaseFailed ? "StopFailed" : "Stopped";
            }
        }
    }

    private void RollbackAcquisition(long leaseId)
    {
        KernelEtwSubscriptionRegistration[] remainingSubscriptions;
        IKernelEtwSessionRuntime? remainingRuntime;
        lock (stateGate)
        {
            subscriptions.Remove(leaseId);
            configurationVersion++;
            remainingSubscriptions = subscriptions.Values.OrderBy(static item => item.LeaseId).ToArray();
            remainingRuntime = runtime;
        }

        // A failed disposal already owns the cleanup obligation. Never retry it
        // in the failed acquisition call or deliver a lease that did not succeed.
        if (remainingRuntime is null || runtimeDisposalPending) return;
        if (remainingSubscriptions.Length > 0 && !restartBlockedByUnreleasedRuntime && remainingRuntime.IsProcessing)
        {
            try
            {
                remainingRuntime.UpdateSubscriptions(remainingSubscriptions, new(generation, startedAt));
                return;
            }
            catch
            {
                // If callbacks cannot be rolled back, drain this runtime rather
                // than leave an undelivered holder's bindings in it.
            }
        }
        if (restartBlockedByUnreleasedRuntime)
        {
            DisposeRuntime(remainingRuntime, false);
            return;
        }
        KernelEtwSessionStopResult result;
        try { result = remainingRuntime.Stop(StopWait); }
        catch (Exception stopFailure)
        {
            RecordStopException(remainingRuntime, stopFailure);
            DisposeRuntime(remainingRuntime, false);
            throw;
        }
        try
        {
            if (!result.IsReleased) RecordStopFailure(remainingRuntime, result, "failed acquisition rollback");
        }
        finally { DisposeRuntime(remainingRuntime, result.IsReleased); }
    }

    private bool RetryPendingDisposal()
    {
        if (!runtimeDisposalPending) return false;
        DisposeRuntime(runtime!, pendingNativeReleaseConfirmed);
        return true;
    }

    private void DisposeRuntime(IKernelEtwSessionRuntime remainingRuntime, bool nativeReleaseConfirmed)
    {
        try { remainingRuntime.Dispose(); }
        catch (Exception failure)
        {
            lock (stateGate)
            {
                runtime = remainingRuntime;
                runtimeDisposalPending = true;
                pendingNativeReleaseConfirmed = nativeReleaseConfirmed;
                restartBlockedByUnreleasedRuntime = true;
                appliedConfigurationVersion = -1;
                enabledKeywords = KernelTraceEventParser.Keywords.None;
                startedAt = DateTimeOffset.MinValue;
                state = "StopFailed";
                message = $"共享 kernel ETW 本地消费者释放失败：{failure.Message}";
            }
            throw;
        }
        lock (stateGate)
        {
            runtime = null;
            runtimeDisposalPending = false;
            restartBlockedByUnreleasedRuntime = !nativeReleaseConfirmed;
            appliedConfigurationVersion = -1;
            enabledKeywords = KernelTraceEventParser.Keywords.None;
            startedAt = DateTimeOffset.MinValue;
            state = !nativeReleaseConfirmed ? "StopFailed" : Volatile.Read(ref stopping) != 0 ? "Stopped" : "Idle";
            if (nativeReleaseConfirmed)
                message = Volatile.Read(ref stopping) != 0 ? "共享 kernel ETW 会话已随程序停止。" : "共享 kernel ETW 会话等待订阅。";
        }
    }

    private void RecordStopException(IKernelEtwSessionRuntime remainingRuntime, Exception failure)
    {
        lock (stateGate)
        {
            runtime = remainingRuntime;
            restartBlockedByUnreleasedRuntime = true;
            state = "StopFailed";
            message = $"共享 kernel ETW 会话停止失败：{failure.Message}";
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
