using System.Collections.Immutable;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.ReadWrite.Publications;
using ResourceManager.ReadWrite.Windows.Metrics.Writes;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Fields;
using ResourceManager.ReadWrite.Native;
using ResourceManager.ReadWrite.Windows.Cpu;
using ResourceManager.ReadWrite.Windows.Metrics.Capture;
using ResourceManager.ReadWrite.Windows.Metrics.Configuration;
using ResourceManager.ReadWrite.Windows.Metrics.Resources;
using ResourceManager.ReadWrite.Windows.Metrics.Topology;

namespace ResourceManager.ReadWrite.Windows.Metrics;

/// <summary>Explicit direct capture over the original source resources, native workspace and managed current.
/// Starts no worker. Product subscriptions borrow the same capture algorithm with their existing leases.</summary>
public sealed partial class WindowsHardwareMetricOwner : IDisposable
{
    private readonly WindowsMetricConfiguration configuration;
    private readonly WindowsHardwareMetricResources resources;
    private readonly WindowsHardwareMetricCapture capture;
    private readonly NvidiaMetricFieldWriter? nvidiaWriter;
    private readonly WindowsMetricControlFields controlFields;
    private readonly WindowsHardwareMetricTopologySource topologySource;
    private readonly LastSuccessfulHardwareMetricSnapshot current = new();
    private readonly Func<string, NativeMetricSnapshotZoneMode> modeProvider;
    private readonly WindowsMetricResourceOptions resourceOptions;
    private NativeMetricSnapshotWorkspace? workspace;
    private NativeMetricSnapshotCatalogTopologySnapshot? topology;
    private IDisposable? intelFanDirectoryLease;
    private IDisposable? npuEngineDirectoryLease;
    private HardwareMetricSnapshot? catalogSnapshot;
    private ulong workspaceIdentity;
    private bool topologyRefreshRequested = true;
    private int disposed;

    public WindowsHardwareMetricOwner(WindowsMetricConfiguration configuration,
        WindowsMetricResourceOptions? resourceOptions = null,
        Func<string, NativeMetricSnapshotZoneMode>? modeProvider = null,
        TimeSpan? unavailableCooldown = null, TimeProvider? clock = null)
        : this(configuration, WindowsHardwareMetricResources.CreateDefault(configuration.PdhOptions,
            resourceOptions, clock), resourceOptions ?? new(), modeProvider, unavailableCooldown)
    { }

    /// <summary>Explicit original reader/control borrowing. The resource bundle
    /// disposes only resources registered as owned by its actual default factory;
    /// supplied callback/control resources remain caller-owned.</summary>
    public WindowsHardwareMetricOwner(WindowsMetricConfiguration configuration, WindowsHardwareMetricResources resources,
        WindowsMetricResourceOptions resourceOptions, Func<string, NativeMetricSnapshotZoneMode>? modeProvider = null,
        TimeSpan? unavailableCooldown = null)
    {
        this.configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        this.resourceOptions = resourceOptions;
        this.modeProvider = modeProvider ?? (static _ => NativeMetricSnapshotZoneMode.Normal);
        this.resources = resources;
        nvidiaWriter = resources.NvidiaControl is { } writer ? new(writer, resources.ReadGpuInventory) : null;
        controlFields = new(resources.Controls ?? new(), resources.ReadGpuInventory,
            source => this.modeProvider(source) == NativeMetricSnapshotZoneMode.Freeze);
        topologySource = new(new(resources.ReadGpuInventory, resources.ReadStorageIdentities, resources.ReadFanIdentities,
            resources.ReadBatteryIdentities, resources.ReadNetworkIdentities, resources.ReadNumaIdentities)
            { OpenIgclFanDirectory = (inventory, maximum, prior) => this.modeProvider(NativeMetricSnapshotSources.IntelGcl) == NativeMetricSnapshotZoneMode.Freeze
                ? new(NativeMetricSnapshotCatalogTopologyStatus.Unavailable, []) : resources.OpenIgclFanDirectory(inventory, maximum, prior),
                OpenNpuEngineDirectory = (maximum, prior) => this.modeProvider(NativeMetricSnapshotSources.WindowsNpuEngine) == NativeMetricSnapshotZoneMode.Freeze
                    ? new(NativeMetricSnapshotCatalogTopologyStatus.Unavailable, []) : resources.OpenNpuEngineDirectory(maximum, prior),
                ReadNvmlFanIdentities = resources.ReadNvmlFanIdentities, ReadNpuMemoryIdentities = resources.ReadNpuMemoryIdentities },
            resources.Controls?.ControlReadCpuTopology
                ?? (static () => new WindowsCpuOsTopologyReader().ReadInformation(includeUsage: false)));
        capture = new(resources, this.modeProvider, unavailableCooldown ?? TimeSpan.FromSeconds(5),
            new AmdIntegratedGpuSensorRoute(), () => topologyRefreshRequested = true);
    }

