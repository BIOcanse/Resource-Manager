using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.ProcessFields;
using ResourceManager.Subscription;
using ResourceManager.Subscription.Native;

namespace ResourceManager.App.Infrastructure.CpuTopology;

public sealed partial class EtwCpuCoreResidencyReader
{
    private const string FieldPublicationKey = "process.cpu.residency";
    private readonly NativeFieldSubscriptionDelivery fieldDeliveries = new();
    private FieldCollectionResult? pendingFieldCollection;

    public IDisposable AcquireFieldSubscription(string subscriptionId,
        ImmutableArray<FieldReference> fields, TimeSpan interval,
        FieldSubscriptionOptions options, IReadWriteBackend backend)
        => CreateFieldDemand(subscriptionId, fields, interval, options, backend,
            CancellationToken.None, registerDelivery: false);

    public async IAsyncEnumerable<ImmutableArray<FieldReading>> SubscribeFieldsAsync(string subscriptionId,
        ImmutableArray<FieldReference> fields, TimeSpan interval, FieldSubscriptionOptions options,
        IReadWriteBackend backend,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var demand = CreateFieldDemand(subscriptionId, fields, interval, options, backend,
            cancellationToken, registerDelivery: true);
        var registration = demand.Registration
            ?? throw new InvalidOperationException("A field subscription requires a delivery registration.");
        await foreach (var readings in registration.ReadAllAsync(cancellationToken)
            .WithCancellation(cancellationToken).ConfigureAwait(false))
            yield return readings;
    }

    private FieldDemandLease CreateFieldDemand(string subscriptionId,
        ImmutableArray<FieldReference> fields, TimeSpan interval,
        FieldSubscriptionOptions options, IReadWriteBackend backend, CancellationToken cancellationToken,
        bool registerDelivery)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionId);
        if (fields.IsDefaultOrEmpty) throw new ArgumentException("Select at least one CPU residency field.", nameof(fields));
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(backend);
        if (options.SamplingMode is not (FieldSubscriptionSamplingMode.HighestFrequencyShared
            or FieldSubscriptionSamplingMode.FreshOnTrigger))
            throw new ArgumentOutOfRangeException(nameof(options));
        subscriptions.ValidatePeriod(interval);
        ValidateResidencyFields(fields, backend);

        var key = $"field:{Interlocked.Increment(ref nextLeaseId)}:{subscriptionId.Trim()}";
        var request = new CpuResidencySampleRequest(key, cancellationToken);
        NativeFieldSubscriptionDelivery.Registration? registration = null;
        if (registerDelivery)
        {
            var triggerItems = subscriptions.GetDeliveryItemIds(request);
            var bindings = fields.Select(field => new FieldCollectionBinding(field,
                [.. triggerItems], [FieldPublicationKey], FieldCollectionSourceKind.SourceQuery)).ToImmutableArray();
            registration = fieldDeliveries.Register(key, backend, fields, bindings);
        }

        try
        {
            lock (subscriptionGate)
            {
                ObjectDisposedException.ThrowIf(ownedResourcesDisposed, this);
                var flags = NativeSamplingSubscriptionSourceFlags.PeriodicTriggers;
                if (options.SamplingMode == FieldSubscriptionSamplingMode.FreshOnTrigger)
                    flags |= NativeSamplingSubscriptionSourceFlags.FreshOnTrigger;
                if (options.AlignSameItemPhase)
                    flags |= NativeSamplingSubscriptionSourceFlags.AlignSameItemPhase;
                subscriptions.TrackPersistent(request, DateTimeOffset.UtcNow, interval, flags);
            }
        }
        catch
        {
            registration?.Dispose();
            throw;
        }

        SignalDemand();
        return new FieldDemandLease(this, key, registration);
    }

    private static void ValidateResidencyFields(ImmutableArray<FieldReference> fields, IReadWriteBackend backend)
    {
        var catalog = backend.Catalog;
        foreach (var field in fields)
        {
            var capability = catalog.Require(field);
            if (!capability.ReadSource.Equals("windows.etw.cpu-residency", StringComparison.Ordinal)
                || ProcessMetricFieldRegistry.Find(field.FieldName)?.Code != ProcessMetricFieldCode.CpuResidency)
                throw new ArgumentException("The selected field is not owned by ETW CPU residency.", nameof(fields));
        }
    }

    private sealed class FieldDemandLease(EtwCpuCoreResidencyReader owner, string key,
        NativeFieldSubscriptionDelivery.Registration? registration) : IDisposable
    {
        private EtwCpuCoreResidencyReader? current = owner;
        public NativeFieldSubscriptionDelivery.Registration? Registration { get; } = registration;

        public void Dispose()
        {
            var reader = Interlocked.Exchange(ref current, null);
            if (reader is null) return;
            try { Registration?.Dispose(); }
            finally { reader.ReleaseLease(key); }
        }
    }
}
