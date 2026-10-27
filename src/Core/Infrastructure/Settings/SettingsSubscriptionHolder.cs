using System.Runtime.CompilerServices;
using System.Threading.Channels;
using ResourceManager.Shared.BusinessBlocks.State;

namespace ResourceManager.App.Infrastructure.Settings;

/// <summary>A bounded transport holder; the state directory remains the only change-event path.</summary>
internal sealed class SettingsSubscriptionHolder<T> : IDisposable
{
    private readonly Channel<T> values = Channel.CreateBounded<T>(new BoundedChannelOptions(16)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.Wait
    });
    private readonly IDisposable subscription;

    // The settings owner's write gate orders initial read, registration and later commits.
    internal SettingsSubscriptionHolder(StateAuthority authority, StateItemKey key, string holder)
    {
        values.Writer.TryWrite(authority.Read<T>(key).Value);
        subscription = authority.Subscribe(key, holder, _ =>
        {
            var current = authority.Read<T>(key);
            if (!current.HasValue)
                values.Writer.TryComplete(new IOException($"Settings source has no current value ({current.NoValueReason})."));
            else if (!values.Writer.TryWrite(current.Value))
                values.Writer.TryComplete(new IOException("Settings subscription holder exceeded its pending-value limit."));
            return Task.CompletedTask;
        });
    }

    internal async IAsyncEnumerable<T> ReadAsync([EnumeratorCancellation] CancellationToken token)
    {
        await foreach (var value in values.Reader.ReadAllAsync(token)) yield return value;
    }

    public void Dispose()
    {
        subscription.Dispose();
        values.Writer.TryComplete();
    }
}
