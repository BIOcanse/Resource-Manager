using ResourceManager.ReadWrite.Windows.Devices;
using ResourceManager.ReadWrite.Vendors.Nvidia;
using ResourceManager.ReadWrite.Windows.Devices.Snapshots;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ResourceManager.App.Application.DeviceTopology;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.Software;
using ResourceManager.ReadWrite.Windows.Services;
using ResourceManager.ReadWrite.Windows.SystemInformation;
using System.Collections.Immutable;
using ResourceManager.ReadWrite;
using ResourceManager.App.Infrastructure.DiskUsage;
using ResourceManager.App.Infrastructure.Adaptation;

namespace ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;

public sealed partial class DeviceTopologySnapshotProvider(
    IDeviceTopologyReader reader,
    IDeviceTopologySnapshotStore store,
    DeviceTopologySemanticComparer semanticComparer,
    HostManagerSamplingSubscriptionOwner samplingSubscriptionOwner,
    ILogger<DeviceTopologySnapshotProvider> logger,
    IAcpiFirmwareTableReader acpiReader,
    WindowsInstalledSoftwareInventory? softwareInventory = null,
    WindowsServiceInformationOwner? serviceInventory = null,
    WindowsOperatingSystemInformationOwner? operatingSystemInventory = null,
    NvidiaNvapiMonitoringZone? nvidiaDrsZone = null,
    WindowsNetworkEndpointInformationReader? networkEndpoints = null,
    WindowsVolumeSpaceInformationReader? volumeSpace = null,
    ResourceManager.Shared.BusinessBlocks.State.StateAuthority? stateAuthority = null)
    : IDeviceTopologySnapshotProvider, IHostedService, IDisposable
{
    internal const string NetworkEndpointsItemId = "deviceTopology.network-endpoints";
    private readonly WindowsNetworkEndpointInformationReader networkEndpointReader = networkEndpoints ?? new();
    internal const string NvidiaDrsItemId = "deviceTopology.nvidia-drs";
    internal const string SnapshotItemId = "deviceTopology.snapshot";
    internal const string AcpiItemId = "deviceTopology.acpi";
    internal const string SoftwareItemId = "deviceTopology.software";
    internal const string ServicesItemId = "deviceTopology.services";
    internal const string OperatingSystemItemId = "deviceTopology.operating-system";
    internal WindowsOperatingSystemInformationOwner OperatingSystemInventory { get; } = operatingSystemInventory ?? new();
    private readonly bool ownsOperatingSystemInventory = operatingSystemInventory is null;
    internal WindowsServiceInformationOwner ServicesInventory { get; } = serviceInventory ?? new();
    private readonly bool ownsServicesInventory = serviceInventory is null;
    internal WindowsInstalledSoftwareInventory? SoftwareInventory { get; } = softwareInventory;
    private readonly SemaphoreSlim demandSignal = new(0, 1);
    private readonly object publicationGate = new();
    private readonly object resourceDisposeGate = new();
    private readonly DeviceTopologyCaptureOwner captureOwner = new(acpiReader,
        new DeviceTopologyCapturePolicy(reader, semanticComparer.ComputeHash, DeviceTopologyInformationProjection.Project,
            (changed, token) => store.SaveAsync(new DeviceTopologyPersistedSnapshot(
                DeviceTopologySnapshotState.CurrentSchemaVersion, string.Empty, changed.ContentGeneration,
                changed.SemanticHash, changed.Snapshot), token),
            static error => error is DeviceTopologyIncompleteSourcesException incomplete
                ? new(incomplete.Diagnostics[0].Code, incomplete.Diagnostics)
                : new("device-topology-read-failed", []),
            (error, code, diagnostics, changed) => {
                if (changed) logger.LogWarning(error,
                    "Device topology background sample failed with {FailureCode}; sources: {SourceIds}.",
                    code, string.Join(",", diagnostics.Select(static diagnostic => diagnostic.SourceId)));
                else logger.LogDebug("Device topology source failure remains unchanged: {FailureCode}.", code);
            },
            error => logger.LogWarning(error, "Unable to persist the device topology snapshot."),
            () => logger.LogDebug("Raw SMBIOS declarations are unavailable; current values were cleared.")),
        error => logger.LogWarning(error, "ACPI firmware enumeration failed."), CreateVolumeFreeSpacePublisher(stateAuthority));
    private readonly NativeItemSamplingSubscriptionTracker<DeviceTopologySampleRequest> subscriptions =
        CreateSubscriptions(samplingSubscriptionOwner, null);
    private CancellationTokenSource? samplingCts;
    private Task? samplingTask;
    private long nextLeaseId;
    private int disposed;
    private int ownedResourcesDisposed;

    private static NativeItemSamplingSubscriptionTracker<DeviceTopologySampleRequest> CreateSubscriptions(
        HostManagerSamplingSubscriptionOwner owner,
        Func<ulong, uint, ResourceManager.Subscription.INativeSamplingOutputScratch>? createOutputScratch)
        => new(owner, 2, static request => request.SourceKey,
            static request => SamplingItemIds(request.Scope),
            static (itemIds, sources) => CreateSampleRequest(itemIds, sources),
            coalesceItemsIntoLatestCapture: false, createOutputScratch);

    internal DeviceTopologySnapshotProvider(
        IDeviceTopologyReader reader,
        IDeviceTopologySnapshotStore store,
        DeviceTopologySemanticComparer semanticComparer,
        HostManagerSamplingSubscriptionOwner samplingSubscriptionOwner,
        ILogger<DeviceTopologySnapshotProvider> logger,
        IAcpiFirmwareTableReader acpiReader,
        ResourceManagerLedgerOwner ledgerOwner,
        WindowsInstalledSoftwareInventory? softwareInventory = null,
        WindowsServiceInformationOwner? serviceInventory = null,
        WindowsOperatingSystemInformationOwner? operatingSystemInventory = null,
        NvidiaNvapiMonitoringZone? nvidiaDrsZone = null,
        WindowsNetworkEndpointInformationReader? networkEndpoints = null,
        WindowsVolumeSpaceInformationReader? volumeSpace = null,
        ResourceManager.Shared.BusinessBlocks.State.StateAuthority? stateAuthority = null)
        : this(reader, store, semanticComparer, samplingSubscriptionOwner, logger, acpiReader,
            softwareInventory, serviceInventory, operatingSystemInventory, nvidiaDrsZone, networkEndpoints, volumeSpace, stateAuthority)
    {
        ArgumentNullException.ThrowIfNull(ledgerOwner);
        subscriptions = CreateSubscriptions(samplingSubscriptionOwner,
            (length, alignment) => SamplingOutputScratch.Create(ledgerOwner.CreateScratch, length, alignment));
    }

    public async IAsyncEnumerable<DeviceTopologySnapshotState> SubscribeAsync(
        string subscriptionId,
        TimeSpan interval,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionId);
        cancellationToken.ThrowIfCancellationRequested();
        using var publication = captureOwner.SubscribeTopologyPublications();
        using var lease = AcquireInformationSubscription(subscriptionId, interval, cancellationToken: cancellationToken);
        // The listener is installed before reading current, so later publications cannot be lost.
        yield return ReadState();
        while (!cancellationToken.IsCancellationRequested)
        {
            await publication.WaitAsync(cancellationToken);
            yield return ReadState();
        }
    }

    [RuntimeRead(ReadVolatilityKind.EventDriven, nameof(SubscribeChangesAsync))]
    public async IAsyncEnumerable<DeviceTopologySnapshotState> SubscribeChangesAsync(
        string subscriptionId, TimeSpan interval, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var publication = captureOwner.SubscribeTopologyChanges();
        using var lease = AcquireInformationSubscription(subscriptionId, interval, cancellationToken: cancellationToken);
        var previous = captureOwner.ReadChangeState();
        yield return previous;
        while (!cancellationToken.IsCancellationRequested)
        {
            await publication.WaitAsync(cancellationToken);
            var next = captureOwner.ReadChangeState();
            if (ReferenceEquals(previous, next)) continue;
            previous = next;
            yield return next;
        }
    }
    public DeviceTopologySnapshotState ReadState()
    {
        return captureOwner.ReadState();
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        var persisted = await store.LoadAsync(cancellationToken);
        if (persisted is not null)
        {
            captureOwner.InitializePersisted(persisted.Snapshot, persisted.SemanticHash, persisted.ContentGeneration);
        }

        lock (publicationGate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            if (samplingTask is not null) throw new InvalidOperationException("The device sampling worker is already running or draining.");
            samplingCts?.Dispose();
            var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            samplingCts = cts;
            samplingTask = Task.Run(() => RunSamplingLoopAsync(cts.Token), CancellationToken.None);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task? task;
        CancellationTokenSource? cts;
        lock (publicationGate)
        {
            task = samplingTask;
            cts = samplingCts;
            cts?.Cancel();
            fieldSubscriptionDelivery.RetireAll();
            if (task is null)
            {
                subscriptions.Clear(DateTimeOffset.UtcNow);
                return;
            }
        }

        SignalDemand();
        try
        {
            await task.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (task.IsCompleted)
        {
        }

        lock (publicationGate)
        {
            // Another concurrent StopAsync may have already retired this run and
            // allowed StartAsync to publish its successor. Only the owner of the
            // captured task may clear its subscriptions or cancellation source.
            if (ReferenceEquals(samplingTask, task))
            {
                subscriptions.Clear(DateTimeOffset.UtcNow);
                samplingTask = null;
                // The cancelled CTS is also the existing stopped-admission state.
                // StartAsync replaces/disposes it; final disposal owns its release.
            }
        }
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref disposed, 1);

        CancellationTokenSource? cts;
        Task? task;
        lock (publicationGate)
        {
            cts = samplingCts;
            task = samplingTask;
            cts?.Cancel();
            fieldSubscriptionDelivery.RetireAll();
        }
        SignalDemand();
        try
        {
            task?.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (task?.IsCompleted == true)
        {
        }
        catch (Exception error)
        {
            logger.LogError(error, "Device topology sampling worker failed while draining for disposal.");
        }
        lock (resourceDisposeGate)
        {
            if (Volatile.Read(ref ownedResourcesDisposed) != 0) return;
            lock (publicationGate)
            {
                // The worker has completed and settled its current Plan before Clear or output close.
                subscriptions.Clear(DateTimeOffset.UtcNow);
            }
            subscriptions.CloseOutputScratch();
            DisposeCaptureResources();
        }
    }

    private void DisposeCaptureResources()
    {
        if (Volatile.Read(ref ownedResourcesDisposed) != 0) return;
        lock (publicationGate)
        {
            var cts = samplingCts;
            samplingCts = null;
            samplingTask = null;
            cts?.Dispose();
        }
        captureOwner.Dispose();
        if (ownsServicesInventory) ServicesInventory.Dispose();
        if (ownsOperatingSystemInventory) OperatingSystemInventory.Dispose();
        demandSignal.Dispose();
        Volatile.Write(ref ownedResourcesDisposed, 1);
    }

    internal Task<DeviceTopologySampleCompletion> SampleNowAsync(
        CancellationToken cancellationToken)
    {
        return TrySampleAsync(null, cancellationToken);
    }

    private Task RunSamplingLoopAsync(CancellationToken cancellationToken)
    {
        FieldCollectionResult? pendingFieldResult = null;
        var worker = new NativeSamplingSubscriptionWorker<DeviceTopologySampleRequest, SamplingOwnerToken>(
            subscriptions, demandSignal, static _ => { },
            static failure => System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure.Error).Throw(),
            TimeSpan.Zero);
        return worker.RunAsync(async (plan, token) =>
        {
            var completion = await SampleRequestedAsync(plan.Request!.Scope, plan.OwnerToken, token,
                plan.Request.IncludeServiceConfiguration, plan.Request.OperatingSystemFields,
                (scope, queriedKeys) => pendingFieldResult = MergeFieldCapture(pendingFieldResult,
                    CaptureFieldResult(plan.Triggers, scope, queriedKeys)), plan.Request.NvidiaDrsRequests, plan.Request.VolumeFields);
            return completion switch
            {
                DeviceTopologySampleCompletion.Sampled => SubscriptionSampleCompletion.Sampled,
                DeviceTopologySampleCompletion.Failed => SubscriptionSampleCompletion.Failed,
                DeviceTopologySampleCompletion.SkippedBusy => SubscriptionSampleCompletion.Skipped,
                DeviceTopologySampleCompletion.Canceled => SubscriptionSampleCompletion.Canceled,
                _ => throw new InvalidOperationException("Device topology sampling returned an unknown completion.")
            };
        }, cancellationToken, planCreated: plan =>
        {
            pendingFieldResult = null;
            if (plan.DueItemIds.Count == 0)
                fieldSubscriptionDelivery.Deliver(plan.Triggers, null, freshOnly: false);
        }, collectionCompleted: (plan, completion) =>
        {
            var result = pendingFieldResult;
            pendingFieldResult = null;
            if (completion != SubscriptionSampleCompletion.Canceled)
                fieldSubscriptionDelivery.Deliver(plan.Triggers, result,
                    freshOnly: plan.DueItemIds.Count == 0 ? true : null);
        });
    }

    private Task<DeviceTopologySampleCompletion> TrySampleAsync(SamplingOwnerToken? ownerToken,
        CancellationToken cancellationToken, bool requireDemand = true,
        Action<DeviceTopologySampleScope, IReadOnlySet<string>>? collectionCommitted = null)
    {
        var queried = false;
        return captureOwner.SampleTopologyAsync(Permission(ownerToken, DeviceTopologySampleScope.Snapshot,
            hosted: ownerToken is not null, committed: () => collectionCommitted?.Invoke(DeviceTopologySampleScope.Snapshot,
                queried ? new HashSet<string>(StringComparer.Ordinal) { SnapshotItemId } : new HashSet<string>(StringComparer.Ordinal))),
            cancellationToken, requireDemand, sourceQueryStarted: () => queried = true);
    }

    private Task<DeviceTopologySampleCompletion> TrySampleAcpiAsync(SamplingOwnerToken? ownerToken,
        CancellationToken cancellationToken, bool requireDemand = true,
        Action<DeviceTopologySampleScope, IReadOnlySet<string>>? collectionCommitted = null)
    {
        var queried = false;
        return captureOwner.SampleAcpiAsync(Permission(ownerToken, DeviceTopologySampleScope.Acpi,
            hosted: ownerToken is not null, committed: () => collectionCommitted?.Invoke(DeviceTopologySampleScope.Acpi,
                queried ? new HashSet<string>(StringComparer.Ordinal) { AcpiItemId } : new HashSet<string>(StringComparer.Ordinal))),
            cancellationToken, requireDemand, sourceQueryStarted: () => queried = true);
    }

    private DeviceTopologyCapturePermission? Permission(SamplingOwnerToken? ownerToken, DeviceTopologySampleScope scope,
        bool hosted = false, Action? committed = null)
        => ownerToken is null ? null : hosted
            ? new(() => IsHostedActive(ownerToken), action => TryPublishHosted(ownerToken, action, committed), () => HasCurrentDemand(scope))
            : new(() => ownerToken.IsActive, ownerToken.TryPublish, () => HasCurrentDemand(scope));

    private void SignalDemand()
    {
        if (demandSignal.CurrentCount == 0)
        {
            try
            {
                demandSignal.Release();
            }
            catch (SemaphoreFullException)
            {
            }
            catch (ObjectDisposedException) when (Volatile.Read(ref disposed) != 0)
            {
                // A retiring or closed worker no longer needs a wake-up.
            }
        }
    }



    private static IEnumerable<string> SamplingItemIds(DeviceTopologySampleScope scope)
    {
        if ((scope & DeviceTopologySampleScope.VolumeFreeSpace) != 0) yield return VolumeFreeSpaceItemId;
        if ((scope & DeviceTopologySampleScope.NetworkEndpoints) != 0) yield return NetworkEndpointsItemId;
        if ((scope & DeviceTopologySampleScope.Snapshot) != 0) yield return SnapshotItemId;
        if ((scope & DeviceTopologySampleScope.Acpi) != 0) yield return AcpiItemId;
        if ((scope & DeviceTopologySampleScope.Software) != 0) yield return SoftwareItemId;
        if ((scope & DeviceTopologySampleScope.Services) != 0) yield return ServicesItemId;
        if ((scope & DeviceTopologySampleScope.OperatingSystem) != 0) yield return OperatingSystemItemId;
        if ((scope & DeviceTopologySampleScope.PassiveCurrent) != 0) yield return DiskUsageTreeStore.InformationItemId;
        if ((scope & DeviceTopologySampleScope.NvidiaDrs) != 0) yield return NvidiaDrsItemId;
    }

    private static DeviceTopologySampleRequest? CreateSampleRequest(IReadOnlyList<string> items,
        IReadOnlyList<ResourceManager.Subscription.NativeItemSamplingSubscriptionSourceView<DeviceTopologySampleRequest>> sources)
    {
        var scope = DeviceTopologySampleScope.None;
        if (items.Contains(VolumeFreeSpaceItemId, StringComparer.OrdinalIgnoreCase)) scope |= DeviceTopologySampleScope.VolumeFreeSpace;
        if (items.Contains(NetworkEndpointsItemId, StringComparer.OrdinalIgnoreCase)) scope |= DeviceTopologySampleScope.NetworkEndpoints;
        if (items.Contains(SnapshotItemId, StringComparer.OrdinalIgnoreCase)) scope |= DeviceTopologySampleScope.Snapshot;
        if (items.Contains(AcpiItemId, StringComparer.OrdinalIgnoreCase)) scope |= DeviceTopologySampleScope.Acpi;
        if (items.Contains(SoftwareItemId, StringComparer.OrdinalIgnoreCase)) scope |= DeviceTopologySampleScope.Software;
        if (items.Contains(ServicesItemId, StringComparer.OrdinalIgnoreCase)) scope |= DeviceTopologySampleScope.Services;
        if (items.Contains(OperatingSystemItemId, StringComparer.OrdinalIgnoreCase)) scope |= DeviceTopologySampleScope.OperatingSystem;
        if (items.Contains(DiskUsageTreeStore.InformationItemId, StringComparer.OrdinalIgnoreCase)) scope |= DeviceTopologySampleScope.PassiveCurrent;
        if (items.Contains(NvidiaDrsItemId, StringComparer.OrdinalIgnoreCase)) scope |= DeviceTopologySampleScope.NvidiaDrs;
        return scope == DeviceTopologySampleScope.None ? null : new("provider", scope,
            IncludeServiceConfiguration: sources.Any(source => (source.Request.Scope & DeviceTopologySampleScope.Services) != 0
                && source.Request.IncludeServiceConfiguration && !source.Request.CancellationToken.IsCancellationRequested),
            OperatingSystemFields: sources.Where(source => (source.Request.Scope & DeviceTopologySampleScope.OperatingSystem) != 0
                && !source.Request.CancellationToken.IsCancellationRequested)
                .Aggregate(WindowsOperatingSystemInformationFields.None, static (fields, source) => fields | source.Request.OperatingSystemFields),
            VolumeFields: sources.Where(source => (source.Request.Scope & DeviceTopologySampleScope.VolumeFreeSpace) != 0
                && !source.Request.CancellationToken.IsCancellationRequested).SelectMany(source => source.Request.VolumeFields.IsDefault
                    ? ImmutableArray<FieldReference>.Empty : source.Request.VolumeFields).Distinct().ToImmutableArray(),
            NvidiaDrsRequests: sources.Where(source => (source.Request.Scope & DeviceTopologySampleScope.NvidiaDrs) != 0
                && !source.Request.CancellationToken.IsCancellationRequested)
                .SelectMany(source => source.Request.NvidiaDrsRequests.IsDefault
                    ? ImmutableArray<NvidiaDrsInformationRequest>.Empty : source.Request.NvidiaDrsRequests).ToImmutableArray());
    }

    private async Task<DeviceTopologySampleCompletion> SampleRequestedAsync(
        DeviceTopologySampleScope scope, SamplingOwnerToken ownerToken, CancellationToken cancellationToken,
        bool includeServiceConfiguration = false,
        WindowsOperatingSystemInformationFields operatingSystemFields = WindowsOperatingSystemInformationFields.All,
        Action<DeviceTopologySampleScope, IReadOnlySet<string>>? collectionCommitted = null,
        ImmutableArray<NvidiaDrsInformationRequest> nvidiaDrsRequests = default,
        ImmutableArray<FieldReference> volumeFields = default)
    {
        var completion = DeviceTopologySampleCompletion.Sampled;
        if ((scope & DeviceTopologySampleScope.Snapshot) != 0)
            completion = await TrySampleAsync(ownerToken, cancellationToken, collectionCommitted: collectionCommitted);
        if (completion is DeviceTopologySampleCompletion.Canceled or DeviceTopologySampleCompletion.SkippedBusy)
            return completion;
        if ((scope & DeviceTopologySampleScope.Acpi) != 0)
        {
            var acpiCompletion = await TrySampleAcpiAsync(ownerToken, cancellationToken, collectionCommitted: collectionCommitted);
            if (acpiCompletion is DeviceTopologySampleCompletion.Canceled or DeviceTopologySampleCompletion.SkippedBusy)
                return acpiCompletion;
            if (acpiCompletion == DeviceTopologySampleCompletion.Failed) completion = acpiCompletion;
        }
        if ((scope & DeviceTopologySampleScope.Software) != 0)
        {
            var softwareCompletion = await TrySampleSoftwareAsync(ownerToken, cancellationToken, collectionCommitted);
            if (softwareCompletion is DeviceTopologySampleCompletion.Canceled or DeviceTopologySampleCompletion.SkippedBusy)
                return softwareCompletion;
            if (softwareCompletion == DeviceTopologySampleCompletion.Failed) completion = softwareCompletion;
        }
        if ((scope & DeviceTopologySampleScope.Services) != 0)
        {
            var serviceCompletion = await TrySampleServicesAsync(ownerToken, includeServiceConfiguration, cancellationToken, collectionCommitted);
            if (serviceCompletion is DeviceTopologySampleCompletion.Canceled or DeviceTopologySampleCompletion.SkippedBusy)
                return serviceCompletion;
            if (serviceCompletion == DeviceTopologySampleCompletion.Failed) completion = serviceCompletion;
        }
        if ((scope & DeviceTopologySampleScope.OperatingSystem) != 0)
        {
            var osCompletion = await TrySampleOperatingSystemAsync(ownerToken, cancellationToken, operatingSystemFields, collectionCommitted);
            if (osCompletion is DeviceTopologySampleCompletion.Canceled or DeviceTopologySampleCompletion.SkippedBusy)
                return osCompletion;
            if (osCompletion == DeviceTopologySampleCompletion.Failed) completion = osCompletion;
        }
        if ((scope & DeviceTopologySampleScope.NetworkEndpoints) != 0)
        {
            var networkCompletion = await TrySampleNetworkEndpointsAsync(ownerToken, cancellationToken, collectionCommitted);
            if (networkCompletion is DeviceTopologySampleCompletion.Canceled or DeviceTopologySampleCompletion.SkippedBusy) return networkCompletion;
            if (networkCompletion == DeviceTopologySampleCompletion.Failed) completion = networkCompletion;
        }
        if ((scope & DeviceTopologySampleScope.NvidiaDrs) != 0)
        {
            var drsCompletion = await TrySampleNvidiaDrsAsync(ownerToken, cancellationToken, nvidiaDrsRequests, collectionCommitted);
            if (drsCompletion is DeviceTopologySampleCompletion.Canceled or DeviceTopologySampleCompletion.SkippedBusy) return drsCompletion;
            if (drsCompletion == DeviceTopologySampleCompletion.Failed) completion = drsCompletion;
        }
        if ((scope & DeviceTopologySampleScope.VolumeFreeSpace) != 0)
        {
            var volumeCompletion = await TrySampleVolumeFreeSpaceAsync(ownerToken, volumeFields, cancellationToken, collectionCommitted);
            if (volumeCompletion is DeviceTopologySampleCompletion.Canceled or DeviceTopologySampleCompletion.SkippedBusy) return volumeCompletion;
            if (volumeCompletion == DeviceTopologySampleCompletion.Failed) completion = volumeCompletion;
        }
        return completion;
    }

    [Flags]
    private enum DeviceTopologySampleScope : ushort { None = 0, Snapshot = 1, Acpi = 2, Software = 4, Services = 8, OperatingSystem = 16, PassiveCurrent = 32, NvidiaDrs = 64, NetworkEndpoints = 128, VolumeFreeSpace = 256 }

    private bool HasCurrentDemand(DeviceTopologySampleScope scope)
        => subscriptions.HasPersistentDemand(request =>
            (request.Scope & scope) != 0 && !request.CancellationToken.IsCancellationRequested);

    private sealed record DeviceTopologySampleRequest(
        string SourceKey, DeviceTopologySampleScope Scope, CancellationToken CancellationToken = default,
        bool IncludeServiceConfiguration = false,
        WindowsOperatingSystemInformationFields OperatingSystemFields = WindowsOperatingSystemInformationFields.All,
        ImmutableArray<NvidiaDrsInformationRequest> NvidiaDrsRequests = default,
        ImmutableArray<FieldReference> VolumeFields = default);
}





