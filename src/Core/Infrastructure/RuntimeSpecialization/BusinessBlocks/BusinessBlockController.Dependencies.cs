using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Application.RuntimeSpecialization;
using Microsoft.Extensions.DependencyInjection;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;

public sealed partial class BusinessBlockController
{
    private readonly object dependencyQueueSync = new();
    private Task dependencyQueueTail = Task.CompletedTask;
    private bool acceptingDependencies = true;

    public long CurrentPlanVersion => services.GetService<IRuntimePlanProvider>()?.Current.Version ?? 0;

    public IBusinessBlockDependency AcquireDependency(string id, BusinessBlockReason reason,
        TimeSpan? interval = null, IReadOnlyList<string>? outputs = null)
    {
        Validate(reason);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (interval is { } value && value <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(interval), "A dependency interval must be positive or null.");
        var outputSnapshot = outputs?.ToArray() ?? Array.Empty<string>();
        if (outputSnapshot.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Dependency outputs must be non-empty names.", nameof(outputs));

        var entry = Get(id);
        var leaseFacts = new BusinessBlockDependencyFacts(reason, interval, Array.AsReadOnly(outputSnapshot));
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!acceptingDependencies)
                throw new InvalidOperationException("The business-block host lifetime is already stopping.");

            var lease = new DependencyLease(this, entry, leaseFacts);
            lease.SetReady(EnqueueDependencyChange(() => AcquireDependencyAsync(lease), "acquire", id));
            return lease;
        }
    }

    public Task SetEnabledAsync(string id, bool enabled, BusinessBlockReason reason)
    {
        Validate(reason);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        _ = Get(id);
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!acceptingDependencies)
                throw new InvalidOperationException("The business-block host lifetime is already stopping.");
            return EnqueueDependencyChange(() => SetEnabledCoreAsync(id, enabled, reason), "set-enabled", id);
        }
    }

    private async Task SetEnabledCoreAsync(string id, bool enabled, BusinessBlockReason reason)
    {
        await requests.WaitAsync().ConfigureAwait(false);
        try
        {
            var target = Get(id);
            HashSet<string> neededBefore;
            lock (sync)
            {
                if (target.Enabled == enabled) return;
                neededBefore = NeededBlockIds();
                target.Enabled = enabled;
            }

            var neededAfter = NeededBlockIds();
            var affectedRoots = DemandRootIds().Where(root => Ordered([root])
                .Any(entry => entry.Registration.Id == id)).ToArray();
            if (enabled)
            {
                foreach (var entry in OrderedEnabled(affectedRoots))
                    if (!neededBefore.Contains(entry.Registration.Id) && neededAfter.Contains(entry.Registration.Id))
                        await StartOneAsync(entry, entry.Registration.Id == id ? reason : reason with
                        {
                            Kind = BusinessBlockRequestKind.Dependency,
                            ConfigurationOrDependency = id
                        }, CancellationToken.None).ConfigureAwait(false);

                var failed = OrderedEnabled(affectedRoots).FirstOrDefault(entry => entry.State == BusinessBlockState.Failed);
                if (failed is not null)
                    throw new InvalidOperationException($"Business block {failed.Registration.Id} is failed while enabled demand remains.");
                if (neededAfter.Count > 0) EnsureSamplingStarted();
            }
            else
            {
                foreach (var entry in Ordered(affectedRoots.Append(id)).AsEnumerable().Reverse())
                    if (!neededAfter.Contains(entry.Registration.Id))
                        await StopOneAsync(entry, entry.Registration.Id == id ? reason : reason with
                        {
                            Kind = BusinessBlockRequestKind.Dependency,
                            ConfigurationOrDependency = id
                        }, CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally { requests.Release(); }
    }

    private async Task AcquireDependencyAsync(DependencyLease lease)
    {
        await requests.WaitAsync().ConfigureAwait(false);
        try
        {
            var ordered = Ordered([lease.Entry.Registration.Id]);
            lock (sync)
            {
                var neededBefore = NeededBlockIds();
                lease.NewlyNeeded = ordered.Select(entry => entry.Registration.Id)
                    .Where(id => !neededBefore.Contains(id)).ToHashSet(StringComparer.Ordinal);
                lease.Entry.ActiveDependencies.Add(lease);
            }
            // A disabled graph records demand but remains stopped until configuration enables it.
            if (ordered.Any(entry => !entry.Enabled)) return;

            foreach (var entry in ordered)
            {
                if (!lease.NewlyNeeded.Contains(entry.Registration.Id)) continue;
                await StartOneAsync(entry, entry.Registration.Id == lease.Entry.Registration.Id
                    ? lease.Facts.Reason
                    : lease.Facts.Reason with
                    {
                        Kind = BusinessBlockRequestKind.Dependency,
                        ConfigurationOrDependency = lease.Entry.Registration.Id
                    }, CancellationToken.None).ConfigureAwait(false);
            }

            var failed = ordered.FirstOrDefault(entry => entry.State == BusinessBlockState.Failed);
            if (failed is not null)
                throw new InvalidOperationException($"Business block {failed.Registration.Id} is failed while this dependency remains active.");
            EnsureSamplingStarted();
        }
        finally { requests.Release(); }
    }

    private Task ReleaseDependency(DependencyLease lease)
        => EnqueueDependencyChange(() => ReleaseDependencyAsync(lease), "release", lease.Entry.Registration.Id);

    private async Task ReleaseDependencyAsync(DependencyLease lease)
    {
        await requests.WaitAsync().ConfigureAwait(false);
        try
        {
            HashSet<string> needed;
            lock (sync)
            {
                if (!lease.Entry.ActiveDependencies.Remove(lease)) return;
                needed = NeededBlockIds();
            }

            foreach (var entry in Ordered([lease.Entry.Registration.Id]).AsEnumerable().Reverse())
                if (!needed.Contains(entry.Registration.Id))
                    await StopOneAsync(entry, entry.Registration.Id == lease.Entry.Registration.Id
                        ? lease.Facts.Reason
                        : lease.Facts.Reason with
                        {
                            Kind = BusinessBlockRequestKind.Dependency,
                            ConfigurationOrDependency = lease.Entry.Registration.Id
                        }, CancellationToken.None).ConfigureAwait(false);
        }
        finally { requests.Release(); }
    }

    public async Task StopDependenciesAsync(BusinessBlockReason reason, CancellationToken cancellationToken = default)
    {
        Validate(reason);
        Task prior;
        lock (sync)
        {
            acceptingDependencies = false;
            lock (dependencyQueueSync) prior = dependencyQueueTail;
        }
        try { await prior.ConfigureAwait(false); }
        catch { /* A failed change is logged; shutdown still withdraws the remaining leases. */ }

        await requests.WaitAsync().ConfigureAwait(false);
        try
        {
            DependencyLease[] leases;
            string[] dependencyRoots;
            HashSet<string> needed;
            lock (sync)
            {
                leases = entries.Values.SelectMany(entry => entry.ActiveDependencies).ToArray();
                dependencyRoots = leases.Select(lease => lease.Entry.Registration.Id).Distinct(StringComparer.Ordinal).ToArray();
                foreach (var entry in entries.Values) entry.ActiveDependencies.Clear();
                foreach (var lease in leases) lease.TryBeginRelease();
                needed = NeededBlockIds();
            }

            Exception? firstError = null;
            var stopErrors = new Dictionary<string, Exception>(StringComparer.Ordinal);
            foreach (var entry in Ordered(dependencyRoots).AsEnumerable().Reverse())
            {
                if (needed.Contains(entry.Registration.Id)) continue;
                try
                {
                    await StopOneAsync(entry, reason with
                    {
                        Kind = BusinessBlockRequestKind.Dependency,
                        ConfigurationOrDependency = entry.Registration.Id
                    }, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    firstError ??= error;
                    stopErrors[entry.Registration.Id] = error;
                }
            }

            foreach (var lease in leases)
            {
                var failedBlock = Ordered([lease.Entry.Registration.Id])
                    .FirstOrDefault(entry => stopErrors.ContainsKey(entry.Registration.Id));
                lease.CompleteReleased(failedBlock is null ? null : stopErrors[failedBlock.Registration.Id]);
            }
            if (firstError is not null) throw firstError;
        }
        finally { requests.Release(); }
    }

    private HashSet<string> NeededBlockIds()
        => OrderedEnabled(DemandRootIds()).Select(entry => entry.Registration.Id).ToHashSet(StringComparer.Ordinal);

    private List<Entry> OrderedEnabled(IEnumerable<string> requested)
    {
        var runnableRoots = requested.Distinct(StringComparer.Ordinal)
            .Where(root => Ordered([root]).All(entry => entry.Enabled)).ToArray();
        return Ordered(runnableRoots);
    }

    private string[] DemandRootIds()
    {
        lock (sync)
            return roots.Concat(entries.Values.SelectMany(entry => entry.ActiveDependencies)
                .Select(lease => lease.Entry.Registration.Id)).Distinct(StringComparer.Ordinal).ToArray();
    }

    private Task EnqueueDependencyChange(Func<Task> change, string operation, string id)
    {
        Task queued;
        lock (dependencyQueueSync)
        {
            var prior = dependencyQueueTail;
            queued = Task.Run(async () =>
            {
                try { await prior.ConfigureAwait(false); }
                catch { /* A prior fault cannot prevent later changes from running. */ }
                await change().ConfigureAwait(false);
            });
            dependencyQueueTail = queued;
        }
        _ = queued.ContinueWith(task =>
        {
            var error = task.Exception?.GetBaseException();
            if (error is not null)
                logger.LogError(error, "Business block dependency {Operation} failed for {BlockId}", operation, id);
        }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return queued;
    }

    private sealed class DependencyLease(
        BusinessBlockController owner, Entry entry, BusinessBlockDependencyFacts facts)
        : IBusinessBlockDependency
    {
        private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int releaseQueued;

        public Entry Entry { get; } = entry;
        public BusinessBlockDependencyFacts Facts { get; } = facts;
        public HashSet<string> NewlyNeeded { get; set; } = new(StringComparer.Ordinal);
        public Task Ready => ready.Task;
        public Task Released => released.Task;

        public void SetReady(Task task)
        {
            Observe(ready.Task);
            Observe(released.Task);
            _ = task.ContinueWith(completed =>
            {
                if (completed.IsCanceled) ready.TrySetCanceled();
                else if (completed.IsFaulted) ready.TrySetException(completed.Exception!.InnerExceptions);
                else ready.TrySetResult();
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        private static void Observe(Task task)
            => _ = task.ContinueWith(completed => _ = completed.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref releaseQueued, 1) != 0) return;
            var task = owner.ReleaseDependency(this);
            _ = task.ContinueWith(completed => CompleteReleased(completed.Exception?.GetBaseException()),
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return new ValueTask(Released);
        }

        public bool TryBeginRelease() => Interlocked.Exchange(ref releaseQueued, 1) == 0;

        public void CompleteReleased(Exception? error)
        {
            if (error is null) released.TrySetResult();
            else released.TrySetException(error);
        }
    }
}
