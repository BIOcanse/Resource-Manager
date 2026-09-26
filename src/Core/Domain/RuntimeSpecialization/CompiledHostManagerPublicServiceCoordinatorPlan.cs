using System.Collections.Immutable;

namespace ResourceManager.App.Domain.RuntimeSpecialization;

public sealed record CompiledHostManagerPublicServiceCoordinatorCapacityPlan(
    int MaximumCapabilityCount,
    int MaximumRouteCount,
    int MaximumModelCount,
    int MaximumModelAliasCount,
    int MaximumRequestCount,
    int MaximumRateBucketCount,
    int MaximumLeaseCount,
    int MaximumSubscriptionCount,
    int MaximumTaskCount,
    int CapabilityIndexCapacity,
    int ModelIndexCapacity,
    int AliasIndexCapacity,
    int RequestIndexCapacity,
    int RateBucketIndexCapacity,
    int LeaseIndexCapacity,
    int SubscriptionIndexCapacity,
    int TaskIndexCapacity,
    int MaximumCatalogTextBytes,
    int MaximumModelTextBytes,
    long ResidentByteBudget)
{
    public bool IsPublished => MaximumCapabilityCount > 0
        && MaximumRouteCount > 0
        && MaximumModelCount > 0
        && MaximumModelAliasCount > 0
        && MaximumRequestCount > 0
        && MaximumRateBucketCount > 0
        && MaximumLeaseCount > 0
        && MaximumSubscriptionCount > 0
        && MaximumTaskCount > 0
        && IsValidIndex(CapabilityIndexCapacity, MaximumCapabilityCount)
        && IsValidIndex(ModelIndexCapacity, MaximumModelCount)
        && IsValidIndex(AliasIndexCapacity, MaximumModelAliasCount)
        && IsValidIndex(RequestIndexCapacity, MaximumRequestCount)
        && IsValidIndex(RateBucketIndexCapacity, MaximumRateBucketCount)
        && IsValidIndex(LeaseIndexCapacity, MaximumLeaseCount)
        && IsValidIndex(SubscriptionIndexCapacity, MaximumSubscriptionCount)
        && IsValidIndex(TaskIndexCapacity, MaximumTaskCount)
        && MaximumCatalogTextBytes > 0
        && MaximumModelTextBytes > 0
        && ResidentByteBudget > 0;

    public bool FitsWithin(CompiledHostManagerPublicServiceCoordinatorCapacityPlan maximum)
        => MaximumCapabilityCount <= maximum.MaximumCapabilityCount
            && MaximumRouteCount <= maximum.MaximumRouteCount
            && MaximumModelCount <= maximum.MaximumModelCount
            && MaximumModelAliasCount <= maximum.MaximumModelAliasCount
            && MaximumRequestCount <= maximum.MaximumRequestCount
            && MaximumRateBucketCount <= maximum.MaximumRateBucketCount
            && MaximumLeaseCount <= maximum.MaximumLeaseCount
            && MaximumSubscriptionCount <= maximum.MaximumSubscriptionCount
            && MaximumTaskCount <= maximum.MaximumTaskCount
            && CapabilityIndexCapacity <= maximum.CapabilityIndexCapacity
            && ModelIndexCapacity <= maximum.ModelIndexCapacity
            && AliasIndexCapacity <= maximum.AliasIndexCapacity
            && RequestIndexCapacity <= maximum.RequestIndexCapacity
            && RateBucketIndexCapacity <= maximum.RateBucketIndexCapacity
            && LeaseIndexCapacity <= maximum.LeaseIndexCapacity
            && SubscriptionIndexCapacity <= maximum.SubscriptionIndexCapacity
            && TaskIndexCapacity <= maximum.TaskIndexCapacity
            && MaximumCatalogTextBytes <= maximum.MaximumCatalogTextBytes
            && MaximumModelTextBytes <= maximum.MaximumModelTextBytes
            && ResidentByteBudget <= maximum.ResidentByteBudget;

    public static CompiledHostManagerPublicServiceCoordinatorCapacityPlan Unpublished { get; } =
        new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    private static bool IsValidIndex(int capacity, int minimum)
        => capacity >= minimum && capacity > 0 && (capacity & (capacity - 1)) == 0;
}

