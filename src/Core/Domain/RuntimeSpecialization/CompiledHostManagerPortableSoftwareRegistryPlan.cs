namespace ResourceManager.App.Domain.RuntimeSpecialization;

public sealed record CompiledHostManagerPortableSoftwareRegistryCapacityPlan(
    int MaximumRegistrationCount,
    int MaximumPathCount,
    int MaximumPersistenceOperationCount,
    int MaximumRegistrationSnapshotCount,
    int MaximumPathSnapshotCount,
    int MaximumExecutablePathByteCount,
    int MaximumRootPathByteCount,
    int RegistrationIndexCapacity,
    int PathIndexCapacity)
{
    public bool IsPublished => MaximumRegistrationCount > 0
        && MaximumPathCount >= MaximumRegistrationCount
        && MaximumPersistenceOperationCount is > 0
        && MaximumPersistenceOperationCount <= MaximumPathCount
        && MaximumRegistrationSnapshotCount is > 0
        && MaximumRegistrationSnapshotCount <= MaximumRegistrationCount
        && MaximumPathSnapshotCount is > 0
        && MaximumPathSnapshotCount <= MaximumPathCount
        && MaximumExecutablePathByteCount >= 3
        && MaximumRootPathByteCount >= 3
        && IsValidIndexCapacity(RegistrationIndexCapacity, MaximumRegistrationCount)
        && IsValidIndexCapacity(PathIndexCapacity, MaximumPathCount);

    private static bool IsValidIndexCapacity(int capacity, int count)
        => capacity >= count && capacity > 0 && (capacity & (capacity - 1)) == 0;
}

public sealed record CompiledHostManagerPortableSoftwareRegistryBuildPlan(
    uint AbiVersion,
    string NativeModule,
    string NativeBinaryFileName,
    string NativeBinarySha256,
    CompiledHostManagerPortableSoftwareRegistryCapacityPlan CapacityLimits)
{
    public bool IsPublished => AbiVersion == 0x0001_0000U
        && string.Equals(NativeModule, "software_identity_portable_registry", StringComparison.Ordinal)
        && !string.IsNullOrWhiteSpace(NativeBinaryFileName)
        && NativeBinarySha256.Length == 64
        && NativeBinarySha256.All(static value => char.IsAsciiHexDigit(value))
        && CapacityLimits.IsPublished;

    public static CompiledHostManagerPortableSoftwareRegistryBuildPlan Unpublished { get; } = new(
        0,
        string.Empty,
        string.Empty,
        string.Empty,
        new CompiledHostManagerPortableSoftwareRegistryCapacityPlan(0, 0, 0, 0, 0, 0, 0, 0, 0));
}

public sealed record CompiledHostManagerPortableSoftwareRegistryRecreatePlan(
    CompiledHostManagerPortableSoftwareRegistryCapacityPlan Capacity)
{
    public bool IsPublished => Capacity.IsPublished;

    public static CompiledHostManagerPortableSoftwareRegistryRecreatePlan Unpublished { get; } = new(
        new CompiledHostManagerPortableSoftwareRegistryCapacityPlan(0, 0, 0, 0, 0, 0, 0, 0, 0));
}

public sealed record CompiledHostManagerPortableSoftwareRegistryHotPublishPlan(
    ulong ConfigurationGeneration,
    int MaximumFutureSkewMilliseconds,
    long ResidentByteBudget)
{
    public bool IsPublished => ConfigurationGeneration > 0
        && MaximumFutureSkewMilliseconds >= 0
        && ResidentByteBudget > 0;

    public static CompiledHostManagerPortableSoftwareRegistryHotPublishPlan Unpublished { get; } = new(
        0,
        0,
        0);
}

public sealed record CompiledHostManagerPortableSoftwareRegistryPlan(
    CompiledHostManagerPortableSoftwareRegistryBuildPlan Build,
    CompiledHostManagerPortableSoftwareRegistryRecreatePlan Recreate,
    CompiledHostManagerPortableSoftwareRegistryHotPublishPlan HotPublish,
    ulong ConfigurationGeneration,
    string ConfigurationSha256)
{
    public bool IsPublished => Build.IsPublished
        && Recreate.IsPublished
        && HotPublish.IsPublished
        && ConfigurationGeneration > 0
        && HotPublish.ConfigurationGeneration == ConfigurationGeneration
        && ConfigurationSha256.Length == 64
        && ConfigurationSha256.All(static value => char.IsAsciiHexDigit(value));

    public static CompiledHostManagerPortableSoftwareRegistryPlan Unpublished { get; } = new(
        CompiledHostManagerPortableSoftwareRegistryBuildPlan.Unpublished,
        CompiledHostManagerPortableSoftwareRegistryRecreatePlan.Unpublished,
        CompiledHostManagerPortableSoftwareRegistryHotPublishPlan.Unpublished,
        0,
        string.Empty);
}
