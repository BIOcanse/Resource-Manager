using ResourceManager.ReadWrite.Vendors.Nvidia;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using ResourceManager.App.Infrastructure.DiskUsage;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Windows.SystemInformation;
using ResourceManager.Subscription;
using ResourceManager.Subscription.Native;

namespace ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;

internal enum DeviceTopologyFieldScope { Snapshot, Acpi, Software, Services, OperatingSystem, PassiveCurrent, NvidiaDrs, NetworkEndpoints }

public sealed partial class DeviceTopologySnapshotProvider
{
    private readonly NativeFieldSubscriptionDelivery fieldSubscriptionDelivery = new();
    // These identities are bound once from typed definitions, never inferred from field names on the sampling path.
    private static readonly (WindowsOperatingSystemInformationFields Field, string Key)[] operatingSystemQueryKeys =
    [
        (WindowsOperatingSystemInformationFields.Uptime, "os.query.uptime"),
        (WindowsOperatingSystemInformationFields.Build, "os.query.build"),
        (WindowsOperatingSystemInformationFields.BootTime, "os.query.boot-time"),
        (WindowsOperatingSystemInformationFields.Activation, "os.query.activation"),
        (WindowsOperatingSystemInformationFields.Environment, "os.query.environment"),
        (WindowsOperatingSystemInformationFields.Startup, "os.query.startup"),
        (WindowsOperatingSystemInformationFields.Packages, "os.query.packages"),
        (WindowsOperatingSystemInformationFields.SessionId, "os.query.session"),
        (WindowsOperatingSystemInformationFields.AcPower, "os.query.ac-power"),
        (WindowsOperatingSystemInformationFields.KeyboardRemap, "os.query.keyboard-remap"),
        (WindowsOperatingSystemInformationFields.Features, "os.query.features"),
        (WindowsOperatingSystemInformationFields.ComponentPackages, "os.query.component-packages"),
        (WindowsOperatingSystemInformationFields.DriverPackages, "os.query.driver-packages")
    ];

    internal IDisposable AcquireFieldSubscription(string id, ImmutableArray<FieldReference> fields, TimeSpan interval,
        FieldSubscriptionOptions options, InformationReadWriteBackend backend, DeviceTopologyFieldScope scope,
        NvidiaDrsInformationRequest? nvidiaDrsRequest = null)
        => AddFieldDemand(id, fields, interval, options, backend, scope, false, default, nvidiaDrsRequest).Lease;

    internal async IAsyncEnumerable<ImmutableArray<FieldReading>> SubscribeFieldsAsync(string id,
        ImmutableArray<FieldReference> fields, TimeSpan interval, FieldSubscriptionOptions options,
        InformationReadWriteBackend backend, DeviceTopologyFieldScope scope,
        [EnumeratorCancellation] CancellationToken cancellationToken, NvidiaDrsInformationRequest? nvidiaDrsRequest = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var demand = AddFieldDemand(id, fields, interval, options, backend, scope, true, cancellationToken, nvidiaDrsRequest);
        using (demand.Lease)
            await foreach (var value in demand.Registration!.ReadAllAsync(cancellationToken))
                yield return value;
    }

