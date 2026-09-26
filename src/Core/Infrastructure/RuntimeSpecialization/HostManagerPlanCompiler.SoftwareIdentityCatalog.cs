using System.Collections.Immutable;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization;

public sealed partial class HostManagerPlanCompiler
{
    private static CompiledHostManagerSoftwareIdentityCatalogBuildPlan CompileSoftwareIdentityCatalogBuild(
        uint abiVersion,
        HostManagerSoftwareIdentityCatalogCapacityProfile source,
        CompiledHostManagerNativeBinaryIdentity binary)
    {
        if (abiVersion != NativeSoftwareIdentityCatalogAbi.Version)
        {
            throw new InvalidDataException(
                "build_specialize.software_identity_catalog_abi_version does not match the binary.");
        }

        return new CompiledHostManagerSoftwareIdentityCatalogBuildPlan(
            abiVersion,
            "software_identity_catalog",
            binary.FileName,
            binary.Sha256,
            CompileSoftwareIdentityCatalogCapacity(
                source,
                "build_specialize.capacity_limits.software_identity_catalog"));
    }

    private static CompiledHostManagerSoftwareIdentityCatalogRecreatePlan
        CompileSoftwareIdentityCatalogRecreate(
            HostManagerSoftwareIdentityCatalogRecreateProfile source,
            CompiledHostManagerSoftwareIdentityCatalogBuildPlan build)
    {
        ArgumentNullException.ThrowIfNull(source);
        var capacity = CompileSoftwareIdentityCatalogCapacity(
            source.Capacity ?? throw Missing("host_recreate.software_identity_catalog.capacity"),
            "host_recreate.software_identity_catalog.capacity");
        var limit = build.CapacityLimits;
        ValidateMaximum(capacity.MaximumEntryCount, limit.MaximumEntryCount, "host_recreate.software_identity_catalog.capacity.maximum_entry_count");
        ValidateMaximum(capacity.MaximumAliasCount, limit.MaximumAliasCount, "host_recreate.software_identity_catalog.capacity.maximum_alias_count");
        ValidateMaximum(capacity.MaximumRootCount, limit.MaximumRootCount, "host_recreate.software_identity_catalog.capacity.maximum_root_count");
        ValidateMaximum(capacity.MaximumCatalogKeyByteCount, limit.MaximumCatalogKeyByteCount, "host_recreate.software_identity_catalog.capacity.maximum_catalog_key_byte_count");
        ValidateMaximum(capacity.MaximumQueryFactCount, limit.MaximumQueryFactCount, "host_recreate.software_identity_catalog.capacity.maximum_query_fact_count");
        ValidateMaximum(capacity.MaximumQuerySignalCount, limit.MaximumQuerySignalCount, "host_recreate.software_identity_catalog.capacity.maximum_query_signal_count");
        ValidateMaximum(capacity.MaximumQueryKeyByteCount, limit.MaximumQueryKeyByteCount, "host_recreate.software_identity_catalog.capacity.maximum_query_key_byte_count");
        ValidateMaximum(capacity.EntryIndexCapacity, limit.EntryIndexCapacity, "host_recreate.software_identity_catalog.capacity.entry_index_capacity");
        ValidateMaximum(capacity.AliasIndexCapacity, limit.AliasIndexCapacity, "host_recreate.software_identity_catalog.capacity.alias_index_capacity");
        ValidateMaximum(capacity.IdentityIndexCapacity, limit.IdentityIndexCapacity, "host_recreate.software_identity_catalog.capacity.identity_index_capacity");
        ValidateMaximum(capacity.RootIndexCapacity, limit.RootIndexCapacity, "host_recreate.software_identity_catalog.capacity.root_index_capacity");

        var prohibitedAliases = CompileCanonicalRuleList(
            source.ProhibitedExecutableAliases,
            "host_recreate.software_identity_catalog.prohibited_executable_aliases");
        var launcherTokens = CompileNormalizedRuleList(
            source.LauncherTokens,
            "host_recreate.software_identity_catalog.launcher_tokens");
        var managedChildSegments = CompileNormalizedRuleList(
            source.ManagedChildSegments,
            "host_recreate.software_identity_catalog.managed_child_segments");
        if (prohibitedAliases.Length + launcherTokens.Length + managedChildSegments.Length
            > capacity.MaximumAliasCount)
        {
            throw new InvalidDataException(
                "host_recreate.software_identity_catalog rule rows exceed maximum_alias_count.");
        }

        return new CompiledHostManagerSoftwareIdentityCatalogRecreatePlan(
            capacity,
            prohibitedAliases,
            launcherTokens,
            managedChildSegments);
    }

