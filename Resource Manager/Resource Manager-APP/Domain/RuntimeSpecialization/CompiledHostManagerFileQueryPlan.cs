namespace ResourceManager.App.Domain.RuntimeSpecialization;

public sealed record CompiledHostManagerFileQueryCapacityPlan(
    int MaximumQueryUtf8ByteCount,
    int MaximumQueryRuneCount,
    int MaximumPlanUtf8ByteCount,
    int MaximumSourcePlanCount,
    int MaximumCandidateCountPerSource,
    int MaximumSubmittedCandidateCount,
    int MaximumUniqueCandidateCount,
    int MaximumCandidateSubmitBatchCount,
    int MaximumCandidateSubmitUtf8ByteCount,
    int CandidateTextArenaByteCount,
    int MaximumFileNameUtf8ByteCount,
    int MaximumResultCount,
    int EntryIndexCapacity,
    int OrdinalIndexCapacity)
{
    public bool IsPublished => MaximumQueryUtf8ByteCount > 0
        && MaximumQueryRuneCount > 0
        && MaximumPlanUtf8ByteCount > 0
        && MaximumSourcePlanCount >= 3
        && MaximumCandidateCountPerSource > 0
        && MaximumSubmittedCandidateCount >= (long)MaximumCandidateCountPerSource * 3
        && MaximumUniqueCandidateCount >= (long)MaximumCandidateCountPerSource * 3
        && MaximumUniqueCandidateCount <= MaximumSubmittedCandidateCount
        && MaximumCandidateSubmitBatchCount > 0
        && MaximumCandidateSubmitBatchCount <= MaximumSubmittedCandidateCount
        && MaximumCandidateSubmitUtf8ByteCount > 0
        && CandidateTextArenaByteCount > 0
        && MaximumFileNameUtf8ByteCount > 0
        && MaximumResultCount > 0
        && MaximumResultCount <= MaximumCandidateCountPerSource
        && IsValidIndexCapacity(EntryIndexCapacity, MaximumUniqueCandidateCount)
        && IsValidIndexCapacity(OrdinalIndexCapacity, MaximumSubmittedCandidateCount);

    private static bool IsValidIndexCapacity(int capacity, int count)
        => capacity > 0 && capacity >= count && (capacity & (capacity - 1)) == 0;

    public static CompiledHostManagerFileQueryCapacityPlan Unpublished { get; } = new(
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
}

public sealed record CompiledHostManagerFileQueryBuildPlan(
    uint AbiVersion,
    string NativeModule,
    string NativeBinaryFileName,
    string NativeBinarySha256,
    int MaximumQuerySessionCount,
    long MaximumTotalResidentByteBudget,
    CompiledHostManagerFileQueryCapacityPlan CapacityLimits)
{
    public bool IsPublished => AbiVersion == 0x0002_0000U
        && string.Equals(NativeModule, "file_query", StringComparison.Ordinal)
        && !string.IsNullOrWhiteSpace(NativeBinaryFileName)
        && NativeBinarySha256.Length == 64
        && NativeBinarySha256.All(static value => char.IsAsciiHexDigit(value))
        && MaximumQuerySessionCount > 0
        && MaximumTotalResidentByteBudget > 0
        && CapacityLimits.IsPublished;

    public static CompiledHostManagerFileQueryBuildPlan Unpublished { get; } = new(
        0,
        string.Empty,
        string.Empty,
        string.Empty,
        0,
        0,
        CompiledHostManagerFileQueryCapacityPlan.Unpublished);
}

public sealed record CompiledHostManagerFileQueryRecreatePlan(
    int QuerySessionCount,
    CompiledHostManagerFileQueryCapacityPlan Capacity)
{
    public bool IsPublished => QuerySessionCount > 0 && Capacity.IsPublished;

    public static CompiledHostManagerFileQueryRecreatePlan Unpublished { get; } = new(
        0,
        CompiledHostManagerFileQueryCapacityPlan.Unpublished);
}

public sealed record CompiledHostManagerFileQueryHotPublishPlan(
    ulong ConfigurationGeneration,
    int ShortQueryRuneThreshold,
    uint UnicodeTokenizerVersion,
    uint UnicodeRemoveDiacriticsMode,
    uint TrigramTokenizerContractVersion,
    int CandidateLimitMultiplier,
    int CandidateLimitFloor,
    int CandidateLimitCeiling,
    int FileNamePriority,
    int RelativePathPriority,
    int SoftwareNamePriority,
    uint TextMatchingVersion,
    long PerSessionResidentByteBudget,
    long TotalResidentByteBudget)
{
    public bool IsPublished => ConfigurationGeneration > 0
        && ShortQueryRuneThreshold >= 3
        && UnicodeTokenizerVersion == 0x0006_0100U
        && UnicodeRemoveDiacriticsMode == 2U
        && TrigramTokenizerContractVersion == 0x0001_0000U
        && CandidateLimitMultiplier > 0
        && CandidateLimitFloor > 0
        && CandidateLimitCeiling >= CandidateLimitFloor
        && FileNamePriority > 0
        && RelativePathPriority > 0
        && SoftwareNamePriority > 0
        && FileNamePriority != RelativePathPriority
        && FileNamePriority != SoftwareNamePriority
        && RelativePathPriority != SoftwareNamePriority
        && TextMatchingVersion == 0x0001_0000U
        && PerSessionResidentByteBudget > 0
        && TotalResidentByteBudget > 0;

    public static CompiledHostManagerFileQueryHotPublishPlan Unpublished { get; } = new(
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
}

public sealed record CompiledHostManagerFileQueryPlan(
    CompiledHostManagerFileQueryBuildPlan Build,
    CompiledHostManagerFileQueryRecreatePlan Recreate,
    CompiledHostManagerFileQueryHotPublishPlan HotPublish,
    ulong ConfigurationGeneration,
    string ConfigurationSha256)
{
    public bool IsPublished => Build.IsPublished
        && Recreate.IsPublished
        && HotPublish.IsPublished
        && Recreate.QuerySessionCount <= Build.MaximumQuerySessionCount
        && CapacityWithin(Recreate.Capacity, Build.CapacityLimits)
        && HotPublish.CandidateLimitCeiling
            <= Recreate.Capacity.MaximumCandidateCountPerSource
        && (long)Recreate.Capacity.MaximumResultCount
            * HotPublish.CandidateLimitMultiplier <= uint.MaxValue
        && HotPublish.PerSessionResidentByteBudget
            <= HotPublish.TotalResidentByteBudget / Recreate.QuerySessionCount
        && HotPublish.TotalResidentByteBudget
            <= Build.MaximumTotalResidentByteBudget
        && HotPublish.ConfigurationGeneration == ConfigurationGeneration
        && ConfigurationGeneration > 0
        && ConfigurationSha256.Length == 64
        && ConfigurationSha256.All(static value => char.IsAsciiHexDigit(value));

    private static bool CapacityWithin(
        CompiledHostManagerFileQueryCapacityPlan value,
        CompiledHostManagerFileQueryCapacityPlan maximum)
        => value.MaximumQueryUtf8ByteCount <= maximum.MaximumQueryUtf8ByteCount
            && value.MaximumQueryRuneCount <= maximum.MaximumQueryRuneCount
            && value.MaximumPlanUtf8ByteCount <= maximum.MaximumPlanUtf8ByteCount
            && value.MaximumSourcePlanCount <= maximum.MaximumSourcePlanCount
            && value.MaximumCandidateCountPerSource
                <= maximum.MaximumCandidateCountPerSource
            && value.MaximumSubmittedCandidateCount
                <= maximum.MaximumSubmittedCandidateCount
            && value.MaximumUniqueCandidateCount <= maximum.MaximumUniqueCandidateCount
            && value.MaximumCandidateSubmitBatchCount
                <= maximum.MaximumCandidateSubmitBatchCount
            && value.MaximumCandidateSubmitUtf8ByteCount
                <= maximum.MaximumCandidateSubmitUtf8ByteCount
            && value.CandidateTextArenaByteCount <= maximum.CandidateTextArenaByteCount
            && value.MaximumFileNameUtf8ByteCount
                <= maximum.MaximumFileNameUtf8ByteCount
            && value.MaximumResultCount <= maximum.MaximumResultCount
            && value.EntryIndexCapacity <= maximum.EntryIndexCapacity
            && value.OrdinalIndexCapacity <= maximum.OrdinalIndexCapacity;

    public static CompiledHostManagerFileQueryPlan Unpublished { get; } = new(
        CompiledHostManagerFileQueryBuildPlan.Unpublished,
        CompiledHostManagerFileQueryRecreatePlan.Unpublished,
        CompiledHostManagerFileQueryHotPublishPlan.Unpublished,
        0,
        string.Empty);
}
