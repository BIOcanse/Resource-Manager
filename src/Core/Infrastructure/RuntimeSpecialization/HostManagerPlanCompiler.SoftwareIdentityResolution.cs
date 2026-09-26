using System.Collections.Immutable;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization;

public sealed partial class HostManagerPlanCompiler
{
    private static CompiledHostManagerSoftwareIdentityResolutionBuildPlan
        CompileSoftwareIdentityResolutionBuild(
            uint abiVersion,
            HostManagerSoftwareIdentityResolutionCapacityProfile source,
            CompiledHostManagerNativeBinaryIdentity binary)
    {
        if (abiVersion != NativeSoftwareIdentityResolutionAbi.Version)
        {
            throw new InvalidDataException(
                "build_specialize.software_identity_resolution_abi_version does not match the binary.");
        }

        return new CompiledHostManagerSoftwareIdentityResolutionBuildPlan(
            abiVersion,
            "software_identity_resolution",
            binary.FileName,
            binary.Sha256,
            CompileSoftwareIdentityResolutionCapacity(
                source,
                "build_specialize.capacity_limits.software_identity_resolution"));
    }

    private static CompiledHostManagerSoftwareIdentityResolutionRecreatePlan
        CompileSoftwareIdentityResolutionRecreate(
            HostManagerSoftwareIdentityResolutionRecreateProfile source,
            CompiledHostManagerSoftwareIdentityResolutionBuildPlan build)
    {
        ArgumentNullException.ThrowIfNull(source);
        var capacity = CompileSoftwareIdentityResolutionCapacity(
            source.Capacity ?? throw Missing("host_recreate.software_identity_resolution.capacity"),
            "host_recreate.software_identity_resolution.capacity");
        ValidateMaximum(capacity.MaximumPolicyCount, build.CapacityLimits.MaximumPolicyCount, "host_recreate.software_identity_resolution.capacity.maximum_policy_count");
        ValidateMaximum(capacity.MaximumObservationCount, build.CapacityLimits.MaximumObservationCount, "host_recreate.software_identity_resolution.capacity.maximum_observation_count");
        ValidateMaximum(capacity.PolicyIndexCapacity, build.CapacityLimits.PolicyIndexCapacity, "host_recreate.software_identity_resolution.capacity.policy_index_capacity");

        var sourceIds = (source.SourceIds ?? throw Missing("host_recreate.software_identity_resolution.source_ids"))
            .ToImmutableArray();
        if (sourceIds.IsDefaultOrEmpty
            || sourceIds.Length > capacity.MaximumPolicyCount
            || !sourceIds.SequenceEqual(sourceIds.Order())
            || sourceIds.Distinct().Count() != sourceIds.Length
            || sourceIds.Any(static value => value is < 1 or > NativeSoftwareIdentityResolutionAbi.MaximumSourceCount))
        {
            throw new InvalidDataException(
                "host_recreate.software_identity_resolution.source_ids must be unique, ascending, non-empty known source ids.");
        }

        return new CompiledHostManagerSoftwareIdentityResolutionRecreatePlan(capacity, sourceIds);
    }

    private static CompiledHostManagerSoftwareIdentityResolutionHotPublishPlan
        CompileSoftwareIdentityResolutionHotPublish(
            HostManagerSoftwareIdentityResolutionHotPublishProfile source,
            CompiledHostManagerSoftwareIdentityResolutionRecreatePlan recreate,
            int profileRevision)
    {
        ArgumentNullException.ThrowIfNull(source);
        ValidateNonNegative(
            source.MaximumFutureSkewMilliseconds,
            "hot_publish.software_identity_resolution.maximum_future_skew_ms");
        ValidatePositive(
            source.ResidentByteBudget,
            "hot_publish.software_identity_resolution.resident_byte_budget");

        var rows = source.SourcePolicies ?? throw Missing(
            "hot_publish.software_identity_resolution.source_policies");
        var policies = rows
            .Select(static row => new CompiledHostManagerSoftwareIdentitySourcePolicyPlan(
                row.SourceId,
                row.Priority,
                row.StopOnUnavailable))
            .ToImmutableArray();
        if (policies.Length != recreate.SourceIds.Length
            || policies.Any(static value => !value.IsPublished)
            || policies.Select(static value => value.SourceId).Distinct().Count() != policies.Length
            || policies.Select(static value => value.Priority).Distinct().Count() != policies.Length
            || !policies.SequenceEqual(policies.OrderBy(static value => value.Priority))
            || !recreate.SourceIds.SequenceEqual(policies.Select(static value => value.SourceId).Order()))
        {
            throw new InvalidDataException(
                "hot_publish.software_identity_resolution.source_policies must exactly cover source_ids with unique ascending priorities.");
        }

        return new CompiledHostManagerSoftwareIdentityResolutionHotPublishPlan(
            (ulong)checked((uint)profileRevision) << 32,
            source.MaximumFutureSkewMilliseconds,
            source.ResidentByteBudget,
            policies);
    }

    private static CompiledHostManagerSoftwareIdentityResolutionPlan
        CompileSoftwareIdentityResolutionPlan(
            CompiledHostManagerSoftwareIdentityResolutionBuildPlan build,
            CompiledHostManagerSoftwareIdentityResolutionRecreatePlan recreate,
            CompiledHostManagerSoftwareIdentityResolutionHotPublishPlan hotPublish)
    {
        var configurationSha256 = HostManagerPlanIdentity.ComputeDigest(new
        {
            build.AbiVersion,
            recreate,
            hotPublish
        });
        return new CompiledHostManagerSoftwareIdentityResolutionPlan(
            build,
            recreate,
            hotPublish,
            hotPublish.ConfigurationGeneration,
            configurationSha256);
    }

    private static CompiledHostManagerSoftwareIdentityResolutionCapacityPlan
        CompileSoftwareIdentityResolutionCapacity(
            HostManagerSoftwareIdentityResolutionCapacityProfile source,
            string path)
    {
        ArgumentNullException.ThrowIfNull(source);
        ValidatePositive(source.MaximumPolicyCount, $"{path}.maximum_policy_count");
        ValidatePositive(source.MaximumObservationCount, $"{path}.maximum_observation_count");
        ValidatePositive(source.PolicyIndexCapacity, $"{path}.policy_index_capacity");
        if (source.MaximumPolicyCount > NativeSoftwareIdentityResolutionAbi.MaximumSourceCount
            || !IsPowerOfTwoAtLeast(source.PolicyIndexCapacity, source.MaximumPolicyCount))
        {
            throw new InvalidDataException(
                $"{path} violates the published software identity resolution capacity relations.");
        }
        return new CompiledHostManagerSoftwareIdentityResolutionCapacityPlan(
            source.MaximumPolicyCount,
            source.MaximumObservationCount,
            source.PolicyIndexCapacity);
    }
}