public sealed record CompiledHostManagerPublicServiceCoordinatorBuildPlan(
    uint AbiVersion,
    string NativeModule,
    string NativeBinaryFileName,
    string NativeBinarySha256,
    CompiledHostManagerPublicServiceCoordinatorCapacityPlan CapacityLimits)
{
    public bool IsPublished => AbiVersion == 0x0003_0000U
        && string.Equals(NativeModule, "public_service_coordinator", StringComparison.Ordinal)
        && !string.IsNullOrWhiteSpace(NativeBinaryFileName)
        && NativeBinarySha256.Length == 64
        && NativeBinarySha256.All(static value => char.IsAsciiHexDigit(value))
        && CapacityLimits.IsPublished;

    public static CompiledHostManagerPublicServiceCoordinatorBuildPlan Unpublished { get; } = new(
        0,
        string.Empty,
        string.Empty,
        string.Empty,
        CompiledHostManagerPublicServiceCoordinatorCapacityPlan.Unpublished);
}

public sealed record CompiledHostManagerPublicServiceCoordinatorRecreatePlan(
    CompiledHostManagerPublicServiceCoordinatorCapacityPlan Capacity,
    int MaximumConcurrentModelTasks,
    int MaximumRequestsPerRateWindow,
    int MaximumInflightRequestsPerCaller,
    uint RetryableTaskOutcomeMask,
    int MaximumTaskAttemptCount,
    uint RetryableHttpStatusPolicyMask,
    long RateWindowMilliseconds,
    long RequestTimeoutMilliseconds,
    long LeaseTimeoutMilliseconds,
    long SubscriptionTimeoutMilliseconds,
    long TaskTimeoutMilliseconds,
    long RetryDelayMilliseconds,
    long ModelCatalogAcquisitionIntervalMilliseconds,
    long ModelCatalogLastGoodLifetimeMilliseconds,
    long ModelCatalogAcquisitionTimeoutMilliseconds,
    bool LoopbackOnly)
{
    public bool IsPublished => Capacity.IsPublished
        && MaximumConcurrentModelTasks > 0
        && MaximumConcurrentModelTasks <= Capacity.MaximumTaskCount
        && MaximumRequestsPerRateWindow > 0
        && MaximumInflightRequestsPerCaller > 0
        && MaximumInflightRequestsPerCaller <= Capacity.MaximumRequestCount
        && (RetryableTaskOutcomeMask & ~0x0000_00DCU) == 0
        && MaximumTaskAttemptCount > 0
        && (RetryableHttpStatusPolicyMask & ~0x0000_0007U) == 0
        && RateWindowMilliseconds > 0
        && RequestTimeoutMilliseconds > 0
        && LeaseTimeoutMilliseconds > 0
        && SubscriptionTimeoutMilliseconds > 0
        && TaskTimeoutMilliseconds > 0
        && RetryDelayMilliseconds > 0
        && ModelCatalogAcquisitionIntervalMilliseconds > 0
        && ModelCatalogLastGoodLifetimeMilliseconds
            >= ModelCatalogAcquisitionIntervalMilliseconds
        && ModelCatalogAcquisitionTimeoutMilliseconds > 0
        && LoopbackOnly;

    public static CompiledHostManagerPublicServiceCoordinatorRecreatePlan Unpublished { get; } =
        new(
            Capacity: CompiledHostManagerPublicServiceCoordinatorCapacityPlan.Unpublished,
            MaximumConcurrentModelTasks: 0,
            MaximumRequestsPerRateWindow: 0,
            MaximumInflightRequestsPerCaller: 0,
            RetryableTaskOutcomeMask: 0,
            MaximumTaskAttemptCount: 0,
            RetryableHttpStatusPolicyMask: 0,
            RateWindowMilliseconds: 0,
            RequestTimeoutMilliseconds: 0,
            LeaseTimeoutMilliseconds: 0,
            SubscriptionTimeoutMilliseconds: 0,
            TaskTimeoutMilliseconds: 0,
            RetryDelayMilliseconds: 0,
            ModelCatalogAcquisitionIntervalMilliseconds: 0,
            ModelCatalogLastGoodLifetimeMilliseconds: 0,
            ModelCatalogAcquisitionTimeoutMilliseconds: 0,
            LoopbackOnly: false);
}

public sealed record CompiledHostManagerPublicServiceCapabilityPlan(
    ulong Handle,
    ulong PayloadHandle,
    string Id,
    string DisplayName,
    string Description,
    bool Enabled,
    bool Available)
{
    public bool IsPublished => Handle > 0
        && PayloadHandle > 0
        && !string.IsNullOrWhiteSpace(Id)
        && !string.IsNullOrWhiteSpace(DisplayName)
        && !string.IsNullOrWhiteSpace(Description);
}

