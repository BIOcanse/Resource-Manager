using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization;

public sealed partial class HostManagerPlanCompiler
{
    private static CompiledHostManagerFileQueryBuildPlan CompileFileQueryBuild(
        uint abiVersion,
        HostManagerFileQueryBuildCapacityProfile source,
        CompiledHostManagerNativeBinaryIdentity binary)
    {
        if (abiVersion != NativeFileQueryAbi.Version)
        {
            throw new InvalidDataException(
                "build_specialize.file_query_abi_version does not match the binary.");
        }
        ArgumentNullException.ThrowIfNull(source);
        ValidatePositive(
            source.MaximumQuerySessionCount,
            "build_specialize.capacity_limits.file_query.maximum_query_session_count");
        ValidatePositive(
            source.MaximumTotalResidentByteBudget,
            "build_specialize.capacity_limits.file_query.maximum_total_resident_byte_budget");
        var capacity = CompileFileQueryCapacity(
            source.PerSession
                ?? throw Missing("build_specialize.capacity_limits.file_query.per_session"),
            "build_specialize.capacity_limits.file_query.per_session");

        return new CompiledHostManagerFileQueryBuildPlan(
            abiVersion,
            "file_query",
            binary.FileName,
            binary.Sha256,
            source.MaximumQuerySessionCount,
            source.MaximumTotalResidentByteBudget,
            capacity);
    }

    private static CompiledHostManagerFileQueryRecreatePlan CompileFileQueryRecreate(
        HostManagerFileQueryRecreateProfile source,
        CompiledHostManagerFileQueryBuildPlan build)
    {
        ArgumentNullException.ThrowIfNull(source);
        ValidatePositive(
            source.QuerySessionCount,
            "host_recreate.file_query.query_session_count");
        ValidateMaximum(
            source.QuerySessionCount,
            build.MaximumQuerySessionCount,
            "host_recreate.file_query.query_session_count");
        var capacity = CompileFileQueryCapacity(
            source.PerSession ?? throw Missing("host_recreate.file_query.per_session"),
            "host_recreate.file_query.per_session");
        ValidateFileQueryCapacityMaximum(capacity, build.CapacityLimits);
        return new CompiledHostManagerFileQueryRecreatePlan(source.QuerySessionCount, capacity);
    }

