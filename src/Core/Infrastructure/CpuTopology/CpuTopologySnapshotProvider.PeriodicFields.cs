using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using ResourceManager.ReadWrite;
using ResourceManager.Subscription;
using ResourceManager.Subscription.Native;

namespace ResourceManager.App.Infrastructure.CpuTopology;

public sealed partial class CpuTopologySnapshotProvider
{
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
        if (fields.IsDefaultOrEmpty) throw new ArgumentException("Select at least one CPU field.", nameof(fields));
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(backend);
        if (options.SamplingMode is not (FieldSubscriptionSamplingMode.HighestFrequencyShared
            or FieldSubscriptionSamplingMode.FreshOnTrigger))
            throw new ArgumentOutOfRangeException(nameof(options));
        subscriptions.ValidatePeriod(interval);
        ValidateCpuFields(fields, backend);

        var key = $"field:{Interlocked.Increment(ref nextLeaseId)}:{subscriptionId.Trim()}";
        var request = new TopologyRequest(key, cancellationToken);
        NativeFieldSubscriptionDelivery.Registration? registration = null;
        if (registerDelivery)
        {
            var triggerItems = subscriptions.GetDeliveryItemIds(request);
            var bindings = fields.Select(field => new FieldCollectionBinding(field,
                [.. triggerItems], [SnapshotItemId], FieldCollectionSourceKind.SourceQuery)).ToImmutableArray();
            registration = fieldDeliveries.Register(key, backend, fields, bindings);
        }

        try
        {
            lock (publicationGate)
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref ownedResourcesDisposed) != 0, this);
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

    private static void ValidateCpuFields(ImmutableArray<FieldReference> fields, IReadWriteBackend backend)
    {
        var catalog = backend.Catalog;
        foreach (var field in fields)
        {
            _ = catalog.Require(field);
            if (!catalog.Devices.Any(device => device.Identity == field.DeviceIdentity
                && device.Resource.Equals("cpu", StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("The selected field is not owned by the CPU topology backend.", nameof(fields));
        }
    }

    private void ReleaseFieldDemand(string key)
    {
        lock (publicationGate)
        {
            if (Volatile.Read(ref ownedResourcesDisposed) != 0) return;
            subscriptions.Remove(key, DateTimeOffset.UtcNow);
        }
        SignalDemand();
    }

    private sealed class FieldDemandLease(CpuTopologySnapshotProvider owner, string key,
        NativeFieldSubscriptionDelivery.Registration? registration) : IDisposable
    {
        private CpuTopologySnapshotProvider? current = owner;
        public NativeFieldSubscriptionDelivery.Registration? Registration { get; } = registration;

        public void Dispose()
        {
            var provider = Interlocked.Exchange(ref current, null);
            if (provider is null) return;
            try { Registration?.Dispose(); }
            finally { provider.ReleaseFieldDemand(key); }
        }
    }
}
