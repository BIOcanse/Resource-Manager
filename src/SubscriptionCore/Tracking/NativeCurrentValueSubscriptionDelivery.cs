using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace ResourceManager.Subscription;

/// <summary>Delivers one current value after all original member triggers in a native round settle.
/// Owns no clock, sampling demand or current-value cache.</summary>
public sealed class NativeCurrentValueSubscriptionDelivery<T>
{
    private readonly object gate = new();
    private readonly Dictionary<string, Registration> registrations = new(StringComparer.Ordinal);

    public Registration Register(string sourceKey, IEnumerable<string> itemIds, Func<T> readCurrent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceKey);
        ArgumentNullException.ThrowIfNull(itemIds);
        ArgumentNullException.ThrowIfNull(readCurrent);
        var items = itemIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (items.Count == 0 || items.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Bind the original native delivery items.", nameof(itemIds));
        var registration = new Registration(this, sourceKey, items, readCurrent);
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

    public void Deliver<TRequest>(ImmutableArray<NativeItemSamplingSubscriptionTrigger<TRequest>> triggers)
    {
        if (triggers.IsDefaultOrEmpty) return;
        lock (gate)
        {
            foreach (var trigger in triggers)
                if (!trigger.IsFresh && registrations.TryGetValue(trigger.SourceKey, out var registration))
                    registration.Complete(trigger.ItemId, trigger.Sequence);
        }
    }

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

    public sealed class Registration : IDisposable
    {
        private readonly NativeCurrentValueSubscriptionDelivery<T> owner;
        private readonly HashSet<string> items;
        private readonly Func<T> readCurrent;
        private readonly Dictionary<ulong, HashSet<string>> rounds = [];
        private readonly Channel<T> values = Channel.CreateBounded<T>(new BoundedChannelOptions(1)
        {
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.DropOldest
        });
        private int active = 1;

        internal Registration(NativeCurrentValueSubscriptionDelivery<T> owner, string sourceKey,
            HashSet<string> items, Func<T> readCurrent)
        {
            this.owner = owner;
            SourceKey = sourceKey;
            this.items = items;
            this.readCurrent = readCurrent;
        }

        internal string SourceKey { get; }

        public async IAsyncEnumerable<T> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Volatile.Read(ref active) == 0) yield break;
                if (!await values.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false)) yield break;
                cancellationToken.ThrowIfCancellationRequested();
                if (!values.Reader.TryRead(out var value)) continue;
                cancellationToken.ThrowIfCancellationRequested();
                if (Volatile.Read(ref active) == 0) yield break;
                yield return value;
            }
        }

        internal void Complete(string item, ulong sequence)
        {
            if (Volatile.Read(ref active) == 0 || !items.Contains(item)) return;
            if (!rounds.TryGetValue(sequence, out var completed))
                rounds.Add(sequence, completed = new(StringComparer.OrdinalIgnoreCase));
            if (!completed.Add(item) || !items.IsSubsetOf(completed)) return;
            rounds.Remove(sequence);
            values.Writer.TryWrite(readCurrent());
        }

        internal void Retire()
        {
            if (Interlocked.Exchange(ref active, 0) == 0) return;
            rounds.Clear();
            while (values.Reader.TryRead(out _)) { }
            values.Writer.TryComplete();
        }

        public void Dispose() => owner.Unregister(this);
    }
}
