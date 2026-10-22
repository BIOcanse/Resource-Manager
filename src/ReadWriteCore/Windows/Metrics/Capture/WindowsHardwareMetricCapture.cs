using System.Collections.Immutable;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Native;
using ResourceManager.ReadWrite.Native.Pdh;
using ResourceManager.ReadWrite.Windows.Metrics.Resources;
using ResourceManager.ReadWrite.Windows.Metrics.Writes;
using ResourceManager.ReadWrite.Windows.Pdh;
namespace ResourceManager.ReadWrite.Windows.Metrics.Capture;

internal sealed record WindowsMetricProjectionCopy(string VirtualMemoryDetail, Func<string, string>? LegacyLabel = null);
/// <summary>The original shared capture gate and complete selected-source algorithm. No worker/current duplicate.
/// The host borrows SampleGate; the default owner acquires the same gate before invoking this capture.</summary>
internal sealed partial class WindowsHardwareMetricCapture
{
    internal readonly SemaphoreSlim SampleGate = new(1, 1);
    private readonly WindowsHardwareMetricResources resources;
    private readonly NativeMetricSnapshotSourceRuntimeFacts sourceRuntimeFacts;
    private readonly MetricReadingSourceTracker readingSources = new();
    private readonly AmdIntegratedGpuSensorRoute integratedGpuSensorRoute;
    private readonly Action requestTopologyRefresh;
    private readonly Action<Exception, string>? logFailure;
    public WindowsHardwareMetricCapture(WindowsHardwareMetricResources resources,
        Func<string, NativeMetricSnapshotZoneMode> modeProvider, TimeSpan transientUnavailableDuration,
        AmdIntegratedGpuSensorRoute integratedGpuSensorRoute, Action requestTopologyRefresh,
        Action<Exception, string>? logFailure = null)
    {
        this.resources = resources ?? throw new ArgumentNullException(nameof(resources));
        this.integratedGpuSensorRoute = integratedGpuSensorRoute ?? throw new ArgumentNullException(nameof(integratedGpuSensorRoute));
        this.requestTopologyRefresh = requestTopologyRefresh ?? throw new ArgumentNullException(nameof(requestTopologyRefresh));
        this.logFailure = logFailure;
        sourceRuntimeFacts = new(modeProvider, transientUnavailableDuration);
    }
    /// <summary>The caller holds this instance's original SampleGate and owns workspace lifetime and atomic publication permission.</summary>
    internal async Task<HardwareMetricSnapshot?> CaptureUnderGateAsync(MetricSampleRequest request,
        NativeMetricSnapshotWorkspace workspace, ulong workspaceIdentity,
        NativeMetricSnapshotCatalogTopologySnapshot? topology, Func<Action, bool> tryPublish,
        WindowsMetricProjectionCopy copy, CancellationToken cancellationToken,
        Action<NativeMetricSnapshotWorkspace>? checkpoint = null, Action<HardwareMetricSnapshot>? onCommitted = null,
        Func<HardwareMetricSnapshot, MetricSampleRequest, CancellationToken, Action<string>?,
            ValueTask<HardwareMetricSnapshot>>? enrichBeforeCommit = null,
        Action<ImmutableHashSet<string>>? sourceQueriesCompleted = null,
        HardwareMetricSnapshot? fieldCatalog = null)
    {
        var catalog = workspace.ReadCatalog();
        var commandAt = DateTimeOffset.UtcNow;
        var collectionRequest = CreateCollectionRequest(request, catalog, fieldCatalog);
        collectionRequest = readingSources.ExpandLegacyRequest(collectionRequest, workspace.ReadCommitted(), workspaceIdentity);
        var requestedMetricHandles = NativeMetricSnapshotRequestProjection.CreateMetricHandles(collectionRequest, catalog);
        var queriedMetricAddresses = sourceQueriesCompleted is null ? null
            : ImmutableHashSet.CreateBuilder<string>(StringComparer.OrdinalIgnoreCase);
        PdhGpuEngineUsageRead? pdhGpuEngineUsage = null;
        var sourceIdentityInvalidated = false;
        Action rejectPublication = () => sourceIdentityInvalidated = true;
        var completions = new List<(NativeMetricSnapshotCollectionPlan Plan, NativeMetricSnapshotSourceCompletion Completion)>();
        if (requestedMetricHandles.Length != 0)
        {
            var includeGpuInventory = collectionRequest.IsAll || requestedMetricHandles.Any(handle =>
                catalog.MetricBindings.GetValueOrDefault(handle).Field?.Resource == MetricResource.Gpu);
            if (sourceRuntimeFacts.NeedsWorkspaceSourceSynchronization(workspaceIdentity))
            {
                sourceRuntimeFacts.SynchronizeWorkspaceSources(
                    workspaceIdentity,
                    catalog,
                    workspace.ReadCommitted().Sources);
            }
            var sourceModes = sourceRuntimeFacts.CaptureModesForRequest(
                catalog, commandAt, requestedMetricHandles, includeGpuInventory);
            var plan = workspace.PlanCollection(requestedMetricHandles, sourceModes, includeGpuInventory, commandAt);
            var completedQuerySources = queriedMetricAddresses is null ? null : new HashSet<ulong>();
            foreach (var sourcePlan in plan.Sources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var completion = await CollectNativeSourceAsync(plan, sourcePlan, cancellationToken, topology, rejectPublication);
                if (completion.PdhGpuEngineUsage is { } engineUsage)
                    pdhGpuEngineUsage = engineUsage;
                completions.Add((plan, completion));
                if (completedQuerySources is not null
                    && (sourcePlan.Flags & (uint)NativeMetricSnapshotSourcePlanFlags.Selected) != 0
                    && sourcePlan.ZoneMode != (uint)NativeMetricSnapshotZoneMode.Freeze
                    && sourcePlan.Availability == (uint)NativeMetricSnapshotSourceAvailability.Available)
                    completedQuerySources.Add(sourcePlan.SourceHandle);
            }

            if (completedQuerySources is not null)
            foreach (var metricPlan in plan.Metrics)
            {
                if ((metricPlan.Flags & (uint)NativeMetricSnapshotMetricPlanFlags.Selected) == 0
                    || !completedQuerySources.Contains(metricPlan.SourceHandle))
                    continue;
                if (plan.Catalog.MetricIds.TryGetValue(metricPlan.MetricHandle, out var metricAddress))
                    queriedMetricAddresses!.Add(metricAddress);
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        HardwareMetricSnapshot? prepared = null;
        if (enrichBeforeCommit is not null)
        {
            // Managed settings share this original SampleGate and the same final
            // publication permission. No numeric value is committed before the permit.
            prepared = NativeMetricSnapshotCommittedProjection.Create(workspace.ReadCommitted(), resources.CpuName,
                resources.MemoryDescription, workspaceIdentity, copy.VirtualMemoryDetail, copy.LegacyLabel);
            if (request.IncludesCatalog) prepared = AddCatalogObservations(prepared, topology, commandAt);
            prepared = await enrichBeforeCommit(prepared, request, cancellationToken,
                queriedMetricAddresses is null ? null : address => queriedMetricAddresses.Add(address)).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        if (queriedMetricAddresses is not null) sourceQueriesCompleted!(queriedMetricAddresses.ToImmutable());
        HardwareMetricSnapshot? published = null;
        if (sourceIdentityInvalidated || !tryPublish(() =>
            {
                // Source results become native committed values at the same admission
                // boundary as the caller's current. A rejected/canceled collection
                // cannot reappear through a later frozen or partially selected read.
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var (plan, completion) in completions)
                {
                    var completedAt = DateTimeOffset.UtcNow;
                    _ = workspace.CompleteSource(plan, completion, completedAt);
                    sourceRuntimeFacts.RecordCompletion(completion.SourceHandle, completion.Status, completedAt);
                }
                checkpoint?.Invoke(workspace);
                var committed = workspace.ReadCommitted();
                readingSources.Observe(committed, workspaceIdentity, completions.Select(static row => row.Completion));
                var sourceSelection = readingSources.Select(committed, workspaceIdentity);
                var snapshot = NativeMetricSnapshotCommittedProjection.Create(committed, resources.CpuName,
                    resources.MemoryDescription, workspaceIdentity, copy.VirtualMemoryDetail, copy.LegacyLabel, sourceSelection);
                if (request.IncludesCatalog) snapshot = AddCatalogObservations(snapshot, topology, commandAt);
                if (prepared is not null) snapshot = WindowsMetricControlFields.ApplyPreparedFields(snapshot, prepared);
                snapshot = ProjectGpuEngineFields(snapshot, request, fieldCatalog, pdhGpuEngineUsage);
                snapshot = ProjectNpuDeviceBusyFields(snapshot, request);
                snapshot = readingSources.ApplyProjectedFields(snapshot);
                onCommitted?.Invoke(snapshot);
                published = snapshot;
            })) return null;
        return published;
    }

    internal static MetricSampleRequest CreateCollectionRequest(
        MetricSampleRequest request,
        NativeMetricSnapshotCatalogProjection catalog,
        HardwareMetricSnapshot? fieldCatalog)
    {
        if (request.IsCatalogProbe)
        {
            return MetricSampleRequest.ForIds(catalog.RuleBindings.Values
                .Where(static binding => binding.FieldCode == MetricFieldCode.GpuEngineBusy
                    && binding.SourceId == NativeMetricSnapshotSources.PdhGpuEngine)
                .Select(static binding => binding.MetricId)).WithCatalog();
        }
        if (request.IsAll)
            return request;

        var requestedBusyDevices = catalog.RuleBindings.Values
            .Where(binding => binding.FieldCode == MetricFieldCode.NpuDeviceEngineBusyPercent
                && request.Includes(binding.MetricId))
            .Select(static binding => binding.DeviceIdentity)
            .ToHashSet(StringComparer.Ordinal);
        var npuEngineTriggers = catalog.RuleBindings.Values
            .Where(binding => binding.FieldCode == MetricFieldCode.NpuEngineReportedRunningTimeRatioPercent
                && binding.NpuEngine is { } engine && requestedBusyDevices.Contains(engine.ParentDeviceIdentity))
            .Select(static binding => binding.MetricId)
            .ToArray();

        var projectedBindings = fieldCatalog?.FieldBindings.Values
            .Where(binding => binding.PdhGpuEngineIdentity is not null
                && request.Includes(binding.RequestAddress))
            .ToArray() ?? [];
        if (projectedBindings.Length == 0 && npuEngineTriggers.Length == 0)
            return request;

        var projectedAddresses = projectedBindings.Select(static binding => binding.RequestAddress)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var projectedTriggers = projectedBindings.Select(static binding => binding.EffectiveSampleRequestAddress);
        var expanded = MetricSampleRequest.ForIds(request.Ids.Where(id => !projectedAddresses.Contains(id))
            .Concat(projectedTriggers)
            .Concat(npuEngineTriggers));
        return request.IncludesCatalog ? expanded.WithCatalog() : expanded;
    }

    internal static HardwareMetricSnapshot ProjectGpuEngineFields(
        HardwareMetricSnapshot snapshot,
        MetricSampleRequest request,
        HardwareMetricSnapshot? fieldCatalog,
        PdhGpuEngineUsageRead? pdhRead)
    {
        var triggerBindings = snapshot.FieldBindings.Values
            .Where(static binding => binding.Field.Code == MetricFieldCode.GpuEngineBusy
                && binding.PdhGpuEngineIdentity is null)
            .ToArray();
        var engineBindings = request.IsCatalogProbe
            ? CreateDiscoveredEngineBindings(triggerBindings, pdhRead)
            : fieldCatalog?.FieldBindings.Values
                .Where(static binding => binding.PdhGpuEngineIdentity is not null)
                .ToArray() ?? [];
        if (engineBindings.Length == 0 && triggerBindings.Length == 0)
            return snapshot;

        var bindings = snapshot.FieldBindings.Values
            .Where(static binding => binding.Field.Code != MetricFieldCode.GpuEngineBusy)
            .ToDictionary(static binding => binding.RequestAddress, StringComparer.OrdinalIgnoreCase);
        foreach (var binding in engineBindings)
            bindings[binding.RequestAddress] = binding;

        var items = snapshot.Items.ToDictionary(static pair => pair.Key,
            static pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        var datasets = snapshot.Datasets.ToDictionary(static pair => pair.Key,
            static pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        if (!request.IsCatalogProbe)
        {
            foreach (var binding in engineBindings)
            {
                var trigger = binding.EffectiveSampleRequestAddress;
                if (!request.IsAll && !request.Includes(binding.RequestAddress)
                    && !request.Includes(trigger))
                    continue;
                if (binding.PdhGpuEngineIdentity is not { } identity)
                    continue;

                var value = 0d;
                var hasCurrent = pdhRead?.ProviderAvailability == NativePdhProviderAvailability.Available
                    && pdhRead.ObservedAt.HasValue
                    && pdhRead.UsageByEngine.TryGetValue(identity, out value);
                var status = hasCurrent
                    ? SamplingObservationStatus.Current
                    : pdhRead is null
                        && snapshot.Datasets.GetValueOrDefault(trigger)?.Status == SamplingObservationStatus.Unsupported
                            ? SamplingObservationStatus.Unsupported
                            : SamplingObservationStatus.Unavailable;
                var timestamp = hasCurrent ? pdhRead!.ObservedAt!.Value.UtcTicks : 0;
                datasets[binding.RequestAddress] = new HardwareMetricDatasetObservation(
                    binding.RequestAddress,
                    status,
                    snapshot.CommittedGeneration,
                    timestamp,
                    snapshot.WorkspaceIdentity,
                    snapshot.ConfigurationGeneration,
                    snapshot.CatalogGeneration,
                    snapshot.CommittedGeneration)
                {
                    LastAttemptAtUtcTicks = snapshot.CapturedAt.UtcTicks,
                    LastSuccessAtUtcTicks = hasCurrent ? timestamp : 0,
                    FailureCode = hasCurrent ? null : status == SamplingObservationStatus.Unsupported
                        ? "pdh-gpu-engine-source-unsupported" : "pdh-gpu-engine-reading-unavailable"
                };
                if (hasCurrent)
                {
                    items[binding.RequestAddress] = new MetricValue(
                        binding.RequestAddress,
                        binding.Field.Name,
                        "gpu_engine",
                        $"{value:F1}%",
                        value,
                        binding.Field.Unit,
                        value,
                        null)
                    {
                        FieldName = binding.Field.Name,
                        DeviceIdentity = binding.DeviceIdentity,
                        ValueKind = binding.Field.ValueKind,
                        SourceId = binding.SourceId
                    };
                }
                else
                {
                    items.Remove(binding.RequestAddress);
                }
            }
        }

        foreach (var trigger in triggerBindings)
        {
            items.Remove(trigger.RequestAddress);
            datasets.Remove(trigger.RequestAddress);
            foreach (var alias in trigger.RequestAliases)
            {
                items.Remove(alias);
                datasets.Remove(alias);
            }
        }

        return snapshot with { FieldBindings = bindings, Items = items, Datasets = datasets };
    }

    private static MetricDeviceFieldBinding[] CreateDiscoveredEngineBindings(
        IReadOnlyList<MetricDeviceFieldBinding> triggerBindings,
        PdhGpuEngineUsageRead? pdhRead)
    {
        if (pdhRead is not
            { ProviderAvailability: NativePdhProviderAvailability.Available, ObservedAt: not null })
            return [];
        var definition = MetricFieldRegistry.FindByName("gpu_all_engine_busy_percent");
        if (definition is null)
            return [];
        return pdhRead.UsageByEngine.Keys
            .OrderBy(static identity => identity.AdapterLuid)
            .ThenBy(static identity => identity.PhysicalIndex)
            .ThenBy(static identity => identity.EngineIndex)
            .ThenBy(static identity => identity.EngineClass)
            .Select(identity => (Identity: identity,
                Trigger: triggerBindings.FirstOrDefault(binding => binding.AdapterKey == identity.AdapterLuid)))
            .Where(static row => row.Trigger is not null)
            .Select((row, ordinal) =>
            {
                var identity = row.Identity;
                var trigger = row.Trigger!;
                var deviceIdentity = $"gpu-engine|{identity.AdapterLuid:X16}|{identity.PhysicalIndex}|{identity.EngineIndex}|{(uint)identity.EngineClass}";
                var address = $"gpu-engine/{identity.AdapterLuid:X16}/{identity.PhysicalIndex}/{identity.EngineIndex}/{(uint)identity.EngineClass}/busyPercent";
                return new MetricDeviceFieldBinding(deviceIdentity, definition,
                    address, NativeMetricSnapshotSources.PdhGpuEngine)
                {
                    AdapterKey = identity.AdapterLuid,
                    PdhGpuEngineIdentity = identity,
                    SampleRequestAddress = trigger.RequestAddress,
                    Device = new("gpu_engine", MetricDeviceAddress.GpuEngine(trigger.Device.Address
                        ?? throw new InvalidOperationException("A GPU engine trigger must have its adapter locator."),
                        identity.AdapterLuid, identity.PhysicalIndex, identity.EngineIndex, (uint)identity.EngineClass),
                        null, ordinal)
                };
            })
            .ToArray();
    }
    public void SynchronizePlatformSensorDemand(IReadOnlyCollection<ulong> activeHandles, NativeMetricSnapshotCatalogProjection catalog)
        => resources.SynchronizeNotebookDemand(catalog.RuleBindings.Values.Any(rule =>
            rule.SourceId == NativeMetricSnapshotSources.NotebookOemFan && activeHandles.Contains(rule.MetricHandle)));
    public void ReleasePlatformSensorDemand() => resources.SynchronizeNotebookDemand(false);
    // Managed semaphore has no AvailableWaitHandle. Keep it usable for late admitted waiters to observe owner disposal.
    // The host borrows this gate and retains its original worker cancellation/drain/disposal contract.
    public static HardwareMetricSnapshot AddCatalogObservations(HardwareMetricSnapshot snapshot,
        NativeMetricSnapshotCatalogTopologySnapshot? topology, DateTimeOffset observedAt)
    {
        var datasets = new Dictionary<string, HardwareMetricDatasetObservation>(snapshot.Datasets,
            StringComparer.OrdinalIgnoreCase);
        Add(MetricDatasetIds.SystemMetricCatalog, topology?.GpuStatus);
        Add(MetricDatasetIds.StorageInventory, topology?.StorageStatus);
        Add(MetricDatasetIds.FanInventory, topology is null ? null
            : topology.FanStatus == NativeMetricSnapshotCatalogTopologyStatus.Complete
                && topology.NvmlFanStatus == NativeMetricSnapshotCatalogTopologyStatus.Complete
                && topology.IgclFanStatus == NativeMetricSnapshotCatalogTopologyStatus.Complete
                && topology.NotebookOemFanStatus == NativeMetricSnapshotCatalogTopologyStatus.Complete
                    ? NativeMetricSnapshotCatalogTopologyStatus.Complete : NativeMetricSnapshotCatalogTopologyStatus.Unavailable);
        Add(MetricDatasetIds.BatteryInventory, topology?.BatteryStatus);
        Add(MetricDatasetIds.NumaInventory, topology?.NumaStatus);
        return snapshot with { CapturedAt = observedAt, Datasets = datasets };

        void Add(string id, NativeMetricSnapshotCatalogTopologyStatus? status)
        {
            var current = status == NativeMetricSnapshotCatalogTopologyStatus.Complete;
            datasets[id] = new(id, current ? SamplingObservationStatus.Current : SamplingObservationStatus.Unavailable,
                topology?.Generation ?? 0, current ? observedAt.UtcTicks : 0, snapshot.WorkspaceIdentity,
                snapshot.ConfigurationGeneration, snapshot.CatalogGeneration, snapshot.CommittedGeneration)
            {
                LastAttemptAtUtcTicks = observedAt.UtcTicks,
                LastSuccessAtUtcTicks = current ? observedAt.UtcTicks : 0,
                FailureCode = current ? null : "metric-catalog-enumeration-unavailable"
            };
        }
    }

}


