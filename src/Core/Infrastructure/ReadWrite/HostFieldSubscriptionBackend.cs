using System.Collections.Immutable;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite;
using ResourceManager.Subscription;

namespace ResourceManager.App.Infrastructure.ReadWrite;

/// <summary>Routes fields through the metric sampler's original periodic planner.
/// The sampler retains its single tracker, current values and worker lifecycle.</summary>
public sealed class HostFieldSubscriptionBackend(HostReadWriteBackend backend,
    IMetricSnapshotObservationSource observations, IMetricSnapshotPushSource publications) : IFieldSubscriptionBackend
{
    private readonly WindowsHardwareMetricSampler? owner = ResolveOwner(observations, publications);

    public bool SupportsPeriodicDelivery => owner is not null;

    public IDisposable Acquire(string subscriptionId, ImmutableArray<FieldReference> fields, TimeSpan interval)
        => Acquire(subscriptionId, fields, interval, FieldSubscriptionOptions.Default);

    public IDisposable Acquire(string subscriptionId, ImmutableArray<FieldReference> fields, TimeSpan interval,
        FieldSubscriptionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var sampler = RequireOwner();
        var bindings = backend.ResolveBindings(fields);
        return sampler.AcquireFieldSubscription(subscriptionId, fields, bindings, interval, options);
    }

    public IAsyncEnumerable<ImmutableArray<FieldReading>> SubscribeAsync(string subscriptionId,
        ImmutableArray<FieldReference> fields, TimeSpan interval,
        CancellationToken cancellationToken)
        => SubscribeAsync(subscriptionId, fields, interval, FieldSubscriptionOptions.Default, cancellationToken);

    public IAsyncEnumerable<ImmutableArray<FieldReading>> SubscribeAsync(string subscriptionId,
        ImmutableArray<FieldReference> fields, TimeSpan interval, FieldSubscriptionOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        var sampler = RequireOwner();
        var bindings = backend.ResolveBindings(fields);
        return sampler.SubscribeFieldAsync(subscriptionId, fields, bindings, interval, options,
            backend, cancellationToken);
    }

    private WindowsHardwareMetricSampler RequireOwner()
        => owner ?? throw new NotSupportedException(
            "Host metric field subscriptions require the same WindowsHardwareMetricSampler as observation and publication owner.");

    private static WindowsHardwareMetricSampler? ResolveOwner(
        IMetricSnapshotObservationSource observations, IMetricSnapshotPushSource publications)
        => observations is WindowsHardwareMetricSampler sampler && ReferenceEquals(sampler, publications)
            ? sampler : null;
}
