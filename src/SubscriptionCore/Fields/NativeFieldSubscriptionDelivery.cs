using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using ResourceManager.ReadWrite;

namespace ResourceManager.Subscription;

/// <summary>Projects completed original tracker triggers into bounded field rounds.
/// This class owns no sampling loop, worker, or field current.</summary>
public sealed class NativeFieldSubscriptionDelivery
{
    private readonly object gate = new();
    private readonly Dictionary<string, Registration> registrations = new(StringComparer.Ordinal);

    /// <summary>Registers one subscription against its original backend and bindings.</summary>
    public Registration Register(string sourceKey, IReadWriteBackend backend,
        ImmutableArray<FieldReference> fields, ImmutableArray<FieldCollectionBinding> bindings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceKey);
        ArgumentNullException.ThrowIfNull(backend);
        if (fields.IsDefaultOrEmpty) throw new ArgumentException("Select at least one exact field.", nameof(fields));
        if (bindings.IsDefaultOrEmpty) throw new ArgumentException("Bind each selected field to its original collection items.", nameof(bindings));

        var expected = fields.Select(backend.Catalog.Require).ToImmutableArray();
        var fieldSet = fields.ToHashSet();
        var byField = new Dictionary<FieldReference, FieldCollectionBinding>();
        foreach (var binding in bindings)
        {
            if (binding is null || !fieldSet.Contains(binding.Field))
                throw new ArgumentException("Bindings must describe only the selected exact fields.", nameof(bindings));
            if (binding.CollectionItemIds.IsDefaultOrEmpty
                || binding.CollectionItemIds.Any(string.IsNullOrWhiteSpace))
                throw new ArgumentException("Each selected field needs an original collection item identity.", nameof(bindings));
            if (binding.SourceKind is not (FieldCollectionSourceKind.CurrentValue or FieldCollectionSourceKind.SourceQuery))
                throw new ArgumentOutOfRangeException(nameof(bindings), "The field source kind is unknown.");
            if (!byField.TryAdd(binding.Field, binding) && !SameBinding(byField[binding.Field], binding))
                throw new ArgumentException("A selected field has conflicting original bindings.", nameof(bindings));
        }
        if (fields.Any(field => !byField.ContainsKey(field)))
            throw new ArgumentException("Every selected field needs exactly one original binding.", nameof(bindings));

