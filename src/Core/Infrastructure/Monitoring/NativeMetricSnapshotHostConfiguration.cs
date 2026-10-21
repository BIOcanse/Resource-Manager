using ResourceManager.ReadWrite.Native;
using ResourceManager.ReadWrite.Windows.Metrics.Configuration;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.ReadWrite.Windows.Devices.Npu;
using ResourceManager.ReadWrite.Windows.Devices.Battery;
using ResourceManager.ReadWrite.Vendors.Intel.Msr;
namespace ResourceManager.App.Infrastructure.Monitoring;

internal static class NativeMetricSnapshotHostConfiguration
{
    // Standalone integration helper keeps the explicit ordinary library path.
    internal static NativeMetricSnapshotWorkspace CreateWorkspace(CompiledHostManagerMetricSnapshotPlan plan, string persistencePath)
        => CreateWorkspace(plan, persistencePath, NativeMetricSnapshotScratch.CreateOrdinary);

    internal static NativeMetricSnapshotWorkspace CreateWorkspace(CompiledHostManagerMetricSnapshotPlan plan, string persistencePath,
        Func<ulong, uint, INativeMetricSnapshotScratch> createScratch)
    {
        ArgumentNullException.ThrowIfNull(createScratch);
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.IsPublished) throw new ArgumentException("The metric-snapshot plan must be published.", nameof(plan));
        if (string.IsNullOrWhiteSpace(persistencePath) || !Path.IsPathFullyQualified(persistencePath))
            throw new ArgumentException("The metric-snapshot persistence path must be absolute.", nameof(persistencePath));
        var store = new NativeMetricSnapshotPersistenceStore(Path.GetFullPath(persistencePath));
        return new(CreateConfiguration(plan), CreateCatalogConfiguration(plan), new(store.Load, store.Save, store.Discard), createScratch);
    }

    internal static NativeMetricSnapshotCatalogConfiguration CreateCatalogConfiguration(CompiledHostManagerMetricSnapshotPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.IsPublished) throw new ArgumentException("The metric-snapshot plan must be published.", nameof(plan));
        return new(plan.HotPublish.CatalogGenerationBase, plan.HotPublish.CatalogManifestSha256,
            plan.Recreate.Capacity.MaximumGpuAdapterCount, plan.Recreate.Capacity.MaximumRuleCount,
            plan.Recreate.Capacity.MaximumMetricCount,
            plan.HotPublish.SourcePolicies.Select(static row => new NativeMetricSnapshotSourcePolicy(
                row.SourceId, row.SourceHandle, row.SourceRole, row.Priority, row.RetentionPolicy, row.Required, row.CapabilityMask, row.SemanticFingerprint)).ToImmutableArray(),
            plan.HotPublish.RuleTemplates.Select(static row => new NativeMetricSnapshotRuleTemplate(
                row.TemplateId, row.MetricIdTemplate, row.SourceId, row.SourceHandle, row.SourcePriority, row.ScopeExpansion, row.MetricKind, row.ScopeKind, row.ValueKind, row.RetentionPolicy, row.CapabilityMask, row.MetricFlags, row.MinimumValueBits, row.MaximumValueBits, row.GpuVendorMask, row.ApplicabilityMask, row.MaximumInstanceCount, row.SemanticFingerprint)).ToImmutableArray());
    }

    internal static NativeMetricSnapshotCatalogProjection CreateCatalogProjection(
        CompiledHostManagerMetricSnapshotPlan plan,
        IReadOnlyList<NativeMetricSnapshotGpuCatalogIdentity> gpuAdapters,
        IReadOnlyList<NativeMetricSnapshotOrdinalCatalogIdentity> storageSensors,
        IReadOnlyList<NativeMetricSnapshotOrdinalCatalogIdentity> systemFans,
        NativeMetricSnapshotCatalogHandleMap handleMap,
        MetricVendor cpuVendor = MetricVendor.All,
        IReadOnlyList<BatteryCatalogIdentity>? batteries = null,
        IReadOnlyList<NumaMemoryCatalogIdentity>? numaNodes = null,
        IReadOnlyList<IntelMsrCatalogIdentity>? intelCpus = null,
        IReadOnlyList<NetworkInterfaceCatalogIdentity>? networkInterfaces = null,
        IReadOnlyList<NpuMemoryCatalogIdentity>? npuMemoryIdentities = null,
        IReadOnlyList<NpuEngineCatalogIdentity>? npuEngineIdentities = null, IWindowsNpuEngineDirectory? npuEngineDirectory = null)
        => NativeMetricSnapshotCatalogProjection.Create(CreateCatalogConfiguration(plan), gpuAdapters,
            storageSensors, systemFans, handleMap, cpuVendor, batteries, numaNodes, intelCpus, networkInterfaces, npuMemoryIdentities, npuEngineIdentities, npuEngineDirectory);

    internal static NativeMetricSnapshotConfiguration CreateConfiguration(CompiledHostManagerMetricSnapshotPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var capacity = plan.Recreate.Capacity;
        var nativeCapacity = new MetricSnapshotCapacity(capacity.MaximumSourceCount, capacity.MaximumMetricCount, capacity.MaximumRuleCount, capacity.MaximumRequestedCount, capacity.MaximumObservationCount, capacity.MaximumGpuAdapterCount, capacity.MaximumPersistenceSourceCount, capacity.MaximumPersistenceRuleCount, capacity.MaximumPersistenceGpuCount, capacity.SourceIndexCapacity, capacity.MetricIndexCapacity, capacity.RuleIndexCapacity, capacity.GpuIndexCapacity, capacity.GpuLuidIndexCapacity, capacity.GpuKeyIndexCapacity, capacity.MaximumPlanMetricCount, capacity.MaximumSourceModeCount, capacity.MaximumSourcePlanCount, capacity.MaximumMetricPlanCount, capacity.ResidentByteBudget);
        var recreate = new MetricSnapshotRecreateConfiguration(nativeCapacity,
            plan.Recreate.CatalogContractVersion, plan.Recreate.ValueContractVersion, plan.Recreate.ObservationContractVersion,
            plan.Recreate.InventoryContractVersion, plan.Recreate.PersistenceContractVersion, plan.Recreate.CpuCounterContractVersion);
        var catalog = CreateCatalogConfiguration(plan);
        var hot = new MetricSnapshotPolicyConfiguration(plan.HotPublish.ConfigurationGeneration,
            plan.HotPublish.CatalogGenerationBase, plan.HotPublish.CatalogManifestVersion, plan.HotPublish.CatalogManifestSha256,
            plan.HotPublish.MaximumFutureSkewMilliseconds, plan.HotPublish.ResidentByteBudget, catalog.SourcePolicies, catalog.RuleTemplates);
        return WindowsMetricConfigurationCompiler.MapConfiguration(plan.Build.AbiVersion, recreate, hot);
    }

}
