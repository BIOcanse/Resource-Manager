using Microsoft.Extensions.Options;
using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;

/// <summary>Owns lifecycle facts; Generic Host remains the owner of process lifetime.</summary>
public sealed class BusinessBlockController : IBusinessBlockQuery, IBusinessBlockDiagnostics, IAsyncDisposable
{
    private readonly object sync = new();
    private readonly SemaphoreSlim requests = new(1, 1);
    private readonly IServiceProvider services;
    private readonly ILogger<BusinessBlockController> logger;
    private readonly Dictionary<string, Entry> entries;
    private readonly HashSet<string> roots = new(StringComparer.Ordinal);
    private readonly Queue<BusinessBlockTransition> history;
    private readonly BusinessBlockOptions options;
    private readonly CancellationTokenSource samplingStop = new();
    private Task? samplingTask;
    private long sequence;
    private bool disposed;

    public BusinessBlockController(IServiceProvider services,
        IEnumerable<BusinessBlockRegistration> registrations, IOptions<BusinessBlockOptions> options,
        ILogger<BusinessBlockController> logger)
    {
        this.services = services;
        this.logger = logger;
        this.options = options.Value;
        ArgumentOutOfRangeException.ThrowIfNegative(this.options.HistoryLimit);
        if (this.options.MetricsInterval is { } interval && interval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "MetricsInterval must be positive or null (disabled).");
        history = new Queue<BusinessBlockTransition>(this.options.HistoryLimit);
        entries = registrations.ToDictionary(value => value.Id,
            value => new Entry(value, this.options.Blocks.GetValueOrDefault(value.Id) ?? this.options.Policy), StringComparer.Ordinal);
        foreach (var entry in entries.Values)
        {
            if (!Enum.IsDefined(entry.Policy.OnTimeout) || !Enum.IsDefined(entry.Policy.OnFailure))
                throw new ArgumentException("Business block timeout and failure policies must be declared values.", nameof(options));
            if (entry.Policy.StopDeadline is { } deadline && deadline <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(options), "StopDeadline must be positive or null (disabled).");
            foreach (var dependency in entry.Registration.Dependencies) _ = Get(dependency);
        }
        // Validate the complete declared graph once, without constructing business services.
        _ = Ordered(entries.Keys);
    }