        var registration = new Registration(this, sourceKey, backend, expected, byField);
        lock (gate)
        {
            if (!registrations.TryAdd(sourceKey, registration))
            {
                registration.Retire();
                throw new InvalidOperationException("A delivery registration already uses this source key.");
            }
        }
        return registration;
    }

    /// <summary>Completes trigger rounds from the original backend's current values or the supplied explicit query result.</summary>
    public void Deliver<TRequest>(ImmutableArray<NativeItemSamplingSubscriptionTrigger<TRequest>> triggers,
        FieldCollectionResult? result, bool? freshOnly = null)
    {
        if (triggers.IsDefaultOrEmpty) return;
        Dictionary<FieldReference, FieldReading>? readings = null;
        HashSet<FieldReference>? queried = null;
        if (freshOnly != false && triggers.Any(static trigger => trigger.IsFresh)
            && result is { Committed: true })
        {
            readings = new();
            foreach (var reading in result.Readings)
                if (reading is not null) readings.TryAdd(reading.Field, reading);
            queried = result.QueriedFields.ToHashSet();
        }

        lock (gate)
        {
            var current = new List<Registration>();
            foreach (var trigger in triggers)
            {
                if (freshOnly.HasValue && trigger.IsFresh != freshOnly.Value) continue;
                if (!registrations.TryGetValue(trigger.SourceKey, out var registration)) continue;
                registration.Complete(trigger.ItemId, trigger.Sequence, trigger.IsFresh,
                    readings, queried, current);
            }
            foreach (var group in current.GroupBy(static registration => registration.Backend,
                (IEqualityComparer<IReadWriteBackend>)ReferenceEqualityComparer.Instance))
            {
                var expected = group.SelectMany(static registration => registration.Expected).Distinct().ToImmutableArray();
                var part = CompositeFieldSubscriptionBackend.ReadCurrent(group.Key, expected);
                var byCapability = expected.Select((capability, index) => (capability, part[index]))
                    .ToDictionary(static entry => entry.capability, static entry => entry.Item2);
                foreach (var registration in group) registration.PublishCurrent(byCapability);
            }
        }
    }

    /// <summary>Snapshots the original current after the owning publication gate accepted a capture.
    /// QueriedFields is formed only from SourceQuery bindings whose explicit publication keys were queried.</summary>
    public FieldCollectionResult CaptureCompleted<TRequest>(
        ImmutableArray<NativeItemSamplingSubscriptionTrigger<TRequest>> triggers,
        IReadOnlySet<string> queriedPublicationKeys, bool committed)
    {
        ArgumentNullException.ThrowIfNull(queriedPublicationKeys);
        var selected = new List<(IReadWriteBackend Backend, FieldCapability Capability)>();
        var selectedFields = new HashSet<FieldReference>();
        var queriedFieldSet = new HashSet<FieldReference>();
        var queriedFields = new List<FieldReference>();
        lock (gate)
        {
            if (!triggers.IsDefaultOrEmpty)
            {
                foreach (var trigger in triggers)
                {
                    if (!registrations.TryGetValue(trigger.SourceKey, out var registration)) continue;
                    foreach (var index in registration.IndicesForItem(trigger.ItemId))
                    {
                        var capability = registration.Expected[index];
                        var binding = registration.BindingFor(capability.Field);
                        if (selectedFields.Add(capability.Field))
                            selected.Add((registration.Backend, capability));
                        if (binding.SourceKind == FieldCollectionSourceKind.SourceQuery
                            && PublicationKeyWasQueried(binding, queriedPublicationKeys)
                            && queriedFieldSet.Add(capability.Field))
                            queriedFields.Add(capability.Field);
                    }
                }
            }

            if (!committed)
                return new FieldCollectionResult(ImmutableArray<FieldReading>.Empty,
                    Committed: false, QueriedFields: queriedFields.ToImmutableArray());

            var readings = new Dictionary<FieldReference, FieldReading>();
            foreach (var group in selected.GroupBy(static entry => entry.Backend,
                (IEqualityComparer<IReadWriteBackend>)ReferenceEqualityComparer.Instance))
                foreach (var reading in CompositeFieldSubscriptionBackend.ReadCurrent(group.Key,
                    [.. group.Select(static entry => entry.Capability)]))
                    readings.Add(reading.Field, reading);
            return new FieldCollectionResult([.. selected.Select(entry => readings[entry.Capability.Field])], Committed: true,
                QueriedFields: queriedFields.ToImmutableArray());
        }
    }

    /// <summary>Closes all active registrations and drops their buffered rounds. The delivery object remains reusable.</summary>
    public void RetireAll()
    {
        lock (gate)
        {
            foreach (var registration in registrations.Values) registration.Retire();
            registrations.Clear();
        }
    }

    private void Unregister(Registration registration)
    {
        lock (gate)
        {
            if (registrations.TryGetValue(registration.SourceKey, out var current)
                && ReferenceEquals(current, registration))
                registrations.Remove(registration.SourceKey);
            registration.Retire();
        }
    }

    private static bool SameBinding(FieldCollectionBinding left, FieldCollectionBinding right)
        => left.SourceKind == right.SourceKind
            && left.CollectionItemIds.SequenceEqual(right.CollectionItemIds, StringComparer.Ordinal)
            && NormalizePublicationKeys(left.PublicationKeys)
                .SequenceEqual(NormalizePublicationKeys(right.PublicationKeys), StringComparer.Ordinal);

    private static ImmutableArray<string> NormalizePublicationKeys(ImmutableArray<string> keys)
        => keys.IsDefault ? [] : keys;

    private static bool PublicationKeyWasQueried(FieldCollectionBinding binding, IReadOnlySet<string> queriedPublicationKeys)
        => !binding.PublicationKeys.IsDefaultOrEmpty
            && binding.PublicationKeys.Any(queriedPublicationKeys.Contains);

    /// <summary>A bounded delivery registration for one subscription.</summary>
    public sealed class Registration : IDisposable
    {
        private readonly NativeFieldSubscriptionDelivery owner;
        private readonly HashSet<string> collectionItems;
        private readonly Dictionary<FieldReference, FieldCollectionBinding> bindings;
        private readonly Dictionary<ulong, Round> rounds = [];
        private readonly Channel<ImmutableArray<FieldReading>> values = Channel.CreateBounded<ImmutableArray<FieldReading>>(
            new BoundedChannelOptions(1)
            {
                SingleWriter = true,
                SingleReader = false,
                FullMode = BoundedChannelFullMode.DropOldest
            });
        private int active = 1;

        internal Registration(NativeFieldSubscriptionDelivery owner, string sourceKey,
            IReadWriteBackend backend, ImmutableArray<FieldCapability> expected,
            Dictionary<FieldReference, FieldCollectionBinding> bindings)
        {
            this.owner = owner;
            SourceKey = sourceKey;
            Backend = backend;
            Expected = expected;
            this.bindings = bindings;
            collectionItems = bindings.Values.SelectMany(static binding => binding.CollectionItemIds)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        public string SourceKey { get; }
        internal IReadWriteBackend Backend { get; }
        internal ImmutableArray<FieldCapability> Expected { get; }

        public async IAsyncEnumerable<ImmutableArray<FieldReading>> ReadAllAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Volatile.Read(ref active) == 0) yield break;
                if (!await values.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false)) yield break;
                cancellationToken.ThrowIfCancellationRequested();
                if (!values.Reader.TryRead(out var round)) continue;
                cancellationToken.ThrowIfCancellationRequested();
                if (Volatile.Read(ref active) == 0) yield break;
                yield return round;
            }
        }

        public void Dispose() => owner.Unregister(this);

        internal FieldCollectionBinding BindingFor(FieldReference field) => bindings[field];

        internal IEnumerable<int> IndicesForItem(string item)
        {
            var found = false;
            for (var index = 0; index < Expected.Length; index++)
            {
                if (!bindings[Expected[index].Field].CollectionItemIds.Contains(item, StringComparer.OrdinalIgnoreCase))
                    continue;
                found = true;
                yield return index;
            }

            // The tracker represents a coalesced full capture with one native trigger
            // item that is intentionally distinct from the original collection item IDs.
            if (!found)
                for (var index = 0; index < Expected.Length; index++)
                    yield return index;
        }

        internal void Complete(string item, ulong sequence, bool fresh,
            Dictionary<FieldReference, FieldReading>? readings, HashSet<FieldReference>? queried,
            List<Registration> current)
        {
            if (Volatile.Read(ref active) == 0) return;
            if (!rounds.TryGetValue(sequence, out var round))
                rounds.Add(sequence, round = new Round(Expected.Length));
            if (!round.Items.Add(item)) return;

            var itemIndices = IndicesForItem(item).ToArray();
            var coalesced = itemIndices.Length == Expected.Length && !collectionItems.Contains(item);
            if (coalesced) round.Coalesced = true;
            if (fresh)
            {
                foreach (var index in itemIndices)
                {
                    var field = Expected[index];
                    var binding = bindings[field.Field];
                    if (binding.SourceKind == FieldCollectionSourceKind.SourceQuery
                        && queried?.Contains(field.Field) == true
                        && readings is not null && readings.TryGetValue(field.Field, out var reading))
                        round.Readings[index] = reading;
                    else
                        round.Readings[index] = new(field.Field,
                            binding.SourceKind == FieldCollectionSourceKind.CurrentValue
                                ? FieldObservationStatus.Unsupported
                                : FieldObservationStatus.Unavailable,
                            null, null, field.ReadSource);
                }
            }

            if (!round.Coalesced && collectionItems.Any(collectionItem => !round.Items.Contains(collectionItem))) return;
            rounds.Remove(sequence);
            if (fresh)
            {
                var delivered = round.Readings.Select((reading, index) => reading
                    ?? new FieldReading(Expected[index].Field, FieldObservationStatus.Unavailable,
                        null, null, Expected[index].ReadSource)).ToImmutableArray();
                values.Writer.TryWrite(delivered);
            }
            else current.Add(this);
        }

        internal void PublishCurrent(IReadOnlyDictionary<FieldCapability, FieldReading> readings)
            => values.Writer.TryWrite([.. Expected.Select(field => readings[field])]);

        internal void Retire()
        {
            if (Interlocked.Exchange(ref active, 0) == 0) return;
            rounds.Clear();
            while (values.Reader.TryRead(out _)) { }
            values.Writer.TryComplete();
        }

        private sealed class Round(int count)
        {
            internal readonly HashSet<string> Items = new(StringComparer.OrdinalIgnoreCase);
            internal readonly FieldReading?[] Readings = new FieldReading?[count];
            internal bool Coalesced;
        }
    }
}
