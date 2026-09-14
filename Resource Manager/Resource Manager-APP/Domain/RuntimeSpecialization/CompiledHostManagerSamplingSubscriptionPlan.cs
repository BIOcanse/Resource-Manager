using System.Collections.Immutable;

namespace ResourceManager.App.Domain.RuntimeSpecialization;

public sealed record CompiledHostManagerSamplingSubscriptionRoleCapacityPlan(
    int RoleId,
    int MaximumSourceCount,
    int MaximumItemCount,
    int MaximumMembershipCount,
    int MaximumDueItemCount,
    int MaximumSourceViewCount,
    int MaximumExpiredSourceCount)
{
    public bool IsPublished => RoleId is >= 1 and <= CompiledHostManagerSamplingSubscriptionPlan.RoleCount
        && MaximumSourceCount > 0
        && MaximumItemCount > 0
        && MaximumMembershipCount >= MaximumSourceCount
        && MaximumMembershipCount >= MaximumItemCount
        && MaximumDueItemCount >= MaximumItemCount
        && MaximumSourceViewCount >= MaximumSourceCount
        && MaximumExpiredSourceCount >= MaximumSourceCount;
}

public sealed record CompiledHostManagerSamplingSubscriptionBuildPlan(
    uint AbiVersion,
    string NativeModule,
    string NativeBinaryFileName,
    string NativeBinarySha256,
    ImmutableArray<CompiledHostManagerSamplingSubscriptionRoleCapacityPlan> RoleCapacityLimits)
{
    public bool IsPublished => AbiVersion == 0x0003_0000U
        && string.Equals(NativeModule, "sampling_subscription", StringComparison.Ordinal)
        && !string.IsNullOrWhiteSpace(NativeBinaryFileName)
        && NativeBinarySha256.Length == 64
        && NativeBinarySha256.All(static value => char.IsAsciiHexDigit(value))
        && CompiledHostManagerSamplingSubscriptionPlan.HasExactRoles(RoleCapacityLimits)
        && RoleCapacityLimits.All(static role => role.IsPublished);

    public static CompiledHostManagerSamplingSubscriptionBuildPlan Unpublished { get; } = new(
        0,
        string.Empty,
        string.Empty,
        string.Empty,
        []);
}

public sealed record CompiledHostManagerSamplingSubscriptionRecreatePlan(
    ImmutableArray<CompiledHostManagerSamplingSubscriptionRoleCapacityPlan> Roles)
{
    public bool IsPublished => CompiledHostManagerSamplingSubscriptionPlan.HasExactRoles(Roles)
        && Roles.All(static role => role.IsPublished);

    public static CompiledHostManagerSamplingSubscriptionRecreatePlan Unpublished { get; } = new([]);
}

public sealed record CompiledHostManagerSamplingSubscriptionRoleHotPublishPlan(
    int RoleId,
    long DefaultIntervalMilliseconds,
    long MinimumIntervalMilliseconds,
    long ActiveTtlMilliseconds,
    long MaximumFutureSkewMilliseconds,
    long FreshnessGraceMilliseconds)
{
    public bool IsPublished => RoleId is >= 1 and <= CompiledHostManagerSamplingSubscriptionPlan.RoleCount
        && MinimumIntervalMilliseconds > 0
        && DefaultIntervalMilliseconds >= MinimumIntervalMilliseconds
        && ActiveTtlMilliseconds >= MinimumIntervalMilliseconds
        && MaximumFutureSkewMilliseconds >= 0
        && FreshnessGraceMilliseconds > 0;
}

public sealed record CompiledHostManagerSamplingSubscriptionHotPublishPlan(
    ulong ConfigurationGeneration,
    ImmutableArray<CompiledHostManagerSamplingSubscriptionRoleHotPublishPlan> Roles)
{
    public bool IsPublished => ConfigurationGeneration > 0
        && CompiledHostManagerSamplingSubscriptionPlan.HasExactRoles(Roles)
        && Roles.All(static role => role.IsPublished);

    public static CompiledHostManagerSamplingSubscriptionHotPublishPlan Unpublished { get; } = new(
        0,
        []);
}

public sealed record CompiledHostManagerSamplingSubscriptionPlan(
    CompiledHostManagerSamplingSubscriptionBuildPlan Build,
    CompiledHostManagerSamplingSubscriptionRecreatePlan Recreate,
    CompiledHostManagerSamplingSubscriptionHotPublishPlan HotPublish,
    ulong ConfigurationGeneration,
    string ConfigurationSha256)
{
    public const int RoleCount = 7;

    public bool IsPublished => Build.IsPublished
        && Recreate.IsPublished
        && HotPublish.IsPublished
        && ConfigurationGeneration > 0
        && HotPublish.ConfigurationGeneration == ConfigurationGeneration
        && ConfigurationSha256.Length == 64
        && ConfigurationSha256.All(static value => char.IsAsciiHexDigit(value));

    public static CompiledHostManagerSamplingSubscriptionPlan Unpublished { get; } = new(
        CompiledHostManagerSamplingSubscriptionBuildPlan.Unpublished,
        CompiledHostManagerSamplingSubscriptionRecreatePlan.Unpublished,
        CompiledHostManagerSamplingSubscriptionHotPublishPlan.Unpublished,
        0,
        string.Empty);

    internal static bool HasExactRoles<T>(ImmutableArray<T> roles)
        where T : notnull
    {
        if (roles.Length != RoleCount)
        {
            return false;
        }

        for (var index = 0; index < roles.Length; index++)
        {
            var roleId = roles[index] switch
            {
                CompiledHostManagerSamplingSubscriptionRoleCapacityPlan capacity => capacity.RoleId,
                CompiledHostManagerSamplingSubscriptionRoleHotPublishPlan hot => hot.RoleId,
                _ => 0
            };
            if (roleId != index + 1)
            {
                return false;
            }
        }

        return true;
    }
}