    private (IDisposable Lease, NativeFieldSubscriptionDelivery.Registration? Registration) AddFieldDemand(
        string id, ImmutableArray<FieldReference> fields, TimeSpan interval, FieldSubscriptionOptions options,
        InformationReadWriteBackend backend, DeviceTopologyFieldScope publicScope, bool deliver, CancellationToken token,
        NvidiaDrsInformationRequest? nvidiaDrsRequest = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(options);
        if (fields.IsDefaultOrEmpty) throw new ArgumentException("Select at least one exact field.", nameof(fields));
        if (options.SamplingMode is not (FieldSubscriptionSamplingMode.HighestFrequencyShared or FieldSubscriptionSamplingMode.FreshOnTrigger))
            throw new ArgumentOutOfRangeException(nameof(options));
        token.ThrowIfCancellationRequested();
        var selection = backend.Select(fields);
        var scope = publicScope switch
        {
            DeviceTopologyFieldScope.Snapshot => DeviceTopologySampleScope.Snapshot,
            DeviceTopologyFieldScope.Acpi => DeviceTopologySampleScope.Acpi,
            DeviceTopologyFieldScope.Software => DeviceTopologySampleScope.Software,
            DeviceTopologyFieldScope.Services => DeviceTopologySampleScope.Services,
            DeviceTopologyFieldScope.OperatingSystem => DeviceTopologySampleScope.OperatingSystem,
            DeviceTopologyFieldScope.PassiveCurrent => DeviceTopologySampleScope.PassiveCurrent,
            DeviceTopologyFieldScope.NetworkEndpoints => DeviceTopologySampleScope.NetworkEndpoints,
            DeviceTopologyFieldScope.NvidiaDrs => DeviceTopologySampleScope.NvidiaDrs,
            _ => throw new ArgumentOutOfRangeException(nameof(publicScope))
        };
        if (scope == DeviceTopologySampleScope.Software && SoftwareInventory is null)
            throw new InvalidOperationException("No software inventory is bound to this provider.");
        var osFields = scope == DeviceTopologySampleScope.OperatingSystem
            ? WindowsOperatingSystemInformationOwner.SelectFields(selection) : WindowsOperatingSystemInformationFields.None;
        lock (publicationGate)
        {
            ValidateSubscriptionAdmission();
            subscriptions.ValidatePeriod(interval);
            var key = $"lease:{Interlocked.Increment(ref nextLeaseId)}:{id.Trim()}";
            var request = new DeviceTopologySampleRequest(key, scope, token,
                IncludeServiceConfiguration: scope == DeviceTopologySampleScope.Services
                    && selection.Any(static field => field.Definition.Name == "service_all_start_type_configured"),
                OperatingSystemFields: osFields,
                NvidiaDrsRequests: scope == DeviceTopologySampleScope.NvidiaDrs
                    ? [nvidiaDrsRequest?.Select(selection) ?? throw new ArgumentNullException(nameof(nvidiaDrsRequest))] : []);
            var items = subscriptions.GetDeliveryItemIds(request).ToImmutableArray();
            var bindings = selection.Select(field => new FieldCollectionBinding(field.Field, items,
                scope == DeviceTopologySampleScope.NvidiaDrs ? [field.Field.DeviceIdentity]
                    : scope == DeviceTopologySampleScope.OperatingSystem
                    ? [OperatingSystemPublicationKey(WindowsOperatingSystemInformationOwner.SelectFields([field]))]
                    : items,
                scope is DeviceTopologySampleScope.PassiveCurrent or DeviceTopologySampleScope.Acpi
                    || (scope == DeviceTopologySampleScope.OperatingSystem
                        && WindowsOperatingSystemInformationOwner.IsFixedDeclaration(WindowsOperatingSystemInformationOwner.SelectFields([field])))
                    ? FieldCollectionSourceKind.CurrentValue : FieldCollectionSourceKind.SourceQuery))
                .ToImmutableArray();
            var registration = deliver ? fieldSubscriptionDelivery.Register(key, backend, fields, bindings) : null;
            var flags = NativeSamplingSubscriptionSourceFlags.None;
            if (deliver || options.SamplingMode == FieldSubscriptionSamplingMode.FreshOnTrigger || options.AlignSameItemPhase)
                flags |= NativeSamplingSubscriptionSourceFlags.PeriodicTriggers;
            if (options.SamplingMode == FieldSubscriptionSamplingMode.FreshOnTrigger)
                flags |= NativeSamplingSubscriptionSourceFlags.FreshOnTrigger;
            if (options.AlignSameItemPhase) flags |= NativeSamplingSubscriptionSourceFlags.AlignSameItemPhase;
            try { subscriptions.TrackPersistent(request, DateTimeOffset.UtcNow, interval, flags); }
            catch { registration?.Dispose(); throw; }
            SignalDemand();
            return (new FieldDemandLease(this, key, registration), registration);
        }
    }

    private bool IsHostedActive(SamplingOwnerToken token)
    {
        lock (publicationGate)
            return Volatile.Read(ref disposed) == 0 && samplingCts is { IsCancellationRequested: false } && token.IsActive;
    }

    private void ValidateSubscriptionAdmission()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (samplingCts?.IsCancellationRequested == true)
            throw new InvalidOperationException("The device sampling worker is stopped or draining.");
    }

    private IDisposable TrackInformationDemand(DeviceTopologySampleRequest request, TimeSpan interval)
    {
        lock (publicationGate)
        {
            ValidateSubscriptionAdmission();
            subscriptions.TrackPersistent(request, DateTimeOffset.UtcNow, interval);
            SignalDemand();
            return new InformationLease(this, request.SourceKey);
        }
    }

    private bool TryPublishHosted(SamplingOwnerToken token, Action publish, Action? committed = null)
    {
        lock (publicationGate)
        {
            if (Volatile.Read(ref disposed) != 0 || samplingCts is not { IsCancellationRequested: false }) return false;
            return token.TryPublish(() => { publish(); committed?.Invoke(); });
        }
    }

    private FieldCollectionResult CaptureFieldResult(ImmutableArray<NativeItemSamplingSubscriptionTrigger<DeviceTopologySampleRequest>> triggers,
        DeviceTopologySampleScope scope, IReadOnlySet<string> queriedKeys)
    {
        var item = SamplingItemIds(scope).Single();
        return fieldSubscriptionDelivery.CaptureCompleted(
            triggers.Where(trigger => string.Equals(trigger.ItemId, item, StringComparison.OrdinalIgnoreCase)).ToImmutableArray(),
            queriedKeys, committed: true);
    }

    private static FieldCollectionResult MergeFieldCapture(FieldCollectionResult? previous, FieldCollectionResult result)
        => previous is null ? result : new(previous.Readings.AddRange(result.Readings),
            previous.Committed || result.Committed, previous.QueriedFields.AddRange(result.QueriedFields).Distinct().ToImmutableArray());

    private static string OperatingSystemPublicationKey(WindowsOperatingSystemInformationFields field)
        => operatingSystemQueryKeys.First(pair => pair.Field == field).Key;

    private static IReadOnlySet<string> OperatingSystemPublicationKeys(WindowsOperatingSystemInformationFields fields)
        => operatingSystemQueryKeys.Where(pair => (fields & pair.Field) != 0).Select(static pair => pair.Key)
            .ToHashSet(StringComparer.Ordinal);

    private sealed class FieldDemandLease(DeviceTopologySnapshotProvider provider, string key,
        NativeFieldSubscriptionDelivery.Registration? registration) : IDisposable
    {
        private DeviceTopologySnapshotProvider? owner = provider;
        public void Dispose()
        {
            var current = Interlocked.Exchange(ref owner, null);
            if (current is null) return;
            registration?.Dispose();
            if (Volatile.Read(ref current.disposed) != 0) return;
            current.subscriptions.Remove(key, DateTimeOffset.UtcNow);
            current.SignalDemand();
        }
    }
}
