using System.Text.Json.Serialization;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class HostManagerFileQueryCapacityProfile
{
    [JsonPropertyName("maximum_query_utf8_byte_count")]
    public required int MaximumQueryUtf8ByteCount { get; init; }

    [JsonPropertyName("maximum_query_rune_count")]
    public required int MaximumQueryRuneCount { get; init; }

    [JsonPropertyName("maximum_plan_utf8_byte_count")]
    public required int MaximumPlanUtf8ByteCount { get; init; }

    [JsonPropertyName("maximum_source_plan_count")]
    public required int MaximumSourcePlanCount { get; init; }

    [JsonPropertyName("maximum_candidate_count_per_source")]
    public required int MaximumCandidateCountPerSource { get; init; }

    [JsonPropertyName("maximum_submitted_candidate_count")]
    public required int MaximumSubmittedCandidateCount { get; init; }

    [JsonPropertyName("maximum_unique_candidate_count")]
    public required int MaximumUniqueCandidateCount { get; init; }

    [JsonPropertyName("maximum_candidate_submit_batch_count")]
    public required int MaximumCandidateSubmitBatchCount { get; init; }

    [JsonPropertyName("maximum_candidate_submit_utf8_byte_count")]
    public required int MaximumCandidateSubmitUtf8ByteCount { get; init; }

    [JsonPropertyName("candidate_text_arena_byte_count")]
    public required int CandidateTextArenaByteCount { get; init; }

    [JsonPropertyName("maximum_file_name_utf8_byte_count")]
    public required int MaximumFileNameUtf8ByteCount { get; init; }

    [JsonPropertyName("maximum_result_count")]
    public required int MaximumResultCount { get; init; }

    [JsonPropertyName("entry_index_capacity")]
    public required int EntryIndexCapacity { get; init; }

    [JsonPropertyName("ordinal_index_capacity")]
    public required int OrdinalIndexCapacity { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class HostManagerFileQueryBuildCapacityProfile
{
    [JsonPropertyName("maximum_query_session_count")]
    public required int MaximumQuerySessionCount { get; init; }

    [JsonPropertyName("maximum_total_resident_byte_budget")]
    public required long MaximumTotalResidentByteBudget { get; init; }

    [JsonPropertyName("per_session")]
    public required HostManagerFileQueryCapacityProfile PerSession { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class HostManagerFileQueryRecreateProfile
{
    [JsonPropertyName("query_session_count")]
    public required int QuerySessionCount { get; init; }

    [JsonPropertyName("per_session")]
    public required HostManagerFileQueryCapacityProfile PerSession { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class HostManagerFileQueryHotPublishProfile
{
    [JsonPropertyName("short_query_rune_threshold")]
    public required int ShortQueryRuneThreshold { get; init; }

    [JsonPropertyName("unicode_tokenizer_version")]
    public required uint UnicodeTokenizerVersion { get; init; }

    [JsonPropertyName("unicode_remove_diacritics_mode")]
    public required uint UnicodeRemoveDiacriticsMode { get; init; }

    [JsonPropertyName("trigram_tokenizer_contract_version")]
    public required uint TrigramTokenizerContractVersion { get; init; }

    [JsonPropertyName("candidate_limit_multiplier")]
    public required int CandidateLimitMultiplier { get; init; }

    [JsonPropertyName("candidate_limit_floor")]
    public required int CandidateLimitFloor { get; init; }

    [JsonPropertyName("candidate_limit_ceiling")]
    public required int CandidateLimitCeiling { get; init; }

    [JsonPropertyName("file_name_priority")]
    public required int FileNamePriority { get; init; }

    [JsonPropertyName("relative_path_priority")]
    public required int RelativePathPriority { get; init; }

    [JsonPropertyName("software_name_priority")]
    public required int SoftwareNamePriority { get; init; }

    [JsonPropertyName("text_matching_version")]
    public required uint TextMatchingVersion { get; init; }

    [JsonPropertyName("per_session_resident_byte_budget")]
    public required long PerSessionResidentByteBudget { get; init; }

    [JsonPropertyName("total_resident_byte_budget")]
    public required long TotalResidentByteBudget { get; init; }
}