    public async Task StartAsync(string id, BusinessBlockReason reason, CancellationToken cancellationToken = default)
    {
        Validate(reason);
        await requests.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var ordered = Ordered([id]);
            var originalRuns = ordered.ToDictionary(entry => entry.Registration.Id, entry => entry.Run, StringComparer.Ordinal);
            try
            {
                foreach (var entry in ordered)
                {
                    var cause = entry.Registration.Id == id ? reason : reason with
                    { Kind = BusinessBlockRequestKind.Dependency, ConfigurationOrDependency = id };
                    await StartOneAsync(entry, cause, cancellationToken).ConfigureAwait(false);
                }
            }
            catch
            {
                var needed = Ordered(roots).Select(entry => entry.Registration.Id).ToHashSet(StringComparer.Ordinal);
                for (var index = ordered.Count - 1; index >= 0; index--)
                    if (!needed.Contains(ordered[index].Registration.Id)
                        && !ReferenceEquals(originalRuns[ordered[index].Registration.Id], ordered[index].Run))
                        await StopOneAsync(ordered[index], reason with
                        { Kind = BusinessBlockRequestKind.Dependency, ConfigurationOrDependency = "start-failed:" + id }, CancellationToken.None).ConfigureAwait(false);
                throw;
            }
            roots.Add(id);
            if (options.MetricsInterval is not null && samplingTask is null)
                samplingTask = SampleLoopAsync(samplingStop.Token);
        }
        finally { requests.Release(); }
    }

    private async Task StartOneAsync(Entry entry, BusinessBlockReason reason, CancellationToken cancellationToken)
    {
        lock (sync)
        {
            if (entry.Run is { IsCompleted: false } || entry.StopCompletion is { IsCompleted: false }
                || entry.ObservedThreads != 0 && entry.Stop?.IsCancellationRequested == true)
            {
                if (entry.Stop?.IsCancellationRequested == true)
                    throw new InvalidOperationException($"{entry.Registration.Id} still has an outstanding stop; a new run cannot start yet.");
                return;
            }
            if (entry.Registration.ObservationOnly)
                throw new InvalidOperationException($"{entry.Registration.Id} is an observed legacy session; its producer still owns activation in step 1.");
            entry.Stop?.Dispose();
            entry.Stop = new();
            entry.StopCompletion = null;
            entry.Run = null;
            entry.Block = null;
            // Legacy producers can already hold a consumer before their hosted adapter starts.
            // Preserve its measured lifetime instead of resetting an active thread registration.
            if (entry.ObservedThreads == 0) entry.Metrics.Reset(DateTimeOffset.UtcNow);
            entry.HasReportedFailure = false;
            Transition(entry, BusinessBlockState.Starting, reason);
        }
        IBusinessBlock block;
        try { block = entry.Registration.Create(services); }
        catch (Exception error)
        {
            lock (sync) Transition(entry, BusinessBlockState.Failed, reason, BusinessBlockTransitionOutcome.Failed, error.GetType().FullName, error.Message);
            ScheduleFailurePolicy(entry);
            throw;
        }
        lock (sync) entry.Block = block;
        if (block is HostedBusinessBlock hosted) hosted.SetStartCancellation(cancellationToken);
        var run = RunAsync(entry, block, reason);
        lock (sync) entry.Run = run;
        if (block is IBusinessBlockStartup startup)
        {
            await startup.Started.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        lock (sync)
        {
            if (entry.State == BusinessBlockState.Starting) Transition(entry, BusinessBlockState.Running, reason);
        }
    }

    private async Task<BusinessBlockResult> RunAsync(Entry entry, IBusinessBlock block, BusinessBlockReason reason)
    {
        BusinessBlockResult result;
        try { result = await block.RunAsync(entry.Stop!.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (entry.Stop!.IsCancellationRequested) { result = BusinessBlockResult.Stopped; }
        catch (Exception error) { result = BusinessBlockResult.Failed(error); }
        // A legacy StopAsync can return before its private consumer exits. The controller
        // owns that registered execution too, so the same deadline must cover its drain.
        Task consumersReleased;
        lock (sync) consumersReleased = entry.ConsumersReleased?.Task ?? Task.CompletedTask;
        var failureReportedBeforeDrain = result.Outcome == BusinessBlockOutcome.Failed && !consumersReleased.IsCompleted;
        if (failureReportedBeforeDrain)
        {
            lock (sync) Transition(entry, BusinessBlockState.Failed, reason, BusinessBlockTransitionOutcome.Failed, result.FailureType, result.Failure);
            ScheduleFailurePolicy(entry);
        }
        await consumersReleased.ConfigureAwait(false);
        // Let StartOne publish its task before a synchronous completion is observed.
        await Task.Yield();
        lock (sync)
        {
            var outcome = entry.HasReportedFailure ? BusinessBlockTransitionOutcome.Failed : result.Outcome switch
            {
                BusinessBlockOutcome.Done => BusinessBlockTransitionOutcome.Done,
                BusinessBlockOutcome.Stopped => BusinessBlockTransitionOutcome.Stopped,
                _ => BusinessBlockTransitionOutcome.Failed
            };
            entry.Metrics.Sample(DateTimeOffset.UtcNow);
            if (!failureReportedBeforeDrain || entry.State != BusinessBlockState.Failed || entry.Outcome != BusinessBlockTransitionOutcome.Failed)
                Transition(entry, outcome == BusinessBlockTransitionOutcome.Failed ? BusinessBlockState.Failed : BusinessBlockState.Stopped,
                    entry.Stop!.IsCancellationRequested ? entry.Reason! : reason, outcome, result.FailureType, result.Failure);
        }
        if (result.Outcome == BusinessBlockOutcome.Failed) ScheduleFailurePolicy(entry);
        return result;
    }

    public async Task StopAsync(string id, BusinessBlockReason reason, CancellationToken cancellationToken = default)
    {
        Validate(reason);
        await requests.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _ = Get(id);
            roots.Remove(id);
            var needed = Ordered(roots).Select(value => value.Registration.Id).ToHashSet(StringComparer.Ordinal);
            var candidates = Ordered([id]);
            for (var index = candidates.Count - 1; index >= 0; index--)
            {
                var entry = candidates[index];
                if (!needed.Contains(entry.Registration.Id))
                    await StopOneAsync(entry, entry.Registration.Id == id ? reason : reason with
                    { Kind = BusinessBlockRequestKind.Dependency, ConfigurationOrDependency = id }, cancellationToken).ConfigureAwait(false);
            }
        }
        finally { requests.Release(); }
    }

    private async Task StopOneAsync(Entry entry, BusinessBlockReason reason, CancellationToken cancellationToken)
    {
        Task<BusinessBlockResult>? run;
        lock (sync)
        {
            run = entry.Run;
            if (run is null || run.IsCompleted) return;
            Transition(entry, BusinessBlockState.Stopping, reason);
            // The legacy adapter uses the same stop deadline as the controller.
            if (entry.Block is HostedBusinessBlock hosted) hosted.SetStopDeadline(cancellationToken, entry.Policy.StopDeadline);
        }
        // Async notification keeps a misbehaving cancellation callback inside the same stop deadline.
        var completion = Task.WhenAll(entry.Stop!.CancelAsync(), run);
        lock (sync) entry.StopCompletion = completion;
        try
        {
            if (entry.Policy.StopDeadline is { } deadline)
                await completion.WaitAsync(deadline, cancellationToken).ConfigureAwait(false);
            else await completion.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (entry.Block is HostedBusinessBlock { StopFailure: { } failure })
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
        catch (TimeoutException error)
        {
            lock (sync) Transition(entry, BusinessBlockState.Failed, reason, BusinessBlockTransitionOutcome.StopTimeout, error.GetType().FullName, error.Message);
            if (entry.Policy.OnTimeout == BusinessBlockTimeoutPolicy.ContinueWaiting)
                await completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            lock (sync)
                if (entry.Outcome != BusinessBlockTransitionOutcome.Failed)
                    Transition(entry, BusinessBlockState.Failed, reason, BusinessBlockTransitionOutcome.Failed, error.GetType().FullName, error.Message);
            ScheduleFailurePolicy(entry);
            throw;
        }
    }

    public Task<BusinessBlockResult> GetCompletion(string id)
    {
        lock (sync) return Get(id).Run ?? throw new InvalidOperationException($"{id} has not run.");
    }

    internal bool IsStopRequested(string id)
    {
        lock (sync) return Get(id).Stop?.IsCancellationRequested == true;
    }

    public void ReportFailure(string blockId, Exception error, string operation)
    {
        ArgumentNullException.ThrowIfNull(error);
        Entry entry;
        lock (sync)
        {
            entry = Get(blockId);
            entry.HasReportedFailure = true;
            var reason = entry.Reason ?? new(BusinessBlockRequestKind.Dependency, blockId, operation, 0);
            Transition(entry, BusinessBlockState.Failed, reason with { ConfigurationOrDependency = operation },
                BusinessBlockTransitionOutcome.Failed, error.GetType().FullName, error.Message);
        }
        ScheduleFailurePolicy(entry);
    }

    private void ScheduleFailurePolicy(Entry entry)
    {
        if (entry.Policy.OnFailure == BusinessBlockFailurePolicy.RecordOnly) return;
        lock (sync)
        {
            if (disposed || entry.FailurePolicyTask is { IsCompleted: false }) return;
            entry.FailurePolicyTask = ApplyFailurePolicyAsync(entry);
        }
    }

    private async Task ApplyFailurePolicyAsync(Entry failed)
    {
        // Policy runs separately from the execution it may have to drain. No recursive lifecycle calls.
        await Task.Yield();
        await requests.WaitAsync().ConfigureAwait(false);
        try
        {
            var id = failed.Registration.Id;
            var affected = new HashSet<string>(StringComparer.Ordinal) { id };
            if (failed.Policy.OnFailure == BusinessBlockFailurePolicy.StopDependents)
            {
                var failedRoots = new List<string>();
                foreach (var root in roots)
                    if (Ordered([root]).Any(entry => entry.Registration.Id == id))
                    {
                        failedRoots.Add(root);
                        foreach (var entry in Ordered([root])) affected.Add(entry.Registration.Id);
                    }
                foreach (var root in failedRoots) roots.Remove(root);
                var needed = Ordered(roots).Select(entry => entry.Registration.Id).ToHashSet(StringComparer.Ordinal);
                affected.ExceptWith(needed);
            }
            var ordered = Ordered(affected);
            for (var index = ordered.Count - 1; index >= 0; index--)
                if (affected.Contains(ordered[index].Registration.Id))
                    await StopOneAsync(ordered[index], new(BusinessBlockRequestKind.Dependency, id, "failure-policy", failed.Reason?.PlanVersion ?? 0), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error) { logger.LogError(error, "Business block failure policy could not complete for {BlockId}", failed.Registration.Id); }
        finally { requests.Release(); }
    }

    public IDisposable MeasureSegment(string blockId) => Get(blockId).Metrics.MeasureSegment();

    internal IDisposable RegisterConsumerThread(string blockId, string sessionName)
    {
        var entry = Get(blockId);
        lock (sync)
        {
            if (entry.Registration.ObservationOnly && entry.ObservedThreads == 0)
            {
                entry.Metrics.Reset(DateTimeOffset.UtcNow);
                entry.HasReportedFailure = false;
                var reason = new BusinessBlockReason(BusinessBlockRequestKind.Dependency, sessionName, "etw-consumer", 0);
                Transition(entry, BusinessBlockState.Starting, reason);
                Transition(entry, BusinessBlockState.Running, reason);
            }
            if (entry.ObservedThreads == 0)
                entry.ConsumersReleased = new(TaskCreationOptions.RunContinuationsAsynchronously);
            entry.ObservedThreads++;
        }
        IDisposable registration;
        try { registration = entry.Metrics.RegisterCurrentThread(sessionName); }
        catch (Exception error)
        {
            lock (sync)
            {
                entry.ObservedThreads--;
                if (entry.ObservedThreads == 0) entry.ConsumersReleased!.TrySetResult();
            }
            ReportFailure(blockId, error, "thread-registration");
            throw;
        }
        lock (sync) entry.Metrics.Sample(DateTimeOffset.UtcNow);
        return new ThreadRegistration(this, entry, registration);
    }

    private sealed class ThreadRegistration(BusinessBlockController owner, Entry entry, IDisposable registration) : IDisposable
    {
        private bool released;
        public void Dispose()
        {
            if (released) return;
            registration.Dispose();
            released = true;
            lock (owner.sync)
            {
                entry.Metrics.Sample(DateTimeOffset.UtcNow);
                entry.ObservedThreads--;
                if (entry.ObservedThreads == 0) entry.ConsumersReleased!.TrySetResult();
                if (entry.Registration.ObservationOnly && entry.ObservedThreads == 0)
                {
                    owner.Transition(entry, BusinessBlockState.Stopping, entry.Reason!);
                    owner.Transition(entry, entry.HasReportedFailure ? BusinessBlockState.Failed : BusinessBlockState.Stopped,
                        entry.Reason!, entry.HasReportedFailure ? BusinessBlockTransitionOutcome.Failed : BusinessBlockTransitionOutcome.Stopped);
                }
            }
        }
    }

    private async Task SampleLoopAsync(CancellationToken stop)
    {
        using var timer = new PeriodicTimer(options.MetricsInterval!.Value);
        try
        {
            while (await timer.WaitForNextTickAsync(stop).ConfigureAwait(false))
            {
                lock (sync)
                {
                    var now = DateTimeOffset.UtcNow;
                    foreach (var entry in entries.Values) entry.Metrics.Sample(now);
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception error) { logger.LogError(error, "Business block CPU sampling failed"); }
    }

    public BusinessBlocksSnapshot Read()
    {
        lock (sync) return new(DateTimeOffset.UtcNow, options.HistoryLimit, options.MetricsInterval,
            entries.Values.Select(entry => new BusinessBlockFacts(entry.Registration.Id, entry.State,
                entry.Registration.Dependencies.ToArray(), entry.Policy, entry.Reason, entry.Outcome,
                entry.Run is { IsCompleted: false } || entry.StopCompletion is { IsCompleted: false } || entry.ObservedThreads != 0, entry.Metrics.Read())
                { ObservationOnly = entry.Registration.ObservationOnly }).ToArray(), history.ToArray());
    }

    private Entry Get(string id) => entries.TryGetValue(id, out var entry) ? entry : throw new KeyNotFoundException($"Unknown business block: {id}");

    private List<Entry> Ordered(IEnumerable<string> requested)
    {
        var included = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(requested);
        while (pending.TryPop(out var id))
        {
            var entry = Get(id);
            if (!included.Add(id)) continue;
            foreach (var dependency in entry.Registration.Dependencies) pending.Push(dependency);
        }
        var remaining = included.ToDictionary(id => id, id => Get(id).Registration.Dependencies.Count, StringComparer.Ordinal);
        var ready = new Queue<Entry>(entries.Values.Where(entry => remaining.GetValueOrDefault(entry.Registration.Id, -1) == 0));
        var result = new List<Entry>(included.Count);
        while (ready.TryDequeue(out var entry))
        {
            result.Add(entry);
            foreach (var candidate in entries.Values)
            {
                if (remaining.TryGetValue(candidate.Registration.Id, out var count) && count > 0 && candidate.Registration.Dependencies.Contains(entry.Registration.Id))
                {
                    remaining[candidate.Registration.Id] = --count;
                    if (count == 0) ready.Enqueue(candidate);
                }
            }
        }
        if (result.Count != included.Count) throw new InvalidOperationException("Business block dependency cycle.");
        return result;
    }

    private static void Validate(BusinessBlockReason reason)
    {
        ArgumentNullException.ThrowIfNull(reason);
        if (!Enum.IsDefined(reason.Kind) || string.IsNullOrWhiteSpace(reason.Owner) || string.IsNullOrWhiteSpace(reason.ConfigurationOrDependency))
            throw new ArgumentException("A request requires its explicit source, owner and configuration/dependency.", nameof(reason));
    }

    private void Transition(Entry entry, BusinessBlockState next, BusinessBlockReason reason,
        BusinessBlockTransitionOutcome outcome = BusinessBlockTransitionOutcome.None, string? failureType = null, string? failure = null)
    {
        var transition = new BusinessBlockTransition(++sequence, DateTimeOffset.UtcNow, entry.Registration.Id, entry.State, next,
            reason, outcome, failureType, failure);
        entry.State = next;
        entry.Reason = reason;
        entry.Outcome = outcome;
        if (options.HistoryLimit > 0)
        {
            if (history.Count == options.HistoryLimit) history.Dequeue();
            history.Enqueue(transition);
        }
        logger.LogInformation("Business block {BlockId}: {From} -> {To}; source={Source}, owner={Owner}, cause={Cause}, plan={PlanVersion}, outcome={Outcome}, failureType={FailureType}, failure={Failure}",
            transition.BlockId, transition.From, transition.To, reason.Kind, reason.Owner, reason.ConfigurationOrDependency,
            reason.PlanVersion, outcome, transition.FailureType, transition.Failure);
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        samplingStop.Cancel();
        if (samplingTask is not null) await samplingTask.ConfigureAwait(false);
        await requests.WaitAsync().ConfigureAwait(false);
        try
        {
            var ordered = Ordered(entries.Keys);
            for (var index = ordered.Count - 1; index >= 0; index--)
            {
                var entry = ordered[index];
                // A prior timeout keeps the real task visible; disposal must not pretend to release it.
                if (entry.Outcome != BusinessBlockTransitionOutcome.StopTimeout)
                    await StopOneAsync(entry, new(BusinessBlockRequestKind.Configuration, "controller-dispose", "process-lifetime", entry.Reason?.PlanVersion ?? 0), CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally { requests.Release(); }
        foreach (var entry in entries.Values)
        {
            if (entry.FailurePolicyTask is { } policy) await policy.ConfigureAwait(false);
            if ((entry.Run is null || entry.Run.IsCompleted) && (entry.StopCompletion is null || entry.StopCompletion.IsCompleted))
                entry.Stop?.Dispose();
        }
        samplingStop.Dispose();
    }

    private sealed class Entry(BusinessBlockRegistration registration, BusinessBlockPolicy policy)
    {
        public BusinessBlockRegistration Registration { get; } = registration;
        public BusinessBlockPolicy Policy { get; } = policy;
        public BusinessBlockMetrics Metrics { get; } = new();
        public BusinessBlockState State;
        public BusinessBlockReason? Reason;
        public BusinessBlockTransitionOutcome Outcome;
        public CancellationTokenSource? Stop;
        public IBusinessBlock? Block;
        public Task<BusinessBlockResult>? Run;
        public Task? StopCompletion;
        public Task? FailurePolicyTask;
        public bool HasReportedFailure;
        public int ObservedThreads;
        public TaskCompletionSource? ConsumersReleased;
    }
}
