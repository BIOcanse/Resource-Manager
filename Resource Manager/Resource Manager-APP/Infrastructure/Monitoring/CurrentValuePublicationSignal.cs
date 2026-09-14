using System.Threading.Channels;

namespace ResourceManager.App.Infrastructure.Monitoring;

internal sealed class CurrentValuePublicationSignal
{
    private readonly object gate = new();
    private readonly HashSet<Subscription> subscriptions = [];

    public Subscription Subscribe()
    {
        var subscription = new Subscription(this);
        lock (gate)
        {
            subscriptions.Add(subscription);
        }
        return subscription;
    }

    public void Publish()
    {
        Subscription[] current;
        lock (gate)
        {
            current = [.. subscriptions];
        }
        foreach (var subscription in current)
        {
            subscription.Signal();
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
        private readonly CurrentValuePublicationSignal owner;
        private readonly Channel<byte> channel = Channel.CreateBounded<byte>(
            new BoundedChannelOptions(1)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.DropOldest,
                AllowSynchronousContinuations = false
            });
        private int disposed;

        public Subscription(CurrentValuePublicationSignal owner)
        {
            this.owner = owner;
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

        internal void Signal()
        {
            if (Volatile.Read(ref disposed) == 0)
            {
                channel.Writer.TryWrite(0);
            }
        }
    }
}
