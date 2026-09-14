using System.Threading.Channels;

namespace ResourceManager.App.Infrastructure.Monitoring;

internal sealed class SamplingPublicationSignal
{
    private readonly object gate = new();
    private readonly HashSet<Subscription> subscriptions = [];

    public Subscription Subscribe(IEnumerable<string> datasetIds)
    {
        ArgumentNullException.ThrowIfNull(datasetIds);
        var subscription = new Subscription(this, datasetIds);
        lock (gate)
        {
            subscriptions.Add(subscription);
        }
        return subscription;
    }

    public void Publish(IEnumerable<string> datasetIds)
    {
        ArgumentNullException.ThrowIfNull(datasetIds);
        var published = datasetIds
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (published.Count == 0)
        {
            return;
        }

        Subscription[] current;
        lock (gate)
        {
            current = [.. subscriptions];
        }
        foreach (var subscription in current)
        {
            subscription.SignalIfMatches(published);
        }
    }

    private void Remove(Subscription subscription)
    {
        lock (gate)
        {
            subscriptions.Remove(subscription);
        }
    }

    internal sealed class Subscription : IDisposable
    {
        private readonly SamplingPublicationSignal owner;
        private readonly HashSet<string> datasetIds;
        private readonly Channel<byte> channel = Channel.CreateBounded<byte>(
            new BoundedChannelOptions(1)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.DropOldest,
                AllowSynchronousContinuations = false
            });
        private int disposed;

        public Subscription(
            SamplingPublicationSignal owner,
            IEnumerable<string> datasetIds)
        {
            this.owner = owner;
            this.datasetIds = datasetIds
                .Where(static id => !string.IsNullOrWhiteSpace(id))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        public async ValueTask WaitAsync(CancellationToken cancellationToken)
        {
            _ = await channel.Reader.ReadAsync(cancellationToken);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }
            owner.Remove(this);
            channel.Writer.TryComplete();
        }

        internal void SignalIfMatches(IReadOnlySet<string> published)
        {
            if (Volatile.Read(ref disposed) == 0
                && datasetIds.Overlaps(published))
            {
                channel.Writer.TryWrite(0);
            }
        }
    }
}