    private static CompiledHostManagerSoftwareIdentityCatalogHotPublishPlan
        CompileSoftwareIdentityCatalogHotPublish(
            HostManagerSoftwareIdentityCatalogHotPublishProfile source,
            int profileRevision)
    {
        ArgumentNullException.ThrowIfNull(source);
        ValidatePositive(source.ResidentByteBudget, "hot_publish.software_identity_catalog.resident_byte_budget");
        ValidatePositive(source.MinimumContainsKeyLength, "hot_publish.software_identity_catalog.minimum_contains_key_length");
        ValidatePositive(source.ExactTextScore, "hot_publish.software_identity_catalog.exact_text_score");
        ValidatePositive(source.ContainsTextScore, "hot_publish.software_identity_catalog.contains_text_score");
        ValidateNonNegative(source.IdentityMinimumScore, "hot_publish.software_identity_catalog.identity_minimum_score");
        ValidateNonNegative(source.StrongEvidenceMinimumScore, "hot_publish.software_identity_catalog.strong_evidence_minimum_score");
        ValidateNonNegative(source.RootHitBonus, "hot_publish.software_identity_catalog.root_hit_bonus");
        ValidateNonNegative(source.QueryLauncherMatchBonus, "hot_publish.software_identity_catalog.query_launcher_match_bonus");
        ValidateNonNegative(source.QueryLauncherNonmatchPenalty, "hot_publish.software_identity_catalog.query_launcher_nonmatch_penalty");
        ValidateNonNegative(source.RootLauncherMatchBonus, "hot_publish.software_identity_catalog.root_launcher_match_bonus");
        ValidateNonNegative(source.RootLauncherNonmatchPenalty, "hot_publish.software_identity_catalog.root_launcher_nonmatch_penalty");

        var signalWeights = CompileSixNonNegativeWeights(
            source.SignalWeights,
            "hot_publish.software_identity_catalog.signal_weights");
        var rootSignalWeights = CompileSixNonNegativeWeights(
            source.RootSignalWeights,
            "hot_publish.software_identity_catalog.root_signal_weights");
        if ((source.StrongSignalMask & ~0x3FU) != 0)
        {
            throw new InvalidDataException(
                "hot_publish.software_identity_catalog.strong_signal_mask contains unknown bits.");
        }
        if ((source.RootSignalMask & ~0x3FU) != 0)
        {
            throw new InvalidDataException(
                "hot_publish.software_identity_catalog.root_signal_mask contains unknown bits.");
        }

        var textScore = Math.Max(source.ExactTextScore, source.ContainsTextScore);
        var strongest = signalWeights.Max(static value => (long)value) + textScore;
        var identityTotal = strongest + source.RootHitBonus + source.QueryLauncherMatchBonus;
        var rootTotal = rootSignalWeights.Sum(static value => (long)value)
            + (long)textScore * rootSignalWeights.Length
            + source.RootLauncherMatchBonus;
        if (identityTotal > int.MaxValue || rootTotal > int.MaxValue)
        {
            throw new InvalidDataException(
                "hot_publish.software_identity_catalog score bounds exceed the native i32 contract.");
        }

        return new CompiledHostManagerSoftwareIdentityCatalogHotPublishPlan(
            (ulong)checked((uint)profileRevision) << 32,
            source.ResidentByteBudget,
            source.MinimumContainsKeyLength,
            source.ExactTextScore,
            source.ContainsTextScore,
            source.IdentityMinimumScore,
            source.StrongEvidenceMinimumScore,
            source.RootHitBonus,
            source.QueryLauncherMatchBonus,
            source.QueryLauncherNonmatchPenalty,
            source.RootLauncherMatchBonus,
            source.RootLauncherNonmatchPenalty,
            source.RootRejectScore,
            signalWeights,
            rootSignalWeights,
            source.StrongSignalMask,
            source.RootSignalMask,
            source.LauncherNonEntryRequiresRoot);
    }

    private static CompiledHostManagerSoftwareIdentityCatalogPlan CompileSoftwareIdentityCatalogPlan(
        CompiledHostManagerSoftwareIdentityCatalogBuildPlan build,
        CompiledHostManagerSoftwareIdentityCatalogRecreatePlan recreate,
        CompiledHostManagerSoftwareIdentityCatalogHotPublishPlan hotPublish)
    {
        var configurationSha256 = HostManagerPlanIdentity.ComputeDigest(new
        {
            build.AbiVersion,
            recreate,
            hotPublish
        });
        return new CompiledHostManagerSoftwareIdentityCatalogPlan(
            build,
            recreate,
            hotPublish,
            hotPublish.ConfigurationGeneration,
            configurationSha256);
    }

