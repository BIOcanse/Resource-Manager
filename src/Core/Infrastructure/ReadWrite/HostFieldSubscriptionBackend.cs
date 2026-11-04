using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite;
using ResourceManager.Subscription;
using ResourceManager.App.Infrastructure.Control.Writers;
using ResourceManager.ReadWrite.Fields;
using ResourceManager.ReadWrite.Windows.Metrics.Writes;

namespace ResourceManager.App.Infrastructure.ReadWrite;

/// <summary>Routes fields through the metric sampler's original periodic planner.
/// The sampler retains its single tracker, current values and worker lifecycle.</summary>
public sealed class HostFieldSubscriptionBackend(HostReadWriteBackend backend,
    IMetricSnapshotObservationSource observations, IMetricSnapshotPushSource publications,
    HardwareBridgeClient? bridge = null) : IFieldSubscriptionBackend
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
        var sampling = sampler.AcquireFieldSubscription(subscriptionId, fields, bindings, interval, options);
        try { return new Subscription(sampling, AcquireBridgeReadings(bindings)); }
        catch { sampling.Dispose(); throw; }
    }

    public IAsyncEnumerable<ImmutableArray<FieldReading>> SubscribeAsync(string subscriptionId,
        ImmutableArray<FieldReference> fields, TimeSpan interval,
        CancellationToken cancellationToken)
        => SubscribeAsync(subscriptionId, fields, interval, FieldSubscriptionOptions.Default, cancellationToken);

    public async IAsyncEnumerable<ImmutableArray<FieldReading>> SubscribeAsync(string subscriptionId,
        ImmutableArray<FieldReference> fields, TimeSpan interval, FieldSubscriptionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        var sampler = RequireOwner();
        var bindings = backend.ResolveBindings(fields);
        using var readings = AcquireBridgeReadings(bindings);
        await foreach (var values in sampler.SubscribeFieldAsync(subscriptionId, fields, bindings, interval, options,
            backend, cancellationToken).ConfigureAwait(false)) yield return values;
    }

    private IDisposable? AcquireBridgeReadings(MetricFieldSelection[] bindings)
        => bindings.Any(selection => WindowsMetricControlBindings.TryFind(selection.Binding)?.ReadDependencies
            .Contains("hardware-bridge") == true)
            ? bridge?.SubscribeReadings() : null;

    private sealed class Subscription(IDisposable sampling, IDisposable? readings) : IDisposable
    {
        public void Dispose() { try { sampling.Dispose(); } finally { readings?.Dispose(); } }
    }

    private WindowsHardwareMetricSampler RequireOwner()
        => owner ?? throw new NotSupportedException(
            "Host metric field subscriptions require the same WindowsHardwareMetricSampler as observation and publication owner.");

    private static WindowsHardwareMetricSampler? ResolveOwner(
        IMetricSnapshotObservationSource observations, IMetricSnapshotPushSource publications)
        => observations is WindowsHardwareMetricSampler sampler && ReferenceEquals(sampler, publications)
            ? sampler : null;
}