    private static CompiledHostManagerFileQueryHotPublishPlan CompileFileQueryHotPublish(
        HostManagerFileQueryHotPublishProfile source,
        CompiledHostManagerFileQueryBuildPlan build,
        CompiledHostManagerFileQueryRecreatePlan recreate,
        int profileRevision)
    {
        ArgumentNullException.ThrowIfNull(source);
        ValidatePositive(
            source.ShortQueryRuneThreshold,
            "hot_publish.file_query.short_query_rune_threshold");
        if (source.ShortQueryRuneThreshold < 3)
        {
            throw new InvalidDataException(
                "hot_publish.file_query.short_query_rune_threshold must be at least 3.");
        }
        if (source.UnicodeTokenizerVersion != NativeFileQueryAbi.UnicodeTokenizerVersion
            || source.UnicodeRemoveDiacriticsMode != NativeFileQueryAbi.UnicodeRemoveDiacriticsMode
            || source.TrigramTokenizerContractVersion != NativeFileQueryAbi.TrigramTokenizerContractVersion
            || source.TextMatchingVersion != NativeFileQueryAbi.TextMatchingVersion)
        {
            throw new InvalidDataException(
                "hot_publish.file_query tokenizer or matching contract does not match the native ABI.");
        }
        ValidatePositive(source.CandidateLimitMultiplier, "hot_publish.file_query.candidate_limit_multiplier");
        ValidatePositive(source.CandidateLimitFloor, "hot_publish.file_query.candidate_limit_floor");
        ValidatePositive(source.CandidateLimitCeiling, "hot_publish.file_query.candidate_limit_ceiling");
        if (source.CandidateLimitFloor > source.CandidateLimitCeiling
            || source.CandidateLimitCeiling > recreate.Capacity.MaximumCandidateCountPerSource)
        {
            throw new InvalidDataException(
                "hot_publish.file_query candidate limits exceed the recreate capacity.");
        }
        if ((long)recreate.Capacity.MaximumResultCount * source.CandidateLimitMultiplier
            > uint.MaxValue)
        {
            throw new InvalidDataException(
                "hot_publish.file_query result and candidate multiplier exceed the native range.");
        }
        ValidatePositive(source.FileNamePriority, "hot_publish.file_query.file_name_priority");
        ValidatePositive(source.RelativePathPriority, "hot_publish.file_query.relative_path_priority");
        ValidatePositive(source.SoftwareNamePriority, "hot_publish.file_query.software_name_priority");
        if (source.FileNamePriority == source.RelativePathPriority
            || source.FileNamePriority == source.SoftwareNamePriority
            || source.RelativePathPriority == source.SoftwareNamePriority)
        {
            throw new InvalidDataException(
                "hot_publish.file_query source priorities must be distinct.");
        }
        ValidatePositive(
            source.PerSessionResidentByteBudget,
            "hot_publish.file_query.per_session_resident_byte_budget");
        ValidatePositive(
            source.TotalResidentByteBudget,
            "hot_publish.file_query.total_resident_byte_budget");
        if (source.PerSessionResidentByteBudget
                > source.TotalResidentByteBudget / recreate.QuerySessionCount
            || source.TotalResidentByteBudget > build.MaximumTotalResidentByteBudget)
        {
            throw new InvalidDataException(
                "hot_publish.file_query resident budgets do not cover the complete session pool.");
        }

        return new CompiledHostManagerFileQueryHotPublishPlan(
            (ulong)checked((uint)profileRevision) << 32,
            source.ShortQueryRuneThreshold,
            source.UnicodeTokenizerVersion,
            source.UnicodeRemoveDiacriticsMode,
            source.TrigramTokenizerContractVersion,
            source.CandidateLimitMultiplier,
            source.CandidateLimitFloor,
            source.CandidateLimitCeiling,
            source.FileNamePriority,
            source.RelativePathPriority,
            source.SoftwareNamePriority,
            source.TextMatchingVersion,
            source.PerSessionResidentByteBudget,
            source.TotalResidentByteBudget);
    }

    private static CompiledHostManagerFileQueryPlan CompileFileQueryPlan(
        CompiledHostManagerFileQueryBuildPlan build,
        CompiledHostManagerFileQueryRecreatePlan recreate,
        CompiledHostManagerFileQueryHotPublishPlan hotPublish)
    {
        var configurationSha256 = HostManagerPlanIdentity.ComputeDigest(new
        {
            build.AbiVersion,
            recreate,
            hotPublish
        });
        return new CompiledHostManagerFileQueryPlan(
            build,
            recreate,
            hotPublish,
            hotPublish.ConfigurationGeneration,
            configurationSha256);
    }

    private static CompiledHostManagerFileQueryCapacityPlan CompileFileQueryCapacity(
        HostManagerFileQueryCapacityProfile source,
        string path)
    {
        ArgumentNullException.ThrowIfNull(source);
        ValidatePositive(source.MaximumQueryUtf8ByteCount, $"{path}.maximum_query_utf8_byte_count");
        ValidatePositive(source.MaximumQueryRuneCount, $"{path}.maximum_query_rune_count");
        ValidatePositive(source.MaximumPlanUtf8ByteCount, $"{path}.maximum_plan_utf8_byte_count");
        ValidatePositive(source.MaximumSourcePlanCount, $"{path}.maximum_source_plan_count");
        ValidatePositive(source.MaximumCandidateCountPerSource, $"{path}.maximum_candidate_count_per_source");
        ValidatePositive(source.MaximumSubmittedCandidateCount, $"{path}.maximum_submitted_candidate_count");
        ValidatePositive(source.MaximumUniqueCandidateCount, $"{path}.maximum_unique_candidate_count");
        ValidatePositive(source.MaximumCandidateSubmitBatchCount, $"{path}.maximum_candidate_submit_batch_count");
        ValidatePositive(source.MaximumCandidateSubmitUtf8ByteCount, $"{path}.maximum_candidate_submit_utf8_byte_count");
        ValidatePositive(source.CandidateTextArenaByteCount, $"{path}.candidate_text_arena_byte_count");
        ValidatePositive(source.MaximumFileNameUtf8ByteCount, $"{path}.maximum_file_name_utf8_byte_count");
        ValidatePositive(source.MaximumResultCount, $"{path}.maximum_result_count");
        ValidatePositive(source.EntryIndexCapacity, $"{path}.entry_index_capacity");
        ValidatePositive(source.OrdinalIndexCapacity, $"{path}.ordinal_index_capacity");
        var result = new CompiledHostManagerFileQueryCapacityPlan(
            source.MaximumQueryUtf8ByteCount,
            source.MaximumQueryRuneCount,
            source.MaximumPlanUtf8ByteCount,
            source.MaximumSourcePlanCount,
            source.MaximumCandidateCountPerSource,
            source.MaximumSubmittedCandidateCount,
            source.MaximumUniqueCandidateCount,
            source.MaximumCandidateSubmitBatchCount,
            source.MaximumCandidateSubmitUtf8ByteCount,
            source.CandidateTextArenaByteCount,
            source.MaximumFileNameUtf8ByteCount,
            source.MaximumResultCount,
            source.EntryIndexCapacity,
            source.OrdinalIndexCapacity);
        if (!result.IsPublished)
        {
            throw new InvalidDataException($"{path} violates the native file-query shape contract.");
        }
        return result;
    }