    private static CompiledHostManagerSoftwareIdentityCatalogCapacityPlan
        CompileSoftwareIdentityCatalogCapacity(
            HostManagerSoftwareIdentityCatalogCapacityProfile source,
            string path)
    {
        ArgumentNullException.ThrowIfNull(source);
        ValidatePositive(source.MaximumEntryCount, $"{path}.maximum_entry_count");
        ValidatePositive(source.MaximumAliasCount, $"{path}.maximum_alias_count");
        ValidateNonNegative(source.MaximumRootCount, $"{path}.maximum_root_count");
        ValidatePositive(source.MaximumCatalogKeyByteCount, $"{path}.maximum_catalog_key_byte_count");
        ValidatePositive(source.MaximumQueryFactCount, $"{path}.maximum_query_fact_count");
        ValidatePositive(source.MaximumQuerySignalCount, $"{path}.maximum_query_signal_count");
        ValidatePositive(source.MaximumQueryKeyByteCount, $"{path}.maximum_query_key_byte_count");
        ValidatePositive(source.EntryIndexCapacity, $"{path}.entry_index_capacity");
        ValidatePositive(source.AliasIndexCapacity, $"{path}.alias_index_capacity");
        ValidatePositive(source.IdentityIndexCapacity, $"{path}.identity_index_capacity");
        ValidatePositive(source.RootIndexCapacity, $"{path}.root_index_capacity");
        if (source.MaximumQuerySignalCount > NativeSoftwareIdentityCatalogAbi.SignalCount
            || !IsPowerOfTwoAtLeast(source.EntryIndexCapacity, source.MaximumEntryCount)
            || !IsPowerOfTwoAtLeast(source.AliasIndexCapacity, source.MaximumAliasCount)
            || !IsPowerOfTwoAtLeast(source.IdentityIndexCapacity, source.MaximumEntryCount)
            || !IsPowerOfTwoAtLeast(source.RootIndexCapacity, source.MaximumRootCount))
        {
            throw new InvalidDataException(
                $"{path} violates the published software identity catalog capacity relations.");
        }

        return new CompiledHostManagerSoftwareIdentityCatalogCapacityPlan(
            source.MaximumEntryCount,
            source.MaximumAliasCount,
            source.MaximumRootCount,
            source.MaximumCatalogKeyByteCount,
            source.MaximumQueryFactCount,
            source.MaximumQuerySignalCount,
            source.MaximumQueryKeyByteCount,
            source.EntryIndexCapacity,
            source.AliasIndexCapacity,
            source.IdentityIndexCapacity,
            source.RootIndexCapacity);
    }

    private static ImmutableArray<string> CompileCanonicalRuleList(string[]? source, string path)
    {
        var values = source ?? throw Missing(path);
        var result = values.ToImmutableArray();
        if (!result.SequenceEqual(result.Order(StringComparer.Ordinal))
            || result.Distinct(StringComparer.Ordinal).Count() != result.Length
            || result.Any(static value => string.IsNullOrEmpty(value)
                || value[0] == ' '
                || value[^1] == ' '
                || value.Contains('\0')
                || value.Any(static character => character is >= 'A' and <= 'Z')))
        {
            throw new InvalidDataException(
                $"{path} must be unique, ordinal-sorted, non-empty canonical lowercase values.");
        }
        return result;
    }

    private static ImmutableArray<string> CompileNormalizedRuleList(string[]? source, string path)
    {
        var result = CompileCanonicalRuleList(source, path);
        if (result.Any(static value => value.Any(static character =>
            character < 0x80 && !char.IsAsciiLetterOrDigit(character))))
        {
            throw new InvalidDataException(
                $"{path} must contain normalized alphanumeric rule keys.");
        }
        return result;
    }

    private static ImmutableArray<int> CompileSixNonNegativeWeights(int[]? source, string path)
    {
        var values = source ?? throw Missing(path);
        if (values.Length != NativeSoftwareIdentityCatalogAbi.SignalCount
            || values.Any(static value => value < 0))
        {
            throw new InvalidDataException($"{path} must contain exactly six non-negative values.");
        }
        return values.ToImmutableArray();
    }
}
