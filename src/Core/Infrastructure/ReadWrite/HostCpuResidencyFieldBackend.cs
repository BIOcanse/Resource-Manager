using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using ResourceManager.App.Infrastructure.CpuTopology;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.ProcessFields;
using ResourceManager.Subscription;
namespace ResourceManager.App.Infrastructure.ReadWrite;

/// <summary>Canonical pure view of the original role6 current; original worker owns sampling and leases.</summary>
public sealed class HostCpuResidencyFieldBackend : IReadWriteBackend, IFieldSubscriptionBackend
{
    public bool SupportsPeriodicDelivery => true;
    private readonly EtwCpuCoreResidencyReader reader;
    private readonly ProcessFieldReadWriteBackend backend;
    public HostCpuResidencyFieldBackend(EtwCpuCoreResidencyReader reader)
    {
        this.reader = reader;
        backend = new(() => [reader.ReadCanonical()], _ => reader.ReadCanonical(),
            () => reader.CanonicalRevision, reader.CaptureCanonicalAsync);
    }
    public ReadWriteCatalog Catalog => backend.Catalog;
    public async ValueTask<ReadWriteCatalog> DiscoverAsync(CancellationToken cancellationToken)
    {
        await reader.CaptureCanonicalAsync(["process_all_cpu_residency_attributed"], cancellationToken);
        return Catalog;
    }
    public FieldReading ReadCached(FieldReference field)
    {
        var selected = backend.Select([field]).Single();
        return Scalar(selected, reader.Read());
    }
    private static FieldReading Scalar(ProcessFieldReadWriteBackend.Selection selected,
        ResourceManager.App.Domain.CpuTopology.CpuCoreResidencySnapshot? snapshot)
        => WindowsCpuResidencyProcessFields.ProjectReading(selected.Field,
            new(selected.ProcessId, long.Parse(selected.Creation, System.Globalization.CultureInfo.InvariantCulture)),
            selected.LogicalProcessor!, snapshot);
    public ValueTask<ImmutableArray<FieldReading>> CollectAsync(ImmutableArray<FieldReference> fields, CancellationToken cancellationToken)
        => backend.CollectAsync(fields, cancellationToken);
    public ValueTask<FieldWriteResult> WriteAsync(FieldReference field, FieldValue value, CancellationToken cancellationToken)
        => backend.WriteAsync(field, value, cancellationToken);
    public IDisposable Acquire(string id, ImmutableArray<FieldReference> fields, TimeSpan interval)
    { _ = backend.Select(fields); return reader.AcquireSubscription(id, interval); }

    public IDisposable Acquire(string id, ImmutableArray<FieldReference> fields, TimeSpan interval,
        FieldSubscriptionOptions options)
    {
        ValidateResidencyFields(fields);
        return reader.AcquireFieldSubscription(id, fields, interval, options, this);
    }

    public async IAsyncEnumerable<ImmutableArray<FieldReading>> SubscribeAsync(string id, ImmutableArray<FieldReference> fields,
        TimeSpan interval, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var selected = backend.Select(fields); // retain original bindings through retirement
        await foreach (var snapshot in reader.SubscribeAsync(id, interval, cancellationToken))
        {
            yield return [.. selected.Select(item => Scalar(item, snapshot))];
        }
    }

    public IAsyncEnumerable<ImmutableArray<FieldReading>> SubscribeAsync(string id,
        ImmutableArray<FieldReference> fields, TimeSpan interval, FieldSubscriptionOptions options,
        CancellationToken cancellationToken)
    {
        ValidateResidencyFields(fields);
        return reader.SubscribeFieldsAsync(id, fields, interval, options, this, cancellationToken);
    }

    private void ValidateResidencyFields(ImmutableArray<FieldReference> fields)
    {
        var selected = backend.Select(fields);
        if (selected.Any(static item => item.Definition.Code != ProcessMetricFieldCode.CpuResidency))
            throw new ArgumentException("Only canonical CPU residency fields can use this periodic source.", nameof(fields));
    }
}
