using System.Text.Json.Serialization;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class HostManagerSoftwareIdentityCatalogCapacityProfile
{
    [JsonPropertyName("maximum_entry_count")]
    public required int MaximumEntryCount { get; init; }

    [JsonPropertyName("maximum_alias_count")]
    public required int MaximumAliasCount { get; init; }

    [JsonPropertyName("maximum_root_count")]
    public required int MaximumRootCount { get; init; }

    [JsonPropertyName("maximum_catalog_key_byte_count")]
    public required int MaximumCatalogKeyByteCount { get; init; }

    [JsonPropertyName("maximum_query_fact_count")]
    public required int MaximumQueryFactCount { get; init; }

    [JsonPropertyName("maximum_query_signal_count")]
    public required int MaximumQuerySignalCount { get; init; }

    [JsonPropertyName("maximum_query_key_byte_count")]
    public required int MaximumQueryKeyByteCount { get; init; }

    [JsonPropertyName("entry_index_capacity")]
    public required int EntryIndexCapacity { get; init; }

    [JsonPropertyName("alias_index_capacity")]
    public required int AliasIndexCapacity { get; init; }

    [JsonPropertyName("identity_index_capacity")]
    public required int IdentityIndexCapacity { get; init; }

    [JsonPropertyName("root_index_capacity")]
    public required int RootIndexCapacity { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class HostManagerSoftwareIdentityCatalogRecreateProfile
{
    [JsonPropertyName("capacity")]
    public required HostManagerSoftwareIdentityCatalogCapacityProfile Capacity { get; init; }

    [JsonPropertyName("prohibited_executable_aliases")]
    public required string[] ProhibitedExecutableAliases { get; init; }

    [JsonPropertyName("launcher_tokens")]
    public required string[] LauncherTokens { get; init; }

    [JsonPropertyName("managed_child_segments")]
    public required string[] ManagedChildSegments { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class HostManagerSoftwareIdentityCatalogHotPublishProfile
{
    [JsonPropertyName("resident_byte_budget")]
    public required long ResidentByteBudget { get; init; }

    [JsonPropertyName("minimum_contains_key_length")]
    public required int MinimumContainsKeyLength { get; init; }

    [JsonPropertyName("exact_text_score")]
    public required int ExactTextScore { get; init; }

    [JsonPropertyName("contains_text_score")]
    public required int ContainsTextScore { get; init; }

    [JsonPropertyName("identity_minimum_score")]
    public required int IdentityMinimumScore { get; init; }

    [JsonPropertyName("strong_evidence_minimum_score")]
    public required int StrongEvidenceMinimumScore { get; init; }

    [JsonPropertyName("root_hit_bonus")]
    public required int RootHitBonus { get; init; }

    [JsonPropertyName("query_launcher_match_bonus")]
    public required int QueryLauncherMatchBonus { get; init; }

    [JsonPropertyName("query_launcher_nonmatch_penalty")]
    public required int QueryLauncherNonmatchPenalty { get; init; }

    [JsonPropertyName("root_launcher_match_bonus")]
    public required int RootLauncherMatchBonus { get; init; }

    [JsonPropertyName("root_launcher_nonmatch_penalty")]
    public required int RootLauncherNonmatchPenalty { get; init; }

    [JsonPropertyName("root_reject_score")]
    public required int RootRejectScore { get; init; }

    [JsonPropertyName("signal_weights")]
    public required int[] SignalWeights { get; init; }

    [JsonPropertyName("root_signal_weights")]
    public required int[] RootSignalWeights { get; init; }

    [JsonPropertyName("strong_signal_mask")]
    public required uint StrongSignalMask { get; init; }

    [JsonPropertyName("root_signal_mask")]
    public required uint RootSignalMask { get; init; }

    [JsonPropertyName("launcher_non_entry_requires_root")]
    public required bool LauncherNonEntryRequiresRoot { get; init; }
}
