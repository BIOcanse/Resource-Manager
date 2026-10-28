using System.Collections.Immutable;
using ResourceManager.ReadWrite;

namespace Resource_Manager_APP.Tests;

// Faults the actual producer's current-value projection, after registration and
// source setup. This leaves the consumer waiting across a failed producer run.
internal sealed class ProducerSubscriptionCurrentBackend(IReadWriteBackend inner) : IReadWriteBackend
{
    internal const string FailureMessage = "Recorded producer current projection failed.";
    private int failReads = 1;
    public ReadWriteCatalog Catalog { get; } = inner.Catalog;

    internal void AllowReads() => Volatile.Write(ref failReads, 0);

    public FieldReading ReadCached(FieldReference field)
        => Volatile.Read(ref failReads) != 0
            ? throw new InvalidOperationException(FailureMessage)
            : inner.ReadCached(field);

    public ValueTask<ReadWriteCatalog> DiscoverAsync(CancellationToken cancellationToken)
        => inner.DiscoverAsync(cancellationToken);

    public ValueTask<ImmutableArray<FieldReading>> CollectAsync(ImmutableArray<FieldReference> fields,
        CancellationToken cancellationToken) => inner.CollectAsync(fields, cancellationToken);

    public ValueTask<FieldWriteResult> WriteAsync(FieldReference field, FieldValue value,
        CancellationToken cancellationToken) => inner.WriteAsync(field, value, cancellationToken);
}
