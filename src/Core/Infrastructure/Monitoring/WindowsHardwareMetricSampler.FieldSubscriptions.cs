using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Fields;
using ResourceManager.Subscription;
using ResourceManager.Subscription.Native;

namespace ResourceManager.App.Infrastructure.Monitoring;

public sealed partial class WindowsHardwareMetricSampler
{
    private readonly NativeFieldSubscriptionDelivery fieldSubscriptionDelivery = new();

    internal IDisposable AcquireFieldSubscription(string subscriptionId,
        ImmutableArray<FieldReference> fields, MetricFieldSelection[] selections, TimeSpan interval,
        FieldSubscriptionOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionId);
        ArgumentNullException.ThrowIfNull(options);
        ValidateFieldSubscription(fields, selections, options);
        subscriptions.ValidatePeriod(interval);

        var request = metricCatalog.SelectSubscribable(Request(selections));
        if (request.IsEmpty) return EmptyObservationSubscription.Instance;

        lock (lifecycleGate)
        {
            ValidateFieldSubscriptionAdmission();
            var sourceKey = CreateFieldSubscriptionSourceKey(subscriptionId);
            return TrackFieldSubscription(sourceKey, request, interval, options);
        }
    }

    internal async IAsyncEnumerable<ImmutableArray<FieldReading>> SubscribeFieldAsync(
        string subscriptionId, ImmutableArray<FieldReference> fields, MetricFieldSelection[] selections,
        TimeSpan interval, FieldSubscriptionOptions options, IReadWriteBackend backend,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionId);
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(options);
        ValidateFieldSubscription(fields, selections, options);
        subscriptions.ValidatePeriod(interval);

        var request = metricCatalog.SelectSubscribable(Request(selections));
        if (request.IsEmpty) yield break;

        var bindings = CreateFieldCollectionBindings(fields, selections);
        NativeFieldSubscriptionDelivery.Registration registration;
        IDisposable lease;
        lock (lifecycleGate)
        {
            ValidateFieldSubscriptionAdmission();
            var sourceKey = CreateFieldSubscriptionSourceKey(subscriptionId);
            registration = fieldSubscriptionDelivery.Register(sourceKey, backend, fields, bindings);
            try
            {
                lease = TrackFieldSubscription(sourceKey, request, interval, options);
            }
            catch
            {
                registration.Dispose();
                throw;
            }
        }
        try
        {
            await foreach (var readings in registration.ReadAllAsync(cancellationToken)
                .WithCancellation(cancellationToken).ConfigureAwait(false))
                yield return readings;
        }
        finally
        {
            registration.Dispose();
            lease.Dispose();
        }
    }

    private static void ValidateFieldSubscription(ImmutableArray<FieldReference> fields,
        MetricFieldSelection[] selections, FieldSubscriptionOptions options)
    {
        if (fields.IsDefaultOrEmpty) throw new ArgumentException("Select at least one field.", nameof(fields));
        ArgumentNullException.ThrowIfNull(selections);
        if (fields.Length != selections.Length)
            throw new ArgumentException("Every selected field needs its original metric binding.", nameof(selections));
        if (options.SamplingMode is not (FieldSubscriptionSamplingMode.HighestFrequencyShared
            or FieldSubscriptionSamplingMode.FreshOnTrigger))
            throw new ArgumentOutOfRangeException(nameof(options), "The field sampling mode is unknown.");
    }

    private static MetricSampleRequest Request(MetricFieldSelection[] selections)
        => MetricSampleRequest.ForIds(selections.Select(static item => item.Binding.RequestAddress));

    private ImmutableArray<FieldCollectionBinding> CreateFieldCollectionBindings(
        ImmutableArray<FieldReference> fields, MetricFieldSelection[] selections)
        => [.. selections.Select((selection, index) =>
        {
            var request = MetricSampleRequest.ForIds([selection.Binding.RequestAddress]);
            return new FieldCollectionBinding(fields[index],
                [.. subscriptions.GetDeliveryItemIds(request)],
                [selection.Binding.EffectiveSampleRequestAddress],
                FieldCollectionSourceKind.SourceQuery);
        })];

    private string CreateFieldSubscriptionSourceKey(string subscriptionId)
    {
        var leaseId = Interlocked.Increment(ref nextObservationLeaseId);
        return $"lease:{leaseId}:{subscriptionId.Trim()}";
    }

    private IDisposable TrackFieldSubscription(string sourceKey, MetricSampleRequest request,
        TimeSpan interval, FieldSubscriptionOptions options)
    {
        var flags = NativeSamplingSubscriptionSourceFlags.PeriodicTriggers;
        if (options.SamplingMode == FieldSubscriptionSamplingMode.FreshOnTrigger)
            flags |= NativeSamplingSubscriptionSourceFlags.FreshOnTrigger;
        if (options.AlignSameItemPhase)
            flags |= NativeSamplingSubscriptionSourceFlags.AlignSameItemPhase;
        subscriptions.TrackPersistent(sourceKey, request, DateTimeOffset.UtcNow, interval, flags);
        SignalDemand(demandSignal);
        return new ObservationSubscriptionLease(this, sourceKey);
    }

    private void ValidateFieldSubscriptionAdmission()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (samplingStopInProgress)
            throw new InvalidOperationException("The hardware sampling worker is stopping; field demand cannot be admitted.");
    }
}