public sealed record CompiledHostManagerPublicServiceRoutePlan(
    ulong Handle,
    ulong CapabilityHandle,
    ulong PayloadHandle,
    string Path,
    uint MethodMask,
    bool ExactPath,
    bool CatalogRoute,
    bool BypassRateLimit)
{
    public bool IsPublished => Handle > 0
        && PayloadHandle > 0
        && Path.StartsWith("/", StringComparison.Ordinal)
        && MethodMask > 0
        && (CatalogRoute ? CapabilityHandle == 0 : CapabilityHandle > 0);
}

public sealed record CompiledHostManagerPublicServiceCoordinatorHotPublishPlan(
    ulong ConfigurationGeneration,
    string ServiceName,
    string ApiVersion,
    string BasePath,
    bool ServiceEnabled,
    ulong AnonymousCallerHandle,
    ulong AiProviderHandle,
    string AiProvider,
    string AiEndpoint,
    bool AiAutoStart,
    uint ModelAliasProjectionContractVersion,
    long LoadTaskBaseScore,
    long UnloadTaskBaseScore,
    ImmutableArray<CompiledHostManagerPublicServiceCapabilityPlan> Capabilities,
    ImmutableArray<CompiledHostManagerPublicServiceRoutePlan> Routes)
{
    public bool IsPublished => ConfigurationGeneration > 0
        && !string.IsNullOrWhiteSpace(ServiceName)
        && string.Equals(ApiVersion, "1.0.0", StringComparison.Ordinal)
        && BasePath.StartsWith("/", StringComparison.Ordinal)
        && AnonymousCallerHandle > 0
        && AiProviderHandle > 0
        && string.Equals(AiProvider, "lm-studio", StringComparison.Ordinal)
        && Uri.TryCreate(AiEndpoint, UriKind.Absolute, out var endpoint)
        && endpoint.IsLoopback
        && ModelAliasProjectionContractVersion == 0x0001_0000U
        && LoadTaskBaseScore > 0
        && UnloadTaskBaseScore > 0
        && !Capabilities.IsDefaultOrEmpty
        && !Routes.IsDefaultOrEmpty
        && Capabilities.All(static item => item.IsPublished)
        && Routes.All(static item => item.IsPublished)
        && Capabilities.Select(static item => item.Handle).Distinct().Count()
            == Capabilities.Length
        && Routes.Select(static item => item.Handle).Distinct().Count() == Routes.Length
        && Routes.All(route => route.CatalogRoute
            ? route.CapabilityHandle == 0
            : Capabilities.Any(
                capability => capability.Handle == route.CapabilityHandle));

    public static CompiledHostManagerPublicServiceCoordinatorHotPublishPlan Unpublished { get; } =
        new(
            0,
            string.Empty,
            string.Empty,
            string.Empty,
            false,
            0,
            0,
            string.Empty,
            string.Empty,
            false,
            0,
            0,
            0,
            [],
            []);
}

public sealed record CompiledHostManagerPublicServiceCoordinatorPlan(
    CompiledHostManagerPublicServiceCoordinatorBuildPlan Build,
    CompiledHostManagerPublicServiceCoordinatorRecreatePlan Recreate,
    CompiledHostManagerPublicServiceCoordinatorHotPublishPlan HotPublish,
    string ConfigurationSha256)
{
    public bool IsPublished => Build.IsPublished
        && Recreate.IsPublished
        && HotPublish.IsPublished
        && Recreate.Capacity.FitsWithin(Build.CapacityLimits)
        && HotPublish.Capabilities.Length <= Recreate.Capacity.MaximumCapabilityCount
        && HotPublish.Routes.Length <= Recreate.Capacity.MaximumRouteCount
        && ConfigurationSha256.Length == 64
        && ConfigurationSha256.All(static value => char.IsAsciiHexDigit(value));

    public static CompiledHostManagerPublicServiceCoordinatorPlan Unpublished { get; } = new(
        CompiledHostManagerPublicServiceCoordinatorBuildPlan.Unpublished,
        CompiledHostManagerPublicServiceCoordinatorRecreatePlan.Unpublished,
        CompiledHostManagerPublicServiceCoordinatorHotPublishPlan.Unpublished,
        string.Empty);
}