    public static WindowsHardwareMetricOwner CreateDefault(WindowsMetricResourceOptions? resourceOptions = null,
        Func<string, NativeMetricSnapshotZoneMode>? modeProvider = null)
        => new(WindowsMetricConfiguration.CreateDefault(), resourceOptions, modeProvider);

    public HardwareMetricSnapshot? ReadCachedSnapshot() => current.Read();

    private readonly object backendGate = new();
    private MetricReadWriteBackend? backend;
    public SamplingPublicationSignal Publications { get; } = new();
    public IReadWriteBackend CreateBackend()
    {
        lock (backendGate) return backend ??= new MetricReadWriteBackend(
            () => Volatile.Read(ref catalogSnapshot), _ => current.Read(), CaptureAsync,
            snapshot => Volatile.Write(ref catalogSnapshot, snapshot), ReadSupport,
            binding => controlFields.IsControlBinding(binding) ? controlFields.Query(binding)
                : nvidiaWriter?.Query(binding) ?? OperationSupport.Unsupported("metric-write-owner-not-bound"),
            WriteAsync, controlFields.GetContract);
    }

    private OperationSupport ReadSupport(HardwareMetricSnapshot _, MetricDeviceFieldBinding binding)
    {
        if (controlFields.IsControlBinding(binding)) return controlFields.ReadSupport(binding);
        var dependencies = MetricReadWriteBackend.DependenciesOf(binding.SourceId);
        if (modeProvider(binding.SourceId) == NativeMetricSnapshotZoneMode.Freeze)
            return new(false, "source-frozen-by-caller", dependencies);
        if (binding.SourceId == NativeMetricSnapshotSources.IntelMsr
            && (resourceOptions.IntelModulePath is null || resourceOptions.PawnIoLibraryPath is null))
            return new(false, "intel-pawnio-paths-not-supplied", dependencies);
        if (binding.SourceId == NativeMetricSnapshotSources.AmdSmu && resourceOptions.AmdSmu is null)
            return new(false, "amd-smu-pawnio-options-not-supplied", dependencies);
        if (binding.SourceId == NativeMetricSnapshotSources.NotebookOemFan && !resourceOptions.EnableMechrevo)
            return new(false, "oem-firmware-policy-not-enabled", dependencies);
        return new(true, null, dependencies);
    }

    public async ValueTask<HardwareMetricSnapshot> CaptureAsync(MetricSampleRequest request, CancellationToken cancellationToken = default)
    {
        var snapshot = await CaptureCoreAsync(request, cancellationToken, null, null);
        if (snapshot is not null) return snapshot;
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        throw new InvalidOperationException("The metric collection frame was rejected before publication.");
    }

