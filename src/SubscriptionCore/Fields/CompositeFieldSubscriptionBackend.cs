using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using ResourceManager.ReadWrite;

namespace ResourceManager.Subscription;

/// <summary>Combines original demand leases and publication enumerators. A
/// single owner is passed through; mixed owners require no extra worker or cache.</summary>
public sealed class CompositeFieldSubscriptionBackend(CompositeReadWriteBackend core,
    IReadOnlyDictionary<IReadWriteBackend, IFieldSubscriptionBackend> subscriptions) : IFieldSubscriptionBackend
{
    public bool SupportsPeriodicDelivery => subscriptions.Values.All(static source => source.SupportsPeriodicDelivery);

    public IDisposable Acquire(string subscriptionId, ImmutableArray<FieldReference> fields, TimeSpan interval)
        => AcquireCore(subscriptionId, fields, interval, null);

    public IDisposable Acquire(string subscriptionId, ImmutableArray<FieldReference> fields, TimeSpan interval,
        FieldSubscriptionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        return AcquireCore(subscriptionId, fields, interval, options);
    }

    private IDisposable AcquireCore(string subscriptionId, ImmutableArray<FieldReference> fields, TimeSpan interval,
        FieldSubscriptionOptions? options)
    {
        var leases = new List<IDisposable>();
        try
        {
            foreach (var group in core.Select(fields)) leases.Add(options is null
                ? Require(group.Backend).Acquire(subscriptionId, group.Fields, interval)
                : Require(group.Backend).Acquire(subscriptionId, group.Fields, interval, options));
            return new Leases([.. leases]);
        }
        catch { new Leases([.. leases]).Dispose(); throw; }
    }

    public IAsyncEnumerable<ImmutableArray<FieldReading>> SubscribeAsync(string subscriptionId,
        ImmutableArray<FieldReference> fields, TimeSpan interval, CancellationToken cancellationToken)
    {
        var groups = core.Select(fields);
        return groups.Length == 1
            ? Require(groups[0].Backend).SubscribeAsync(subscriptionId, groups[0].Fields, interval, cancellationToken)
            : new SettledMoveAsyncEnumerable<ImmutableArray<FieldReading>>(
                MergeAsync(subscriptionId, fields, groups, interval, null, cancellationToken));
    }

    public IAsyncEnumerable<ImmutableArray<FieldReading>> SubscribeAsync(string subscriptionId,
        ImmutableArray<FieldReference> fields, TimeSpan interval, FieldSubscriptionOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        var groups = core.Select(fields);
        return groups.Length == 1
            ? Require(groups[0].Backend).SubscribeAsync(subscriptionId, groups[0].Fields, interval, options, cancellationToken)
            : new SettledMoveAsyncEnumerable<ImmutableArray<FieldReading>>(
                MergeAsync(subscriptionId, fields, groups, interval, options, cancellationToken));
    }

    private IFieldSubscriptionBackend Require(IReadWriteBackend owner)
        => subscriptions.TryGetValue(owner, out var source) ? source : throw new InvalidOperationException("Field owner has no subscription binding.");

    private async IAsyncEnumerable<ImmutableArray<FieldReading>> MergeAsync(string subscriptionId,
        ImmutableArray<FieldReference> fields, ImmutableArray<BackendFieldSelection> groups, TimeSpan interval, FieldSubscriptionOptions? options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var enumerators = new List<IAsyncEnumerator<ImmutableArray<FieldReading>>>();
        var pending = new Dictionary<int, Task<bool>>();
        var completeRound = options is not null || groups.All(group => Require(group.Backend).SupportsPeriodicDelivery);
        var expected = groups.Select(group => group.Fields.Select(group.Backend.Catalog.Require).ToImmutableArray()).ToArray();
        Exception? failure = null;
        try
        {
            try
            {
                for (var index = 0; index < groups.Length; index++)
                {
                    var backend = Require(groups[index].Backend);
                    var source = (options is null
                        ? backend.SubscribeAsync(subscriptionId, groups[index].Fields, interval, linked.Token)
                        : backend.SubscribeAsync(subscriptionId, groups[index].Fields, interval, options, linked.Token))
                            .GetAsyncEnumerator(linked.Token);
                    enumerators.Add(source);
                    pending.Add(index, MoveNextOrStopAsync(source, linked));
                }
            }
            catch (Exception error) { failure = error; throw; }
            while (pending.Count > 0)
            {
                ImmutableArray<FieldReading> values;
                int[] ready = [];
                try
                {
                    bool[] finished = [];
                    var stopped = false;
                    try
                    {
                        if (completeRound) ready = [.. pending.Keys];
                        else
                        {
                            await Task.WhenAny(pending.Values).WaitAsync(cancellationToken);
                            ready = [.. pending.Where(static pair => pair.Value.IsCompleted).Select(static pair => pair.Key)];
                        }
                        finished = await Task.WhenAll(ready.Select(owner => pending[owner])).WaitAsync(cancellationToken);
                    }
                    catch (OperationCanceledException) when (linked.IsCancellationRequested && !cancellationToken.IsCancellationRequested) { stopped = true; }
                    if (stopped) yield break;
                    if (finished.Any(static present => !present)) yield break;
                    foreach (var owner in ready) pending.Remove(owner);
                    var merged = new FieldReading[fields.Length];
                    for (var owner = 0; owner < groups.Length; owner++)
                    {
                        var hasResult = ready.Contains(owner);
                        var published = hasResult ? enumerators[owner].Current : ReadCurrent(groups[owner].Backend, expected[owner]);
                        if (hasResult && published.Length != groups[owner].Fields.Length)
                            throw new InvalidOperationException("Subscription owner omitted selected readings.");
                        for (var index = 0; index < groups[owner].Fields.Length; index++)
                        {
                            var original = expected[owner][index];
                            if (hasResult && published[index].Field != original.Field)
                                throw new InvalidOperationException("Subscription owner changed selected identities.");
                            merged[groups[owner].Positions[index]] = published[index];
                        }
                    }
                    values = [.. merged];
                }
                catch (Exception error) { failure = error; throw; }
                yield return values;
                foreach (var owner in ready)
                    pending.Add(owner, MoveNextOrStopAsync(enumerators[owner], linked));
            }
        }
        finally
        {
            List<Exception>? errors = null;
            try { await linked.CancelAsync(); } catch (Exception error) { (errors ??= []).Add(error); }
            try { await Task.WhenAll(pending.Values); } catch (Exception) { /* Preserve the original stream result while settling cancellation. */ }
            foreach (var enumerator in enumerators)
                try { await enumerator.DisposeAsync(); } catch (Exception error) { (errors ??= []).Add(error); }
            if (errors is not null)
            {
                if (failure is not null) errors.Insert(0, failure);
                throw new AggregateException(errors);
            }
        }
    }

    private static async Task<bool> MoveNextOrStopAsync(IAsyncEnumerator<ImmutableArray<FieldReading>> source,
        CancellationTokenSource linked)
    {
        try
        {
            var present = await source.MoveNextAsync();
            if (!present) await linked.CancelAsync();
            return present;
        }
        catch
        {
            await linked.CancelAsync();
            throw;
        }
    }

    internal static ImmutableArray<FieldReading> ReadCurrent(IReadWriteBackend owner, ImmutableArray<FieldCapability> expected)
    {
        var catalog = owner.Catalog;
        var selected = expected.Where(original => Matches(catalog, original))
            .DistinctBy(static original => original.Field).ToImmutableArray();
        var readings = new Dictionary<FieldReference, FieldReading>();
        if (!selected.IsEmpty)
        {
            var part = owner.ReadCachedBatch([.. selected.Select(static original => original.Field)]);
            if (part.Length != selected.Length) throw new InvalidOperationException("Field owner omitted selected readings.");
            for (var index = 0; index < part.Length; index++)
            {
                if (part[index].Field != selected[index].Field) throw new InvalidOperationException("Field owner changed selected identities.");
                readings[part[index].Field] = part[index].Status == FieldObservationStatus.Unavailable && string.IsNullOrEmpty(part[index].SourceId)
                    ? part[index] with { SourceId = selected[index].ReadSource } : part[index];
            }
        }
        return [.. expected.Select(original => Matches(catalog, original) && readings.TryGetValue(original.Field, out var reading)
            ? reading : new(original.Field, FieldObservationStatus.Unavailable, null, null, original.ReadSource))];
    }

    private static bool Matches(ReadWriteCatalog catalog, FieldCapability original)
        => catalog.Find(original.Field) is { } current && current.Definition == original.Definition
            && current.ReadSource == original.ReadSource;

    private sealed class Leases(IDisposable[] values) : IDisposable
    {
        private IDisposable[]? owned = values;
        public void Dispose()
        {
            var released = Interlocked.Exchange(ref owned, null);
            if (released is null) return;
            List<Exception>? errors = null;
            foreach (var lease in released)
                try { lease.Dispose(); } catch (Exception error) { (errors ??= []).Add(error); }
            if (errors is not null) throw new AggregateException(errors);
        }
    }

    /// <summary>
    /// Serializes disposal of the merged iterator with an in-flight MoveNextAsync.
    /// Cancellation is requested first so a blocked merge can finish its own drain.
    /// </summary>
    private sealed class SettledMoveAsyncEnumerable<T>(IAsyncEnumerable<T> source) : IAsyncEnumerable<T>
    {
        public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            try { return new SettledMoveAsyncEnumerator<T>(source.GetAsyncEnumerator(stop.Token), stop); }
            catch { stop.Dispose(); throw; }
        }
    }

    private sealed class SettledMoveAsyncEnumerator<T>(IAsyncEnumerator<T> source,
        CancellationTokenSource stop) : IAsyncEnumerator<T>
    {
        private readonly object gate = new();
        private Task<bool>? activeMove;
        private Task? disposeTask;
        private bool moveInProgress;
        private bool disposed;

        public T Current => source.Current;

        public ValueTask<bool> MoveNextAsync()
        {
            Task<bool> sourceMove;
            lock (gate)
            {
                if (disposed) return ValueTask.FromResult(false);
                if (moveInProgress)
                    throw new InvalidOperationException("A merged field subscription already has a MoveNextAsync in progress.");
                moveInProgress = true;
                try
                {
                    sourceMove = source.MoveNextAsync().AsTask();
                    activeMove = sourceMove;
                }
                catch
                {
                    moveInProgress = false;
                    activeMove = null;
                    throw;
                }
            }
            return new ValueTask<bool>(TrackMoveAsync(sourceMove));
        }

        private async Task<bool> TrackMoveAsync(Task<bool> sourceMove)
        {
            try { return await sourceMove.ConfigureAwait(false); }
            finally
            {
                lock (gate)
                {
                    if (ReferenceEquals(activeMove, sourceMove)) activeMove = null;
                    moveInProgress = false;
                }
            }
        }

        public ValueTask DisposeAsync()
        {
            Task<bool>? move;
            TaskCompletionSource completion;
            lock (gate)
            {
                if (disposeTask is not null) return new ValueTask(disposeTask);
                disposed = true;
                move = activeMove;
                completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                disposeTask = completion.Task;
            }
            _ = CompleteDisposeAsync(move, completion);
            return new ValueTask(completion.Task);
        }

        private async Task CompleteDisposeAsync(Task<bool>? move, TaskCompletionSource completion)
        {
            Exception? failure = null;
            try { await stop.CancelAsync().ConfigureAwait(false); }
            catch (Exception error) { failure = error; }

            if (move is not null)
            {
                try { await move.ConfigureAwait(false); }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
                catch (Exception error) { failure ??= error; }
            }

            try { await source.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) { failure ??= error; }
            try { stop.Dispose(); }
            catch (Exception error) { failure ??= error; }

            if (failure is null) completion.TrySetResult();
            else completion.TrySetException(failure);
        }
    }
}

