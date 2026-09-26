using System.Collections.Immutable;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization;

public sealed partial class HostManagerPlanCompiler
{
    private static CompiledHostManagerSamplingSubscriptionBuildPlan CompileSamplingSubscriptionBuild(
        uint abiVersion,
        HostManagerSamplingSubscriptionRoleCapacityProfile[]? source,
        CompiledHostManagerNativeBinaryIdentity binary)
    {
        if (abiVersion != NativeSamplingSubscriptionAbi.Version)
        {
            throw new InvalidDataException(
                "build_specialize.sampling_subscription_abi_version does not match the binary.");
        }

        return new CompiledHostManagerSamplingSubscriptionBuildPlan(
            abiVersion,
            "sampling_subscription",
            binary.FileName,
            binary.Sha256,
            CompileSamplingRoleCapacities(
                source,
                "build_specialize.capacity_limits.sampling_subscription_roles"));
    }

    private static CompiledHostManagerSamplingSubscriptionRecreatePlan CompileSamplingSubscriptionRecreate(
        HostManagerSamplingSubscriptionRecreateProfile source,
        CompiledHostManagerSamplingSubscriptionBuildPlan build)
    {
        ArgumentNullException.ThrowIfNull(source);
        var roles = CompileSamplingRoleCapacities(
            source.Roles,
            "host_recreate.sampling_subscription.roles");
        for (var index = 0; index < roles.Length; index++)
        {
            var actual = roles[index];
            var limit = build.RoleCapacityLimits[index];
            ValidateMaximum(actual.MaximumSourceCount, limit.MaximumSourceCount, $"host_recreate.sampling_subscription.roles[{index}].maximum_source_count");
            ValidateMaximum(actual.MaximumItemCount, limit.MaximumItemCount, $"host_recreate.sampling_subscription.roles[{index}].maximum_item_count");
            ValidateMaximum(actual.MaximumMembershipCount, limit.MaximumMembershipCount, $"host_recreate.sampling_subscription.roles[{index}].maximum_membership_count");
            ValidateMaximum(actual.MaximumDueItemCount, limit.MaximumDueItemCount, $"host_recreate.sampling_subscription.roles[{index}].maximum_due_item_count");
            ValidateMaximum(actual.MaximumSourceViewCount, limit.MaximumSourceViewCount, $"host_recreate.sampling_subscription.roles[{index}].maximum_source_view_count");
            ValidateMaximum(actual.MaximumExpiredSourceCount, limit.MaximumExpiredSourceCount, $"host_recreate.sampling_subscription.roles[{index}].maximum_expired_source_count");
        }

        return new CompiledHostManagerSamplingSubscriptionRecreatePlan(roles);
    }

    private static CompiledHostManagerSamplingSubscriptionHotPublishPlan CompileSamplingSubscriptionHotPublish(
        HostManagerSamplingSubscriptionHotPublishProfile source,
        int profileRevision)
    {
        ArgumentNullException.ThrowIfNull(source);
        var rows = source.Roles ?? throw Missing("hot_publish.sampling_subscription.roles");
        if (rows.Length != CompiledHostManagerSamplingSubscriptionPlan.RoleCount)
        {
            throw new InvalidDataException(
                $"hot_publish.sampling_subscription.roles must contain exactly {CompiledHostManagerSamplingSubscriptionPlan.RoleCount} rows.");
        }

        var builder = ImmutableArray.CreateBuilder<CompiledHostManagerSamplingSubscriptionRoleHotPublishPlan>(
            CompiledHostManagerSamplingSubscriptionPlan.RoleCount);
        for (var index = 0; index < rows.Length; index++)
        {
            var row = rows[index] ?? throw Missing($"hot_publish.sampling_subscription.roles[{index}]");
            var expectedRoleId = index + 1;
            if (row.RoleId != expectedRoleId)
            {
                throw new InvalidDataException(
                    $"hot_publish.sampling_subscription.roles must be ordered and exactly cover role_id 1..{CompiledHostManagerSamplingSubscriptionPlan.RoleCount}.");
            }

            ValidatePositive(row.MinimumIntervalMilliseconds, $"hot_publish.sampling_subscription.roles[{index}].minimum_interval_ms");
            if (row.DefaultIntervalMilliseconds < row.MinimumIntervalMilliseconds)
            {
                throw new InvalidDataException(
                    $"hot_publish.sampling_subscription.roles[{index}].default_interval_ms must be at least minimum_interval_ms.");
            }
            if (row.ActiveTtlMilliseconds < row.MinimumIntervalMilliseconds)
            {
                throw new InvalidDataException(
                    $"hot_publish.sampling_subscription.roles[{index}].active_ttl_ms must be at least minimum_interval_ms.");
            }
            if (row.MaximumFutureSkewMilliseconds < 0)
            {
                throw new InvalidDataException(
                    $"hot_publish.sampling_subscription.roles[{index}].maximum_future_skew_ms must not be negative.");
            }
            ValidatePositive(
                row.FreshnessGraceMilliseconds,
                $"hot_publish.sampling_subscription.roles[{index}].freshness_grace_ms");
            builder.Add(new CompiledHostManagerSamplingSubscriptionRoleHotPublishPlan(
                row.RoleId,
                row.DefaultIntervalMilliseconds,
                row.MinimumIntervalMilliseconds,
                row.ActiveTtlMilliseconds,
                row.MaximumFutureSkewMilliseconds,
                row.FreshnessGraceMilliseconds));
        }

        return new CompiledHostManagerSamplingSubscriptionHotPublishPlan(
            ((ulong)checked((uint)profileRevision) << 32) | NativeSamplingSubscriptionAbi.Version,
            builder.MoveToImmutable());
    }

