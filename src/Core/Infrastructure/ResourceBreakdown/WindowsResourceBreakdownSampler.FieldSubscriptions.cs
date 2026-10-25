using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.ProcessFields;
using ResourceManager.Subscription;
using ResourceManager.Subscription.Native;

namespace ResourceManager.App.Infrastructure.ResourceBreakdown;

public sealed partial class WindowsResourceBreakdownSampler
{
    private readonly NativeFieldSubscriptionDelivery fieldSubscriptionDelivery = new();
    private FieldCollectionResult? pendingFieldCollectionResult;

    internal IDisposable AcquireFieldSubscription(string subscriptionId,
        ImmutableArray<FieldReference> fields, ProcessFieldReadWriteBackend.Selection[] selections,
        TimeSpan interval, FieldSubscriptionOptions options, IReadWriteBackend backend)
        => AddFieldDemand(subscriptionId, fields, selections, interval, options, backend,
            registerDelivery: false).Lease;

    internal async IAsyncEnumerable<ImmutableArray<FieldReading>> SubscribeFieldAsync(
        string subscriptionId, ImmutableArray<FieldReference> fields,
        ProcessFieldReadWriteBackend.Selection[] selections, TimeSpan interval,
        FieldSubscriptionOptions options, IReadWriteBackend backend,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var demand = AddFieldDemand(subscriptionId, fields, selections, interval, options, backend,
            registerDelivery: true);
        var registration = demand.Registration;
        if (registration is null) yield break;

        using (demand.Lease)
        using (registration)
        {
            await foreach (var readings in registration.ReadAllAsync(cancellationToken)
                .WithCancellation(cancellationToken).ConfigureAwait(false))
                yield return readings;
        }
    }

    private FieldDemand AddFieldDemand(string subscriptionId,
        ImmutableArray<FieldReference> fields, ProcessFieldReadWriteBackend.Selection[] selections,
        TimeSpan interval, FieldSubscriptionOptions options, IReadWriteBackend backend,
        bool registerDelivery)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionId);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(backend);
        if (fields.IsDefaultOrEmpty)
            throw new ArgumentException("Select at least one canonical process field.", nameof(fields));
        ArgumentNullException.ThrowIfNull(selections);
        if (selections.Length != fields.Length
            || selections.Where((selection, index) => selection.Field != fields[index]).Any())
            throw new ArgumentException("Each selected field must retain its exact original process binding.", nameof(selections));
        if (selections.Any(static selection =>
                selection.Definition.DatasetId == ProcessMetricDatasetIds.ObservedCpuResidency))
            throw new ArgumentException(
                "CPU residency is owned by the Role 6 CPU residency backend and cannot be requested from the Role 3 process sampler.",
                nameof(fields));
        if (options.SamplingMode is not (FieldSubscriptionSamplingMode.HighestFrequencyShared
            or FieldSubscriptionSamplingMode.FreshOnTrigger))
            throw new ArgumentOutOfRangeException(nameof(options), "The field sampling mode is unknown.");
        subscriptions.ValidatePeriod(interval);

        var request = SelectSubscribable(ResourceBreakdownSampleRequest.ForResourceTable(
            selections.Select(static selection => selection.Definition.Name).ToArray(),
            new Dictionary<string, string>()));
        if (request.MetricIds.Count == 0 || !GetSubscriptionDatasetIds(request).Any())
            return new(null, EmptyObservationSubscription.Instance);

        lock (lifecycleGate)
        {
            ValidateFieldSubscriptionAdmission();
            var sourceKey = CreateFieldSubscriptionSourceKey(subscriptionId);
            NativeFieldSubscriptionDelivery.Registration? registration = null;
            try
            {
                if (registerDelivery)
                    registration = fieldSubscriptionDelivery.Register(sourceKey, backend, fields,
                        CreateFieldCollectionBindings(fields, selections, request));
                var flags = FieldSamplingFlags(options, registerDelivery);
                var lease = AcquireHostedSubscription(sourceKey, request, interval, flags, registration);
                return new(registration, lease);
            }
            catch
            {
                registration?.Dispose();
                throw;
            }
        }
    }

    private ImmutableArray<FieldCollectionBinding> CreateFieldCollectionBindings(
        ImmutableArray<FieldReference> fields, ProcessFieldReadWriteBackend.Selection[] selections,
        ResourceBreakdownSampleRequest request)
    {
        var itemIds = subscriptions.GetDeliveryItemIds(request);
        return [.. selections.Select((selection, index) => new FieldCollectionBinding(fields[index],
            [.. itemIds], [selection.Definition.DatasetId],
            FieldCollectionSourceKind.SourceQuery))];
    }

    private static NativeSamplingSubscriptionSourceFlags FieldSamplingFlags(
        FieldSubscriptionOptions options, bool registerDelivery)
    {
        var flags = NativeSamplingSubscriptionSourceFlags.None;
        if (registerDelivery || options.SamplingMode == FieldSubscriptionSamplingMode.FreshOnTrigger)
            flags |= NativeSamplingSubscriptionSourceFlags.PeriodicTriggers;
        if (options.SamplingMode == FieldSubscriptionSamplingMode.FreshOnTrigger)
            flags |= NativeSamplingSubscriptionSourceFlags.FreshOnTrigger;
        if (options.AlignSameItemPhase)
            flags |= NativeSamplingSubscriptionSourceFlags.AlignSameItemPhase
                | NativeSamplingSubscriptionSourceFlags.PeriodicTriggers;
        return flags;
    }

    private string CreateFieldSubscriptionSourceKey(string subscriptionId)
    {
        var leaseId = Interlocked.Increment(ref nextObservationLeaseId);
        return $"lease:{leaseId}:{subscriptionId.Trim()}";
    }

    private void ValidateFieldSubscriptionAdmission()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (samplingStopInProgress)
            throw new InvalidOperationException(
                "The resource breakdown worker is stopping; field demand cannot be admitted.");
    }

    private IDisposable AcquireHostedSubscription(string sourceKey,
        ResourceBreakdownSampleRequest request, TimeSpan interval,
        NativeSamplingSubscriptionSourceFlags flags, IDisposable? fieldDeliveryLease)
    {
        IDisposable? hardwareLease = null;
        IDisposable? allocationLease = null;
        try
        {
            var hardwareRequest = CreateHardwareRequest(request);
            if (!hardwareRequest.IsEmpty)
            {
                var observationSource = metricSampler as IMetricSnapshotObservationSource
                    ?? throw new InvalidOperationException(
                        "Hosted resource subscriptions require a hosted hardware observation source.");
                hardwareLease = observationSource.AcquireSubscription(
                    $"resource-breakdown:{sourceKey}", hardwareRequest, interval);
            }
            if (NeedsGpuAllocations(request))
                allocationLease = gpuAllocations?.AcquireSubscription(interval);
            subscriptions.TrackPersistent(sourceKey, request, DateTimeOffset.UtcNow, interval, flags);
            SignalDemand();
            return new ObservationSubscriptionLease(this, sourceKey, hardwareLease, allocationLease,
                fieldDeliveryLease);
        }
        catch
        {
            hardwareLease?.Dispose();
            allocationLease?.Dispose();
            fieldDeliveryLease?.Dispose();
            throw;
        }
    }

    private sealed record FieldDemand(
        NativeFieldSubscriptionDelivery.Registration? Registration,
        IDisposable Lease);
}
