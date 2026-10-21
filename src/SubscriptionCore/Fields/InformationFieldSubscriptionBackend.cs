using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Information;

namespace ResourceManager.Subscription;

/// <summary>Uses the owner's original demand lease and publication stream.</summary>
public sealed class InformationFieldSubscriptionBackend(InformationReadWriteBackend backend,
    Func<string, TimeSpan, IDisposable> acquire,
    Func<string, TimeSpan, CancellationToken, IAsyncEnumerable<DeviceInformationSnapshot>> subscribe) : IFieldSubscriptionBackend
{
    public IDisposable Acquire(string subscriptionId, ImmutableArray<FieldReference> fields, TimeSpan interval)
    {
        _ = backend.Select(fields);
        return acquire(subscriptionId, interval);
    }

    public async IAsyncEnumerable<ImmutableArray<FieldReading>> SubscribeAsync(string subscriptionId,
        ImmutableArray<FieldReference> fields, TimeSpan interval, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var selection = backend.Select(fields);
        await foreach (var snapshot in subscribe(subscriptionId, interval, cancellationToken).WithCancellation(cancellationToken))
            yield return InformationReadWriteBackend.Read(selection, snapshot);
    }
}