    private static void ValidateFileQueryCapacityMaximum(
        CompiledHostManagerFileQueryCapacityPlan value,
        CompiledHostManagerFileQueryCapacityPlan maximum)
    {
        ValidateMaximum(value.MaximumQueryUtf8ByteCount, maximum.MaximumQueryUtf8ByteCount, "host_recreate.file_query.per_session.maximum_query_utf8_byte_count");
        ValidateMaximum(value.MaximumQueryRuneCount, maximum.MaximumQueryRuneCount, "host_recreate.file_query.per_session.maximum_query_rune_count");
        ValidateMaximum(value.MaximumPlanUtf8ByteCount, maximum.MaximumPlanUtf8ByteCount, "host_recreate.file_query.per_session.maximum_plan_utf8_byte_count");
        ValidateMaximum(value.MaximumSourcePlanCount, maximum.MaximumSourcePlanCount, "host_recreate.file_query.per_session.maximum_source_plan_count");
        ValidateMaximum(value.MaximumCandidateCountPerSource, maximum.MaximumCandidateCountPerSource, "host_recreate.file_query.per_session.maximum_candidate_count_per_source");
        ValidateMaximum(value.MaximumSubmittedCandidateCount, maximum.MaximumSubmittedCandidateCount, "host_recreate.file_query.per_session.maximum_submitted_candidate_count");
        ValidateMaximum(value.MaximumUniqueCandidateCount, maximum.MaximumUniqueCandidateCount, "host_recreate.file_query.per_session.maximum_unique_candidate_count");
        ValidateMaximum(value.MaximumCandidateSubmitBatchCount, maximum.MaximumCandidateSubmitBatchCount, "host_recreate.file_query.per_session.maximum_candidate_submit_batch_count");
        ValidateMaximum(value.MaximumCandidateSubmitUtf8ByteCount, maximum.MaximumCandidateSubmitUtf8ByteCount, "host_recreate.file_query.per_session.maximum_candidate_submit_utf8_byte_count");
        ValidateMaximum(value.CandidateTextArenaByteCount, maximum.CandidateTextArenaByteCount, "host_recreate.file_query.per_session.candidate_text_arena_byte_count");
        ValidateMaximum(value.MaximumFileNameUtf8ByteCount, maximum.MaximumFileNameUtf8ByteCount, "host_recreate.file_query.per_session.maximum_file_name_utf8_byte_count");
        ValidateMaximum(value.MaximumResultCount, maximum.MaximumResultCount, "host_recreate.file_query.per_session.maximum_result_count");
        ValidateMaximum(value.EntryIndexCapacity, maximum.EntryIndexCapacity, "host_recreate.file_query.per_session.entry_index_capacity");
        ValidateMaximum(value.OrdinalIndexCapacity, maximum.OrdinalIndexCapacity, "host_recreate.file_query.per_session.ordinal_index_capacity");
    }
}