    private static CompiledHostManagerSamplingSubscriptionPlan CompileSamplingSubscriptionPlan(
        CompiledHostManagerSamplingSubscriptionBuildPlan build,
        CompiledHostManagerSamplingSubscriptionRecreatePlan recreate,
        CompiledHostManagerSamplingSubscriptionHotPublishPlan hotPublish)
    {
        var configurationSha256 = HostManagerPlanIdentity.ComputeDigest(new
        {
            build.AbiVersion,
            recreate,
            hotPublish
        });
        return new CompiledHostManagerSamplingSubscriptionPlan(
            build,
            recreate,
            hotPublish,
            hotPublish.ConfigurationGeneration,
            configurationSha256);
    }

    private static ImmutableArray<CompiledHostManagerSamplingSubscriptionRoleCapacityPlan>
        CompileSamplingRoleCapacities(
            HostManagerSamplingSubscriptionRoleCapacityProfile[]? source,
            string path)
    {
        var rows = source ?? throw Missing(path);
        if (rows.Length != CompiledHostManagerSamplingSubscriptionPlan.RoleCount)
        {
            throw new InvalidDataException(
                $"{path} must contain exactly {CompiledHostManagerSamplingSubscriptionPlan.RoleCount} rows.");
        }

        var builder = ImmutableArray.CreateBuilder<CompiledHostManagerSamplingSubscriptionRoleCapacityPlan>(
            CompiledHostManagerSamplingSubscriptionPlan.RoleCount);
        for (var index = 0; index < rows.Length; index++)
        {
            var row = rows[index] ?? throw Missing($"{path}[{index}]");
            if (row.RoleId != index + 1)
            {
                throw new InvalidDataException($"{path} must be ordered and exactly cover role_id 1..{CompiledHostManagerSamplingSubscriptionPlan.RoleCount}.");
            }
            ValidatePositive(row.MaximumSourceCount, $"{path}[{index}].maximum_source_count");
            ValidatePositive(row.MaximumItemCount, $"{path}[{index}].maximum_item_count");
            ValidatePositive(row.MaximumMembershipCount, $"{path}[{index}].maximum_membership_count");
            ValidatePositive(row.MaximumDueItemCount, $"{path}[{index}].maximum_due_item_count");
            ValidatePositive(row.MaximumSourceViewCount, $"{path}[{index}].maximum_source_view_count");
            ValidatePositive(row.MaximumExpiredSourceCount, $"{path}[{index}].maximum_expired_source_count");
            if (row.MaximumMembershipCount < row.MaximumSourceCount
                || row.MaximumMembershipCount < row.MaximumItemCount
                || row.MaximumDueItemCount < row.MaximumItemCount
                || row.MaximumSourceViewCount < row.MaximumSourceCount
                || row.MaximumExpiredSourceCount < row.MaximumSourceCount)
            {
                throw new InvalidDataException($"{path}[{index}] violates the published sampling capacity relations.");
            }

            builder.Add(new CompiledHostManagerSamplingSubscriptionRoleCapacityPlan(
                row.RoleId,
                row.MaximumSourceCount,
                row.MaximumItemCount,
                row.MaximumMembershipCount,
                row.MaximumDueItemCount,
                row.MaximumSourceViewCount,
                row.MaximumExpiredSourceCount));
        }

        return builder.MoveToImmutable();
    }
}