    private async ValueTask<HardwareMetricSnapshot?> CaptureCoreAsync(MetricSampleRequest request,
        CancellationToken cancellationToken, IMetricPublicationPermit? permit, HardwareCollectionBorrow? borrow,
        Action<ImmutableHashSet<string>>? sourceQueriesCompleted = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        await capture.SampleGate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            borrow?.RequireActive();
            if (request.IncludesCatalog) topologyRefreshRequested = true;
            if (topologyRefreshRequested) RefreshTopology();
            cancellationToken.ThrowIfCancellationRequested();
            var active = workspace ?? throw new InvalidOperationException("Metric workspace is unavailable.");
            if (borrow is null && !request.IsCatalogProbe)
            {
                var catalog = active.ReadCatalog();
                var handles = NativeMetricSnapshotRequestProjection.CreateMetricHandles(request, catalog);
                if (catalog.RuleBindings.Values.Any(rule => rule.SourceId == NativeMetricSnapshotSources.NotebookOemFan
                    && handles.Contains(rule.MetricHandle))) directNotebookDemand = true;
            }
            var snapshot = await capture.CaptureUnderGateAsync(request, active, workspaceIdentity, topology,
                commit => CommitCollection(commit, permit, borrow),
                new(""), cancellationToken, onCommitted: request.IsCatalogProbe ? null : completed => current.Publish(completed, request),
                enrichBeforeCommit: EnrichControlFieldsAsync,
                sourceQueriesCompleted: sourceQueriesCompleted,
                fieldCatalog: Volatile.Read(ref catalogSnapshot));
            if (request.IsCatalogProbe && snapshot is not null)
                nvidiaWriter?.Discover(snapshot, modeProvider(NativeMetricSnapshotSources.NvidiaNvml) == NativeMetricSnapshotZoneMode.Freeze);
            if (!request.IsCatalogProbe && snapshot is not null)
                Publications.Publish(PublicationKeys(request, snapshot));
            return snapshot;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            if (!request.IsCatalogProbe && CommitCollection(() =>
                current.RecordFailure(request, DateTimeOffset.UtcNow, "metric-collection-failed"), permit, borrow))
                Publications.Publish(PublicationKeys(request, current.Read()));
            throw;
        }
        finally { capture.SampleGate.Release(); }
    }

    private bool CommitCollection(Action commit, IMetricPublicationPermit? permit, HardwareCollectionBorrow? borrow)
    {
        var committed = false;
        void Publish()
        {
            if (Volatile.Read(ref disposed) != 0 || borrow?.IsDisposed == true) return;
            commit(); committed = true;
        }
        if (permit is null) Publish(); else permit.TryPublish(Publish);
        return committed;
    }

    private static IEnumerable<string> PublicationKeys(MetricSampleRequest request, HardwareMetricSnapshot? snapshot)
        => MetricDatasetIds.ExpandBoundSystemDatasets(
            MetricDatasetIds.ResolveSystemDatasets(request, snapshot?.Datasets.Keys ?? []),
            snapshot?.FieldBindings.Values ?? []);

    private async ValueTask<FieldWriteResult> WriteAsync(FieldReference field, MetricDeviceFieldBinding binding,
        FieldValue value, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        await capture.SampleGate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            if (controlFields.IsControlBinding(binding))
                return await controlFields.WriteAsync(field, binding, value, cancellationToken);
            if (modeProvider(binding.SourceId) == NativeMetricSnapshotZoneMode.Freeze)
                return new(field, "unsupported", "source-frozen-by-caller");
            return nvidiaWriter is null ? new(field, "unsupported", "metric-write-owner-not-bound")
                : await nvidiaWriter.WriteAsync(field, binding, value, cancellationToken);
        }
        finally { capture.SampleGate.Release(); }
    }
    private async ValueTask<HardwareMetricSnapshot> EnrichControlFieldsAsync(HardwareMetricSnapshot snapshot,
        MetricSampleRequest request, CancellationToken token, Action<string>? sourceQueryStarted)
    {
        if (request.IsCatalogProbe) snapshot = controlFields.Discover(snapshot);
        return request.IsCatalogProbe ? snapshot
            : await controlFields.ReadSelectedAsync(snapshot, request, token, sourceQueryStarted);
    }

    /// <summary>Explicit Host adaptation; validates under this owner's existing gate
    /// before invoking the original product permission/write callback.</summary>
    public async ValueTask<FieldWriteResult> ExecuteBoundControlWriteAsync(FieldReference field,
        MetricDeviceFieldBinding binding, FieldValue value,
        Func<CancellationToken, ValueTask<FieldWriteResult>> write, CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        await capture.SampleGate.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            return await controlFields.ExecuteBoundWriteAsync(field, binding, value, write, token);
        }
        finally { capture.SampleGate.Release(); }
    }

    /// <summary>Explicit direct restoration; Host plans retain their original
    /// release/retirement policy. Uses the original resource's only baseline.</summary>
    public async ValueTask<bool> RestoreControlFieldAsync(FieldReference field,
        IReadOnlySet<string>? stillConfiguredCapabilities = null, CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        await capture.SampleGate.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            var binding = ((MetricReadWriteBackend)CreateBackend()).Select([field]).Single().Binding;
            return await controlFields.RestoreAsync(binding, stillConfiguredCapabilities ?? new HashSet<string>(), token);
        }
        finally { capture.SampleGate.Release(); }
    }

    private void RefreshTopology()
    {
        var observed = topologySource.Capture(WindowsNpuMemoryTopology.EffectiveMaximumInstanceCount(configuration.CatalogConfiguration.RuleTemplates), WindowsIntelFanTopology.EffectiveMaximumInstanceCount(configuration.CatalogConfiguration.RuleTemplates), workspace?.ReadCatalog(), WindowsNpuEngineTopology.EffectiveMaximumInstanceCount(configuration.CatalogConfiguration.RuleTemplates));
        try
        {
            WindowsMetricCatalogTopologyPublication.RequireGpuTopology(observed);
            if (workspace is { } active)
            {
                var prior = active.ReadCatalog();
                var available = WindowsMetricCatalogTopologyPublication.RetainUnavailableOptionalIdentities(observed, prior);
                var desired = WindowsMetricCatalogTopologyPublication.ProjectCatalog(configuration.CatalogConfiguration, available, prior.HandleMap);
                if (WindowsMetricCatalogTopologyPublication.CanRetainCatalog(prior, desired))
                { topology = observed; topologyRefreshRequested = false; return; }
                observed = available;
            }
            NativeMetricSnapshotWorkspace? replacement = null;
            IDisposable? replacementDirectory = null;
            IDisposable? replacementEngineDirectory = null;
            try
            {
                replacement = new(configuration.Configuration, configuration.CatalogConfiguration);
                var catalog = WindowsMetricCatalogTopologyPublication.ProjectCatalog(configuration.CatalogConfiguration, observed,
                    replacement.CreateCatalogHandleMap());
                replacementDirectory = WindowsIntelFanTopology.RetainDirectory(catalog.SystemFanIdentities);
                replacementEngineDirectory = catalog.NpuEngineDirectory?.AcquireLease();
                var header = replacement.ReplaceCatalog(catalog, DateTimeOffset.UtcNow);
                if (header.CatalogFingerprint != catalog.NativeRowFingerprint || header.MetricCount != catalog.MetricIds.Count)
                    throw new InvalidOperationException("The native metric-snapshot catalog does not match the compiled manifest projection.");
                var prior = workspace;
                var priorDirectory = intelFanDirectoryLease;
                var priorEngineDirectory = npuEngineDirectoryLease;
                npuEngineDirectoryLease = replacementEngineDirectory;
                replacementEngineDirectory = null;
                intelFanDirectoryLease = replacementDirectory;
                replacementDirectory = null;
                workspace = replacement;
                replacement = null;
                workspaceIdentity = checked(workspaceIdentity + 1);
                topology = observed;
                topologyRefreshRequested = false;
                try { prior?.Dispose(); } finally { try { priorDirectory?.Dispose(); } finally { priorEngineDirectory?.Dispose(); } }
            }
            finally { try { replacement?.Dispose(); } finally { try { replacementDirectory?.Dispose(); } finally { replacementEngineDirectory?.Dispose(); } } }
        }
        catch when (workspace is not null)
        {
            // Preserve the declared catalog after failed refresh, as the original host owner does.
            topology = observed with { GpuStatus = NativeMetricSnapshotCatalogTopologyStatus.Unavailable,
                StorageStatus = NativeMetricSnapshotCatalogTopologyStatus.Unavailable,
                FanStatus = NativeMetricSnapshotCatalogTopologyStatus.Unavailable,
                NvmlFanStatus = NativeMetricSnapshotCatalogTopologyStatus.Unavailable,
                IgclFanStatus = NativeMetricSnapshotCatalogTopologyStatus.Unavailable,
                NotebookOemFanStatus = NativeMetricSnapshotCatalogTopologyStatus.Unavailable,
                BatteryStatus = NativeMetricSnapshotCatalogTopologyStatus.Unavailable,
                NpuEngineStatus = NativeMetricSnapshotCatalogTopologyStatus.Unavailable,
                NpuMemoryStatus = NativeMetricSnapshotCatalogTopologyStatus.Unavailable,
                NetworkInterfaceStatus = NativeMetricSnapshotCatalogTopologyStatus.Unavailable,
                NumaStatus = NativeMetricSnapshotCatalogTopologyStatus.Unavailable,
                IntelCpuCoreStatus = NativeMetricSnapshotCatalogTopologyStatus.Unavailable,
                IntelCpuPackageStatus = NativeMetricSnapshotCatalogTopologyStatus.Unavailable,
                IntelCpuLogicalProcessorStatus = NativeMetricSnapshotCatalogTopologyStatus.Unavailable,
                IntelCpuDieStatus = NativeMetricSnapshotCatalogTopologyStatus.Unavailable,
                IntelPlatformStatus = NativeMetricSnapshotCatalogTopologyStatus.Unavailable };
        }
        finally { try { observed.IgclFanDirectoryReference?.Dispose(); } finally { observed.NpuEngineDirectoryReference?.Dispose(); } }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        capture.SampleGate.Wait();
        try
        {
            capture.ReleasePlatformSensorDemand();
            try { workspace?.Dispose(); }
            finally
            {
                workspace = null;
                try { intelFanDirectoryLease?.Dispose(); }
                finally { intelFanDirectoryLease = null; try { npuEngineDirectoryLease?.Dispose(); } finally { npuEngineDirectoryLease = null; resources.Dispose(); } }
            }
        }
        finally { capture.SampleGate.Release(); }
    }
}

