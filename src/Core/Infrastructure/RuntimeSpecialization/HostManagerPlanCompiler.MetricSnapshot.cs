using System.Collections.Immutable;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.ReadWrite.Native;
using ResourceManager.ReadWrite.Windows.Metrics.Configuration;
namespace ResourceManager.App.Infrastructure.RuntimeSpecialization;

public sealed partial class HostManagerPlanCompiler
{
    private static CompiledHostManagerMetricSnapshotBuildPlan CompileMetricSnapshotBuild(
        uint abiVersion,
        HostManagerMetricSnapshotCapacityProfile source,
        IReadOnlySet<string> nativeModules)
    {
        if (abiVersion != NativeMetricSnapshotAbi.Version)
        {
            throw new InvalidDataException(
                "build_specialize.metric_snapshot_abi_version must match the native metric-snapshot ABI.");
        }
        if (!nativeModules.Contains("metric_snapshot"))
        {
            throw new InvalidDataException(
                "build_specialize.native_modules must include metric_snapshot.");
        }

        return new CompiledHostManagerMetricSnapshotBuildPlan(
            abiVersion,
            "metric_snapshot",
            CompileMetricSnapshotCapacity(
                source,
                "build_specialize.capacity_limits.metric_snapshot"));
    }
    private static CompiledHostManagerMetricSnapshotPlan CompileMetricSnapshotPlan(
        CompiledHostManagerMetricSnapshotBuildPlan build,
        CompiledHostManagerMetricSnapshotRecreatePlan recreate,
        CompiledHostManagerMetricSnapshotHotPublishPlan hotPublish)
    {
        var result = new CompiledHostManagerMetricSnapshotPlan(
            build,
            recreate,
            hotPublish,
            HostManagerPlanIdentity.ComputeDigest(new
            {
                Module = "metric_snapshot",
                Build = build,
                Recreate = recreate
            }),
            HostManagerPlanIdentity.ComputeDigest(new
            {
                Module = "metric_snapshot",
                HotPublish = hotPublish
            }));
        if (!result.IsPublished)
        {
            throw new InvalidDataException(
                "The compiled metric-snapshot plan is invalid.");
        }

        return result;
    }

    private static CompiledHostManagerMetricSnapshotCapacityPlan CompileMetricSnapshotCapacity(HostManagerMetricSnapshotCapacityProfile source, string path)
    {
        var value = WindowsMetricConfigurationCompiler.CompileMetricSnapshotCapacity(source, path);
        return new(value.MaximumSourceCount, value.MaximumMetricCount, value.MaximumRuleCount, value.MaximumRequestedCount, value.MaximumObservationCount, value.MaximumGpuAdapterCount, value.MaximumPersistenceSourceCount, value.MaximumPersistenceRuleCount, value.MaximumPersistenceGpuCount, value.SourceIndexCapacity, value.MetricIndexCapacity, value.RuleIndexCapacity, value.GpuIndexCapacity, value.GpuLuidIndexCapacity, value.GpuKeyIndexCapacity, value.MaximumPlanMetricCount, value.MaximumSourceModeCount, value.MaximumSourcePlanCount, value.MaximumMetricPlanCount, value.ResidentByteBudget);
    }

    private static MetricSnapshotCapacity ToMetricCapacity(CompiledHostManagerMetricSnapshotCapacityPlan value)
        => new(value.MaximumSourceCount, value.MaximumMetricCount, value.MaximumRuleCount, value.MaximumRequestedCount, value.MaximumObservationCount, value.MaximumGpuAdapterCount, value.MaximumPersistenceSourceCount, value.MaximumPersistenceRuleCount, value.MaximumPersistenceGpuCount, value.SourceIndexCapacity, value.MetricIndexCapacity, value.RuleIndexCapacity, value.GpuIndexCapacity, value.GpuLuidIndexCapacity, value.GpuKeyIndexCapacity, value.MaximumPlanMetricCount, value.MaximumSourceModeCount, value.MaximumSourcePlanCount, value.MaximumMetricPlanCount, value.ResidentByteBudget);

    private static CompiledHostManagerMetricSnapshotRecreatePlan CompileMetricSnapshotRecreate(HostManagerMetricSnapshotRecreateProfile source, CompiledHostManagerMetricSnapshotBuildPlan build)
    {
        var value = WindowsMetricConfigurationCompiler.CompileRecreate(source, ToMetricCapacity(build.CapacityLimits));
        return new(CompileMetricSnapshotCapacity(source.Capacity, "host_recreate.metric_snapshot.capacity"),
            NormalizeStrictRelativePath(source.PersistenceRelativePath, "host_recreate.metric_snapshot.persistence_relative_path"),
            value.CatalogContractVersion, value.ValueContractVersion, value.ObservationContractVersion,
            value.InventoryContractVersion, value.PersistenceContractVersion, value.CpuCounterContractVersion);
    }

    private static CompiledHostManagerMetricSnapshotHotPublishPlan CompileMetricSnapshotHotPublish(HostManagerMetricSnapshotHotPublishProfile source,
        CompiledHostManagerMetricSnapshotRecreatePlan recreate, int profileRevision)
    {
        var nativeRecreate = new MetricSnapshotRecreateConfiguration(ToMetricCapacity(recreate.Capacity),
            recreate.CatalogContractVersion, recreate.ValueContractVersion, recreate.ObservationContractVersion,
            recreate.InventoryContractVersion, recreate.PersistenceContractVersion, recreate.CpuCounterContractVersion);
        var value = WindowsMetricConfigurationCompiler.CompileMetricSnapshotHotPublish(source, nativeRecreate, profileRevision);
        return new(value.ConfigurationGeneration, value.CatalogGenerationBase, value.CatalogManifestVersion,
            value.CatalogManifestSha256, value.MaximumFutureSkewMilliseconds, value.ResidentByteBudget,
            value.SourcePolicies.Select(static row => new CompiledHostManagerMetricSnapshotSourcePolicy(row.SourceId, row.SourceHandle, row.SourceRole, row.Priority, row.RetentionPolicy, row.Required, row.CapabilityMask, row.SemanticFingerprint)).ToImmutableArray(),
            value.RuleTemplates.Select(static row => new CompiledHostManagerMetricSnapshotRuleTemplate(row.TemplateId, row.MetricIdTemplate, row.SourceId, row.SourceHandle, row.SourcePriority, row.ScopeExpansion, row.MetricKind, row.ScopeKind, row.ValueKind, row.RetentionPolicy, row.CapabilityMask, row.MetricFlags, row.MinimumValueBits, row.MaximumValueBits, row.GpuVendorMask, row.ApplicabilityMask, row.MaximumInstanceCount, row.SemanticFingerprint)).ToImmutableArray());
    }
}
